using System.Text.Json.Nodes;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;
using PerfettoMcp.Tools;
using Xunit;

namespace PerfettoMcp.Tests;

public class ToolHelperTests
{
    [Theory]
    [InlineData("no such table: foo", "MissingTable")]
    [InlineData("Module not found: chrome.x", "MissingModule")]
    [InlineData("INCLUDE: unknown module 'a.b'", "MissingModule")]
    [InlineData("no such column: navigation_id", "MissingColumn")]
    [InlineData("ambiguous column name: depth", "AmbiguousColumn")]
    [InlineData("no such function: foo", "MissingFunction")]
    [InlineData("No Such Table: foo", "Other")]
    public void ClassifiesErrors(string message, string kind) =>
        Assert.Equal(kind, QueryErrors.Classify(message).ToString());

    [Fact]
    public void IncludedModuleHintPointsAtTableFunctions()
    {
        string msg = QueryErrors.Format("no such table: android_heap_graph_class_find_descendants",
            "INCLUDE PERFETTO MODULE android.memory.heap_graph.class_relationship; SELECT * FROM android_heap_graph_class_find_descendants");
        Assert.Contains("already included", msg);
        Assert.DoesNotContain("prepend", msg);
    }

    [Fact]
    public void SuggestsStdlibModuleForMissingTable()
    {
        string msg = QueryErrors.Format("no such table: android_binder_txns");
        Assert.Contains("INCLUDE PERFETTO MODULE android.binder;", msg);
    }

    [Fact]
    public void StdlibIndexIsEmbedded()
    {
        Assert.True(StdlibIndex.All.Count > 100);
        Assert.Equal("android.startup.startups", StdlibIndex.ModuleFor("android_startups"));
        Assert.Equal("slices.with_context", StdlibIndex.ModuleFor("thread_slice"));
    }

    [Fact]
    public void AcceptsNumbersAsStrings()
    {
        JsonObject a = new() { ["limit"] = "15", ["upid"] = 7, ["f"] = "2.5", ["b"] = "true" };
        Assert.Equal(15, Args.OptionalInt(a, "limit", 1, 1, 100));
        Assert.Equal(7L, Args.OptionalLong(a, "upid"));
        Assert.Equal(2.5, Args.OptionalDouble(a, "f"));
        Assert.True(Args.OptionalBool(a, "b", false));
        Assert.Throws<McpInvalidParamsException>(() => Args.OptionalInt(a, "limit", 1, 1, 10));
    }

    [Fact]
    public void RejectsUnknownParameters()
    {
        ToolDescriptor d = new() { Name = "x", InputSchema = new SchemaBuilder().Str("sql", "s").Build() };
        Args.RejectUnknown(new JsonObject { ["sql"] = "x" }, d);
        McpInvalidParamsException ex = Assert.Throws<McpInvalidParamsException>(
            () => Args.RejectUnknown(new JsonObject { ["sql"] = "x", ["threshold_ms"] = 1 }, d));
        Assert.Contains("threshold_ms", ex.Message);
    }

    [Fact]
    public void QuotesSqlLiterals()
    {
        Assert.Equal("'it''s'", SqlText.Literal("it's"));
        Assert.Throws<McpInvalidParamsException>(() => SqlText.Identifier("a; DROP", "p"));
    }

    [Fact]
    public void ThreadSelectorDefaultsToMainThread()
    {
        Assert.Equal("(p.name = 'com.a' OR p.cmdline = 'com.a') AND t.tid = p.pid",
            ThreadSelector.Predicate(new JsonObject { ["process_name"] = "com.a" }));
        // utid wins over the process filter and also covers the thread's split-off (shadow) utids.
        string byUtid = ThreadSelector.Predicate(new JsonObject { ["utid"] = 3, ["process_name"] = "x" });
        Assert.StartsWith("(t.utid = 3 OR t.utid IN (", byUtid);
        Assert.DoesNotContain("'x'", byUtid);
    }
}

public class CompareVerdictTests
{
    [Theory]
    [InlineData("binder:1355_1E / system_server", "binder:N_N / system_server")]
    [InlineData("binder:29762_A", "binder:N_N")]
    [InlineData("Choreographer#doFrame 3153054", "Choreographer#doFrame N")]
    [InlineData("Profile Saver", "Profile Saver")]
    public void NormalizesRunSpecificNames(string name, string expected)
    {
        Assert.Equal(expected, PerfettoMcp.Tools.CompareTracesTool.NormalizeName(name));
    }

    [Theory]
    [InlineData("startup.dur_ms", -23.95, -4.47, "same")]
    [InlineData("startup.dur_ms", -40, -8, "improved")]
    [InlineData("app.cpu_ms", 3, 10, "same")]
    [InlineData("app.sync_binder_calls", 1, 0.47, "same")]
    [InlineData("app.sync_binder_calls", 20, 10, "regressed")]
    [InlineData("frames.jank_pct", 1.5, 2, "regressed")]
    [InlineData("system.mem_available_min_mb", 80, 6, "improved")]
    [InlineData("main.Sleeping_ms", 30, 20, null)]
    [InlineData("main.Sleeping_ms", -117, -38, null)]
    [InlineData("app.threads_on_cpu", 5, 50, null)]
    public void ClassifiesChanges(string key, double delta, double pct, string? expected) =>
        Assert.Equal(expected, PerfettoMcp.Tools.CompareTracesTool.Verdict(key, delta, pct));

    [Theory]
    // process_cpu_pct (% of one CPU): servicemanager 0.75 -> 0.03 with NFC retries gone is a real change in idle.
    [InlineData("/system/bin/servicemanager", -0.72, -96.1, "improved")]
    [InlineData("kworker/uN:N", 0.05, 30, "same")]
    [InlineData("system_server", 1.74, 2574, "regressed")]
    public void ProcessCpuShareUsesAFinerFloor(string key, double delta, double pct, string expected) =>
        Assert.Equal(expected, PerfettoMcp.Tools.CompareTracesTool.Verdict(key, delta, pct, 0.1));
}

public class DemangleTests
{
    [Theory]
    [InlineData("_ZN8Glesbase22updateFusionParametersEPdPfS0_", "Glesbase::updateFusionParameters")]
    [InlineData("_ZL15__pthread_startPv", "__pthread_start")]
    [InlineData("_ZN3art9ArtMethod6InvokeEPNS_6ThreadEPjjPNS_6JValueEPKc", "art::ArtMethod::Invoke")]
    [InlineData("_ZNK3art6Thread4DumpERNSt3__113basic_ostreamIcNS1_11char_traitsIcEEEEb", "art::Thread::Dump")]
    [InlineData("_ZNSt6vectorC2Ev", "std::vector::vector")]
    [InlineData("_ZN3FooD1Ev", "Foo::~Foo")]
    [InlineData("_ZN3FooIiE3barEv", "_ZN3FooIiE3barEv")]
    [InlineData("__memcpy_aarch64_simd", "__memcpy_aarch64_simd")]
    [InlineData("com.example.Foo.bar", "com.example.Foo.bar")]
    [InlineData("_Z99x", "_Z99x")]
    public void SimplifiesQualifiedNames(string mangled, string expected) =>
        Assert.Equal(expected, PerfettoMcp.Tools.Demangle.Simplify(mangled));
}
