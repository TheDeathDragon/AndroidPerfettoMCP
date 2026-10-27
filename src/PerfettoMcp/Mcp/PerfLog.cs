using System;
using System.Collections.Generic;
using System.Linq;

namespace PerfettoMcp.Mcp;

// Recent tool calls and slow queries, reported by server_stats.
internal static class PerfLog
{
    public const double SlowQueryMs = 200;
    private const int MaxCalls = 200;
    private const int MaxQueries = 100;

    private static readonly object Sync = new();
    private static readonly Queue<ToolCall> Calls = new();
    private static readonly Queue<SlowQuery> Queries = new();

    public static void RecordCall(string tool, double ms, bool ok)
    {
        lock (Sync)
        {
            Calls.Enqueue(new ToolCall(tool, ms, ok, DateTime.UtcNow));
            while (Calls.Count > MaxCalls)
            {
                Calls.Dequeue();
            }
        }
    }

    public const double SlowWaitMs = 1000;

    // execMs: time inside trace_processor; the rest of wallMs is waiting for the trace (gate, reload) and RPC.
    public static void RecordQuery(string traceId, string sql, double wallMs, double? execMs)
    {
        double exec = execMs ?? wallMs;
        double wait = Math.Max(0, wallMs - exec);
        if (exec < SlowQueryMs && wait < SlowWaitMs)
        {
            return;
        }
        string flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        lock (Sync)
        {
            Queries.Enqueue(new SlowQuery(traceId, flat.Length <= 300 ? flat : flat[..300] + "...", exec, wait, DateTime.UtcNow));
            while (Queries.Count > MaxQueries)
            {
                Queries.Dequeue();
            }
        }
    }

    public static (List<ToolCall> Calls, List<SlowQuery> Queries) Snapshot()
    {
        lock (Sync)
        {
            return (Calls.ToList(), Queries.ToList());
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Calls.Clear();
            Queries.Clear();
        }
    }

    internal sealed record ToolCall(string Tool, double Ms, bool Ok, DateTime Utc);

    internal sealed record SlowQuery(string TraceId, string Sql, double ExecMs, double WaitMs, DateTime Utc);
}
