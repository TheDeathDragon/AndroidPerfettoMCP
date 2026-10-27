using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class MemorySummaryTool : TraceToolBase
{
    public MemorySummaryTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("memory_summary",
        """
        System memory pressure in a window: meminfo (MemAvailable / MemFree / Cached / SwapFree, MB at start/end and
        min/max), vmstat deltas (major faults, swap in/out, direct scan/steal) with per-second rates, LMK kills,
        direct-reclaim stalls per thread, kswapd CPU, and the processes with the highest peak RSS and the largest
        anon+swap growth. Needs linux.sys_stats (meminfo/vmstat), linux.process_stats or kmem/rss_stat, and
        vmscan/lowmemorykiller ftrace for the respective sections.
        """,
        TimeWindow.AddParams(new SchemaBuilder())
            .Int("limit", "Rows per process section (default 10).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        int limit = Args.OptionalInt(args, "limit", 10, 1, 200);
        string seconds = $"(({w.End} - {w.Start}) / 1e9)";

        JsonObject report = new() { ["window"] = w.Description };
        report["system"] = await SectionAsync(s, $"""
            {w.Prelude}WITH tr AS (SELECT id, name, type FROM track WHERE type IN ('meminfo', 'vmstat')),
            v AS (
              SELECT tr.name, tr.type,
                COALESCE((SELECT c.value FROM counter c WHERE c.track_id = tr.id AND c.ts <= {w.Start} ORDER BY c.ts DESC LIMIT 1),
                         (SELECT c.value FROM counter c WHERE c.track_id = tr.id ORDER BY c.ts LIMIT 1)) AS v_start,
                (SELECT c.value FROM counter c WHERE c.track_id = tr.id AND c.ts <= {w.End} ORDER BY c.ts DESC LIMIT 1) AS v_end,
                (SELECT MIN(c.value) FROM counter c WHERE c.track_id = tr.id AND c.ts BETWEEN {w.Start} AND {w.End}) AS v_min,
                (SELECT MAX(c.value) FROM counter c WHERE c.track_id = tr.id AND c.ts BETWEEN {w.Start} AND {w.End}) AS v_max
              FROM tr)
            SELECT name, IIF(type = 'meminfo', 'MB', 'count') AS unit,
                   IIF(type = 'meminfo', ROUND(v_start / 1048576.0, 1), v_start) AS at_start,
                   IIF(type = 'meminfo', ROUND(v_end / 1048576.0, 1), v_end) AS at_end,
                   IIF(type = 'meminfo', ROUND((v_end - v_start) / 1048576.0, 1), v_end - v_start) AS delta,
                   IIF(type = 'meminfo', ROUND(v_min / 1048576.0, 1), NULL) AS min,
                   IIF(type = 'meminfo', ROUND(v_max / 1048576.0, 1), NULL) AS max,
                   IIF(type = 'vmstat', ROUND((v_end - v_start) / {seconds}, 1), NULL) AS per_s
            FROM v ORDER BY type, name
            """, ctx, 100).ConfigureAwait(false);

        report["lmk_kills"] = await SectionAsync(s, $"""
            {w.Prelude}INCLUDE PERFETTO MODULE android.memory.lmk;
            SELECT ts, process_name, pid, oom_score_adj, kill_reason FROM android_lmk_events
            WHERE ts BETWEEN {w.Start} AND {w.End} ORDER BY ts
            """, ctx, 50).ConfigureAwait(false);

        report["direct_reclaim"] = await SectionAsync(s, $"""
            {w.Prelude}INCLUDE PERFETTO MODULE slices.with_context;
            SELECT process_name, thread_name, tid, COUNT(*) AS n, ROUND(SUM(dur) / 1e6, 2) AS total_ms,
                   ROUND(MAX(dur) / 1e6, 2) AS max_ms, MIN(ts) AS first_ts
            FROM thread_slice WHERE name = 'mm_vmscan_direct_reclaim' AND {w.Overlaps("ts", "dur")}
            GROUP BY utid ORDER BY SUM(dur) DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);

        report["kswapd"] = await SectionAsync(s, $"""
            {w.Prelude}SELECT t.name AS thread, COUNT(*) AS sched_slices, ROUND(SUM({w.Clipped("x.ts", "x.dur")}) / 1e6, 1) AS cpu_ms
            FROM sched x JOIN thread t USING (utid)
            WHERE t.name GLOB 'kswapd*' AND {w.Overlaps("x.ts", "x.dur")}
            GROUP BY t.utid ORDER BY cpu_ms DESC
            """, ctx, 16).ConfigureAwait(false);

        // Reads the mem.* counter tracks directly: the stdlib per-process view span-joins every process on first use.
        QueryTable procs = await RunAsync(s, $"""
            {w.Prelude}WITH {MemCountersCte("1", w, "'mem.rss', 'mem.swap', 'mem.rss.anon'")},
            r AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY upid, name ORDER BY ts) AS rf,
                            ROW_NUMBER() OVER (PARTITION BY upid, name ORDER BY ts DESC) AS rl FROM mc),
            pg AS (
              SELECT upid, ROUND(MAX(IIF(name = 'mem.rss', value, NULL)) / 1048576.0, 1) AS peak_rss_mb,
                     ROUND(MAX(IIF(name = 'mem.swap', value, NULL)) / 1048576.0, 1) AS peak_swap_mb,
                     ROUND(MAX(IIF(name = 'mem.rss' AND rl = 1, value, NULL)) / 1048576.0, 1) AS end_rss_mb,
                     SUM(IIF(name IN ('mem.rss.anon', 'mem.swap') AND rf = 1, value, 0)) / 1048576.0 AS start_mb,
                     SUM(IIF(name IN ('mem.rss.anon', 'mem.swap') AND rl = 1, value, 0)) / 1048576.0 AS end_mb
              FROM r GROUP BY upid)
            SELECT pg.upid, p.name AS process, peak_rss_mb, peak_swap_mb, end_rss_mb,
                   ROUND(end_mb - start_mb, 1) AS anon_swap_growth_mb,
                   CASE WHEN end_mb - start_mb >= 100 OR (end_mb - start_mb >= 5 AND end_mb - start_mb >= 0.5 * start_mb) THEN 'critical'
                        WHEN end_mb - start_mb >= 50 OR (end_mb - start_mb >= 5 AND end_mb - start_mb >= 0.2 * start_mb) THEN 'warning'
                   END AS growth_rating
            FROM pg LEFT JOIN process p USING (upid)
            """, ctx).ConfigureAwait(false);
        report["top_rss"] = TableFormat.ToJson(Top(procs, 2, limit, _ => true), limit);
        report["top_growth"] = TableFormat.ToJson(Top(procs, 5, limit, v => v > 0), limit);
        return ToolCallResult.Json(report);
    }

    // CTE `mc(upid, name, ts, value)`: mem.* samples in effect during the window (LEAD gives each sample's end),
    // read from the tracks to avoid the stdlib view's span join over all processes.
    internal static string MemCountersCte(string upidPredicate, TimeWindow w, string names) => $"""
        mc AS (SELECT upid, name, ts, value FROM (
          SELECT t.upid, t.name, c.ts, c.value, LEAD(c.ts) OVER (PARTITION BY c.track_id ORDER BY c.ts) AS next_ts
          FROM counter c JOIN process_counter_track t ON c.track_id = t.id
          WHERE t.name IN ({names}) AND {upidPredicate})
        WHERE ts < {w.End} AND IFNULL(next_ts, {w.End}) > {w.Start})
        """;

    // Rows of `t` ordered by the numeric column `col` (descending), keeping those whose value passes `keep`.
    private static QueryTable Top(QueryTable t, int col, int limit, Func<double, bool> keep)
    {
        QueryTable top = new() { TotalRows = t.TotalRows, ElapsedMs = t.ElapsedMs };
        top.Columns.AddRange(t.Columns);
        top.Rows.AddRange(t.Rows
            .Where(r => r[col] is not null && keep(Convert.ToDouble(r[col])))
            .OrderByDescending(r => Convert.ToDouble(r[col]))
            .Take(limit));
        top.TotalRows = top.Rows.Count;
        return top;
    }
}

internal sealed class ProcessMemoryTool : TraceToolBase
{
    private const int Buckets = 24;

    public ProcessMemoryTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("process_memory",
        $"""
        Memory of one process over a window: RSS components (anon / file / shmem / swap, MB) at start, end and peak,
        a {Buckets}-point peak-RSS timeline, time spent per OOM-adj bucket (foreground, visible, cached ...),
        its LMK kills and direct-reclaim stalls on its threads. Use after memory_summary to explain one process.
        A name can match several instances (restarts); all are listed and the one with the most memory samples in
        the window is analyzed unless `upid` is given.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder())).TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        string proc = ProcessSelector.Predicate(args)
            ?? throw new McpInvalidParamsException("pass upid, pid or process_name");
        QueryTable instances = await RunAsync(s, $"""
            {w.Prelude}SELECT p.upid, p.pid, p.name, p.start_ts, p.end_ts,
                   (SELECT COUNT(*) FROM counter c JOIN process_counter_track t ON c.track_id = t.id
                    WHERE t.upid = p.upid AND t.name = 'mem.rss' AND c.ts >= {w.Start} AND c.ts < {w.End}) AS samples
            FROM process p WHERE {proc} AND p.name IS NOT NULL ORDER BY samples DESC, p.upid DESC
            """, ctx).ConfigureAwait(false);
        if (instances.Rows.Count == 0)
        {
            throw new ToolException($"No process matches {ProcessSelector.Describe(args)}. Check list_processes.");
        }
        long chosen = (long)instances.Rows[0][0]!;
        string upids = $"({chosen})";
        string mem = $"""
            {w.Prelude}WITH {MemorySummaryTool.MemCountersCte($"t.upid IN {upids}", w,
                "'mem.rss.anon', 'mem.rss.file', 'mem.rss.shmem', 'mem.swap', 'mem.rss'")},
            -- rss_stat ftrace updates fields at different times: each sample holds until that field's next one.
            -- A range join (not per-point correlated subqueries, which re-evaluate mc each time) builds the wide rows.
            fs AS (SELECT name, ts, value, LEAD(ts) OVER (PARTITION BY name ORDER BY ts) AS nts FROM mc),
            pts AS (SELECT DISTINCT ts FROM fs),
            m AS (
              SELECT p.ts,
                     SUM(IIF(fs.name = 'mem.rss.anon', fs.value, 0)) AS anon, SUM(IIF(fs.name = 'mem.rss.file', fs.value, 0)) AS file,
                     SUM(IIF(fs.name = 'mem.rss.shmem', fs.value, 0)) AS shmem, SUM(IIF(fs.name = 'mem.swap', fs.value, 0)) AS swap,
                     SUM(IIF(fs.name = 'mem.rss', fs.value, 0)) AS rss
              FROM pts p JOIN fs ON fs.ts <= p.ts AND (fs.nts IS NULL OR fs.nts > p.ts)
              GROUP BY p.ts)
            """;

        JsonObject report = new()
        {
            ["window"] = w.Description,
            ["analyzed_upid"] = chosen,
            ["instances"] = TableFormat.ToJson(instances, 20)
        };
        if (instances.Rows.Count > 1)
        {
            report["note"] = $"{instances.Rows.Count} process instances match; analyzed upid {chosen} (most memory samples in the window). Pass `upid` to choose another.";
        }
        report["rss_mb"] = await SectionAsync(s, mem + """
            , f AS (SELECT * FROM m ORDER BY ts LIMIT 1), l AS (SELECT * FROM m ORDER BY ts DESC LIMIT 1)
            SELECT 'start' AS at, ROUND(anon / 1048576.0, 1) AS anon, ROUND(file / 1048576.0, 1) AS file,
                   ROUND(shmem / 1048576.0, 1) AS shmem, ROUND(swap / 1048576.0, 1) AS swap, ROUND(rss / 1048576.0, 1) AS rss FROM f
            UNION ALL
            SELECT 'end', ROUND(anon / 1048576.0, 1), ROUND(file / 1048576.0, 1), ROUND(shmem / 1048576.0, 1),
                   ROUND(swap / 1048576.0, 1), ROUND(rss / 1048576.0, 1) FROM l
            UNION ALL
            SELECT 'peak', ROUND(MAX(anon) / 1048576.0, 1), ROUND(MAX(file) / 1048576.0, 1), ROUND(MAX(shmem) / 1048576.0, 1),
                   ROUND(MAX(swap) / 1048576.0, 1), ROUND(MAX(rss) / 1048576.0, 1) FROM m
            """, ctx, 3).ConfigureAwait(false);
        report["rss_timeline"] = await SectionAsync(s, mem + $"""
            SELECT CAST((ts - {w.Start}) * {Buckets} / ({w.End} - {w.Start}) AS INT) AS bucket, MIN(ts) AS ts,
                   ROUND(MAX(rss) / 1048576.0, 1) AS peak_rss_mb, ROUND(MAX(swap) / 1048576.0, 1) AS peak_swap_mb
            FROM m GROUP BY bucket ORDER BY bucket
            """, ctx, Buckets + 2).ConfigureAwait(false);
        report["oom_adj"] = await SectionAsync(s, $"""
            {w.Prelude}INCLUDE PERFETTO MODULE android.oom_adjuster;
            SELECT bucket, MIN(score) AS min_score, MAX(score) AS max_score, COUNT(*) AS intervals,
                   ROUND(SUM({w.Clipped("ts", "dur")}) / 1e6, 1) AS time_ms
            FROM android_oom_adj_intervals WHERE upid IN {upids} AND {w.Overlaps("ts", "dur")}
            GROUP BY bucket ORDER BY time_ms DESC
            """, ctx, 20).ConfigureAwait(false);
        report["lmk_kills"] = await SectionAsync(s, $"""
            {w.Prelude}INCLUDE PERFETTO MODULE android.memory.lmk;
            SELECT ts, process_name, pid, oom_score_adj, kill_reason FROM android_lmk_events
            WHERE upid IN {upids} AND ts BETWEEN {w.Start} AND {w.End} ORDER BY ts
            """, ctx, 20).ConfigureAwait(false);
        report["direct_reclaim"] = await SectionAsync(s, $"""
            {w.Prelude}INCLUDE PERFETTO MODULE slices.with_context;
            SELECT thread_name, tid, COUNT(*) AS n, ROUND(SUM(dur) / 1e6, 2) AS total_ms, ROUND(MAX(dur) / 1e6, 2) AS max_ms
            FROM thread_slice WHERE name = 'mm_vmscan_direct_reclaim' AND upid IN {upids} AND {w.Overlaps("ts", "dur")}
            GROUP BY utid ORDER BY SUM(dur) DESC LIMIT 20
            """, ctx, 20).ConfigureAwait(false);
        return ToolCallResult.Json(report);
    }
}
