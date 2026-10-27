using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;
using PerfettoMcp.Tools;
using Xunit;

namespace PerfettoMcp.Tests;

// Regression tests for issues found in the 2026-10-23 code review (docs/04-code-review.md).
public class ReviewRegressionTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "basic.perfetto-trace");

    [Fact]
    public void LogMirrorRotatesWhileRunning()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pmcp_log_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "supervisor.log");
        try
        {
            PerfettoMcp.Logging.Log.MirrorTo(path, maxBytes: 2000);
            for (int i = 0; i < 100; i++)
            {
                PerfettoMcp.Logging.Log.Raw($"line {i} " + new string('x', 40));
            }
            PerfettoMcp.Logging.Log.StopMirror();
            Assert.True(File.Exists(path + ".1"));
            Assert.True(new FileInfo(path).Length <= 2100);
        }
        finally
        {
            PerfettoMcp.Logging.Log.StopMirror();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("7")]
    [InlineData("\"req-1\"")]
    public void SupervisorErrorResponseIsValidJsonRpc(string id)
    {
        // Built as text (no JsonValue<T>): the message needs escaping and the id must round-trip unchanged.
        string line = PerfettoMcp.Supervisor.Supervisor.ErrorResponse(JsonNode.Parse(id)!, "worker \"t1\" gone\nretry");
        JsonObject msg = JsonNode.Parse(line)!.AsObject();
        Assert.Equal(id, msg["id"]!.ToJsonString());
        Assert.Equal(-32603, msg["error"]!["code"]!.GetValue<int>());
        Assert.Equal("worker \"t1\" gone\nretry", msg["error"]!["message"]!.GetValue<string>());
        Assert.False(PerfettoMcp.Supervisor.Supervisor.IsInternal(msg["id"]!));
        Assert.True(PerfettoMcp.Supervisor.Supervisor.IsInternal(JsonNode.Parse("\"__supervisor_3\"")!));
    }

    [Fact]
    public async Task DisposedSessionRejectsUseAndDoubleDisposeIsFast()
    {
        TraceManager m = new(new Settings());
        TraceSession s = await m.LoadAsync(Fixture, RequestContext.None);
        m.Dispose();
        Assert.False(s.IsLive);

        Stopwatch sw = Stopwatch.StartNew();
        m.Dispose();
        s.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));

        ToolException ex = await Assert.ThrowsAsync<ToolException>(() => m.QueryAsync(s, "SELECT 1", RequestContext.None));
        Assert.Contains("unloaded", ex.Message);
    }

    [Fact]
    public async Task UnloadThenReloadGetsFreshSession()
    {
        using TraceManager m = new(new Settings());
        TraceSession a = await m.LoadAsync(Fixture, RequestContext.None);
        Assert.True(m.Unload(a.Id));
        TraceSession b = await m.LoadAsync(Fixture, RequestContext.None);
        Assert.NotSame(a, b);
        QueryTable t = await m.QueryAsync(b, "SELECT 1 AS v", RequestContext.None);
        Assert.Equal(1L, t.Rows[0][0]);
    }

    [Fact]
    public async Task OversizedResultIsAbortedAndProcessorRestarted()
    {
        using TraceManager m = new(new Settings { MaxResultBytes = 4096 });
        TraceSession s = await m.LoadAsync(Fixture, RequestContext.None);
        const string Big = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 100000) SELECT x, 'padding-padding' FROM c";
        ToolException ex = await Assert.ThrowsAsync<ToolException>(() => m.QueryAsync(s, Big, RequestContext.None));
        Assert.Contains("exceeded", ex.Message);
        Assert.False(s.IsLive);
        QueryTable t = await m.QueryAsync(s, "SELECT 7 AS v", RequestContext.None);
        Assert.Equal(7L, t.Rows[0][0]);
    }

    [Fact]
    public async Task SliceHotspotsReportsTheLongestInstance()
    {
        using TraceManager m = new(new Settings());
        await m.LoadAsync(Fixture, RequestContext.None);
        SliceHotspotsTool tool = new(m);
        ToolCallResult r = await tool.InvokeAsync(new JsonObject { ["limit"] = 50 }, RequestContext.None);
        Assert.False(r.IsError, r.Content[0].Text);
        JsonObject o = JsonNode.Parse(r.Content[0].Text!)!.AsObject();
        JsonArray cols = o["columns"]!.AsArray();
        int maxMs = IndexOf(cols, "max_ms");
        int id = IndexOf(cols, "max_slice_id");
        TraceSession s = m.Resolve(null);
        foreach (JsonNode? row in o["rows"]!.AsArray())
        {
            long sliceId = row![id]!.GetValue<long>();
            QueryTable t = await m.QueryAsync(s, $"SELECT ROUND(dur / 1e6, 2) FROM slice WHERE id = {sliceId}", RequestContext.None);
            Assert.Equal(row[maxMs]!.GetValue<double>(), Convert.ToDouble(t.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task CpuProfileExplainsMissingSamples()
    {
        using TraceManager m = new(new Settings());
        await m.LoadAsync(Fixture, RequestContext.None);
        ToolCallResult r = await new CpuProfileTool(m).InvokeAsync(new JsonObject(), RequestContext.None);
        Assert.False(r.IsError, r.Content[0].Text);
        Assert.Contains("linux.perf", r.Content[0].Text);
    }

    [Fact]
    public async Task MissingColumnErrorListsRealColumns()
    {
        using TraceManager m = new(new Settings());
        await m.LoadAsync(Fixture, RequestContext.None);
        ToolCallResult r = await new ExecuteSqlTool(m).InvokeAsync(
            new JsonObject { ["sql"] = "SELECT duration FROM slice" }, RequestContext.None).ContinueWith(t =>
            t.IsFaulted ? ToolCallResult.Error(t.Exception!.InnerException!.Message) : t.Result);
        string text = r.Content[0].Text!;
        Assert.True(r.IsError);
        Assert.Contains("Columns of slice:", text);
        Assert.Contains("closest to 'duration': dur", text);
    }

    [Fact]
    public async Task SummarizeReturnsStatsAndLargestSamples()
    {
        using TraceManager m = new(new Settings());
        await m.LoadAsync(Fixture, RequestContext.None);
        ToolCallResult r = await new ExecuteSqlTool(m).InvokeAsync(new JsonObject
        {
            ["sql"] = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 1000) SELECT x, x % 3 AS k, 'v' || (x % 2) AS s FROM c",
            ["summarize"] = true,
            ["sort_by"] = "x",
            ["sample_rows"] = 3
        }, RequestContext.None);
        JsonObject o = JsonNode.Parse(r.Content[0].Text!)!.AsObject();
        Assert.Equal(1000, o["row_count"]!.GetValue<int>());
        Assert.Equal(3, o["rows"]!.AsArray().Count);
        Assert.Equal(1000, o["rows"]![0]![0]!.GetValue<int>());
        Assert.Equal(500.5, o["column_stats"]!["x"]!["avg"]!.GetValue<double>());
        Assert.Equal(2, o["column_stats"]!["s"]!["distinct"]!.GetValue<int>());

        ToolCallResult cut = await new ExecuteSqlTool(m).InvokeAsync(new JsonObject
        {
            ["sql"] = "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 300) SELECT x FROM c",
            ["max_rows"] = 5
        }, RequestContext.None);
        JsonObject oc = JsonNode.Parse(cut.Content[0].Text!)!.AsObject();
        Assert.Equal(5, oc["rows"]!.AsArray().Count);
        Assert.Equal(300, oc["column_stats"]!["x"]!["max"]!.GetValue<int>());
    }

    [Fact]
    public void LargeIntegersAreEmittedAsStrings()
    {
        Assert.Equal("1700000000000000000", TableFormat.Cell(1700000000000000000L)!.GetValue<string>());
        Assert.Equal(162712013142197L, TableFormat.Cell(162712013142197L)!.GetValue<long>());
    }

    [Fact]
    public void EscapesLikeWildcards() =>
        Assert.Equal(@"a\%b\_c\\", SqlText.EscapeLike(@"a%b_c\"));

    [Fact]
    public async Task ParseErrorResponseCarriesNullId()
    {
        McpServer server = new(new ToolRegistry());
        string? resp = await server.HandleAsync("{not json", _ => Task.CompletedTask);
        Assert.Contains("\"id\":null", resp);
    }

    private static int IndexOf(JsonArray cols, string name)
    {
        for (int i = 0; i < cols.Count; i++)
        {
            if (cols[i]!.GetValue<string>() == name)
            {
                return i;
            }
        }
        throw new InvalidOperationException($"column {name} missing");
    }
}
