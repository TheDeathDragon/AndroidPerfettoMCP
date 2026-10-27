using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;
using PerfettoMcp.Tools;
using Xunit;

namespace PerfettoMcp.Tests;

// User builds deliver no logcat in the trace; load_trace / record_trace merge an `adb logcat` text file instead.
public class TraceBundleTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "basic.perfetto-trace");

    [Fact]
    public async Task LoadTraceMergesALogcatTextFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pmcp_bundle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string trace = Path.Combine(dir, "t.perfetto-trace");
            File.Copy(Fixture, trace);
            string logcat = Path.Combine(dir, "t.logcat.txt");
            File.WriteAllText(logcat,
                "--------- beginning of main\n"
                + "2026-10-26 15:04:33.306   429   435 E android.system.suspend-service: Error opening kernel wakelock stats\n"
                + "2026-10-26 15:04:33.410  1149  1149 I ActivityTaskManager: START u0 {cmp=com.android.settings/.Settings}\n");

            using TraceManager m = new(new Settings());
            ToolCallResult r = await new LoadTraceTool(m).InvokeAsync(
                new JsonObject { ["path"] = trace, ["logcat"] = logcat, ["overview"] = false }, RequestContext.None);
            Assert.False(r.IsError, r.Content[0].Text);
            JsonObject o = JsonNode.Parse(r.Content[0].Text!)!.AsObject();
            Assert.EndsWith("t+logcat.zip", o["path"]!.GetValue<string>(), StringComparison.Ordinal);
            QueryTable q = await m.QueryAsync(m.Resolve(null), "SELECT COUNT(*) FROM android_logs", RequestContext.None);
            Assert.Equal(2L, q.Rows[0][0]);
            // Unchanged inputs reuse the bundle; a different logcat file rebuilds it.
            string zip = o["path"]!.GetValue<string>();
            DateTime built = File.GetLastWriteTimeUtc(zip);
            Assert.Equal(zip, TraceBundle.WithLogcat(trace, logcat));
            Assert.Equal(built, File.GetLastWriteTimeUtc(zip));
            string other = Path.Combine(dir, "other.logcat.txt");
            File.WriteAllLines(other, ["2026-10-26 15:04:34.000  1149  1149 I Tag: one line"]);
            File.SetLastWriteTimeUtc(other, built.AddHours(-1));
            TraceBundle.WithLogcat(trace, other);
            using (System.IO.Compression.ZipArchive a = System.IO.Compression.ZipFile.OpenRead(zip))
            {
                Assert.NotNull(a.GetEntry("other.logcat.txt"));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
