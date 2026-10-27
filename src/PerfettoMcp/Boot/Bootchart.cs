using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace PerfettoMcp.Boot;

// Parses init's bootchart logs (dir or .tgz): blocks of an uptime line (jiffies), a /proc snapshot, a blank line.
internal sealed partial class Bootchart
{
    private const double Hz = 100.0;

    public Dictionary<string, string> Header { get; } = new(StringComparer.Ordinal);

    public List<StatSample> Stat { get; } = new();

    public List<PsSample> Ps { get; } = new();

    public List<DiskSample> Disk { get; } = new();

    public static Bootchart Load(string path)
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        if (Directory.Exists(path))
        {
            foreach (string name in new[] { "header", "proc_stat.log", "proc_ps.log", "proc_diskstats.log" })
            {
                string f = Path.Combine(path, name);
                if (File.Exists(f))
                {
                    files[name] = File.ReadAllText(f);
                }
            }
        }
        else if (File.Exists(path))
        {
            using FileStream fs = File.OpenRead(path);
            using GZipStream gz = new(fs, CompressionMode.Decompress);
            using TarReader tar = new(gz);
            while (tar.GetNextEntry() is { } e)
            {
                if (e.DataStream is null)
                {
                    continue;
                }
                using StreamReader r = new(e.DataStream);
                files[Path.GetFileName(e.Name)] = r.ReadToEnd();
            }
        }
        else
        {
            throw new FileNotFoundException($"No bootchart directory or tgz at {path}");
        }
        if (!files.TryGetValue("proc_stat.log", out string? procStat) || !files.TryGetValue("proc_ps.log", out string? procPs))
        {
            throw new InvalidDataException("bootchart needs at least proc_stat.log and proc_ps.log");
        }

        Bootchart b = new();
        if (files.TryGetValue("header", out string? header))
        {
            foreach (string line in header.Split('\n'))
            {
                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq > 0)
                {
                    b.Header[line[..eq].Trim()] = line[(eq + 3)..].Trim();
                }
            }
        }
        foreach ((double t, List<string> lines) in Blocks(procStat))
        {
            b.Stat.Add(StatSample.Parse(t, lines));
        }
        foreach ((double t, List<string> lines) in Blocks(procPs))
        {
            b.Ps.Add(PsSample.Parse(t, lines));
        }
        if (files.TryGetValue("proc_diskstats.log", out string? disk))
        {
            foreach ((double t, List<string> lines) in Blocks(disk))
            {
                b.Disk.Add(DiskSample.Parse(t, lines));
            }
        }
        return b;
    }

    // Bootloader stage times that UNISOC (and some other) bootloaders append to the kernel cmdline.
    public Dictionary<string, double> BootloaderMs()
    {
        Dictionary<string, double> d = new(StringComparer.Ordinal);
        if (Header.TryGetValue("system.kernel.options", out string? cmdline))
        {
            foreach (Match m in BootloaderStage().Matches(cmdline))
            {
                d[m.Groups[1].Value] = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        }
        return d;
    }

    private static IEnumerable<(double Time, List<string> Lines)> Blocks(string text)
    {
        List<string>? cur = null;
        double t = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (cur is null)
            {
                if (long.TryParse(line.Trim(), out long jiffies))
                {
                    t = jiffies / Hz;
                    cur = new List<string>();
                }
                continue;
            }
            if (line.Length == 0)
            {
                yield return (t, cur);
                cur = null;
                continue;
            }
            cur.Add(line);
        }
        if (cur is { Count: > 0 })
        {
            yield return (t, cur);
        }
    }

    [GeneratedRegex(@"\b(t[a-z]+)=(\d+)ms\b")]
    private static partial Regex BootloaderStage();
}

internal sealed class StatSample
{
    public double Time { get; private init; }

    public long Busy { get; private init; }

    public long Iowait { get; private init; }

    public long Total { get; private init; }

    public int Running { get; private init; }

    public int Blocked { get; private init; }

    public static StatSample Parse(double t, List<string> lines)
    {
        long busy = 0, iowait = 0, total = 0;
        int running = 0, blocked = 0;
        foreach (string l in lines)
        {
            string[] f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length > 7 && f[0] == "cpu")
            {
                long[] v = f.Skip(1).Take(7).Select(long.Parse).ToArray(); // user nice system idle iowait irq softirq
                busy = v[0] + v[1] + v[2] + v[5] + v[6];
                iowait = v[4];
                total = v.Sum();
            }
            else if (f.Length == 2 && f[0] == "procs_running")
            {
                running = int.Parse(f[1], CultureInfo.InvariantCulture);
            }
            else if (f.Length == 2 && f[0] == "procs_blocked")
            {
                blocked = int.Parse(f[1], CultureInfo.InvariantCulture);
            }
        }
        return new StatSample { Time = t, Busy = busy, Iowait = iowait, Total = total, Running = running, Blocked = blocked };
    }
}

internal sealed class PsSample
{
    public double Time { get; private init; }

    public Dictionary<int, ProcStat> Procs { get; } = new();

    public static PsSample Parse(double t, List<string> lines)
    {
        PsSample s = new() { Time = t };
        foreach (string l in lines)
        {
            if (ProcStat.TryParse(l) is { } p)
            {
                s.Procs[p.Pid] = p;
            }
        }
        return s;
    }
}

internal sealed record ProcStat(int Pid, string Name, int Ppid, long CpuJiffies, double StartSeconds, string State, int Threads)
{
    // "/proc/<pid>/stat": comm is in parentheses and may contain spaces, so split after the last ')'.
    public static ProcStat? TryParse(string line)
    {
        int open = line.IndexOf('(');
        int close = line.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return null;
        }
        if (!int.TryParse(line[..open].Trim(), out int pid))
        {
            return null;
        }
        string[] f = line[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // f[0]=state f[1]=ppid ... f[11]=utime f[12]=stime ... f[19]=starttime
        if (f.Length < 20)
        {
            return null;
        }
        // init's bootchart writes the cmdline instead of comm for some processes ("vold --blkid_context=...").
        string name = line[(open + 1)..close];
        int args = name.IndexOf(" -", StringComparison.Ordinal);
        if (args > 0)
        {
            name = name[..args].TrimEnd();
        }
        return new ProcStat(pid, name, int.Parse(f[1], CultureInfo.InvariantCulture),
            long.Parse(f[11], CultureInfo.InvariantCulture) + long.Parse(f[12], CultureInfo.InvariantCulture),
            long.Parse(f[19], CultureInfo.InvariantCulture) / 100.0, f[0], int.Parse(f[17], CultureInfo.InvariantCulture));
    }
}

internal sealed class DiskSample
{
    public double Time { get; private init; }

    // device -> (sectors read, sectors written, ms spent doing IO)
    public Dictionary<string, (long Read, long Written, long IoMs)> Devices { get; } = new(StringComparer.Ordinal);

    public static DiskSample Parse(double t, List<string> lines)
    {
        DiskSample s = new() { Time = t };
        foreach (string l in lines)
        {
            string[] f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 14)
            {
                s.Devices[f[2]] = (long.Parse(f[5], CultureInfo.InvariantCulture), long.Parse(f[9], CultureInfo.InvariantCulture),
                    long.Parse(f[12], CultureInfo.InvariantCulture));
            }
        }
        return s;
    }
}
