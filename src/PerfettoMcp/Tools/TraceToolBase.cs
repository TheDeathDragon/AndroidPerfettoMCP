using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal abstract class TraceToolBase : IToolHandler
{
    protected TraceToolBase(TraceManager traces)
    {
        Traces = traces;
    }

    public abstract ToolDescriptor Descriptor { get; }

    protected TraceManager Traces { get; }

    public abstract Task<ToolCallResult> InvokeAsync(JsonObject arguments, RequestContext ctx);

    protected async Task<QueryTable> RunAsync(TraceSession session, string sql, RequestContext ctx,
        int maxRows = Settings.HardMaxRows, TimeSpan? timeout = null)
    {
        QueryTable t = await Traces.QueryAsync(session, sql, ctx, timeout, maxRows).ConfigureAwait(false);
        TableFormat.ThrowIfError(t, sql);
        return t;
    }

    // One section of a multi-part report: SQL errors are reported in place so the
    // other sections still come back; timeouts/crashes propagate.
    protected async Task<JsonNode> SectionAsync(TraceSession session, string sql, RequestContext ctx, int maxRows)
    {
        try
        {
            QueryTable t = await RunAsync(session, sql, ctx, maxRows).ConfigureAwait(false);
            return TableFormat.ToJson(t, maxRows);
        }
        catch (SqlQueryException ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    protected static ToolDescriptor Describe(string name, string description, SchemaBuilder schema) => new()
    {
        Name = name,
        Description = description,
        InputSchema = schema.Build(),
        Annotations = Annotations.ReadOnly()
    };

    // Generic wrappers that say nothing about what a blocker was doing.
    private const string CriticalPathGenericSlices = "('binder reply', 'binder transaction', 'binder transaction async')";

    // Critical path of `root` over [ws, we) (stdlib sched.thread_executing_span): blocking threads, states, slices.
    // A sleep still open at the window end is charged to its eventual waker; when the window end is the symptom (ANR)
    // or that wake is far away / missing, the path stops at the sleep instead.
    protected async Task<JsonObject> CriticalPathAsync(TraceSession s, long root, long ws, long we, RequestContext ctx,
        bool stopAtOpenSleep = false)
    {
        JsonObject o = new() { ["root_utid"] = root };
        QueryTable open = await RunAsync(s, $"""
            SELECT x.ts, x.state, IIF(x.dur < 0, NULL, x.ts + x.dur) AS woke FROM thread_state x
            WHERE x.utid = {root} AND x.state IN ('S', 'D', 'DK', 'I') AND x.ts < {we} AND (x.dur < 0 OR x.ts + x.dur > {we})
            ORDER BY x.ts DESC LIMIT 1
            """, ctx).ConfigureAwait(false);
        if (open.Rows.Count > 0 && open.Rows[0][0] is long sleepStart
            && (stopAtOpenSleep || open.Rows[0][2] is not long woke || woke - we > 1_000_000_000L))
        {
            if (sleepStart <= ws)
            {
                o["note"] = "The thread slept through the whole window and was not woken by its cause: no critical path to "
                    + "attribute (look at what it waits for: stuck_binder, open slices, blocked_function).";
                return o;
            }
            if (we - sleepStart > 1_000_000)
            {
                o["note"] = $"The thread went to sleep ({open.Rows[0][1]}) at {sleepStart} and stayed asleep past the window end; "
                    + $"the path covers [{ws}, {sleepStart}) only — the last {(we - sleepStart) / 1_000_000} ms have no causal waker.";
                we = sleepStart;
            }
        }
        string cp = $"""
            INCLUDE PERFETTO MODULE sched.thread_executing_span;
            WITH cp AS (SELECT utid, ts, MIN(ts + dur, {we}) - MAX(ts, {ws}) AS dur, MAX(ts, {ws}) AS cs, MIN(ts + dur, {we}) AS ce
                        FROM _thread_executing_span_critical_path({root}, {ws}, {we - ws}) WHERE dur > 0 AND ts < {we} AND ts + dur > {ws})
            """;
        o["by_thread"] = await SectionAsync(s, cp + $"""
            , st AS (
              SELECT cp.utid, x.state, SUM(MIN(IIF(x.dur < 0, cp.ce, x.ts + x.dur), cp.ce) - MAX(x.ts, cp.cs)) AS d
              FROM cp JOIN thread_state x ON x.utid = cp.utid AND x.ts < cp.ce AND IIF(x.dur < 0, cp.ce, x.ts + x.dur) > cp.cs
              GROUP BY cp.utid, x.state)
            SELECT t.name AS thread, t.tid, MAX({ProcessSelector.NameOf("t")}) AS process, ROUND(SUM(cp.dur) / 1e6, 1) AS on_path_ms,
                   ROUND(100.0 * SUM(cp.dur) / {we - ws}, 1) AS pct_of_window,
                   ROUND((SELECT SUM(d) FROM st WHERE st.utid = cp.utid AND st.state = 'Running') / 1e6, 1) AS running_ms,
                   ROUND((SELECT SUM(d) FROM st WHERE st.utid = cp.utid AND st.state IN ('R', 'R+')) / 1e6, 1) AS runnable_ms,
                   ROUND((SELECT SUM(d) FROM st WHERE st.utid = cp.utid AND st.state GLOB 'D*') / 1e6, 1) AS d_ms,
                   IIF(cp.utid = {root}, 1, 0) AS is_root
            FROM cp JOIN thread t USING (utid) LEFT JOIN process p USING (upid)
            GROUP BY cp.utid ORDER BY SUM(cp.dur) DESC LIMIT 12
            """, ctx, 12).ConfigureAwait(false);
        o["blocker_slices"] = await SectionAsync(s, cp + $"""
            SELECT t.name AS thread, x.name AS slice, x.depth, COUNT(*) AS n,
                   ROUND(SUM(MIN(x.ts + x.dur, cp.ce) - MAX(x.ts, cp.cs)) / 1e6, 1) AS on_path_ms
            FROM cp JOIN thread_track tt ON tt.utid = cp.utid JOIN slice x ON x.track_id = tt.id
            JOIN thread t ON t.utid = cp.utid
            WHERE cp.utid != {root} AND x.ts < cp.ce AND x.ts + x.dur > cp.cs AND x.dur > 0 AND x.depth <= 3
              AND x.name NOT IN {CriticalPathGenericSlices}
            GROUP BY cp.utid, x.name, x.depth ORDER BY on_path_ms DESC LIMIT 12
            """, ctx, 12).ConfigureAwait(false);
        if (o["by_thread"] is JsonObject t && t["rows"] is JsonArray { Count: 0 } && o["note"] is null)
        {
            o["note"] = "No executing spans of this thread in the window (not alive, or no sched / wakeup data for it).";
        }
        return o;
    }
}
