using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class IoSummaryTool : TraceToolBase
{
    // Kernel threads are children of kthreadd (pid 2); their D-state sleeps (msleep, kthread) are mostly noise.
    private const string KernelThread = "(p.pid = 2 OR (SELECT pp.pid FROM process pp WHERE pp.upid = p.parent_upid) = 2)";

    public IoSummaryTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("io_summary",
        """
        IO in a window, from two angles:
        1) who was blocked: threads with the most uninterruptible-sleep (D) time split into IO / non-IO, and the kernel
           blocked functions behind it (z_erofs_runqueue, folio_wait_bit_common, eswap_*, f2fs_* ...). Family
           workqueue_wait (__flush_work) means the thread waits for a kworker to finish an item, e.g. a zram / eswap
           read-back from flash (eswap_fault_out) or software crypto; a kernel stack (ftrace stacktrace trigger on
           sched_switch) names it;
        2) the block layer: per device (major:minor; 8:x = UFS/sd, 7:x = loop/APEX, 253/254 = dm) request count, MB,
           avg/max latency and requests over 10 / 50 ms, the slowest requests, and page-cache insertions per process.
        Needs sched_blocked_reason for blocked functions and block/block_rq_issue+complete, filemap ftrace for the rest.
        Kernel threads are excluded from the D-state sections unless include_kernel_threads=true.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder()))
            .Bool("include_kernel_threads", "Include kernel threads (kthreadd children) in the D-state sections.")
            .Int("limit", "Rows per section (default 15).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        int limit = Args.OptionalInt(args, "limit", 15, 1, 500);
        string proc = ProcessSelector.Predicate(args) ?? "1";
        string kernel = Args.OptionalBool(args, "include_kernel_threads", false) ? "1" : $"NOT {KernelThread}";
        string clipped = w.Clipped("x.ts", "x.dur");
        string dState = $"""
            thread_state x JOIN thread t USING (utid) LEFT JOIN process p USING (upid)
            WHERE x.state GLOB 'D*' AND {proc} AND {kernel} AND {w.Overlaps("x.ts", "x.dur")}
            """;

        JsonObject report = new() { ["window"] = w.Description };
        report["blocked_threads"] = await SectionAsync(s, $"""
            {w.Prelude}WITH d AS (
              SELECT {ProcessSelector.NameOf("t")} AS process, t.tid, t.name AS thread, x.io_wait, x.blocked_function, {clipped} AS dur
              FROM {dState}),
            top_fn AS (
              SELECT tid, blocked_function, ROW_NUMBER() OVER (PARTITION BY tid ORDER BY SUM(dur) DESC) AS rn
              FROM d WHERE blocked_function IS NOT NULL GROUP BY tid, blocked_function)
            SELECT MAX(d.process) AS process, d.tid, MAX(d.thread) AS thread,
                   ROUND(SUM(IIF(d.io_wait = 1, d.dur, 0)) / 1e6, 1) AS d_io_ms,
                   ROUND(SUM(IIF(d.io_wait = 1, 0, d.dur)) / 1e6, 1) AS d_other_ms, COUNT(*) AS n,
                   (SELECT blocked_function FROM top_fn f WHERE f.tid = d.tid AND f.rn = 1) AS top_blocked_function
            FROM d GROUP BY d.tid ORDER BY SUM(d.dur) DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);

        report["blocked_functions"] = await SectionAsync(s, $"""
            {w.Prelude}SELECT x.blocked_function,
                   CASE WHEN x.blocked_function GLOB '*erofs*' THEN 'erofs'
                        WHEN x.blocked_function GLOB '*f2fs*' OR x.blocked_function GLOB '*ext4*' THEN 'filesystem'
                        WHEN x.blocked_function GLOB '*folio_wait*' OR x.blocked_function GLOB '*wait_on_page*'
                          OR x.blocked_function GLOB '*filemap*' OR x.blocked_function GLOB '*io_schedule*' THEN 'page_cache_io'
                        WHEN x.blocked_function GLOB '*blk_*' OR x.blocked_function GLOB '*bio_*' THEN 'block_layer'
                        WHEN x.blocked_function GLOB '*ufshcd*' OR x.blocked_function GLOB '*mmc_*' THEN 'storage_driver'
                        WHEN x.blocked_function GLOB 'dm_*' OR x.blocked_function GLOB '*_dm_*' THEN 'device_mapper'
                        WHEN x.blocked_function GLOB '*swap*' OR x.blocked_function GLOB '*zram*' THEN 'swap'
                        WHEN x.blocked_function GLOB '*reclaim*' OR x.blocked_function GLOB '*shrink*' OR x.blocked_function GLOB '*compact*' THEN 'memory_reclaim'
                        WHEN x.blocked_function GLOB '*fence*' OR x.blocked_function GLOB '*mali*' OR x.blocked_function GLOB '*kgsl*' THEN 'gpu_fence'
                        WHEN x.blocked_function GLOB '*mmap*' OR x.blocked_function GLOB '*mprotect*' OR x.blocked_function GLOB '*munmap*' THEN 'mm_lock'
                        WHEN x.blocked_function GLOB '*flush_work*' OR x.blocked_function GLOB '*flush_workqueue*' THEN 'workqueue_wait'
                        WHEN x.blocked_function IS NULL THEN NULL ELSE 'other' END AS family,
                   x.io_wait, COUNT(*) AS n, COUNT(DISTINCT x.utid) AS threads,
                   ROUND(SUM({clipped}) / 1e6, 1) AS total_ms, ROUND(MAX(x.dur) / 1e6, 2) AS max_ms
            FROM {dState}
            GROUP BY x.blocked_function, x.io_wait ORDER BY SUM({clipped}) DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);

        // Pair each block_rq_issue with the next event on the same (dev, sector): its complete.
        string requests = $"""
            {w.Prelude}INCLUDE PERFETTO MODULE linux.block_io;
            WITH ev AS (
              SELECT e.ts, e.name, EXTRACT_ARG(e.arg_set_id, 'dev') AS dev, EXTRACT_ARG(e.arg_set_id, 'sector') AS sector,
                     EXTRACT_ARG(e.arg_set_id, 'nr_sector') AS nr, EXTRACT_ARG(e.arg_set_id, 'rwbs') AS rwbs,
                     EXTRACT_ARG(e.arg_set_id, 'comm') AS comm
              FROM ftrace_event e
              WHERE e.name IN ('block_rq_issue', 'block_rq_complete') AND e.ts >= {w.Start} AND e.ts < {w.End} + 2000000000),
            pr AS (SELECT *, LEAD(name) OVER win AS next_name, LEAD(ts) OVER win AS next_ts
                   FROM ev WINDOW win AS (PARTITION BY dev, sector ORDER BY ts)),
            rq AS (
              SELECT ts, linux_device_major_id(dev) || ':' || linux_device_minor_id(dev) AS device, nr * 512 AS bytes,
                     SUBSTR(rwbs, 1, 1) AS op, rwbs, comm, next_ts - ts AS lat
              FROM pr WHERE name = 'block_rq_issue' AND next_name = 'block_rq_complete' AND ts < {w.End})
            """;
        report["block_devices"] = await SectionAsync(s, requests + $"""
            SELECT device, op, COUNT(*) AS requests, ROUND(SUM(bytes) / 1048576.0, 1) AS mb, ROUND(AVG(lat) / 1e6, 2) AS avg_ms,
                   ROUND(MAX(lat) / 1e6, 2) AS max_ms, SUM(lat > 10e6) AS over_10ms, SUM(lat > 50e6) AS over_50ms
            FROM rq GROUP BY device, op ORDER BY requests DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        report["slowest_requests"] = await SectionAsync(s, requests + $"""
            SELECT ts, device, rwbs, bytes / 1024 AS kb, ROUND(lat / 1e6, 2) AS latency_ms, comm AS issued_by
            FROM rq ORDER BY lat DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);

        report["page_cache_adds"] = await SectionAsync(s, $"""
            {w.Prelude}SELECT {ProcessSelector.NameOf("t")} AS process, COUNT(*) AS pages, ROUND(COUNT(*) * 4 / 1024.0, 1) AS approx_mb
            FROM ftrace_event e JOIN thread t USING (utid) LEFT JOIN process p USING (upid)
            WHERE e.name = 'mm_filemap_add_to_page_cache' AND e.ts BETWEEN {w.Start} AND {w.End} AND {proc}
            GROUP BY 1 ORDER BY pages DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        return ToolCallResult.Json(report);
    }
}
