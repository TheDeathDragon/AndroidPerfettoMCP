using System;
using System.IO;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;
using Xunit;

namespace PerfettoMcp.Tests;

// Runs against the bundled trace_processor_shell copied next to the test assembly.
public class TraceProcessorIntegrationTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "basic.perfetto-trace");

    [Fact]
    public async Task LoadsAndQueriesFixture()
    {
        using TraceManager m = new(new Settings());
        TraceSession s = await m.LoadAsync(Fixture, RequestContext.None);
        QueryTable t = await m.QueryAsync(s, "SELECT COUNT(*) AS n FROM slice", RequestContext.None);
        Assert.Null(t.Error);
        Assert.Equal(["n"], t.Columns);
        Assert.True((long)t.Rows[0][0]! >= 0);
    }

    [Fact]
    public async Task ReusesSessionForSamePath()
    {
        using TraceManager m = new(new Settings());
        TraceSession a = await m.LoadAsync(Fixture, RequestContext.None);
        TraceSession b = await m.LoadAsync(Fixture, RequestContext.None);
        Assert.Same(a, b);
        Assert.Equal(1, a.LoadCount);
    }

    [Fact]
    public async Task TimeoutKillsProcessorAndNextQueryReloads()
    {
        using TraceManager m = new(new Settings());
        TraceSession s = await m.LoadAsync(Fixture, RequestContext.None);
        // Recursive CTE that never finishes on its own.
        const string Endless = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT COUNT(*) FROM c";
        ToolException ex = await Assert.ThrowsAsync<ToolException>(
            () => m.QueryAsync(s, Endless, RequestContext.None, TimeSpan.FromSeconds(1)));
        Assert.Contains("timeout", ex.Message);
        Assert.False(s.IsLive);

        QueryTable t = await m.QueryAsync(s, "SELECT 42 AS v", RequestContext.None);
        Assert.Equal(42L, t.Rows[0][0]);
        Assert.Equal(2, s.LoadCount);
    }

    [Fact]
    public async Task ReportsSqlErrorsWithoutRestarting()
    {
        using TraceManager m = new(new Settings());
        TraceSession s = await m.LoadAsync(Fixture, RequestContext.None);
        QueryTable t = await m.QueryAsync(s, "SELECT nope FROM slice", RequestContext.None);
        Assert.Contains("no such column", t.Error);
        Assert.True(s.IsLive);
        Assert.Equal(1, s.LoadCount);
    }

    [Fact]
    public async Task EvictsLeastRecentlyUsedProcessor()
    {
        string copy = Path.Combine(Path.GetTempPath(), $"pm-evict-{Guid.NewGuid():N}.perfetto-trace");
        File.Copy(Fixture, copy);
        try
        {
            using TraceManager m = new(new Settings { MaxLiveProcessors = 1 });
            TraceSession a = await m.LoadAsync(Fixture, RequestContext.None);
            TraceSession b = await m.LoadAsync(copy, RequestContext.None);
            Assert.False(a.IsLive);
            Assert.True(b.IsLive);
            QueryTable t = await m.QueryAsync(a, "SELECT 1", RequestContext.None);
            Assert.Null(t.Error);
            Assert.True(a.IsLive);
            Assert.False(b.IsLive);
        }
        finally
        {
            File.Delete(copy);
        }
    }
}
