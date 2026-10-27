using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed partial class CompareTracesTool : TraceToolBase
{
    public CompareTracesTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("compare_traces",
        """
        Diff two loaded traces (e.g. base vs fix) with identical metrics: A value, B value, delta and delta %.
        With `process_name` the comparison focuses on that app (matched by name; pids differ between traces):
        its startup (dur / TTID / TTFD), CPU time, main-thread state split, sync binder time, GC, peak RSS, frame
        jank, plus per-thread CPU and per-slice totals matched by name and ranked by the largest change.
        System metrics (total CPU and scheduling rate per second, MemAvailable min, LMK kills, swap-out, kswapd CPU)
        are always included; without `process_name`, process_cpu_pct ranks the processes behind the CPU difference
        (% of one CPU, so traces of different length or from two devices compare).
        align=startup uses each trace's Nth startup of the focus package as the window (best for launch A/B) and adds
        critical_path_ms: the main thread's critical path per thread on both sides (who the launch waited on);
        align=trace compares whole traces (normalize by duration yourself if they differ in length).
        """,
        new SchemaBuilder()
            .Str("trace_a", "Baseline trace_id (from load_trace).", isRequired: true)
            .Str("trace_b", "Candidate trace_id.", isRequired: true)
            .Str("process_name", "Focus process / package name (exact).")
            .Enum("align", "Window per trace: whole trace (default) or the focus package's startup.", "trace", "startup")
            .Int("startup_index", "With align=startup: which startup of the package to use, 1-based (default 1).")
            .Int("limit", "Rows in the thread / slice diff sections (default 12)."));

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession a = Traces.Resolve(Args.RequireString(args, "trace_a"));
        TraceSession b = Traces.Resolve(Args.RequireString(args, "trace_b"));
        string? focus = Args.OptionalString(args, "process_name");
        bool startupAlign = Args.OptionalString(args, "align") == "startup";
        int index = Args.OptionalInt(args, "startup_index", 1, 1, 1000);
        int limit = Args.OptionalInt(args, "limit", 12, 1, 200);
        if (startupAlign && string.IsNullOrWhiteSpace(focus))
        {
            throw new McpInvalidParamsException("align=startup needs process_name (the package to align on)");
        }

        TimeWindow wa = await WindowAsync(a, focus, startupAlign, index, ctx).ConfigureAwait(false);
        TimeWindow wb = await WindowAsync(b, focus, startupAlign, index, ctx).ConfigureAwait(false);

        Dictionary<string, double?> ma = await MetricsAsync(a, wa, focus, ctx).ConfigureAwait(false);
        Dictionary<string, double?> mb = await MetricsAsync(b, wb, focus, ctx).ConfigureAwait(false);

        JsonObject report = new()
        {
            ["a"] = new JsonObject { ["trace_id"] = a.Id, ["file"] = a.FileName, ["window"] = wa.Description },
            ["b"] = new JsonObject { ["trace_id"] = b.Id, ["file"] = b.FileName, ["window"] = wb.Description },
            ["metrics"] = DiffTable(ma.Keys.Union(mb.Keys).ToList(), ma, mb, "metric")
        };
        if (!string.IsNullOrWhiteSpace(focus))
        {
            string threadSql(TimeWindow w) => $"""
                SELECT t.name AS key, SUM({w.Clipped("x.ts", "x.dur")}) / 1e6 AS v
                FROM sched x JOIN thread t USING (utid) JOIN process p USING (upid)
                WHERE {FocusProc(focus)} AND {w.Overlaps("x.ts", "x.dur")} GROUP BY t.name
                """;
            report["thread_cpu_ms"] = await KeyedDiffAsync(a, b, threadSql(wa), threadSql(wb), limit, ctx).ConfigureAwait(false);

            string sliceSql(TimeWindow w) => $"""
                INCLUDE PERFETTO MODULE slices.with_context;
                SELECT x.name AS key, SUM(x.dur) / 1e6 AS v
                FROM thread_slice x JOIN process p USING (upid)
                WHERE {FocusProc(focus)} AND x.is_main_thread AND (p.name IS NOT NULL OR p.cmdline IS NOT NULL) AND x.dur > 0 AND x.ts >= {w.Start} AND x.ts < {w.End}
                GROUP BY x.name
                """;
            report["main_thread_slices_ms"] = await KeyedDiffAsync(a, b, sliceSql(wa), sliceSql(wb), limit, ctx).ConfigureAwait(false);
        }
        else
        {
            // Processes behind the CPU difference, in % of one CPU so traces of different length compare.
            // Summed per utid first: the owner-name expression is then evaluated once per thread, not per sched row.
            string procSql(TimeWindow w) => $"""
                WITH per AS (SELECT x.utid, SUM({w.Clipped("x.ts", "x.dur")}) AS d FROM sched x
                             WHERE x.utid != 0 AND {w.Overlaps("x.ts", "x.dur")} GROUP BY x.utid)
                SELECT {ProcessSelector.NameOf("t")} AS key, SUM(per.d) * 100.0 / ({w.End} - {w.Start}) AS v
                FROM per JOIN thread t USING (utid) LEFT JOIN process p USING (upid) GROUP BY 1
                """;
            // Floor 0.1 percentage point of a CPU (30 ms in 30 s): in idle traces whole processes are below 1 %.
            report["process_cpu_pct"] = await KeyedDiffAsync(a, b, procSql(wa), procSql(wb), limit, ctx, floor: 0.1)
                .ConfigureAwait(false);
        }
        if (startupAlign)
        {
            // Critical-path time per thread on each side: a thread dropping off the path is invisible in CPU alone.
            Dictionary<string, double?> ca = await CriticalPathMsAsync(a, wa, focus!, ctx).ConfigureAwait(false);
            Dictionary<string, double?> cb = await CriticalPathMsAsync(b, wb, focus!, ctx).ConfigureAwait(false);
            foreach (string k in ca.Keys.Union(cb.Keys).ToList())
            {
                ca[k] = ca.GetValueOrDefault(k) ?? 0;
                cb[k] = cb.GetValueOrDefault(k) ?? 0;
            }
            List<string> keys = ca.Keys
                .OrderByDescending(k => Math.Abs((cb[k] ?? 0) - (ca[k] ?? 0)))
                .Take(limit).ToList();
            report["critical_path_ms"] = DiffTable(keys, ca, cb, "thread / process");
        }
        return ToolCallResult.Json(report);
    }

    // On-path milliseconds per "thread / process" (digits normalized) for the main thread of the startup that opens `w`.
    private async Task<Dictionary<string, double?>> CriticalPathMsAsync(TraceSession s, TimeWindow w, string package, RequestContext ctx)
    {
        Dictionary<string, double?> d = new();
        long ws = long.Parse(w.Start, System.Globalization.CultureInfo.InvariantCulture);
        long we = long.Parse(w.End, System.Globalization.CultureInfo.InvariantCulture);
        QueryTable root = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            SELECT st.utid FROM android_startups x JOIN android_startup_threads st USING (startup_id)
            WHERE st.is_main_thread AND x.package = {SqlText.Literal(package)} AND x.ts = {ws} LIMIT 1
            """, ctx).ConfigureAwait(false);
        if (root.Rows.Count == 0 || root.Rows[0][0] is not long utid)
        {
            return d;
        }
        JsonObject cp = await CriticalPathAsync(s, utid, ws, we, ctx).ConfigureAwait(false);
        if (cp["by_thread"]?["rows"] is not JsonArray rows)
        {
            return d;
        }
        foreach (JsonArray row in rows.OfType<JsonArray>())
        {
            string thread = row[0]?.GetValue<string>() ?? "?";
            string process = row[2] is JsonValue pv && pv.TryGetValue(out string? pn) ? pn : "?";
            string key = NormalizeName(row[8] is JsonValue rv && rv.TryGetValue(out long isRoot) && isRoot == 1
                ? $"{thread} (main)" : $"{thread} / {System.IO.Path.GetFileName(process)}");
            double ms = row[3] is JsonValue mv && mv.TryGetValue(out double v) ? v : 0;
            d[key] = (d.GetValueOrDefault(key) ?? 0) + ms;
        }
        return d;
    }

    private static string FocusProc(string name) =>
        $"p.upid IN (SELECT p.upid FROM process p WHERE p.name = {SqlText.Literal(name)} OR p.cmdline = {SqlText.Literal(name)} " +
        $"UNION SELECT ps.upid FROM process ps WHERE ps.name IS NULL AND ps.cmdline IS NULL AND ps.pid IN " +
        $"(SELECT st.tid FROM thread st JOIN process sp ON sp.upid = st.upid WHERE sp.name = {SqlText.Literal(name)}))";

    private async Task<TimeWindow> WindowAsync(TraceSession s, string? focus, bool startup, int index, RequestContext ctx)
    {
        if (!startup)
        {
            QueryTable b = await RunAsync(s, "SELECT start_ts, end_ts FROM trace_bounds", ctx).ConfigureAwait(false);
            return TimeWindow.Of((long)b.Rows[0][0]!, (long)b.Rows[0][1]!, "whole trace");
        }
        QueryTable t = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            SELECT ts, ts + dur, startup_type FROM android_startups WHERE package = {SqlText.Literal(focus!)}
            ORDER BY ts LIMIT 1 OFFSET {index - 1}
            """, ctx).ConfigureAwait(false);
        if (t.Rows.Count == 0)
        {
            throw new ToolException($"Trace {s.Id} has no startup #{index} of {focus}. Check android_startups.");
        }
        return TimeWindow.Of((long)t.Rows[0][0]!, (long)t.Rows[0][1]!, $"startup #{index} of {focus} ({t.Rows[0][2]})");
    }

    private async Task<Dictionary<string, double?>> MetricsAsync(TraceSession s, TimeWindow w, string? focus, RequestContext ctx)
    {
        Dictionary<string, double?> m = new();
        await AddAsync(m, s, $"""
            SELECT ({w.End} - {w.Start}) / 1e6 AS "window_ms",
              (SELECT MIN(c.value) FROM counter c JOIN track tr ON c.track_id = tr.id
               WHERE tr.type = 'meminfo' AND tr.name = 'MemAvailable' AND c.ts BETWEEN {w.Start} AND {w.End}) / 1048576.0 AS "system.mem_available_min_mb",
              (SELECT SUM({w.Clipped("x.ts", "x.dur")}) FROM sched x JOIN thread t USING (utid)
               WHERE t.name GLOB 'kswapd*' AND {w.Overlaps("x.ts", "x.dur")}) / 1e6 AS "system.kswapd_cpu_ms",
              (SELECT MAX(c.value) - MIN(c.value) FROM counter c JOIN track tr ON c.track_id = tr.id
               WHERE tr.type = 'vmstat' AND tr.name = 'pswpout' AND c.ts BETWEEN {w.Start} AND {w.End}) AS "system.swap_out_pages",
              -- Normalized by the window so traces of different length compare (idle / power A-B, two devices).
              (SELECT SUM({w.Clipped("x.ts", "x.dur")}) FROM sched x WHERE x.utid != 0 AND {w.Overlaps("x.ts", "x.dur")})
                * 100.0 / ({w.End} - {w.Start}) AS "system.cpu_pct",
              (SELECT COUNT(*) FROM sched x WHERE x.utid != 0 AND x.ts BETWEEN {w.Start} AND {w.End})
                * 1e9 / ({w.End} - {w.Start}) AS "system.sched_slices_per_s"
            """, ctx).ConfigureAwait(false);
        await AddAsync(m, s, $"""
            INCLUDE PERFETTO MODULE android.memory.lmk;
            SELECT COUNT(*) AS "system.lmk_kills" FROM android_lmk_events WHERE ts BETWEEN {w.Start} AND {w.End}
            """, ctx).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(focus))
        {
            return m;
        }

        string lit = SqlText.Literal(focus);
        string proc = FocusProc(focus);
        await AddAsync(m, s, $"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            INCLUDE PERFETTO MODULE android.startup.time_to_display;
            SELECT COUNT(*) AS "startup.count",
                   (SELECT s.dur FROM android_startups s WHERE s.package = {lit} AND s.ts >= {w.Start} ORDER BY s.ts LIMIT 1) / 1e6 AS "startup.dur_ms",
                   (SELECT d.time_to_initial_display FROM android_startups s JOIN android_startup_time_to_display d USING (startup_id)
                    WHERE s.package = {lit} AND s.ts >= {w.Start} ORDER BY s.ts LIMIT 1) / 1e6 AS "startup.ttid_ms",
                   (SELECT d.time_to_full_display FROM android_startups s JOIN android_startup_time_to_display d USING (startup_id)
                    WHERE s.package = {lit} AND s.ts >= {w.Start} ORDER BY s.ts LIMIT 1) / 1e6 AS "startup.ttfd_ms"
            FROM android_startups WHERE package = {lit} AND ts BETWEEN {w.Start} AND {w.End}
            """, ctx).ConfigureAwait(false);
        await AddAsync(m, s, $"""
            SELECT SUM({w.Clipped("x.ts", "x.dur")}) / 1e6 AS "app.cpu_ms", COUNT(DISTINCT t.tid) AS "app.threads_on_cpu"
            FROM sched x JOIN thread t USING (utid) JOIN process p USING (upid)
            WHERE {proc} AND {w.Overlaps("x.ts", "x.dur")}
            """, ctx).ConfigureAwait(false);

        QueryTable states = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE sched.states;
            SELECT 'main.' || sched_state_io_to_human_readable_string(x.state, x.io_wait) || '_ms' AS key,
                   SUM({w.Clipped("x.ts", "x.dur")}) / 1e6 AS v
            FROM thread_state x JOIN thread t USING (utid) JOIN process p USING (upid)
            WHERE (p.name = {lit} OR p.cmdline = {lit}) AND t.tid = p.pid AND {w.Overlaps("x.ts", "x.dur")}
              -- An earlier, dead instance of the process ends in an open-ended Z / X state that would cover the window.
              AND x.state NOT IN ('Z', 'X', 'x')
            GROUP BY 1
            """, ctx).ConfigureAwait(false);
        foreach (object?[] r in states.Rows)
        {
            m[(string)r[0]!] = Convert.ToDouble(r[1]);
        }

        await AddAsync(m, s, $"""
            INCLUDE PERFETTO MODULE android.binder;
            SELECT COUNT(*) AS "app.sync_binder_calls", SUM(b.client_dur) / 1e6 AS "app.sync_binder_ms",
                   SUM(IIF((b.is_main_thread AND b.client_upid IN (SELECT upid FROM process WHERE name IS NOT NULL OR cmdline IS NOT NULL)), b.client_dur, 0)) / 1e6 AS "main.sync_binder_ms"
            FROM android_binder_txns b
            WHERE b.is_sync AND b.client_upid IN (SELECT p.upid FROM process p WHERE {proc})
              AND b.client_ts >= {w.Start} AND b.client_ts < {w.End}
            """, ctx).ConfigureAwait(false);
        await AddAsync(m, s, $"""
            INCLUDE PERFETTO MODULE android.garbage_collection;
            SELECT COUNT(*) AS "app.gc_count", SUM(g.gc_dur) / 1e6 AS "app.gc_ms"
            FROM android_garbage_collection_events g
            WHERE g.upid IN (SELECT p.upid FROM process p WHERE {proc}) AND {w.Overlaps("g.gc_ts", "g.gc_dur")}
            """, ctx).ConfigureAwait(false);
        await AddAsync(m, s, $"""
            WITH {MemorySummaryTool.MemCountersCte($"t.upid IN (SELECT p.upid FROM process p WHERE {proc})", w, "'mem.rss', 'mem.swap'")}
            SELECT MAX(IIF(name = 'mem.rss', value, NULL)) / 1048576.0 AS "app.peak_rss_mb",
                   MAX(IIF(name = 'mem.swap', value, NULL)) / 1048576.0 AS "app.peak_swap_mb"
            FROM mc
            """, ctx).ConfigureAwait(false);
        await AddAsync(m, s, $"""
            SELECT COUNT(*) AS "frames.count", SUM(a.jank_type != 'None') AS "frames.janky",
                   100.0 * SUM(a.jank_type != 'None') / NULLIF(COUNT(*), 0) AS "frames.jank_pct",
                   100.0 * SUM(a.present_type IN ('Late Present', 'Dropped Frame')) / NULLIF(COUNT(*), 0) AS "frames.visible_jank_pct",
                   MAX(a.dur) / 1e6 AS "frames.max_ms"
            FROM actual_frame_timeline_slice a JOIN process p USING (upid)
            WHERE {proc} AND {w.Overlaps("a.ts", "a.dur")}
            """, ctx).ConfigureAwait(false);
        return m;
    }

    // Single-row query; every column becomes a metric. SQL errors (missing data source) skip silently.
    private async Task AddAsync(Dictionary<string, double?> m, TraceSession s, string sql, RequestContext ctx)
    {
        QueryTable t;
        try
        {
            t = await RunAsync(s, sql, ctx).ConfigureAwait(false);
        }
        catch (SqlQueryException)
        {
            return;
        }
        if (t.Rows.Count == 0)
        {
            return;
        }
        for (int i = 0; i < t.Columns.Count; i++)
        {
            m[t.Columns[i]] = t.Rows[0][i] is null ? null : Convert.ToDouble(t.Rows[0][i]);
        }
    }

    private async Task<JsonObject> KeyedDiffAsync(TraceSession a, TraceSession b, string sqlA, string sqlB, int limit, RequestContext ctx,
        double? floor = null)
    {
        Dictionary<string, double?> va = await KeyedAsync(a, sqlA, ctx).ConfigureAwait(false);
        Dictionary<string, double?> vb = await KeyedAsync(b, sqlB, ctx).ConfigureAwait(false);
        List<string> keys = va.Keys.Union(vb.Keys)
            .OrderByDescending(k => Math.Abs((vb.GetValueOrDefault(k) ?? 0) - (va.GetValueOrDefault(k) ?? 0)))
            .Take(limit).ToList();
        return DiffTable(keys, va, vb, "name", floor);
    }

    private async Task<Dictionary<string, double?>> KeyedAsync(TraceSession s, string sql, RequestContext ctx)
    {
        QueryTable t = await RunAsync(s, sql, ctx).ConfigureAwait(false);
        Dictionary<string, double?> d = new();
        foreach (object?[] r in t.Rows)
        {
            string key = NormalizeName(r[0]?.ToString() ?? "(null)");
            double v = r[1] is null ? 0 : Convert.ToDouble(r[1]);
            d[key] = (d.GetValueOrDefault(key) ?? 0) + v;
        }
        return d;
    }

    // Normalize run-specific numbers (pids, frame numbers, binder pool indexes) so names match across runs.
    internal static string NormalizeName(string name) => Digits().Replace(BinderThread().Replace(name, "binder:N_N"), "N");

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+")]
    private static partial System.Text.RegularExpressions.Regex Digits();

    [System.Text.RegularExpressions.GeneratedRegex(@"binder:\d+_[0-9A-Fa-f]+")]
    private static partial System.Text.RegularExpressions.Regex BinderThread();

    private static JsonObject DiffTable(IEnumerable<string> keys, Dictionary<string, double?> a, Dictionary<string, double?> b, string keyName,
        double? floor = null)
    {
        JsonArray rows = new();
        foreach (string k in keys)
        {
            double? x = a.GetValueOrDefault(k);
            double? y = b.GetValueOrDefault(k);
            if (x is null && y is null)
            {
                continue;
            }
            double? delta = x is not null && y is not null ? y - x : null;
            double? pct = delta is not null && x is not null && Math.Abs(x.Value) > 1e-9 ? 100 * delta / Math.Abs(x.Value) : null;
            rows.Add(new JsonArray(k, Round(x), Round(y), Round(delta), Round(pct), Verdict(k, delta, pct, floor)));
        }
        return new JsonObject
        {
            ["columns"] = new JsonArray(keyName, "a", "b", "delta", "delta_pct", "verdict"),
            ["rows"] = rows
        };
    }

    private static JsonValue? Round(double? v) => v is null ? null : JsonValue.Create(Math.Round(v.Value, 2));

    // Context metrics: a change is informative but neither better nor worse.
    private static readonly HashSet<string> Neutral = new(StringComparer.Ordinal)
    {
        "window_ms", "startup.count", "frames.count", "app.threads_on_cpu",
        // Idle or waiting on binder / locks, and it scales with the window (startup-aligned windows differ): no direction.
        "main.Sleeping_ms"
    };

    // A change counts past an absolute floor for its unit AND 5 % relative (percentages: floor only); lower is
    // better except MemAvailable. Thresholds after SmartPerfetto.
    internal static string? Verdict(string key, double? delta, double? pct, double? floorOverride = null)
    {
        if (delta is null || Neutral.Contains(key))
        {
            return null;
        }
        bool isPct = key.EndsWith("_pct", StringComparison.Ordinal);
        double floor = floorOverride ?? (key.EndsWith("_ms", StringComparison.Ordinal) ? 5 : 1);
        bool absOk = Math.Abs(delta.Value) >= floor;
        bool relOk = pct is null ? delta.Value != 0 : Math.Abs(pct.Value) >= 5;
        if (!(isPct ? absOk : absOk && relOk))
        {
            return "same";
        }
        bool higherIsBetter = key.Contains("mem_available", StringComparison.Ordinal);
        bool better = higherIsBetter ? delta > 0 : delta < 0;
        return better ? "improved" : "regressed";
    }
}
