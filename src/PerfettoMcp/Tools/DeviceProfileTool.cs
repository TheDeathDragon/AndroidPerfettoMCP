using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Device;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

// On-device simpleperf sampling of chosen threads (scenario traces carry no callstack sampling).
internal sealed partial class ProfileThreadsTool : TraceToolBase
{
    private const string RemoteData = "/data/local/tmp/mcp_profile.data";
    private readonly Adb adb;

    public ProfileThreadsTool(TraceManager traces, Adb? adb = null) : base(traces)
    {
        this.adb = adb ?? new Adb();
    }

    public override ToolDescriptor Descriptor { get; } = Describe("profile_threads",
        """
        Sample the call stacks of chosen threads on an adb device with simpleperf for `duration_s` and return the
        hottest functions: `self` (where the samples landed) and, with call graphs, `inclusive` (functions with their
        callees — which path the time went through), plus samples per thread. Targets: `process` (name or pid; all its
        threads) optionally narrowed by `thread` (comm prefix, e.g. CachedAppOptimi, HeapTaskDaemon), or `thread` alone
        for kernel threads (exact comm, e.g. kswapd0, SC2355_TX_THREAD), or `tid`. Drive the scenario meanwhile.
        Needs root (userdebug) for system processes and kernel threads; on user builds only debuggable / profileable
        apps can be sampled. Kernel symbols need kptr_restrict 0, which is set for the run and restored afterwards.
        Complements cpu_profile, which reads samples already inside a trace.
        """,
        new SchemaBuilder()
            .Str("serial", "adb serial; optional when exactly one device is connected.")
            .Str("process", "Process name or pid.")
            .Str("thread", "Thread name: prefix of the comm within `process`, or the exact comm of a kernel thread.")
            .Int("tid", "One thread id.")
            .Num("duration_s", "Sampling time in seconds (default 10, max 60).")
            .Bool("call_graph", "Record call graphs (-g) for the inclusive view (default true).")
            .Int("limit", "Rows per section (default 20)."));

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        CancellationToken ct = ctx.Cancellation;
        string? process = Args.OptionalString(args, "process");
        string? thread = Args.OptionalString(args, "thread");
        int tid = Args.OptionalInt(args, "tid", 0, 0, int.MaxValue);
        if (process is null && thread is null && tid == 0)
        {
            throw new McpInvalidParamsException("pass `process`, `thread` or `tid`");
        }
        double duration = Math.Clamp(Args.OptionalDouble(args, "duration_s") ?? 10, 1, 60);
        bool callGraph = Args.OptionalBool(args, "call_graph", true);
        int limit = Args.OptionalInt(args, "limit", 20, 1, 200);

        string serial = await DeviceSteps.ResolveSerialAsync(adb, Args.OptionalString(args, "serial"), ct).ConfigureAwait(false);
        await DeviceSteps.RootAsync(adb, serial, ct).ConfigureAwait(false);
        bool root = (await adb.ShellAsync(serial, "id -u", DeviceSteps.ShortCmd, ct).ConfigureAwait(false)).Stdout.Trim() == "0";

        (string target, string description) = await ResolveTargetAsync(serial, process, thread, tid, ct).ConfigureAwait(false);
        JsonObject result = new() { ["serial"] = serial, ["target"] = description, ["root"] = root, ["duration_s"] = duration };

