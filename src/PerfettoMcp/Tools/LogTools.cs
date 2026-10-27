using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class AndroidLogsTool : TraceToolBase
{
    public AndroidLogsTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("android_logs",
        """
        Search logcat captured in the trace (android.log data source): filter by level, tag, message substring,
        process and window. Returns matching lines (time-ordered) plus a per-tag count of all matches. Useful for
        ART GC lines, ANR/crash messages, HAL errors aligned with the timeline.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder()))
            .Enum("min_level", "Minimum priority (default V = everything).", "V", "D", "I", "W", "E", "F")
            .Str("tag", "Tag GLOB, e.g. `ActivityManager`, `*Cam*`.")
            .Str("contains", "Case-insensitive substring of the message.")
            .Int("limit", "Max lines (default 50).")
            .Int("max_msg_len", "Truncate messages longer than this (default 300).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        int limit = Args.OptionalInt(args, "limit", 50, 1, 2000);
        int maxLen = Args.OptionalInt(args, "max_msg_len", 300, 0, 100_000);
        int minPrio = (Args.OptionalString(args, "min_level") ?? "V") switch
        {
            "D" => 3,
            "I" => 4,
            "W" => 5,
            "E" => 6,
            "F" => 7,
            _ => 0
        };
        string filter = $"l.prio >= {minPrio} AND l.ts >= {w.Start} AND l.ts < {w.End}";
        if (ProcessSelector.Predicate(args) is { } proc)
        {
            filter += $" AND {proc}";
        }
        if (Args.OptionalString(args, "tag") is { Length: > 0 } tag)
        {
            filter += $" AND l.tag GLOB {SqlText.Literal(tag)}";
        }
        if (Args.OptionalString(args, "contains") is { Length: > 0 } needle)
        {
            filter += $@" AND l.msg LIKE {SqlText.Literal("%" + SqlText.EscapeLike(needle) + "%")} ESCAPE '\'";
        }
        string from = $"android_logs l LEFT JOIN thread t USING (utid) LEFT JOIN process p USING (upid) WHERE {filter}";

        QueryTable lines = await RunAsync(s, $"""
            {w.Prelude}SELECT l.ts, SUBSTR('??VDIWEF', l.prio + 1, 1) AS level, l.tag, p.pid, t.tid, p.name AS process, l.msg
            FROM {from} ORDER BY l.ts LIMIT {limit}
            """, ctx).ConfigureAwait(false);
        JsonObject report = new()
        {
            ["window"] = w.Description,
            ["lines"] = TableFormat.ToJson(lines, limit, maxLen)
        };
        report["by_tag"] = await SectionAsync(s, $"""
            {w.Prelude}SELECT l.tag, COUNT(*) AS n, MIN(l.ts) AS first_ts FROM {from} GROUP BY l.tag ORDER BY n DESC LIMIT 15
            """, ctx, 15).ConfigureAwait(false);
        return ToolCallResult.Json(report);
    }
}

internal sealed class AndroidGcTool : TraceToolBase
{
    public AndroidGcTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("android_gc",
        """
        ART garbage collections (android.garbage_collection stdlib): per-process count, total wall / running time,
        reclaimed MB and heap range, plus the longest GCs with their Running / Runnable / D-state split.
        High GC share of wall time usually means per-frame allocation of large objects.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder()))
            .Int("limit", "Longest GCs to list (default 15).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        int limit = Args.OptionalInt(args, "limit", 15, 1, 500);
        string proc = ProcessSelector.Predicate(args) is { } pred
            ? $"g.upid IN (SELECT p.upid FROM process p WHERE {pred})"
            : "1";
        // The stdlib reads thread states of the GC slice's utid only; a GC thread split into a shadow utid keeps its
        // sched data there, so states are summed over every utid of the same thread.
        string prelude = w.Prelude + $"""
            INCLUDE PERFETTO MODULE android.garbage_collection;
            WITH g AS MATERIALIZED (
              SELECT ROW_NUMBER() OVER (ORDER BY gc_ts, utid) AS k, * FROM android_garbage_collection_events g
              WHERE {proc} AND {w.Overlaps("g.gc_ts", "g.gc_dur")}),
            sib AS (
              SELECT g.k, t2.utid FROM g JOIN thread t1 ON t1.utid = g.utid JOIN thread t2 ON t2.tid = t1.tid
              WHERE t2.utid = t1.utid OR t2.upid = t1.upid OR EXISTS (SELECT 1 FROM process ps WHERE ps.upid IN (t1.upid, t2.upid)
                AND ps.name IS NULL AND ps.cmdline IS NULL AND ps.pid = t1.tid)),
            st AS (
              SELECT k, SUM(IIF(state = 'Running', d, 0)) AS running, SUM(IIF(state IN ('R', 'R+'), d, 0)) AS runnable,
                SUM(IIF(state GLOB 'D*', d, 0)) AS d_state
              FROM (SELECT sib.k, ts.state,
                      MIN(IIF(ts.dur < 0, g.gc_ts + g.gc_dur, ts.ts + ts.dur), g.gc_ts + g.gc_dur) - MAX(ts.ts, g.gc_ts) AS d
                    FROM sib JOIN g USING (k) JOIN thread_state ts ON ts.utid = sib.utid
                      AND ts.ts < g.gc_ts + g.gc_dur AND (ts.dur < 0 OR ts.ts + ts.dur > g.gc_ts))
              GROUP BY k)

            """;
        string from = "g LEFT JOIN st USING (k)";

        JsonObject report = new() { ["window"] = w.Description };
        report["per_process"] = await SectionAsync(s, $"""
            {prelude}SELECT g.upid, g.process_name, COUNT(*) AS gcs, ROUND(SUM(g.gc_dur) / 1e6, 1) AS total_ms,
                   ROUND(SUM(IFNULL(st.running, 0)) / 1e6, 1) AS running_ms, ROUND(SUM(g.reclaimed_mb), 1) AS reclaimed_mb,
                   ROUND(MIN(g.min_heap_mb), 1) AS min_heap_mb, ROUND(MAX(g.max_heap_mb), 1) AS max_heap_mb,
                   ROUND(100.0 * SUM(g.gc_dur) / ({w.End} - {w.Start}), 1) AS pct_of_window
            FROM {from} GROUP BY g.upid ORDER BY SUM(g.gc_dur) DESC LIMIT 20
            """, ctx, 20).ConfigureAwait(false);
        report["longest"] = await SectionAsync(s, $"""
            {prelude}SELECT g.gc_ts, ROUND(g.gc_dur / 1e6, 1) AS dur_ms, g.process_name, g.thread_name, g.gc_type,
                   g.is_mark_compact, ROUND(g.reclaimed_mb, 1) AS reclaimed_mb,
                   ROUND(IFNULL(st.running, 0) / 1e6, 1) AS running_ms, ROUND(IFNULL(st.runnable, 0) / 1e6, 1) AS runnable_ms,
                   ROUND(IFNULL(st.d_state, 0) / 1e6, 1) AS d_state_ms
            FROM {from} ORDER BY g.gc_dur DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        return ToolCallResult.Json(report);
    }
}
