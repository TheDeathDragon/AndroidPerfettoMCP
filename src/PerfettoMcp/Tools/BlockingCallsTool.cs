using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class MainThreadBlockingCallsTool : TraceToolBase
{
    public MainThreadBlockingCallsTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("main_thread_blocking_calls",
        """
        Known blocking calls on main threads (every thread with all_threads=true) from the stdlib's curated list
        (android.critical_blocking_calls: binder transactions, monitor / mutex / thread-list lock contention, inflate,
        measure, traversal, DrawFrames,
        queueBuffer, AIDL handlers, Handler messages ...). Without a process filter: which processes' main threads
        were blocked most; with one: that main thread's blocking calls by type (digits normalized), with count,
        total / max and the ts of the longest instance. Note: the stdlib table is internal and may change.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder()))
            .Bool("all_threads", "Include every thread of the process, not only the main thread (default false).")
            .Int("limit", "Rows (default 20).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        int limit = Args.OptionalInt(args, "limit", 20, 1, 500);
        string? proc = ProcessSelector.Predicate(args);
        string filter = $"{w.Overlaps("c.ts", "c.dur")}" + (proc is null ? string.Empty : $" AND c.upid IN (SELECT p.upid FROM process p WHERE {proc})");
        // The stdlib table covers every thread of a process; keep the main thread unless asked otherwise.
        if (!Args.OptionalBool(args, "all_threads", false))
        {
            // Named processes only: a shadow process (pid == tid) would make a split-off worker thread look like a main thread.
            filter += " AND c.utid IN (SELECT t.utid FROM thread t JOIN process p USING (upid) WHERE t.tid = p.pid "
                + "AND (p.name IS NOT NULL OR p.cmdline IS NOT NULL))";
        }
        string prelude = w.Prelude + "INCLUDE PERFETTO MODULE android.critical_blocking_calls;\n";
        string clipped = w.Clipped("c.ts", "c.dur");

        JsonObject report = new() { ["window"] = w.Description };
        if (proc is null)
        {
            report["by_process"] = await SectionAsync(s, $"""
                {prelude}SELECT c.upid, c.process_name, COUNT(*) AS calls, ROUND(SUM({clipped}) / 1e6, 1) AS total_ms,
                       ROUND(MAX(c.dur) / 1e6, 1) AS max_ms
                FROM _android_critical_blocking_calls c WHERE {filter}
                GROUP BY c.upid ORDER BY SUM({clipped}) DESC LIMIT {limit}
                """, ctx, limit).ConfigureAwait(false);
        }
        report["by_call"] = await SectionAsync(s, $"""
            {prelude}WITH c AS (
              SELECT c.*, REGEXP_REPLACE_SIMPLE(c.name, '[0-9]+', 'N') AS kind FROM _android_critical_blocking_calls c WHERE {filter}),
            r AS (SELECT kind, ts, dur, ROW_NUMBER() OVER (PARTITION BY kind ORDER BY dur DESC) AS rn FROM c)
            SELECT c.kind AS call, COUNT(*) AS n, ROUND(SUM({clipped}) / 1e6, 1) AS total_ms, ROUND(MAX(c.dur) / 1e6, 1) AS max_ms,
                   (SELECT r.ts FROM r WHERE r.kind = c.kind AND r.rn = 1) AS max_ts
            FROM c GROUP BY c.kind ORDER BY SUM({clipped}) DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        return ToolCallResult.Json(report);
    }
}