        string restore = string.Empty;
        if (root)
        {
            // Kernel frames resolve only with kptr_restrict 0; put the device back exactly as it was.
            string old = (await adb.ShellAsync(serial, "cat /proc/sys/kernel/kptr_restrict /proc/sys/kernel/perf_event_paranoid",
                DeviceSteps.ShortCmd, ct).ConfigureAwait(false)).Stdout;
            string[] v = old.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (v.Length == 2 && v.All(x => int.TryParse(x, out _)))
            {
                restore = $"echo {v[0]} > /proc/sys/kernel/kptr_restrict; echo {v[1]} > /proc/sys/kernel/perf_event_paranoid";
                await adb.ShellAsync(serial, "echo 0 > /proc/sys/kernel/kptr_restrict; echo -1 > /proc/sys/kernel/perf_event_paranoid",
                    DeviceSteps.ShortCmd, ct).ConfigureAwait(false);
            }
        }
        try
        {
            string record = $"simpleperf record {(callGraph ? "-g " : "")}{target} --duration {duration.ToString(CultureInfo.InvariantCulture)} -o {RemoteData}";
            AdbResult rec = await DeviceSteps.WithProgressAsync(
                adb.ShellAsync(serial, record, TimeSpan.FromSeconds(duration + 120), ct), duration + 5, ctx,
                $"sampling {description} on {serial}").ConfigureAwait(false);
            if (!rec.Ok || (rec.Output.Contains("failed", StringComparison.OrdinalIgnoreCase) && !rec.Output.Contains("Samples recorded", StringComparison.Ordinal)))
            {
                throw new ToolException($"simpleperf record failed{(root ? "" : " (no root: user builds only allow debuggable / profileable apps)")}: "
                    + Tail(rec.Output));
            }
            result["record"] = Tail(rec.Output, 300);

            AdbResult self = await adb.ShellAsync(serial, $"simpleperf report -i {RemoteData} --sort dso,symbol -n --percent-limit 0.3",
                TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            ReportTable flat = Parse(self.Stdout);
            result["samples"] = flat.Samples;
            result["event_count"] = flat.EventCount;
            result["self"] = flat.ToJson(limit);
            if (callGraph)
            {
                AdbResult incl = await adb.ShellAsync(serial,
                    $"simpleperf report -i {RemoteData} --children --sort dso,symbol -n --percent-limit 1", TimeSpan.FromMinutes(3), ct)
                    .ConfigureAwait(false);
                // Thread start / syscall entry frames are 100% for every sample and say nothing.
                result["inclusive"] = Parse(incl.Stdout).ToJson(limit, skipFullCover: true);
            }
            AdbResult byThread = await adb.ShellAsync(serial, $"simpleperf report -i {RemoteData} --sort tid,comm -n",
                TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            result["by_thread"] = Parse(byThread.Stdout).ToJson(limit);
            if (flat.Samples == 0)
            {
                result["note"] = "No samples: the threads did not run during the window (or the event is not supported).";
            }
        }
        finally
        {
            await adb.ShellAsync(serial, $"rm -f {RemoteData}; {restore}", DeviceSteps.ShortCmd, CancellationToken.None).ConfigureAwait(false);
        }
        return ToolCallResult.Json(result);
    }

    private async Task<(string Target, string Description)> ResolveTargetAsync(string serial, string? process, string? thread, int tid,
        CancellationToken ct)
    {
        if (tid > 0)
        {
            return ($"-t {tid}", $"tid {tid}");
        }
        string pid = string.Empty;
        if (process is not null)
        {
            if (process.All(char.IsDigit))
            {
                pid = process;
            }
            else
            {
                AdbResult r = await adb.ShellAsync(serial, $"pidof {Quote(process)}", DeviceSteps.ShortCmd, ct).ConfigureAwait(false);
                pid = r.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
                if (pid.Length == 0)
                {
                    throw new ToolException($"process '{process}' is not running on {serial}");
                }
            }
            if (thread is null)
            {
                return ($"-p {pid}", $"process {process} (pid {pid})");
            }
        }
        // comm is at most 15 characters: match the given name's prefix of that length.
        string comm = thread!.Length > 15 ? thread[..15] : thread;
        string script = pid.Length > 0
            ? $"for t in /proc/{pid}/task/*; do c=$(cat $t/comm 2>/dev/null); case \"$c\" in {Quote(comm)}*) echo ${{t##*/}};; esac; done"
            : $"pgrep -x {Quote(comm)}";
        AdbResult found = await adb.ShellAsync(serial, script, DeviceSteps.ShortCmd, ct).ConfigureAwait(false);
        List<string> tids = found.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.All(char.IsDigit)).Distinct().Take(64).ToList();
        if (tids.Count == 0)
        {
            throw new ToolException(pid.Length > 0
                ? $"no thread of pid {pid} starts with '{comm}'"
                : $"no thread named exactly '{comm}' (kernel threads); pass `process` to match app / daemon threads by prefix");
        }
        return ($"-t {string.Join(',', tids)}", $"thread {comm}{(pid.Length > 0 ? $" of pid {pid}" : "")} (tids {string.Join(',', tids)})");
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Tail(string s, int max = 600) => s.Length <= max ? s.Trim() : "…" + s[^max..].Trim();

    internal sealed record ReportTable(long Samples, long EventCount, List<string> Columns, List<string[]> Rows)
    {
        public JsonObject ToJson(int limit, bool skipFullCover = false)
        {
            JsonArray rows = new();
            foreach (string[] r in Rows)
            {
                if (skipFullCover && r.Length > 0 && r[0].StartsWith("100", StringComparison.Ordinal))
                {
                    continue;
                }
                if (rows.Count >= limit)
                {
                    break;
                }
                JsonArray row = new();
                foreach (string c in r)
                {
                    row.Add(c);
                }
                rows.Add(row);
            }
            JsonArray cols = new();
            foreach (string c in Columns)
            {
                cols.Add(c);
            }
            return new JsonObject { ["columns"] = cols, ["rows"] = rows };
        }
    }

    // simpleperf report text: counts, a column header, rows split on 2+ spaces (the symbol may contain spaces).
    internal static ReportTable Parse(string text)
    {
        long samples = 0, events = 0;
        List<string> columns = new();
        List<string[]> rows = new();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (columns.Count == 0)
            {
                if (line.StartsWith("Samples:", StringComparison.Ordinal))
                {
                    long.TryParse(line[8..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out samples);
                }
                else if (line.StartsWith("Event count:", StringComparison.Ordinal))
                {
                    long.TryParse(line[12..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out events);
                }
                else if (line.StartsWith("Overhead", StringComparison.Ordinal) || line.StartsWith("Children", StringComparison.Ordinal))
                {
                    columns = Columns().Split(line.Trim()).ToList();
                }
                continue;
            }
            if (line.Trim().Length == 0)
            {
                continue;
            }
            string[] cells = Columns().Split(line.Trim(), columns.Count);
            if (cells.Length == columns.Count && cells[0].EndsWith('%'))
            {
                rows.Add(cells);
            }
        }
        return new ReportTable(samples, events, columns, rows);
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Columns();
}
