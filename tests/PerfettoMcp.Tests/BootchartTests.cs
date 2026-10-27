using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Boot;
using PerfettoMcp.Mcp;
using PerfettoMcp.Tools;
using Xunit;

namespace PerfettoMcp.Tests;

public class BootchartTests
{
    private const string Header = "version = Android init 0.8\nsystem.release = test/build\n"
        + "system.kernel.options = console=ttyS1 tspl=400ms tsml=50ms tuboot=3500ms\n";

    private static string Stat(int jiffies, long user, long idle, int running) =>
        $"{jiffies}\ncpu  {user} 0 0 {idle} 0 0 0 0 0 0\nprocs_running {running}\nprocs_blocked 0\n\n";

    // pid (comm) state ppid ... utime(14) stime(15) ... num_threads(20) ... starttime(22)
    private static string Proc(int pid, string comm, long utime, long start, int threads = 1) =>
        $"{pid} ({comm}) S 1 0 0 0 -1 0 0 0 0 0 {utime} 0 0 0 20 0 {threads} 0 {start} 0 0\n";

    private static string MakeDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm-bootchart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "header"), Header);
        File.WriteAllText(Path.Combine(dir, "proc_stat.log"),
            Stat(500, 100, 300, 2) + Stat(600, 180, 320, 9) + Stat(700, 270, 330, 12) + Stat(800, 300, 400, 3));
        File.WriteAllText(Path.Combine(dir, "proc_ps.log"),
            "500\n" + Proc(1, "init second_stage", 50, 0) + Proc(300, "zygote64", 10, 450) + "\n"
            + "600\n" + Proc(1, "init second_stage", 60, 0) + Proc(300, "zygote64", 20, 450) + Proc(400, "vendor.audio.hal --flag", 80, 520, 12) + "\n"
            + "800\n" + Proc(1, "init second_stage", 70, 0) + Proc(300, "zygote64", 40, 450) + Proc(400, "vendor.audio.hal --flag", 150, 520, 20)
            + Proc(500, "system_server", 5, 790) + "\n");
        return dir;
    }

    [Fact]
    public async Task ParsesDirectoryAndExplainsPhaseGap()
    {
        string dir = MakeDir();
        try
        {
            Bootchart b = Bootchart.Load(dir);
            Assert.Equal(4, b.Stat.Count);
            Assert.Equal(3500, b.BootloaderMs()["tuboot"]);
            Assert.Equal("vendor.audio.hal", b.Ps[1].Procs[400].Name); // cmdline arguments stripped

            ToolCallResult r = await new AnalyzeBootchartTool().InvokeAsync(
                new JsonObject { ["path"] = dir, ["focus_process"] = "audio" }, RequestContext.None);
            Assert.False(r.IsError, r.Content[0].Text);
            JsonObject o = JsonNode.Parse(r.Content[0].Text!)!.AsObject();
            JsonNode gap = o["phase_gaps"]!.AsArray().First(g => g!["from"]!.GetValue<string>() == "zygote")!;
            Assert.Equal(3.4, gap["gap_s"]!.GetValue<double>());
            Assert.Equal("vendor.audio.hal", gap["top_cpu_in_gap"]!["rows"]![0]![0]!.GetValue<string>());
            Assert.Contains(o["findings"]!.AsArray(), f => f!["rule"]?.GetValue<string>() == "BOOTLOADER_SLOW");
            Assert.Equal(2, o["focus"]!["rows"]!.AsArray().Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReadsTgz()
    {
        string dir = MakeDir();
        string tgz = dir + ".tgz";
        try
        {
            using (FileStream fs = File.Create(tgz))
            using (GZipStream gz = new(fs, CompressionLevel.Fastest))
            using (TarWriter tw = new(gz))
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    tw.WriteEntry(f, Path.GetFileName(f));
                }
            }
            Bootchart b = Bootchart.Load(tgz);
            Assert.Equal(3, b.Ps.Count);
            Assert.Equal("test/build", b.Header["system.release"]);
        }
        finally
        {
            Directory.Delete(dir, true);
            File.Delete(tgz);
        }
    }
}
