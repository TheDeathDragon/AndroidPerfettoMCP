using System.Linq;
using System.Text.Json.Nodes;
using PerfettoMcp.Tools;
using Xunit;

namespace PerfettoMcp.Tests;

public class CriticalPathRuleTests
{
    // by_thread columns: thread, tid, process, on_path_ms, pct_of_window, running_ms, runnable_ms, d_ms, is_root
    private static JsonObject Path(params JsonArray[] rows) => new()
    {
        ["by_thread"] = new JsonObject { ["rows"] = new JsonArray(rows) },
        ["blocker_slices"] = new JsonObject
        {
            ["rows"] = new JsonArray(new JsonArray("pool-3-thread-5", "inflate", 0, 36, 199.9))
        }
    };

    [Fact]
    public void FlagsABackgroundThreadHoldingTheLaunch()
    {
        JsonObject cp = Path(
            new JsonArray("main", 100, "app", 500.0, 50.0, 400.0, 50.0, null, 1),
            new JsonArray("pool-3-thread-5", 101, "app", 400.0, 40.0, 350.0, 40.0, null, 0));
        JsonObject f = Assert.Single(StartupsTool.Findings(cp, 1000));
        Assert.Equal("CRITICAL_PATH_BLOCKER", f["rule"]!.GetValue<string>());
        Assert.Equal("critical", f["severity"]!.GetValue<string>());
        Assert.Contains("'inflate'", f["what"]!.GetValue<string>());
        Assert.StartsWith("Move that work", f["suggestion"]!.GetValue<string>());
    }

    [Fact]
    public void StarvedBlockerGetsAPrioritySuggestionAndVsyncIsInfo()
    {
        JsonObject cp = Path(
            new JsonArray("main", 100, "app", 300.0, 30.0, 200.0, 50.0, null, 1),
            new JsonArray("binder:1_2", 7, "system_server", 200.0, 20.0, 50.0, 140.0, null, 0),
            new JsonArray("TimerDispatch", 9, "/system/bin/surfaceflinger", 250.0, 25.0, 3.0, 1.0, null, 0),
            new JsonArray("small", 11, "app", 30.0, 3.0, 30.0, 0.0, null, 0));
        JsonObject[] fs = StartupsTool.Findings(cp, 1000).ToArray();
        Assert.Equal(2, fs.Length);
        JsonObject blocker = fs.Single(f => f["rule"]!.GetValue<string>() == "CRITICAL_PATH_BLOCKER");
        Assert.Equal("warning", blocker["severity"]!.GetValue<string>());
        Assert.StartsWith("The blocking thread was itself starved", blocker["suggestion"]!.GetValue<string>());
        Assert.Equal("info", fs.Single(f => f["rule"]!.GetValue<string>() == "VSYNC_WAIT_ON_PATH")["severity"]!.GetValue<string>());
    }

    private static class StartupsTool
    {
        public static System.Collections.Generic.IEnumerable<JsonObject> Findings(JsonObject cp, double ms) =>
            AndroidStartupsTool.CriticalPathFindings(cp, ms);
    }
}
