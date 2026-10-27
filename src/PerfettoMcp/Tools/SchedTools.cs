using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class ThreadStateBreakdownTool : TraceToolBase
{
    public ThreadStateBreakdownTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("thread_state_breakdown",
        """
        Where did a thread's wall time go in a window: Running / Runnable (waiting for CPU) / Runnable (Preempted) /
        Sleeping / Uninterruptible Sleep (+IO) with kernel blocked functions, clipped to the window.
        Select the thread with utid / tid, or a process (main thread by default; `thread_name` or `all_threads`).
        Window: ts_start/ts_end (ns), startup_id, or whole trace.
        """,
        TimeWindow.AddParams(ThreadSelector.AddParams(new SchemaBuilder()))
            .Int("limit", "Max rows (default 30).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        string threads = ThreadSelector.Predicate(args);
        TimeWindow w = TimeWindow.Parse(args);
        int limit = Args.OptionalInt(args, "limit", 30, 1, 1000);
        string clipped = w.Clipped("x.ts", "x.dur");
        // A thread split across utids (see ProcessSelector.OwnerUpidOf) leaves the old
        // utid's last state open until trace end; cut each utid where the next one starts.
        QueryTable t = await RunAsync(s, $"""
            {w.Prelude}INCLUDE PERFETTO MODULE sched.states;
            WITH sel AS (SELECT t.utid, t.tid, t.name FROM thread t LEFT JOIN process p USING (upid) WHERE {threads}),
            seg AS (
              SELECT sel.utid, LEAD(MIN(ts.ts)) OVER (PARTITION BY sel.tid ORDER BY MIN(ts.ts)) AS next_ts
              FROM thread_state ts JOIN sel USING (utid) GROUP BY sel.utid),
            x AS (
              SELECT ts.utid, ts.ts, ts.state, ts.io_wait, ts.blocked_function,
                     IIF(seg.next_ts IS NOT NULL AND (ts.dur < 0 OR ts.ts + ts.dur > seg.next_ts), seg.next_ts - ts.ts, ts.dur) AS dur
              FROM thread_state ts JOIN seg USING (utid)
              WHERE seg.next_ts IS NULL OR ts.ts < seg.next_ts)
            SELECT GROUP_CONCAT(DISTINCT sel.utid) AS utids, sel.tid, MAX(sel.name) AS thread,
                   sched_state_io_to_human_readable_string(x.state, x.io_wait) AS state, x.blocked_function,
                   COUNT(*) AS n, ROUND(SUM({clipped}) / 1e6, 2) AS dur_ms,
                   ROUND(100.0 * SUM({clipped}) / ({w.End} - {w.Start}), 1) AS pct_of_window
            FROM x JOIN sel USING (utid)
            WHERE {w.Overlaps("x.ts", "x.dur")}
            GROUP BY sel.tid, 4, x.blocked_function
            ORDER BY SUM({clipped}) DESC
            """, ctx).ConfigureAwait(false);
        if (t.TotalRows == 0)
        {
            throw new ToolException("No thread_state rows for that thread/window (is the thread selector right? does the trace have sched data?).");
        }
        JsonObject o = TableFormat.ToJson(t, limit);
        o["window"] = w.Description;
        return ToolCallResult.Json(o);
    }
}

internal sealed class CpuTopThreadsTool : TraceToolBase
{
    public CpuTopThreadsTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("cpu_top_threads",
        """
        Who used the CPU in a window: CPU time per thread (or per process with group_by=process) from sched slices,
        clipped to the window, with % of one CPU. Optional process filter. Window: ts_start/ts_end, startup_id or
        whole trace. group_by=thread_name sums same-named threads (worker pools). `thread_churn` lists thread names
        created >= 5 times in the window (needs task_newtask in the trace): creating threads per frame / per request
        is a common hidden cost (creation, cold caches, CPU oversubscription).
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder()))
            .Enum("group_by", "Aggregate per thread (default), per process or per thread name within a process.",
                "thread", "process", "thread_name")
            .Int("limit", "Max rows (default 25).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        string proc = ProcessSelector.Predicate(args) ?? "1";
        int limit = Args.OptionalInt(args, "limit", 25, 1, 1000);
        string groupMode = Args.OptionalString(args, "group_by") ?? "thread";
        string clipped = w.Clipped("x.ts", "x.dur");
        string owner = ProcessSelector.OwnerUpidOf("t");
        string keyCols = groupMode switch
        {
            "process" => $"{owner} AS upid, MAX({ProcessSelector.NameOf("t")}) AS process, COUNT(DISTINCT t.tid) AS threads",
            "thread_name" => $"{owner} AS upid, MAX({ProcessSelector.NameOf("t")}) AS process, t.name AS thread, COUNT(DISTINCT t.tid) AS threads",
            _ => $"GROUP_CONCAT(DISTINCT t.utid) AS utids, t.tid, MAX(t.name) AS thread, MAX({ProcessSelector.NameOf("t")}) AS process"
        };
        string groupBy = groupMode switch
        {
            "process" => "1",
            "thread_name" => $"{owner}, t.name",
            _ => $"{owner}, t.tid"
        };
        QueryTable t = await RunAsync(s, $"""
            {w.Prelude}SELECT {keyCols}, COUNT(*) AS sched_slices, ROUND(SUM({clipped}) / 1e6, 2) AS cpu_ms,
                   ROUND(100.0 * SUM({clipped}) / ({w.End} - {w.Start}), 1) AS pct_of_one_cpu
            FROM sched x JOIN thread t USING (utid) LEFT JOIN process p USING (upid)
            WHERE x.utid != 0 AND {proc} AND {w.Overlaps("x.ts", "x.dur")}
            GROUP BY {groupBy} ORDER BY SUM({clipped}) DESC
            """, ctx).ConfigureAwait(false);
        JsonObject o = TableFormat.ToJson(t, limit);
        o["window"] = w.Description;

        // thread.start_ts is only set for threads whose creation (task_newtask) is in the trace, so it marks new threads.
        QueryTable churn = await RunAsync(s, $"""
            {w.Prelude}WITH nt AS (
              SELECT t.utid, t.tid, t.name, t.start_ts, t.end_ts, {owner} AS owner
              FROM thread t LEFT JOIN process p USING (upid)
              WHERE t.start_ts BETWEEN {w.Start} AND {w.End} AND {proc}),
            g AS (
              SELECT owner, name, COUNT(*) AS created, SUM(end_ts IS NOT NULL) AS exited,
                     -- The renamed thread is the event's `pid` arg: the event runs on the renaming thread, which
                     -- differs for pthread_setname_np / Thread.setName on another thread.
                     SUM(tid NOT IN (SELECT a.int_value FROM ftrace_event e JOIN args a ON a.arg_set_id = e.arg_set_id
                                     WHERE e.name = 'task_rename' AND a.key = 'pid')) AS unnamed,
                     ROUND(AVG(end_ts - start_ts) / 1e6, 1) AS avg_life_ms
              FROM nt GROUP BY owner, name HAVING COUNT(*) >= 5),
            cpu AS (
              SELECT nt.owner, nt.name, SUM({clipped}) AS d FROM sched x JOIN nt USING (utid)
              WHERE {w.Overlaps("x.ts", "x.dur")} GROUP BY nt.owner, nt.name)
            SELECT p.pid, COALESCE(p.name, p.cmdline) AS process, g.name AS thread, g.created, g.exited, g.unnamed, g.avg_life_ms,
                   ROUND(COALESCE(cpu.d, 0) / 1e6, 1) AS cpu_ms
            FROM g LEFT JOIN cpu ON cpu.owner = g.owner AND cpu.name IS g.name LEFT JOIN process p ON p.upid = g.owner
            ORDER BY g.created DESC LIMIT 10
            """, ctx).ConfigureAwait(false);
        if (churn.Rows.Count > 0)
        {
            JsonObject c = TableFormat.ToJson(churn, 10);
            c["note"] = "unnamed = never renamed (no task_rename): such native threads keep the name of the thread that "
                + "created them, so `thread` is then the creator, not a pool worker.";
            o["thread_churn"] = c;
        }
        return ToolCallResult.Json(o);
    }
}

internal sealed class SliceHotspotsTool : TraceToolBase
{
    public SliceHotspotsTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("slice_hotspots",
        """
        Heaviest trace slices (atrace / track-event sections) grouped by name, with count, total/max/avg duration and
        the ts + id of the longest instance. Filter by process, thread, name GLOB, window and max depth. Covers
        thread-track slices and process-scoped async slices. Note: nested slices are counted at every depth —
        use max_depth=0 for top-level only.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder()))
            .Str("thread_name", "Only slices on threads with this name (* for GLOB).")
            .Bool("main_thread_only", "Only slices on the process main thread.")
            .Str("name_glob", "Slice name GLOB, e.g. `*inflate*`, `Choreographer#doFrame*`.")
            .Int("max_depth", "Only slices at depth <= this (0 = top-level).")
            .Enum("order_by", "Ranking (default total).", "total", "max", "count")
            .Int("limit", "Max rows (default 25).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        int limit = Args.OptionalInt(args, "limit", 25, 1, 1000);
        string filter = ProcessSelector.Predicate(args) ?? "1";
        string? threadName = Args.OptionalString(args, "thread_name");
        if (!string.IsNullOrWhiteSpace(threadName))
        {
            string lit = SqlText.Literal(threadName);
            filter += threadName.Contains('*') ? $" AND x.thread_name GLOB {lit}" : $" AND x.thread_name = {lit}";
        }
        if (Args.OptionalBool(args, "main_thread_only", false))
        {
            string real = ProcessSelector.RealPredicate(args) ?? "1";
            filter += $" AND x.utid IN (SELECT t.utid FROM thread t JOIN process p USING (upid) WHERE {real} AND t.tid = p.pid)";
        }
        if (Args.OptionalString(args, "name_glob") is { Length: > 0 } glob)
        {
            filter += $" AND x.name GLOB {SqlText.Literal(glob)}";
        }
        if (Args.OptionalLong(args, "max_depth") is { } depth)
        {
            filter += $" AND x.depth <= {depth}";
        }
        string order = Args.OptionalString(args, "order_by") switch
        {
            "max" => "max_ms DESC",
            "count" => "n DESC",
            _ => "total_ms DESC"
        };
        // ROW_NUMBER picks the longest instance: SQLite bare columns are defined only with a single MIN/MAX.
        QueryTable t = await RunAsync(s, $"""
            {w.Prelude}INCLUDE PERFETTO MODULE slices.with_context;
            WITH x AS (
              SELECT id, ts, dur, name, depth, utid, tid, thread_name, upid FROM thread_slice
              UNION ALL
              SELECT id, ts, dur, name, depth, NULL, NULL, NULL, upid FROM process_slice),
            f AS (
              SELECT x.*, {ProcessSelector.NameOf("x")} AS process,
                     ROW_NUMBER() OVER (PARTITION BY x.name ORDER BY x.dur DESC) AS rn
              FROM x LEFT JOIN process p USING (upid)
              WHERE x.dur > 0 AND {filter} AND {w.Overlaps("x.ts", "x.dur")}),
            agg AS (
              SELECT name, COUNT(*) AS n, ROUND(SUM(dur) / 1e6, 2) AS total_ms, ROUND(MAX(dur) / 1e6, 2) AS max_ms,
                     ROUND(AVG(dur) / 1e6, 3) AS avg_ms, MIN(depth) AS min_depth
              FROM f GROUP BY name)
            SELECT agg.*, f.ts AS max_ts, f.id AS max_slice_id, f.process AS max_process, f.thread_name AS max_thread
            FROM agg JOIN f ON f.name = agg.name AND f.rn = 1
            ORDER BY {order}
            """, ctx).ConfigureAwait(false);
        JsonObject o = TableFormat.ToJson(t, limit);
        o["window"] = w.Description;
        return ToolCallResult.Json(o);
    }
}
