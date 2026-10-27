using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;

namespace PerfettoMcp.Tools;

internal sealed class ServerStatsTool : IToolHandler
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "server_stats",
        Description = $"""
            Performance of this server since the worker started: per-tool call counts and durations, the slowest recent
            calls, and recent trace_processor queries that ran >= {PerfLog.SlowQueryMs} ms inside trace_processor (exec_ms)
            or waited >= {PerfLog.SlowWaitMs} ms for the trace (wait_ms: another query holding it — parallel calls on one
            trace serialize — or a lazy reload). Tool durations include such waits. `clear=true` resets the record.
            """,
        InputSchema = new SchemaBuilder()
            .Int("limit", "Rows per section (default 10).")
            .Bool("clear", "Reset the record after reading it (default false).")
            .Build()
    };

    public Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        int limit = Args.OptionalInt(args, "limit", 10, 1, 200);
        (List<PerfLog.ToolCall> calls, List<PerfLog.SlowQuery> queries) = PerfLog.Snapshot();
        if (Args.OptionalBool(args, "clear", false))
        {
            PerfLog.Clear();
        }

        JsonArray perTool = new();
        foreach (IGrouping<string, PerfLog.ToolCall> g in calls.GroupBy(c => c.Tool).OrderByDescending(g => g.Sum(c => c.Ms)))
        {
            perTool.Add(new JsonArray(g.Key, g.Count(), g.Count(c => !c.Ok), Math.Round(g.Sum(c => c.Ms)), Math.Round(g.Max(c => c.Ms)),
                Math.Round(g.Average(c => c.Ms))));
        }
        JsonArray slowCalls = new();
        foreach (PerfLog.ToolCall c in calls.OrderByDescending(c => c.Ms).Take(limit))
        {
            slowCalls.Add(new JsonArray(c.Tool, Math.Round(c.Ms), c.Ok, c.Utc.ToString("HH:mm:ss")));
        }
        JsonArray slowQueries = new();
        foreach (PerfLog.SlowQuery q in queries.OrderByDescending(q => q.ExecMs).Take(limit))
        {
            slowQueries.Add(new JsonArray(q.TraceId, Math.Round(q.ExecMs), Math.Round(q.WaitMs), q.Sql, q.Utc.ToString("HH:mm:ss")));
        }
        JsonObject report = new()
        {
            ["calls_recorded"] = calls.Count,
            ["per_tool"] = new JsonObject
            {
                ["columns"] = new JsonArray("tool", "calls", "errors", "total_ms", "max_ms", "avg_ms"),
                ["rows"] = perTool
            },
            ["slowest_calls"] = new JsonObject
            {
                ["columns"] = new JsonArray("tool", "ms", "ok", "utc"),
                ["rows"] = slowCalls
            },
            ["slow_queries"] = new JsonObject
            {
                ["columns"] = new JsonArray("trace_id", "exec_ms", "wait_ms", "sql", "utc"),
                ["rows"] = slowQueries
            }
        };
        return Task.FromResult(ToolCallResult.Json(report));
    }
}
