using PerfettoMcp.Device;
using PerfettoMcp.Tools;
using Xunit;

namespace PerfettoMcp.Tests;

public class RecordTraceTests
{
    [Fact]
    public void BuiltInScenarioConfigIsEmbedded()
    {
        string cfg = RecordTraceTool.LoadConfig("scenario");
        Assert.Contains("linux.ftrace", cfg);
        Assert.Contains("RING_BUFFER", cfg);
        Assert.Equal(30000, RecordTraceTool.DurationMs(cfg));
    }

    [Fact]
    public void BuiltInBootConfigIsEmbedded()
    {
        string cfg = RecordTraceTool.LoadConfig("boot");
        Assert.Contains("RING_BUFFER", cfg);
        Assert.DoesNotContain("DISCARD", cfg);
        Assert.Equal(45000, RecordTraceTool.DurationMs(RecordTraceTool.WithDuration(cfg, 45000)));
    }

    [Fact]
    public void BuiltInHeapConfigHasAProcessPlaceholder()
    {
        string cfg = RecordTraceTool.LoadConfig("heap");
        Assert.Contains("android.java_hprof", cfg);
        Assert.Contains("\"PROCESS\"", cfg);
    }

    [Fact]
    public void IdleConfigKeepsItsOwnOverheadLow()
    {
        // Process list once (no 1 s poll), no atrace; logcat in its own buffer.
        string cfg = RecordTraceTool.LoadConfig("idle");
        Assert.Contains("scan_all_processes_on_start: true", cfg);
        Assert.DoesNotContain("proc_stats_poll_ms", cfg);
        Assert.DoesNotContain("atrace_categories", cfg);
        Assert.Contains("target_buffer: 2", cfg);
        Assert.Equal(30000, RecordTraceTool.DurationMs(cfg));
    }

    [Fact]
    public void DurationOverrideTouchesOnlyTheTopLevelField()
    {
        string cfg = "buffers {\n  size_kb: 1024\n}\ndata_sources {\n  config {\n    duration_ms: 5\n  }\n}\nduration_ms: 30000\n";
        string changed = RecordTraceTool.WithDuration(cfg, 12000);
        Assert.Equal(12000, RecordTraceTool.DurationMs(changed));
        Assert.Contains("    duration_ms: 5", changed);
        Assert.Equal(7000, RecordTraceTool.DurationMs(RecordTraceTool.WithDuration("buffers {\n}\n", 7000)));
    }

    [Fact]
    public async System.Threading.Tasks.Task MissingAdbIsAToolError()
    {
        Adb adb = new("/nonexistent/adb-" + System.Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<PerfettoMcp.Mcp.ToolException>(() =>
            adb.RunAsync(["devices"], System.TimeSpan.FromSeconds(5), System.Threading.CancellationToken.None));
    }

    [Fact]
    public void ParsesConnectedDevicesOnly()
    {
        const string output = "List of devices attached\n0123456789ABCDEF\tdevice\nemulator-5554\toffline\nABC\tunauthorized\n10.0.0.2:5555\tdevice\n\n";
        Assert.Equal(new[] { "0123456789ABCDEF", "10.0.0.2:5555" }, Adb.ParseDevices(output));
    }
}
