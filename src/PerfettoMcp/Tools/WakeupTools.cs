using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class WakeupChainTool : TraceToolBase
{
    // Perfetto stores the waker on the first R/R+ row AFTER a sleep, not on the S/D row itself:
    // every sleep is joined with the row that starts exactly where it ends.
    private const string SleepJoin = "JOIN thread_state n ON n.utid = x.utid AND n.ts = x.ts + x.dur";
    private const string IsSleep = "(x.state GLOB 'S*' OR x.state GLOB 'D*')";

    public WakeupChainTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("wakeup_chain",
        """
        Who a thread was waiting for. For sleeps (S / D) of the selected thread in a window: (1) total sleep time per
        waker thread (or <irq> / <unknown>), (2) the longest sleeps, each followed up the wakeup chain — the waker,
        what it was executing when it woke us, then who woke the waker, up to max_hops — until reaching a thread
        that was already active (the real source of the wait: a binder server, HAL thread, worker, interrupt).
        Thread selection like thread_state_breakdown (main thread of a process by default). Needs sched_wakeup /
        sched_waking ftrace for waker attribution.
        (3) critical_path (stdlib sched.thread_executing_span): which threads actually held up the selected thread's
        progress over the window, with their state (running vs. runnable vs. blocked) while on the path and what they
        were executing — catches blockers the waker chain misses (e.g. a GPU driver compiler thread a RenderThread
        waited on). On by default for explicit windows <= 30 s; costs ~1-4 s the first time on large traces.
        """,
        TimeWindow.AddParams(ThreadSelector.AddParams(new SchemaBuilder()))
            .Num("min_sleep_ms", "Ignore sleeps shorter than this (default 5).")
            .Int("max_hops", "Chain length per sleep (default 4, max 8).")
            .Int("limit", "Longest sleeps to follow (default 6).")
            .Bool("critical_path", "Add the critical-path section (default: on for explicit windows up to 30 s).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        string threads = ThreadSelector.Predicate(args);
        int limit = Args.OptionalInt(args, "limit", 6, 1, 50);
        int maxHops = Args.OptionalInt(args, "max_hops", 4, 1, 8);
        double minMs = Args.OptionalDouble(args, "min_sleep_ms") ?? 5;
        long minNs = (long)(Math.Clamp(minMs, 0, 1e7) * 1e6);
        string sel = $"(SELECT t.utid FROM thread t LEFT JOIN process p USING (upid) WHERE {threads})";
        string sleeps = $"""
            SELECT x.ts, x.dur, x.state, x.blocked_function, n.waker_utid, n.irq_context
            FROM thread_state x {SleepJoin}
            WHERE x.utid IN {sel} AND {IsSleep} AND x.dur >= {minNs} AND {w.Overlaps("x.ts", "x.dur")}
            """;

        JsonObject report = new() { ["window"] = w.Description, ["min_sleep_ms"] = minMs };
        report["by_waker"] = await SectionAsync(s, $"""
            {w.Prelude}WITH sl AS ({sleeps})
            SELECT COALESCE(wt.name, IIF(sl.irq_context, '<irq>', IIF(sl.waker_utid = 0, '<swapper>', '<unknown>'))) AS waker_thread,
                   wt.tid AS waker_tid, {ProcessSelector.NameOf("wt")} AS waker_process,
                   COUNT(*) AS sleeps, ROUND(SUM({w.Clipped("sl.ts", "sl.dur")}) / 1e6, 1) AS total_ms,
                   ROUND(MAX(sl.dur) / 1e6, 1) AS max_ms
            FROM sl LEFT JOIN thread wt ON wt.utid = sl.waker_utid LEFT JOIN process p ON p.upid = wt.upid
            GROUP BY sl.waker_utid, sl.irq_context ORDER BY SUM({w.Clipped("sl.ts", "sl.dur")}) DESC LIMIT 15
            """, ctx, 15).ConfigureAwait(false);

        QueryTable longest = await RunAsync(s, $"""
            {w.Prelude}{sleeps} ORDER BY x.dur DESC LIMIT {limit}
            """, ctx).ConfigureAwait(false);
        JsonArray chains = new();
        foreach (object?[] r in longest.Rows)
        {
            long sleepStart = (long)r[0]!;
            long sleepDur = (long)r[1]!;
            JsonObject item = new()
            {
                ["ts"] = sleepStart,
                ["sleep_ms"] = Math.Round(sleepDur / 1e6, 2),
                ["state"] = TableFormat.Cell(r[2]),
                ["blocked_function"] = TableFormat.Cell(r[3]),
                ["chain"] = await ChainAsync(s, r[4] as long?, r[5] as long?, sleepStart, sleepStart + sleepDur, maxHops, ctx).ConfigureAwait(false)
            };
            chains.Add(item);
        }
        report["longest_sleeps"] = chains;

        QueryTable bounds = await RunAsync(s, $"""
            {w.Prelude}SELECT {w.Start} AS s, {w.End} AS e,
                   -- Several matches (process restarts): the one running most in the window. Only Running counts —
                   -- a dead instance's last state is open-ended (dur = -1) and would cover the whole window.
                   (SELECT t.utid FROM thread t LEFT JOIN process p USING (upid) WHERE {threads}
                    ORDER BY (SELECT IFNULL(SUM({w.Clipped("x.ts", "x.dur")}), 0) FROM thread_state x
                              WHERE x.utid = t.utid AND x.state = 'Running' AND {w.Overlaps("x.ts", "x.dur")}) DESC, t.utid
                    LIMIT 1) AS utid
            """, ctx).ConfigureAwait(false);
        if (bounds.Rows.Count > 0 && bounds.Rows[0] is [long ws, long we, long root])
        {
            bool explicitWindow = w.Description != "whole trace";
            bool want = args.ContainsKey("critical_path")
                ? Args.OptionalBool(args, "critical_path", false)
                : explicitWindow && we - ws <= 30_000_000_000L;
            if (want)
            {
                report["critical_path"] = await CriticalPathAsync(s, root, ws, we, ctx).ConfigureAwait(false);
            }
        }
        return ToolCallResult.Json(report);
    }

    // Walks waker -> waker's waker while each waker was itself woken inside the original sleep window.
    private async Task<JsonArray> ChainAsync(TraceSession s, long? wakerUtid, long? irq, long windowStart, long wakeTs, int maxHops, RequestContext ctx)
    {
        JsonArray hops = new();
        HashSet<long> seen = new();
        long? utid = wakerUtid;
        long? irqCtx = irq;
        long at = wakeTs;
        for (int hop = 1; hop <= maxHops; hop++)
        {
            if (utid is null or 0)
            {
                hops.Add(new JsonObject
                {
                    ["hop"] = hop,
                    ["waker"] = irqCtx == 1 ? "<irq>" : utid == 0 ? "<swapper / idle: timer or interrupt>" : "<unknown: enable sched_waking>",
                    ["end"] = "no thread waker"
                });
                break;
            }
            if (!seen.Add(utid.Value))
            {
                hops.Add(new JsonObject { ["hop"] = hop, ["end"] = "cycle" });
                break;
            }
            QueryTable info = await RunAsync(s, $"""
                SELECT t.tid, t.name, {ProcessSelector.NameOf("t")} AS process,
                  (SELECT x.name FROM slice x JOIN thread_track tt ON x.track_id = tt.id
                   WHERE tt.utid = t.utid AND x.ts <= {at} AND (x.dur < 0 OR x.ts + x.dur >= {at})
                   ORDER BY x.depth DESC LIMIT 1) AS doing,
                  (SELECT SUM(MIN(r.ts + r.dur, {at}) - MAX(r.ts, {windowStart})) FROM sched r
                   WHERE r.utid = t.utid AND r.ts < {at} AND r.ts + r.dur > {windowStart}) / 1e6 AS ran_ms_in_window
                FROM thread t LEFT JOIN process p USING (upid) WHERE t.utid = {utid}
                """, ctx).ConfigureAwait(false);
            JsonObject h = new() { ["hop"] = hop, ["woken_at"] = at };
            if (info.Rows.Count > 0)
            {
                object?[] i = info.Rows[0];
                h["waker_tid"] = TableFormat.Cell(i[0]);
                h["waker_thread"] = TableFormat.Cell(i[1]);
                h["waker_process"] = TableFormat.Cell(i[2]);
                h["executing"] = TableFormat.Cell(i[3], 160);
                h["ran_ms_in_window"] = i[4] is null ? 0 : Math.Round(Convert.ToDouble(i[4]), 1);
            }

            // Was the waker itself asleep and woken during our window? Then keep climbing.
            QueryTable prev = await RunAsync(s, $"""
                SELECT x.ts + x.dur AS woke, n.waker_utid, n.irq_context, x.state, x.blocked_function
                FROM thread_state x {SleepJoin}
                WHERE x.utid = {utid} AND {IsSleep} AND x.ts + x.dur <= {at} AND x.ts + x.dur > {windowStart}
                ORDER BY x.ts + x.dur DESC LIMIT 1
                """, ctx).ConfigureAwait(false);
            if (prev.Rows.Count == 0)
            {
                h["end"] = "waker was already active for the whole wait: its own work (executing) is the source";
                hops.Add(h);
                break;
            }
            object?[] p = prev.Rows[0];
            h["waker_was_sleeping_in"] = TableFormat.Cell(p[3]);
            if (p[4] is string bf)
            {
                h["waker_blocked_function"] = bf;
            }
            hops.Add(h);
            at = (long)p[0]!;
            utid = p[1] as long?;
            irqCtx = p[2] as long?;
        }
        return hops;
    }
}
