using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class LockContentionTool : TraceToolBase
{
    public LockContentionTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("lock_contention",
        """
        Java monitor (synchronized) contention from ART "monitor contention" slices (android.monitor_contention):
        aggregated by process and lock-holder method, then the worst individual contentions with blocked / blocking
        thread and methods. `via_binder_client` shows who was waiting when the blocked thread was serving a binder
        call — i.e. which app's binder call got slow because a system_server lock was held. Needs atrace `dalvik`.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder()))
            .Bool("main_thread_only", "Only contentions that blocked a main thread.")
            .Int("limit", "Rows per section (default 12).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        int limit = Args.OptionalInt(args, "limit", 12, 1, 500);
        string filter = $"{w.Overlaps("c.ts", "c.dur")}";
        if (ProcessSelector.Predicate(args) is { } proc)
        {
            filter += $" AND c.upid IN (SELECT p.upid FROM process p WHERE {proc})";
        }
        if (Args.OptionalBool(args, "main_thread_only", false))
        {
            filter += " AND c.is_blocked_thread_main";
        }
        string prelude = w.Prelude + "INCLUDE PERFETTO MODULE android.monitor_contention;\nINCLUDE PERFETTO MODULE android.binder;\n";

        JsonObject report = new() { ["window"] = w.Description };
        report["by_lock_holder"] = await SectionAsync(s, $"""
            {prelude}SELECT c.process_name, c.short_blocking_method AS holder_method, c.blocking_thread_name AS holder_thread,
                   COUNT(*) AS n, ROUND(SUM(c.dur) / 1e6, 1) AS total_ms, ROUND(MAX(c.dur) / 1e6, 2) AS max_ms,
                   SUM(c.is_blocked_thread_main) AS main_blocked, COUNT(c.binder_reply_id) AS during_binder_reply
            FROM android_monitor_contention_chain c WHERE {filter}
            GROUP BY c.upid, c.short_blocking_method ORDER BY SUM(c.dur) DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        report["findings"] = await SectionAsync(s, $"""
            {prelude}WITH c AS (SELECT * FROM android_monitor_contention_chain c WHERE {filter}),
            f AS (
              SELECT 'critical' AS severity, 'LOCK_MAIN_THREAD' AS rule, process_name, short_blocking_method AS holder_method,
                     COUNT(*) AS n, ROUND(SUM(dur) / 1e6, 1) AS total_ms
              FROM c WHERE is_blocked_thread_main GROUP BY upid, short_blocking_method HAVING COUNT(*) > 5 AND SUM(dur) > 100e6
              UNION ALL
              SELECT IIF(MAX(dur) >= 100e6, 'critical', 'warning'), 'LOCK_MAIN_LONG', process_name, short_blocking_method,
                     COUNT(*), ROUND(SUM(dur) / 1e6, 1)
              FROM c WHERE is_blocked_thread_main AND dur >= 32e6 GROUP BY upid, short_blocking_method
              UNION ALL
              -- A parked / waiting / sleeping holder keeps the monitor while it waits for something else.
              SELECT IIF(SUM(is_blocked_thread_main) > 0, 'critical', 'warning'), 'LOCK_HELD_WHILE_WAITING', process_name,
                     short_blocking_method, COUNT(*), ROUND(SUM(dur) / 1e6, 1)
              FROM c WHERE (short_blocking_method GLOB '*.park' OR short_blocking_method GLOB '*.parkNanos'
                     OR short_blocking_method GLOB '*Object.wait' OR short_blocking_method GLOB '*Thread.sleep'
                     OR short_blocking_method GLOB '*.await' OR short_blocking_method GLOB '*Future*.get')
              GROUP BY upid, short_blocking_method HAVING SUM(dur) > 16e6
              UNION ALL
              SELECT IIF(SUM(is_blocked_thread_main) > 0, 'critical', 'warning'), 'LOCK_HELD_DURING_BINDER', process_name,
                     short_blocking_method, COUNT(*), ROUND(SUM(dur) / 1e6, 1)
              FROM c WHERE short_blocking_method GLOB '*BinderProxy.transact*'
              GROUP BY upid, short_blocking_method HAVING SUM(dur) > 16e6
              UNION ALL
              SELECT 'warning', 'LOCK_PING_PONG', a.process_name,
                     a.blocking_thread_name || ' <-> ' || a.blocked_thread_name, COUNT(*), ROUND(SUM(a.dur) / 1e6, 1)
              FROM c a WHERE a.blocked_utid < a.blocking_utid
                AND EXISTS (SELECT 1 FROM c b WHERE b.blocked_utid = a.blocking_utid AND b.blocking_utid = a.blocked_utid)
              GROUP BY a.upid, a.blocked_utid, a.blocking_utid HAVING SUM(a.dur) > 16e6
              UNION ALL
              SELECT 'warning', 'LOCK_HOTSPOT', process_name, short_blocking_method, COUNT(*), ROUND(SUM(dur) / 1e6, 1)
              FROM c GROUP BY upid, short_blocking_method HAVING COUNT(*) > 20 OR COUNT(DISTINCT blocked_utid) > 5
              UNION ALL
              SELECT 'warning', 'LOCK_IN_BINDER_REPLY', process_name, short_blocking_method, COUNT(*), ROUND(SUM(dur) / 1e6, 1)
              FROM c WHERE binder_reply_id IS NOT NULL GROUP BY upid, short_blocking_method HAVING COUNT(*) > 3 AND SUM(dur) > 50e6
              UNION ALL
              SELECT 'info', 'LOCK_CHAIN', process_name, short_blocking_method, COUNT(*), ROUND(SUM(dur) / 1e6, 1)
              FROM c WHERE parent_id IS NOT NULL AND child_id IS NOT NULL GROUP BY upid, short_blocking_method)
            SELECT f.*, CASE rule
                WHEN 'LOCK_MAIN_THREAD' THEN 'worst rows for this holder; thread_state_breakdown / cpu_profile on the holder thread'
                WHEN 'LOCK_MAIN_LONG' THEN 'worst rows (blocked_method = where main waited); wakeup_chain on the holder thread'
                WHEN 'LOCK_HELD_WHILE_WAITING' THEN 'the holder waits (park/wait/sleep) inside a synchronized block: '
                    || 'wakeup_chain on the holder thread shows what it waited for; move the wait out of the lock'
                WHEN 'LOCK_HELD_DURING_BINDER' THEN 'the holder makes a binder call inside a synchronized block: '
                    || 'android_binder_txns on the holder thread at that ts shows the server and why it was slow'
                WHEN 'LOCK_PING_PONG' THEN 'both threads take the same monitor in turn (worst rows in both directions); '
                    || 'shrink the critical sections or hand work over with a queue'
                WHEN 'LOCK_HOTSPOT' THEN 'which threads wait (worst); consider splitting the lock or shortening the critical section'
                WHEN 'LOCK_IN_BINDER_REPLY' THEN 'via_binder_client in worst: the app whose binder call was delayed by this lock'
                ELSE 'follow parent/child contention: the holder was itself blocked on another lock' END AS next_evidence
            FROM f ORDER BY CASE severity WHEN 'critical' THEN 0 WHEN 'warning' THEN 1 ELSE 2 END, total_ms DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        report["worst"] = await SectionAsync(s, $"""
            {prelude}SELECT c.ts, ROUND(c.dur / 1e6, 2) AS dur_ms, c.process_name, c.blocked_thread_name AS blocked_thread,
                   c.is_blocked_thread_main AS blocked_is_main, c.short_blocked_method AS blocked_method,
                   c.blocking_thread_name AS holder_thread, c.short_blocking_method AS holder_method, c.waiter_count,
                   (SELECT b.client_process || ' / ' || b.client_thread || IIF((b.is_main_thread AND b.client_upid IN (SELECT upid FROM process WHERE name IS NOT NULL OR cmdline IS NOT NULL)), ' (main)', '')
                    FROM android_binder_txns b WHERE b.binder_reply_id = c.binder_reply_id LIMIT 1) AS via_binder_client
            FROM android_monitor_contention_chain c WHERE {filter}
            ORDER BY c.dur DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        return ToolCallResult.Json(report);
    }
}

internal sealed class AndroidAnrsTool : TraceToolBase
{
    private const int MaxDetailed = 3;

    public AndroidAnrsTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("android_anrs",
        $"""
        ANRs (android.anrs stdlib) and, for up to {MaxDetailed} of them, what the process main thread did in the
        timeout window before the ANR:
        - likely_blockers: main-thread time classified (binder wait, lock wait, IO, D-state, CPU busy, runnable
          starvation, idle) with confidence — a ranked starting point, not a verdict;
        - critical_path: which threads held up the main thread over the window (see wakeup_chain);
        - stuck_binder: a binder call still unanswered at ANR time, its destination process, that process's binder
          thread-pool occupancy (busy / total) and what the busy threads were doing (pool exhaustion chains);
        - system_check: verdict app_specific / system_freeze / system_server_freeze from the main threads of
          system_server and all apps in the window (stalled = runnable or D-state >= 50% of their time);
        - state split, slices open when the ANR fired, heaviest slices, slowest completed sync binder calls, monitor
          contention, and other ANRs within 10 s (several at once point to a system-level cause).
        The window length comes from the trace when recorded, else a per-type default (`timeout_source`).
        """,
        ProcessSelector.AddParams(new SchemaBuilder())
            .Int("limit", "Rows per detail section (default 8).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        int limit = Args.OptionalInt(args, "limit", 8, 1, 100);
        string filter = ProcessSelector.Predicate(args) is { } proc ? $"WHERE a.upid IN (SELECT p.upid FROM process p WHERE {proc})" : string.Empty;
        QueryTable anrs = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE android.anrs;
            SELECT a.ts, a.process_name, a.pid, a.upid, a.anr_type, a.subject, a.component, a.anr_dur_ms, a.default_anr_dur_ms
            FROM android_anrs a {filter} ORDER BY a.ts
            """, ctx).ConfigureAwait(false);
        JsonArray list = new();
        foreach (object?[] r in anrs.Rows)
        {
            (long timeout, string source) = Timeout(r[4] as string, r[7], r[8]);
            list.Add(new JsonArray(TableFormat.Cell(r[0]), TableFormat.Cell(r[1]), TableFormat.Cell(r[2]), TableFormat.Cell(r[4]),
                TableFormat.Cell(r[5], 240), TableFormat.Cell(r[6]), timeout, source));
        }
        JsonObject report = new()
        {
            ["anrs"] = new JsonObject
            {
                ["columns"] = new JsonArray("ts", "process_name", "pid", "anr_type", "subject", "component", "timeout_ms", "timeout_source"),
                ["rows"] = list
            }
        };
        if (anrs.Rows.Count == 0)
        {
            report["note"] = "No ANRs found (android_anrs needs the ANR events from atrace/logcat captured in the trace).";
            return ToolCallResult.Json(report);
        }

        JsonArray details = new();
        for (int i = 0; i < Math.Min(MaxDetailed, anrs.Rows.Count); i++)
        {
            details.Add(await DetailAsync(s, anrs, i, limit, ctx).ConfigureAwait(false));
        }
        report["details"] = details;
        return ToolCallResult.Json(report);
    }

    private async Task<JsonObject> DetailAsync(TraceSession s, QueryTable anrs, int i, int limit, RequestContext ctx)
    {
        object?[] r = anrs.Rows[i];
        long ts = (long)r[0]!;
        long upid = (long)r[3]!;
        (long timeoutMs, string source) = Timeout(r[4] as string, r[7], r[8]);
        TimeWindow w = TimeWindow.Of(ts - (timeoutMs * 1_000_000), ts, $"{timeoutMs} ms before ANR ({source})");
        string main = $"(SELECT t.utid FROM thread t JOIN process p USING (upid) WHERE p.upid = {upid} AND t.tid = p.pid)";
        JsonObject d = new()
        {
            ["anr_ts"] = ts,
            ["process"] = TableFormat.Cell(r[1]),
            ["type"] = TableFormat.Cell(r[4]),
            ["window"] = w.Description,
            ["other_anrs_within_10s"] = anrs.Rows.Count(o => o != r && Math.Abs((long)o[0]! - ts) <= 10_000_000_000)
        };

        QueryTable states = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE sched.states;
            SELECT sched_state_io_to_human_readable_string(x.state, x.io_wait) AS state, x.blocked_function,
                   COUNT(*) AS n, ROUND(SUM({w.Clipped("x.ts", "x.dur")}) / 1e6, 1) AS dur_ms
            FROM thread_state x WHERE x.utid IN {main} AND {w.Overlaps("x.ts", "x.dur")}
            GROUP BY 1, 2 ORDER BY dur_ms DESC
            """, ctx).ConfigureAwait(false);
        QueryTable binder = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE android.binder;
            SELECT b.client_ts, ROUND(b.client_dur / 1e6, 1) AS client_ms, b.aidl_name, b.server_process, b.server_thread,
                   ROUND(b.server_dur / 1e6, 1) AS server_ms
            FROM android_binder_txns b WHERE b.is_sync AND b.client_utid IN {main} AND {w.Overlaps("b.client_ts", "b.client_dur")}
            ORDER BY b.client_dur DESC
            """, ctx, limit).ConfigureAwait(false);
        QueryTable locks = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE android.monitor_contention;
            SELECT c.ts, ROUND(c.dur / 1e6, 1) AS dur_ms, c.blocking_thread_name AS holder_thread,
                   c.short_blocking_method AS holder_method, c.short_blocked_method AS blocked_method
            FROM android_monitor_contention c WHERE c.blocked_utid IN {main} AND {w.Overlaps("c.ts", "c.dur")}
            ORDER BY c.dur DESC
            """, ctx, limit).ConfigureAwait(false);
        JsonArray stuck = await StuckBinderAsync(s, main, ts, ctx).ConfigureAwait(false);

        d["likely_blockers"] = Blockers(states, binder, locks, stuck, timeoutMs);
        d["system_check"] = await SystemCheckAsync(s, w, ctx).ConfigureAwait(false);
        if (stuck.Count > 0)
        {
            d["stuck_binder"] = stuck;
        }
        QueryTable mainUtid = await RunAsync(s, $"SELECT utid FROM {main} LIMIT 1", ctx).ConfigureAwait(false);
        if (mainUtid.Rows.Count > 0 && mainUtid.Rows[0][0] is long utid)
        {
            // Long timeouts (service / broadcast: 20-60 s): the last 10 s is where the main thread is stuck.
            long cpStart = timeoutMs <= 30_000 ? ts - (timeoutMs * 1_000_000) : ts - 10_000_000_000L;
            d["critical_path"] = await CriticalPathAsync(s, utid, cpStart, ts, ctx, stopAtOpenSleep: true).ConfigureAwait(false);
            d["critical_path"]!["window"] = $"{(ts - cpStart) / 1_000_000} ms before ANR";
        }
        d["main_thread_states"] = TableFormat.ToJson(states, limit);
        d["open_at_anr"] = await SectionAsync(s, $"""
            SELECT x.ts, ROUND((IIF(x.dur < 0, {ts}, MIN(x.ts + x.dur, {ts})) - x.ts) / 1e6, 1) AS open_for_ms,
                   x.depth, x.name
            FROM slice x JOIN thread_track tt ON x.track_id = tt.id
            WHERE tt.utid IN {main} AND x.ts <= {ts} AND (x.dur < 0 OR x.ts + x.dur >= {ts})
            ORDER BY x.depth LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        d["main_thread_slices"] = await SectionAsync(s, $"""
            SELECT x.name, COUNT(*) AS n, ROUND(SUM({w.Clipped("x.ts", "x.dur")}) / 1e6, 1) AS total_ms,
                   ROUND(MAX({w.Clipped("x.ts", "x.dur")}) / 1e6, 1) AS max_ms, MIN(x.depth) AS min_depth
            FROM slice x JOIN thread_track tt ON x.track_id = tt.id
            WHERE tt.utid IN {main} AND {w.Overlaps("x.ts", "x.dur")}
            GROUP BY x.name ORDER BY total_ms DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        d["main_thread_binder"] = TableFormat.ToJson(binder, limit);
        d["main_thread_lock_contention"] = TableFormat.ToJson(locks, limit);
        return d;
    }

    // Main-thread binder calls still unanswered at ANR time (no reply, so android_binder_txns misses them).
    private async Task<JsonArray> StuckBinderAsync(TraceSession s, string main, long anrTs, RequestContext ctx)
    {
        QueryTable open = await RunAsync(s, $"""
            SELECT x.ts, ROUND(({anrTs} - x.ts) / 1e6, 1) AS open_ms,
                   EXTRACT_ARG(x.arg_set_id, 'destination process') AS dest_pid,
                   EXTRACT_ARG(x.arg_set_id, 'destination thread') AS dest_tid
            FROM slice x JOIN thread_track tt ON x.track_id = tt.id
            WHERE tt.utid IN {main} AND x.name = 'binder transaction' AND x.ts <= {anrTs} AND (x.dur < 0 OR x.ts + x.dur >= {anrTs})
            ORDER BY x.ts LIMIT 2
            """, ctx).ConfigureAwait(false);
        JsonArray result = new();
        foreach (object?[] o in open.Rows)
        {
            long txnTs = (long)o[0]!;
            JsonObject item = new() { ["txn_ts"] = txnTs, ["open_ms"] = TableFormat.Cell(o[1]), ["dest_pid"] = TableFormat.Cell(o[2]) };
            if (o[2] is long destPid)
            {
                string BusyAt(long t) => $"""
                    SELECT DISTINCT t.utid FROM thread t JOIN process p USING (upid)
                    JOIN thread_track rt ON rt.utid = t.utid JOIN slice r ON r.track_id = rt.id
                    WHERE p.pid = {destPid} AND LOWER(t.name) GLOB 'binder*' AND r.name = 'binder reply' AND r.depth = 0
                      AND r.ts <= {t} AND (r.dur < 0 OR r.ts + r.dur >= {t})
                    """;
                QueryTable pool = await RunAsync(s, $"""
                    SELECT MAX(p.name) AS process, COUNT(DISTINCT t.utid) AS binder_threads,
                           (SELECT COUNT(*) FROM ({BusyAt(txnTs)})) AS busy_at_call
                    FROM thread t JOIN process p USING (upid) WHERE p.pid = {destPid} AND LOWER(t.name) GLOB 'binder*'
                    """, ctx).ConfigureAwait(false);
                if (pool.Rows.Count > 0)
                {
                    item["dest_process"] = TableFormat.Cell(pool.Rows[0][0]);
                    item["binder_threads"] = TableFormat.Cell(pool.Rows[0][1]);
                    item["busy_at_call"] = TableFormat.Cell(pool.Rows[0][2]);
                    if (pool.Rows[0][1] is long total && pool.Rows[0][2] is long busy && total > 0 && busy >= total)
                    {
                        item["finding"] = "Destination binder thread pool was exhausted when the call was made; nothing could serve it.";
                    }
                }
                string threads = o[3] is long destTid
                    ? $"SELECT t.utid FROM thread t WHERE t.tid = {destTid}"
                    : BusyAt(txnTs);
                item["server_threads_innermost_open_slice"] = TableFormat.ToJson(await RunAsync(s, $"""
                    WITH th AS ({threads}),
                    o AS (SELECT x.name, ROW_NUMBER() OVER (PARTITION BY x.track_id ORDER BY x.depth DESC) AS rn
                          FROM slice x JOIN thread_track tt ON x.track_id = tt.id
                          WHERE tt.utid IN th AND x.ts <= {anrTs} AND (x.dur < 0 OR x.ts + x.dur >= {anrTs}))
                    SELECT name, COUNT(*) AS threads FROM o WHERE rn = 1 GROUP BY name ORDER BY threads DESC LIMIT 6
                    """, ctx).ConfigureAwait(false), 6);
                item["server_threads_state_at_anr"] = TableFormat.ToJson(await RunAsync(s, $"""
                    INCLUDE PERFETTO MODULE sched.states;
                    WITH th AS ({threads})
                    SELECT sched_state_io_to_human_readable_string(x.state, x.io_wait) AS state, x.blocked_function, COUNT(*) AS threads
                    FROM thread_state x WHERE x.utid IN th AND x.ts <= {anrTs} AND (x.dur < 0 OR x.ts + x.dur > {anrTs})
                    GROUP BY 1, 2 ORDER BY threads DESC
                    """, ctx).ConfigureAwait(false), 8);
            }
            result.Add(item);
        }
        return result;
    }

    // Whole device stuck or just this app? Main threads alive >= 90% of the window: demanding = running + stalled
    // >= 10%, stalled = runnable / D-state >= 50% of the alive time.
    private async Task<JsonObject> SystemCheckAsync(TraceSession s, TimeWindow w, RequestContext ctx)
    {
        string clipped = w.Clipped("x.ts", "x.dur");
        QueryTable t = await RunAsync(s, $"""
            WITH mains AS (
              SELECT t.utid, p.name, p.pid FROM thread t JOIN process p USING (upid)
              WHERE t.tid = p.pid AND (p.name = 'system_server' OR (p.uid % 100000) BETWEEN 10000 AND 89999)),
            st AS (
              SELECT m.name, m.pid, SUM({clipped}) AS alive,
                     SUM(IIF(x.state = 'Running', {clipped}, 0)) AS run,
                     SUM(IIF(x.state IN ('R', 'R+') OR x.state GLOB 'D*', {clipped}, 0)) AS stalled
              FROM thread_state x JOIN mains m USING (utid)
              WHERE {w.Overlaps("x.ts", "x.dur")} GROUP BY m.utid)
            SELECT name, pid, ROUND(alive / 1e6, 1) AS alive_ms, ROUND(run / 1e6, 1) AS running_ms,
                   ROUND(stalled / 1e6, 1) AS stalled_ms,
                   alive >= 0.9 * ({w.End} - {w.Start}) AS evaluable,
                   (run + stalled) >= 0.1 * alive AS demanding,
                   stalled >= 0.5 * alive AS is_stalled
            FROM st ORDER BY stalled DESC
            """, ctx).ConfigureAwait(false);
        var rows = t.Rows.Select(r => new
        {
            Name = r[0] as string,
            Evaluable = Convert.ToInt64(r[5] ?? 0) == 1,
            Demanding = Convert.ToInt64(r[6] ?? 0) == 1,
            Stalled = Convert.ToInt64(r[7] ?? 0) == 1,
            Row = r
        }).ToList();
        var ss = rows.FirstOrDefault(r => r.Name == "system_server");
        var apps = rows.Where(r => r.Name != "system_server" && r.Evaluable).ToList();
        int demanding = apps.Count(a => a.Demanding);
        int stalled = apps.Count(a => a.Demanding && a.Stalled);
        string verdict = ss is { Evaluable: true, Stalled: true } ? "system_server_freeze"
            : demanding >= 3 && stalled * 2 > demanding ? "system_freeze"
            : "app_specific";
        JsonArray stalledApps = new();
        foreach (var a in rows.Where(r => r.Stalled && r.Evaluable).Take(5))
        {
            stalledApps.Add(new JsonArray(TableFormat.Cell(a.Row[0]), TableFormat.Cell(a.Row[1]), TableFormat.Cell(a.Row[2]),
                TableFormat.Cell(a.Row[4])));
        }
        return new JsonObject
        {
            ["verdict"] = verdict,
            ["system_server_main"] = ss is null ? null : new JsonObject
            {
                ["running_ms"] = TableFormat.Cell(ss.Row[3]),
                ["stalled_ms"] = TableFormat.Cell(ss.Row[4]),
                ["stalled"] = ss.Stalled
            },
            ["apps_evaluated"] = apps.Count,
            ["apps_demanding"] = demanding,
            ["apps_stalled"] = stalled,
            ["stalled_main_threads"] = new JsonObject
            {
                ["columns"] = new JsonArray("process", "pid", "alive_ms", "stalled_ms"),
                ["rows"] = stalledApps
            }
        };
    }

    // Ranked hypotheses from the main-thread time split; thresholds follow common ANR triage practice.
    private static JsonArray Blockers(QueryTable states, QueryTable binder, QueryTable locks, JsonArray stuck, long windowMs)
    {
        double Sum(Func<string, bool> pick) => states.Rows
            .Where(r => pick((string?)r[0] ?? string.Empty)).Sum(r => Convert.ToDouble(r[3]));
        double running = Sum(st => st == "Running");
        double runnable = Sum(st => st.StartsWith("Runnable", StringComparison.Ordinal));
        double sleeping = Sum(st => st == "Sleeping");
        double dIo = Sum(st => st.Contains("(IO)", StringComparison.Ordinal));
        double dOther = Sum(st => st.StartsWith("Uninterruptible", StringComparison.Ordinal) && !st.Contains("(IO)", StringComparison.Ordinal));
        double binderMs = binder.Rows.Sum(r => Convert.ToDouble(r[1]));
        double lockMs = locks.Rows.Sum(r => Convert.ToDouble(r[1]));
        double stuckMs = stuck.Sum(n => n?["open_ms"]?.GetValue<double>() ?? 0);

        List<(string Cause, double Ms, string Confidence, string Note)> c = new();
        void Add(string cause, double ms, double high, double medium, string note)
        {
            if (ms >= medium)
            {
                c.Add((cause, ms, ms >= high ? "high" : "medium", note));
            }
        }
        Add("binder_wait", Math.Max(binderMs, stuckMs), 500, 250, stuckMs > 0
            ? "A sync binder call was still unanswered at ANR time; see stuck_binder."
            : "Slow completed sync binder calls; check the server side in main_thread_binder.");
        Add("lock_wait", lockMs, 500, 300, "Monitor contention on the main thread; see main_thread_lock_contention.");
        Add("io_wait", dIo, 500, 300, "Main thread in IO D-state (file / page-cache / swap reads).");
        Add("uninterruptible_wait", dOther, 1000, 500, "D-state without IO evidence (kernel locks, reclaim, driver waits); low certainty.");
        Add("cpu_busy", running, 0.9 * windowMs, 0.7 * windowMs, "Main thread kept running: heavy work on the main thread itself.");
        Add("cpu_starvation", runnable, 0.3 * windowMs, 0.15 * windowMs, "Main thread runnable but not scheduled: CPU contention / priority.");
        if (sleeping >= 0.9 * windowMs && stuckMs == 0 && binderMs < 250 && lockMs < 300)
        {
            c.Add(("idle_main_thread", sleeping, "medium",
                "Main thread mostly idle: the triggering message or broadcast may never have reached it (system_server queue, process start); inspect system_server."));
        }
        JsonArray arr = new();
        foreach ((string cause, double ms, string conf, string note) in c.OrderByDescending(x => x.Ms))
        {
            (string proves, string next) = Boundary(cause);
            arr.Add(new JsonObject
            {
                ["cause"] = cause,
                ["ms"] = Math.Round(ms, 1),
                ["pct_of_window"] = windowMs > 0 ? Math.Round(100 * ms / windowMs, 1) : null,
                ["confidence"] = conf,
                ["note"] = note,
                ["proves"] = proves,
                ["next_evidence"] = next
            });
        }
        return arr;
    }

    // Keeps measured facts apart from root-cause claims: what the time split shows, and what to check next.
    private static (string Proves, string Next) Boundary(string cause) => cause switch
    {
        "binder_wait" => ("The main thread waited on a sync binder call; not why the server was slow.",
            "stuck_binder / android_binder_txns (slowest_breakdown) for the server side; wakeup_chain or thread_state_breakdown on the server thread."),
        "lock_wait" => ("The main thread waited for a Java monitor; not what the holder was doing.",
            "lock_contention (holder thread / method), then thread_state_breakdown or cpu_profile on the holder in that window."),
        "io_wait" => ("The main thread was in IO D-state; not which file or why storage was slow.",
            "io_summary (blocked functions, block-device latency, page-cache readers) and memory_summary (swap / reclaim)."),
        "uninterruptible_wait" => ("The main thread was in non-IO D-state; the kernel function alone does not identify the owner.",
            "thread_state_breakdown blocked_function, memory_summary (direct reclaim), wakeup_chain to see who released it."),
        "cpu_busy" => ("The main thread itself was running; the code responsible is not identified by states.",
            "slice_hotspots / main_thread_blocking_calls in the window; cpu_profile if linux.perf samples exist."),
        "cpu_starvation" => ("The main thread was runnable but not scheduled; not which threads took the CPU.",
            "cpu_top_threads and cpu_frequency in the window; list_threads_in_process for the main thread priority."),
        "idle_main_thread" => ("The main thread was idle; the app may never have received the work.",
            "android_logs (ActivityManager / BroadcastQueue / InputDispatcher), main_thread_blocking_calls and lock_contention for system_server."),
        _ => ("Time-split heuristic only.", "Inspect main_thread_states and open_at_anr."),
    };

    // AOSP default timeouts per ANR type, used when the trace does not record the actual one.
    private static (long Ms, string Source) Timeout(string? type, object? anrDur, object? defaultDur)
    {
        if (anrDur is long a && a > 0)
        {
            return (a, "trace");
        }
        if (defaultDur is long d && d > 0)
        {
            return (d, "stdlib_default");
        }
        string t = type ?? string.Empty;
        long ms = t.Contains("INPUT") ? 5000
            : t.Contains("SHORT_FGS") ? 180_000
            : t.Contains("FOREGROUND_SERVICE") ? 30_000
            : t.Contains("SERVICE") ? 20_000
            : t.Contains("BROADCAST") ? 10_000
            : t.Contains("BIND_APPLICATION") ? 15_000
            : t.Contains("JOB") ? 8000
            : t.Contains("PROVIDER") ? 20_000
            : 5000;
        return (ms, "type_fallback");
    }
}
