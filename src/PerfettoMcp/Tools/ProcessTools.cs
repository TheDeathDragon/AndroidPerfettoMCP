using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class ListProcessesTool : TraceToolBase
{
    public ListProcessesTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("list_processes",
        "List processes in the trace with upid (trace-internal id used by other tools), pid, name, uid, thread " +
        "count and total CPU time, busiest first. Use to pick the upid / process_name for the other tools.",
        new SchemaBuilder()
            .Str("name_filter", "Case-insensitive substring of the process name or cmdline.")
            .Int("limit", "Max rows (default 100).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        string? filter = Args.OptionalString(args, "name_filter");
        int limit = Args.OptionalInt(args, "limit", 100, 1, Settings.HardMaxRows);
        string where = string.Empty;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            string like = SqlText.Literal("%" + SqlText.EscapeLike(filter.Trim()) + "%");
            where = $@"WHERE p.name LIKE {like} ESCAPE '\' OR p.cmdline LIKE {like} ESCAPE '\'";
        }
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        QueryTable t = await RunAsync(s, $"""
            WITH owner AS (SELECT t.utid, t.tid, t.upid, {ProcessSelector.OwnerUpidOf("t")} AS owner
                           FROM thread t LEFT JOIN process p USING (upid)),
                 cpu AS (SELECT o.owner AS upid, SUM(s.dur) AS d FROM sched s JOIN owner o USING (utid)
                         WHERE s.utid != 0 GROUP BY o.owner),
                 thr AS (SELECT owner AS upid, COUNT(DISTINCT tid) AS n FROM owner GROUP BY owner),
                 shadows AS (SELECT owner AS upid, COUNT(DISTINCT upid) AS n FROM owner WHERE owner != upid GROUP BY owner)
            SELECT p.upid, p.pid, p.name, p.uid, COALESCE(thr.n, 0) AS threads,
                   ROUND(COALESCE(cpu.d, 0) / 1e6, 1) AS cpu_ms, p.start_ts, p.end_ts, shadows.n AS merged_shadow_processes
            -- Shadow / placeholder processes whose threads all belong to other processes are folded in, not listed.
            FROM (SELECT * FROM process px WHERE px.name IS NOT NULL OR px.cmdline IS NOT NULL
                  OR EXISTS (SELECT 1 FROM owner o WHERE o.upid = px.upid AND o.owner = px.upid)) p
            LEFT JOIN cpu ON cpu.upid = p.upid LEFT JOIN thr ON thr.upid = p.upid LEFT JOIN shadows ON shadows.upid = p.upid
            {where}
            ORDER BY cpu_ms DESC, p.pid
            """, ctx).ConfigureAwait(false);
        return ToolCallResult.Json(TableFormat.ToJson(t, limit));
    }
}

internal sealed class ListThreadsInProcessTool : TraceToolBase
{
    public ListThreadsInProcessTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("list_threads_in_process",
        "List threads of a process (utid, tid, name, is_main_thread, CPU time, scheduling priority), busiest first. " +
        "`prio` is the kernel priority seen in sched (100-139 = CFS, nice = prio - 120; < 100 = real-time); " +
        "a render thread at nice 0 competing with busy workers is a common jank cause.",
        ProcessSelector.AddParams(new SchemaBuilder())
            .Int("limit", "Max rows (default 200).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        string pred = ProcessSelector.Predicate(args)
            ?? throw new McpInvalidParamsException("pass upid, pid or process_name");
        int limit = Args.OptionalInt(args, "limit", 200, 1, Settings.HardMaxRows);
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        QueryTable t = await RunAsync(s, $"""
            WITH cpu AS (SELECT utid, SUM(dur) AS d, MIN(priority) AS prio, MAX(priority) AS prio_max FROM sched GROUP BY utid)
            SELECT GROUP_CONCAT(DISTINCT t.utid) AS utids, t.tid, MAX(t.name) AS thread_name, MAX(IIF(p.name IS NOT NULL OR p.cmdline IS NOT NULL, t.is_main_thread, 0)) AS is_main_thread,
                   MIN({ProcessSelector.OwnerUpidOf("t")}) AS upid, MAX({ProcessSelector.NameOf("t")}) AS process_name,
                   ROUND(COALESCE(SUM(cpu.d), 0) / 1e6, 1) AS cpu_ms, MIN(cpu.prio) AS prio,
                   IIF(MIN(cpu.prio) BETWEEN 100 AND 139, MIN(cpu.prio) - 120, NULL) AS nice,
                   IIF(MAX(cpu.prio_max) != MIN(cpu.prio), MAX(cpu.prio_max), NULL) AS prio_max
            FROM thread t JOIN process p USING (upid) LEFT JOIN cpu USING (utid)
            WHERE {pred}
            GROUP BY t.tid
            ORDER BY cpu_ms DESC, t.tid
            """, ctx).ConfigureAwait(false);
        if (t.TotalRows == 0)
        {
            throw new ToolException($"No threads found for {ProcessSelector.Describe(args)}. Check list_processes.");
        }
        return ToolCallResult.Json(TableFormat.ToJson(t, limit));
    }
}
