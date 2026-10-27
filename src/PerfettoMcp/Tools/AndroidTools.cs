using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class AndroidStartupsTool : TraceToolBase
{
    public AndroidStartupsTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("android_startups",
        """
        App launches (android.startup stdlib): startup_id, package, cold/warm/hot, duration, TTID/TTFD.
        Pass `startup_id` for a breakdown of that launch: `findings` from a slow-launch rule set (main-thread binder /
        lock / IO / runnable, OpenDexFilesFromOat, AppImage, class loading, JIT, inflate, dlopen, WebView, storage, GC,
        bindApplication share, concurrent forks, dex2oat, low CPU frequency, memory pressure, profiler overhead, and
        app code not AOT-compiled per the odex filter) with values and thresholds; then opinionated blame reasons,
        main-thread state time, heaviest main-thread slices and the slowest main-thread binder calls, and the main
        thread's critical path (which threads held up the launch while the main thread waited; see wakeup_chain).
        """,
        new SchemaBuilder()
            .Str("package", "Filter by package name; * for GLOB.")
            .Int("startup_id", "Break down this startup.")
            .Bool("critical_path", "Include the main thread's critical path (default true; ~1-4 s on large traces).")
            .Int("limit", "Rows per breakdown section (default 15).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        long? id = Args.OptionalLong(args, "startup_id");
        int limit = Args.OptionalInt(args, "limit", 15, 1, 500);
        string? package = Args.OptionalString(args, "package");
        string where = string.IsNullOrWhiteSpace(package) ? string.Empty
            : package.Contains('*') ? $"WHERE s.package GLOB {SqlText.Literal(package)}" : $"WHERE s.package = {SqlText.Literal(package)}";
        if (id is not null)
        {
            where = $"WHERE s.startup_id = {id}";
        }

        QueryTable list = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            INCLUDE PERFETTO MODULE android.startup.time_to_display;
            SELECT s.startup_id, s.package, s.startup_type, s.ts, ROUND(s.dur / 1e6, 1) AS dur_ms,
                   ROUND(d.time_to_initial_display / 1e6, 1) AS ttid_ms, ROUND(d.time_to_full_display / 1e6, 1) AS ttfd_ms
            FROM android_startups s LEFT JOIN android_startup_time_to_display d USING (startup_id)
            {where}
            ORDER BY s.ts
            """, ctx).ConfigureAwait(false);
        if (id is null)
        {
            JsonObject o = TableFormat.ToJson(list, 200);
            if (list.TotalRows == 0)
            {
                o["note"] = "No startups detected. They need atrace 'am' + 'dalvik' categories (and launch slices) in the trace config.";
            }
            return ToolCallResult.Json(o);
        }
        if (list.TotalRows == 0)
        {
            throw new ToolException($"startup_id {id} not found. Call android_startups without startup_id to list them.");
        }

        const string Main = "android_startup_threads st";
        string mainJoin = $"st.startup_id = {id} AND st.is_main_thread";
        // dur = -1 marks a state still open at trace end; it runs to the startup end.
        string clip = "MIN(IIF(x.dur < 0, st.ts + st.dur, x.ts + x.dur), st.ts + st.dur) - MAX(x.ts, st.ts)";
        JsonObject report = new() { ["startup"] = TableFormat.ToJson(list, 1) };
        try
        {
            JsonObject rules = await StartupRules.EvaluateAsync(id.Value, sql => RunAsync(s, sql, ctx)).ConfigureAwait(false);
            report["findings"] = rules["findings"]?.DeepClone();
            report["rule_metrics"] = rules["metrics"]?.DeepClone();
            report["dex_files"] = rules["dex_files"]?.DeepClone();
        }
        catch (SqlQueryException ex)
        {
            report["findings"] = new JsonObject { ["error"] = ex.Message };
        }

        report["blame_breakdown"] = await SectionAsync(s, $"""
            INCLUDE PERFETTO MODULE android.startup.startup_breakdowns;
            SELECT reason, COUNT(*) AS n, ROUND(SUM(dur) / 1e6, 2) AS dur_ms
            FROM android_startup_opinionated_breakdown WHERE startup_id = {id}
            GROUP BY reason ORDER BY SUM(dur) DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);

        report["main_thread_states"] = await SectionAsync(s, $"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            INCLUDE PERFETTO MODULE sched.states;
            SELECT sched_state_io_to_human_readable_string(x.state, x.io_wait) AS state, x.blocked_function,
                   COUNT(*) AS n, ROUND(SUM({clip}) / 1e6, 2) AS dur_ms
            FROM thread_state x JOIN {Main} ON x.utid = st.utid AND {mainJoin}
            WHERE x.ts < st.ts + st.dur AND IIF(x.dur < 0, st.ts + st.dur, x.ts + x.dur) > st.ts
            GROUP BY 1, 2 ORDER BY SUM({clip}) DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);

        report["main_thread_slices"] = await SectionAsync(s, $"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            SELECT x.name, MIN(x.depth) AS min_depth, COUNT(*) AS n, ROUND(SUM(x.dur) / 1e6, 2) AS total_ms,
                   ROUND(MAX(x.dur) / 1e6, 2) AS max_ms
            FROM slice x JOIN thread_track tt ON x.track_id = tt.id JOIN {Main} ON tt.utid = st.utid AND {mainJoin}
            WHERE x.ts >= st.ts AND x.ts < st.ts + st.dur AND x.dur > 0
            GROUP BY x.name ORDER BY SUM(x.dur) DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);

        report["main_thread_binder"] = await SectionAsync(s, $"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            INCLUDE PERFETTO MODULE android.binder;
            SELECT b.aidl_name, b.server_process, b.server_thread, b.client_ts, ROUND(b.client_dur / 1e6, 2) AS client_ms,
                   ROUND(b.server_dur / 1e6, 2) AS server_ms
            FROM android_binder_txns b JOIN {Main} ON b.client_utid = st.utid AND {mainJoin}
            WHERE b.client_ts >= st.ts AND b.client_ts < st.ts + st.dur
            ORDER BY b.client_dur DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);

        if (Args.OptionalBool(args, "critical_path", true))
        {
            QueryTable root = await RunAsync(s, $"""
                INCLUDE PERFETTO MODULE android.startup.startups;
                SELECT st.utid, st.ts, st.ts + st.dur FROM {Main} WHERE {mainJoin} LIMIT 1
                """, ctx).ConfigureAwait(false);
            if (root.Rows.Count > 0 && root.Rows[0] is [long utid, long ws, long we] && we > ws)
            {
                JsonObject cp = await CriticalPathAsync(s, utid, ws, we, ctx).ConfigureAwait(false);
                report["critical_path"] = cp;
                if (report["findings"] is JsonArray findings)
                {
                    foreach (JsonObject f in CriticalPathFindings(cp, (we - ws) / 1e6))
                    {
                        findings.Add(f);
                    }
                }
            }
        }

        return ToolCallResult.Json(report);
    }

    // JsonValue.GetValue<T> is type-exact: try double, long and int.
    private static double? Num(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue(out double d) ? d : v.TryGetValue(out long l) ? l : v.TryGetValue(out int i) ? i : null
        : null;

    internal static IEnumerable<JsonObject> CriticalPathFindings(JsonObject cp, double startupMs)
    {
        if (cp["by_thread"]?["rows"] is not JsonArray rows)
        {
            yield break;
        }
        JsonArray? slices = cp["blocker_slices"]?["rows"] as JsonArray;
        foreach (JsonNode? r in rows)
        {
            if (r is not JsonArray row || Num(row[8]) == 1)
            {
                continue;
            }
            string thread = row[0]?.GetValue<string>() ?? "?";
            string? process = row[2]?.GetValue<string>();
            double ms = Num(row[3]) ?? 0;
            double pct = startupMs > 0 ? 100 * ms / startupMs : 0;
            if (ms < 50 || pct < 15)
            {
                continue;
            }
            if (StartupCriticalPath.IsVsyncThread(thread, process))
            {
                yield return new JsonObject
                {
                    ["severity"] = "info",
                    ["rule"] = "VSYNC_WAIT_ON_PATH",
                    ["value"] = Math.Round(ms, 1),
                    ["unit"] = "ms",
                    ["what"] = $"{Math.Round(pct)}% of the launch the main thread waited for vsync ({thread}): the launch spans several frames",
                    ["suggestion"] = "Not a blocker: look at the frames before the first draw (starting window / transition animation, "
                        + "extra traversals) rather than at one thread.",
                    ["next_evidence"] = "frame_intervals / android_jank_frames with startup_id"
                };
                continue;
            }
            string? doing = slices?.OfType<JsonArray>().FirstOrDefault(sl => sl[0]?.GetValue<string>() == thread)?[1]?.GetValue<string>();
            double runnable = Num(row[6]) ?? 0;
            yield return new JsonObject
            {
                ["severity"] = pct >= 30 ? "critical" : "warning",
                ["rule"] = "CRITICAL_PATH_BLOCKER",
                ["value"] = Math.Round(ms, 1),
                ["unit"] = "ms",
                ["what"] = $"The main thread waited on {thread} ({process}) for {Math.Round(ms)} ms ({Math.Round(pct)}% of the launch): "
                    + $"it ran {row[5]} ms, was runnable {runnable} ms, in D {row[7] ?? 0} ms"
                    + (doing is null ? string.Empty : $"; mostly in '{doing}'"),
                ["suggestion"] = runnable > ms / 3
                    ? "The blocking thread was itself starved of CPU: raise its priority or cut competing work during the launch."
                    : "Move that work off the launch path (defer / lazy-init) or stop waiting for it synchronously.",
                ["next_evidence"] = "critical_path.blocker_slices in this report; wakeup_chain with startup_id; thread_state_breakdown on that thread"
            };
        }
    }
}

internal static class StartupCriticalPath
{
    // Threads that only hand out vsync: time on them means the launch spans several frames, not a blocker.
    public static bool IsVsyncThread(string thread, string? process) =>
        thread.StartsWith("TimerDispatch", StringComparison.Ordinal) || thread.StartsWith("VsyncDispatch", StringComparison.Ordinal)
        || (process?.EndsWith("surfaceflinger", StringComparison.Ordinal) == true && thread.Contains("ync", StringComparison.Ordinal));
}

internal sealed class AndroidJankFramesTool : TraceToolBase
{
    public AndroidJankFramesTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("android_jank_frames",
        """
        Frame jank from FrameTimeline (actual_frame_timeline_slice): per-process frame/jank counts, jank types and
        the longest janky frames with their expected duration. Needs the `android.surfaceflinger.frametimeline`
        data source. Without a process filter all processes (incl. surfaceflinger) are summarized.
        `visible_jank` counts frames presented late or dropped; `janky` also counts "Buffer Stuffing" frames, which
        are presented on time (the app is queuing ahead) and are not user-visible jank by themselves.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder()))
            .Int("limit", "Worst frames to list (default 20).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        int limit = Args.OptionalInt(args, "limit", 20, 1, 1000);
        TimeWindow w = TimeWindow.Parse(args);
        string proc = ProcessSelector.Predicate(args) ?? "1";
        string from = $"actual_frame_timeline_slice a LEFT JOIN process p USING (upid) WHERE {proc} AND {w.Overlaps("a.ts", "a.dur")}";

        JsonObject report = new() { ["window"] = w.Description };
        report["per_process"] = await SectionAsync(s, $"""
            {w.Prelude}SELECT a.upid, p.name AS process, COUNT(*) AS frames,
                   SUM(a.present_type IN ('Late Present', 'Dropped Frame')) AS visible_jank,
                   ROUND(100.0 * SUM(a.present_type IN ('Late Present', 'Dropped Frame')) / COUNT(*), 1) AS visible_jank_pct,
                   SUM(a.jank_type != 'None') AS janky, SUM(a.jank_type = 'Buffer Stuffing') AS buffer_stuffing_only,
                   ROUND(AVG(a.dur) / 1e6, 2) AS avg_ms, ROUND(MAX(a.dur) / 1e6, 2) AS max_ms
            FROM {from}
            GROUP BY a.upid ORDER BY visible_jank DESC, janky DESC LIMIT 30
            """, ctx, 30).ConfigureAwait(false);
        report["jank_types"] = await SectionAsync(s, $"""
            {w.Prelude}SELECT p.name AS process, a.jank_type, COUNT(*) AS n,
                   SUM(a.present_type IN ('Late Present', 'Dropped Frame')) AS visible
            FROM {from} AND a.jank_type != 'None'
            GROUP BY a.upid, a.jank_type ORDER BY visible DESC, n DESC LIMIT 30
            """, ctx, 30).ConfigureAwait(false);
        report["worst_frames"] = await SectionAsync(s, $"""
            {w.Prelude}WITH worst AS (
              SELECT a.id, a.ts, a.dur, a.name, a.upid, a.layer_name, a.jank_type, a.present_type, p.name AS process
              FROM {from} AND a.jank_type != 'None'
              ORDER BY a.present_type IN ('Late Present', 'Dropped Frame') DESC, a.dur DESC LIMIT {limit})
            SELECT w.ts, ROUND(w.dur / 1e6, 2) AS dur_ms,
                   ROUND((SELECT e.dur FROM expected_frame_timeline_slice e WHERE e.upid = w.upid AND e.name = w.name LIMIT 1) / 1e6, 2) AS expected_ms,
                   w.jank_type, w.present_type, w.process, w.layer_name, w.name AS frame_token
            FROM worst w ORDER BY w.present_type IN ('Late Present', 'Dropped Frame') DESC, w.dur DESC
            """, ctx, limit).ConfigureAwait(false);
        report["worst_visible_causes"] = await CausesAsync(s, from, w.Prelude, ctx).ConfigureAwait(false);
        return ToolCallResult.Json(report);
    }

    private const int CauseFrames = 5;

    // Wrapper slices that cover every frame and say nothing about why one frame was slow.
    private const string GenericSlices = """
        ('traversal', 'Record View#draw()', 'flush commands', 'Vulkan finish frame', 'animation', 'input', 'measure',
         'layout', 'draw', 'flush layers', 'queueBuffer', 'dequeueBuffer')
        """;

    // Worst janky frames: where the main thread and RenderThread spent the frame, incl. GPU pipeline compilation.
    private async Task<JsonNode> CausesAsync(TraceSession s, string from, string prelude, RequestContext ctx)
    {
        QueryTable t;
        try
        {
            t = await RunAsync(s, $"""
            {prelude}WITH f AS (
              SELECT ROW_NUMBER() OVER (ORDER BY a.dur DESC) AS k, a.ts, a.dur, a.ts + a.dur AS e, a.upid, a.name AS token,
                     p.name AS process, a.jank_type
              FROM {from} AND a.present_type IN ('Late Present', 'Dropped Frame') AND a.upid IS NOT NULL
                AND p.name NOT GLOB '*surfaceflinger*'
              ORDER BY a.dur DESC LIMIT {CauseFrames}),
            th AS (SELECT f.k, t.utid, IIF(t.tid = pr.pid, 'main', 'render') AS role
                   FROM f JOIN process pr ON pr.upid = f.upid JOIN thread t ON t.upid = f.upid
                   WHERE t.tid = pr.pid OR t.name = 'RenderThread'),
            st AS (SELECT th.k, th.role, x.state,
                          SUM(MIN(IIF(x.dur < 0, f.e, x.ts + x.dur), f.e) - MAX(x.ts, f.ts)) AS d
                   FROM th JOIN f USING (k) JOIN thread_state x ON x.utid = th.utid
                   WHERE x.ts < f.e AND IIF(x.dur < 0, f.e, x.ts + x.dur) > f.ts GROUP BY 1, 2, 3),
            sl AS (SELECT th.k, th.role, x.name, x.dur,
                          ROW_NUMBER() OVER (PARTITION BY th.k, th.role ORDER BY x.dur DESC) AS rn
                   FROM th JOIN f USING (k) JOIN thread_track tt ON tt.utid = th.utid JOIN slice x ON x.track_id = tt.id
                   WHERE x.ts < f.e AND x.ts + x.dur > f.ts AND x.depth >= 1 AND x.name NOT IN {GenericSlices}
                     AND x.name NOT GLOB 'Choreographer#doFrame*' AND x.name NOT GLOB 'DrawFrames*'
                     AND x.name NOT GLOB 'Drawing *' AND x.name NOT GLOB 'draw-VRI*' AND x.name NOT GLOB 'auto skgpu::*'),
            pc AS (SELECT f.k, SUM(MIN(x.ts + x.dur, f.e) - MAX(x.ts, f.ts)) AS d
                   FROM f JOIN thread t ON t.upid = f.upid JOIN thread_track tt ON tt.utid = t.utid JOIN slice x ON x.track_id = tt.id
                   WHERE x.ts < f.e AND x.ts + x.dur > f.ts
                     AND (x.name = 'CreateGraphicsPipeline-CompileAfterCacheMiss' OR x.name GLOB '*ompile*hader*'
                          OR x.name GLOB '*hader*ompil*' OR x.name GLOB 'GrGLProgramBuilder*')
                   GROUP BY 1)
            SELECT f.ts, ROUND(f.dur / 1e6, 2) AS dur_ms, f.process, f.token, f.jank_type,
              ROUND((SELECT SUM(d) FROM st WHERE st.k = f.k AND role = 'main' AND state = 'Running') / 1e6, 1) AS main_running_ms,
              ROUND((SELECT SUM(d) FROM st WHERE st.k = f.k AND role = 'main' AND state = 'S') / 1e6, 1) AS main_sleeping_ms,
              ROUND((SELECT SUM(d) FROM st WHERE st.k = f.k AND role = 'main' AND state IN ('R', 'R+')) / 1e6, 1) AS main_runnable_ms,
              ROUND((SELECT SUM(d) FROM st WHERE st.k = f.k AND role = 'main' AND state GLOB 'D*') / 1e6, 1) AS main_d_ms,
              ROUND((SELECT SUM(d) FROM st WHERE st.k = f.k AND role = 'render' AND state = 'Running') / 1e6, 1) AS render_running_ms,
              ROUND(IFNULL((SELECT d FROM pc WHERE pc.k = f.k), 0) / 1e6, 1) AS pipeline_compile_ms,
              ROUND(IFNULL((SELECT SUM(MIN(x.ts + x.dur, f.e) - MAX(x.ts, f.ts)) FROM th
                 JOIN thread_track tt ON tt.utid = th.utid JOIN slice x ON x.track_id = tt.id
                 WHERE th.k = f.k AND th.role = 'render' AND x.name = 'waitForBufferRelease'
                   AND x.ts < f.e AND x.ts + x.dur > f.ts), 0) / 1e6, 1) AS render_wait_buffer_ms,
              ROUND(IFNULL((SELECT MAX(MIN(x.ts + x.dur, f.e) - MAX(x.ts, f.ts)) FROM slice x
                 WHERE x.name GLOB 'setPowerMode(*' AND x.ts < f.e AND x.ts + x.dur > f.ts), 0) / 1e6, 1) AS display_power_change_ms,
              (SELECT name || ' ' || ROUND(dur / 1e6, 1) || ' ms' FROM sl WHERE sl.k = f.k AND role = 'main' AND rn = 1) AS top_main_slice,
              (SELECT name || ' ' || ROUND(dur / 1e6, 1) || ' ms' FROM sl WHERE sl.k = f.k AND role = 'render' AND rn = 1) AS top_render_slice
            FROM f ORDER BY f.k
            """, ctx, CauseFrames).ConfigureAwait(false);
        }
        catch (SqlQueryException ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
        JsonArray rows = new();
        foreach (object?[] r in t.Rows)
        {
            JsonObject o = new();
            Dictionary<string, double> num = new(StringComparer.Ordinal);
            for (int i = 0; i < t.Columns.Count; i++)
            {
                o[t.Columns[i]] = TableFormat.Cell(r[i]);
                if (r[i] is long or double)
                {
                    num[t.Columns[i]] = Convert.ToDouble(r[i]);
                }
            }
            o["likely"] = LikelyCause(num, r[t.Columns.IndexOf("jank_type")] as string ?? string.Empty);
            rows.Add(o);
        }
        return rows;
    }

    private static string LikelyCause(Dictionary<string, double> f, string jankType)
    {
        double V(string k) => f.GetValueOrDefault(k);
        double dur = V("dur_ms");
        if (jankType.Contains("PowerModeChange", StringComparison.Ordinal) || jankType.Contains("Display not ON", StringComparison.Ordinal)
            || V("display_power_change_ms") >= 0.3 * dur)
        {
            return "display power transition (screen on/off): frames wait while the panel powers up; surfaceflinger setPowerMode overlaps the frame (display_power_change_ms); panel power-on time is in HWC";
        }
        if (V("render_wait_buffer_ms") >= 0.3 * dur)
        {
            return "RenderThread waited for a free buffer (waitForBufferRelease): SurfaceFlinger / HWC not releasing buffers "
                + "(composition or display stalled), check surfaceflinger in this window";
        }
        if (V("pipeline_compile_ms") >= 0.25 * dur)
        {
            return "GPU pipeline / shader compilation on first use (cache miss): pre-warm the shaders or persist the pipeline cache";
        }
        if (V("main_d_ms") >= 0.25 * dur)
        {
            return "main thread blocked in D state (IO / kernel): io_summary / thread_state_breakdown for blocked_function";
        }
        if (V("main_runnable_ms") >= 0.25 * dur)
        {
            return "main thread runnable but not scheduled: CPU contention (cpu_top_threads in this window)";
        }
        if (V("main_sleeping_ms") >= 0.4 * dur && V("render_running_ms") >= 0.3 * dur)
        {
            return "main thread waited for RenderThread (sync draw): heavy GPU command recording / flush, see top_render_slice; "
                + "wakeup_chain over this frame (ts .. ts + dur) shows in critical_path which thread RenderThread itself waited on";
        }
        if (V("main_running_ms") >= 0.5 * dur)
        {
            return "main thread work (measure / layout / input / animation), see top_main_slice";
        }
        if (V("main_sleeping_ms") >= 0.4 * dur)
        {
            return "main thread sleeping (lock, binder, or waiting on another thread): wakeup_chain on the main thread";
        }
        return "mixed; compare main / render times with dur_ms";
    }
}

internal sealed class AndroidBinderTxnsTool : TraceToolBase
{
    public AndroidBinderTxnsTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("android_binder_txns",
        """
        Binder transactions (android.binder stdlib): aggregated by client -> server -> AIDL method, the slowest
        individual calls (overhead_ms = client - server time: queueing / binder thread-pool starvation), and where
        the slowest sync calls spent their time (server thread states incl. D-state kernel function, the part spent in
        monitor / lock contention, client-side binder overhead), and binder storms (> 50 calls per 100 ms from one client: peak, how many
        buckets — many = steady pipeline, few = burst — top server / method and main-thread share in the peak).
        The process filter matches either side unless `side` is set.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder()))
            .Enum("side", "Which side the process filter applies to (default either).", "either", "client", "server")
            .Bool("sync_only", "Only synchronous transactions (default false).")
            .Num("min_dur_ms", "Ignore calls shorter than this client duration.")
            .Int("limit", "Rows per section (default 20).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        int limit = Args.OptionalInt(args, "limit", 20, 1, 1000);
        TimeWindow w = TimeWindow.Parse(args);
        string? procPred = ProcessSelector.Predicate(args);
        string side = Args.OptionalString(args, "side") ?? "either";
        string filter = "1";
        if (procPred is not null)
        {
            string client = $"b.client_upid IN (SELECT p.upid FROM process p WHERE {procPred})";
            string server = $"b.server_upid IN (SELECT p.upid FROM process p WHERE {procPred})";
            filter = side switch
            {
                "client" => client,
                "server" => server,
                _ => $"({client} OR {server})"
            };
        }
        if (Args.OptionalBool(args, "sync_only", false))
        {
            filter += " AND b.is_sync";
        }
        if (Args.OptionalDouble(args, "min_dur_ms") is { } minMs)
        {
            if (!double.IsFinite(minMs) || minMs < 0 || minMs > 1e9)
            {
                throw new McpInvalidParamsException("'min_dur_ms' must be between 0 and 1e9");
            }
            filter += $" AND b.client_dur >= {(long)(minMs * 1e6)}";
        }
        string from = $"android_binder_txns b WHERE {filter} AND {w.Overlaps("b.client_ts", "b.client_dur")}";
        string prelude = w.Prelude + "INCLUDE PERFETTO MODULE android.binder;\n";

        JsonObject report = new() { ["window"] = w.Description };
        report["by_method"] = await SectionAsync(s, $"""
            {prelude}SELECT b.client_process, b.server_process, b.aidl_name, b.is_sync, COUNT(*) AS n,
                   ROUND(SUM(b.client_dur) / 1e6, 2) AS total_ms, ROUND(MAX(b.client_dur) / 1e6, 2) AS max_ms,
                   SUM((b.is_main_thread AND b.client_upid IN (SELECT upid FROM process WHERE name IS NOT NULL OR cmdline IS NOT NULL))) AS from_main_thread
            FROM {from}
            GROUP BY 1, 2, 3, 4 ORDER BY SUM(b.client_dur) DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        report["slowest"] = await SectionAsync(s, $"""
            {prelude}SELECT b.client_ts, ROUND(b.client_dur / 1e6, 2) AS client_ms, ROUND(b.server_dur / 1e6, 2) AS server_ms,
                   ROUND((b.client_dur - b.server_dur) / 1e6, 2) AS overhead_ms,
                   b.client_process, b.client_thread, (b.is_main_thread AND b.client_upid IN (SELECT upid FROM process WHERE name IS NOT NULL OR cmdline IS NOT NULL)) AS is_main_thread, b.server_process, b.server_thread, b.aidl_name, b.is_sync
            FROM {from}
            ORDER BY b.client_dur DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        // Binder storms: > 50 transactions from one client in a 100 ms bucket (> 100 severe).
        report["storms"] = await SectionAsync(s, $"""
            {prelude}WITH b AS (
              SELECT b.client_upid, b.client_process, b.client_ts / 100000000 AS bucket, b.server_process, b.aidl_name,
                     (b.is_main_thread AND b.client_upid IN (SELECT upid FROM process WHERE name IS NOT NULL OR cmdline IS NOT NULL)) AS is_main_thread FROM {from}),
            agg AS (SELECT client_upid, bucket, COUNT(*) AS n FROM b GROUP BY client_upid, bucket),
            hot AS (SELECT client_upid, COUNT(*) AS storm_buckets, MAX(n) AS peak,
                           (SELECT a2.bucket FROM agg a2 WHERE a2.client_upid = agg.client_upid ORDER BY a2.n DESC LIMIT 1) AS peak_bucket
                    FROM agg WHERE n > 50 GROUP BY client_upid)
            SELECT (SELECT MAX(client_process) FROM b WHERE b.client_upid = hot.client_upid) AS client_process,
                   IIF(hot.peak > 100, 'severe', 'storm') AS severity, hot.peak AS peak_per_100ms, hot.storm_buckets,
                   hot.peak_bucket * 100000000 AS peak_ts,
                   (SELECT b.server_process FROM b WHERE b.client_upid = hot.client_upid AND b.bucket = hot.peak_bucket
                    GROUP BY b.server_process ORDER BY COUNT(*) DESC LIMIT 1) AS top_server,
                   (SELECT b.aidl_name FROM b WHERE b.client_upid = hot.client_upid AND b.bucket = hot.peak_bucket
                    GROUP BY b.aidl_name ORDER BY COUNT(*) DESC LIMIT 1) AS top_method,
                   (SELECT SUM(b.is_main_thread) FROM b WHERE b.client_upid = hot.client_upid AND b.bucket = hot.peak_bucket) AS from_main_in_peak
            FROM hot ORDER BY hot.peak DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        // Slowest sync calls; overhead_ms = client - server (queueing). Computed directly: the stdlib binder_breakdown
        // module materializes every transaction on first use.
        report["slowest_breakdown"] = await SectionAsync(s, $"""
            {prelude}WITH top AS (
              SELECT b.server_utid AS utid, b.server_ts AS s, b.server_ts + b.server_dur AS e, b.client_dur - b.server_dur AS overhead
              FROM {from} AND b.is_sync AND b.server_utid IS NOT NULL AND b.server_dur > 0 ORDER BY b.client_dur DESC LIMIT {limit}),
            parts AS (
              SELECT 'server' AS reason_type,
                     CASE WHEN x.state GLOB 'D*' THEN 'D ' || IFNULL(x.blocked_function, IIF(x.io_wait = 1, 'io', '?'))
                          WHEN x.state = 'S' THEN 'S (sleeping)' ELSE x.state END AS reason,
                     MIN(IIF(x.dur < 0, top.e, x.ts + x.dur), top.e) - MAX(x.ts, top.s) AS d
              FROM top JOIN thread_state x ON x.utid = top.utid AND x.ts < top.e AND IIF(x.dur < 0, top.e, x.ts + x.dur) > top.s
              UNION ALL
              SELECT 'server', 'of which monitor / lock contention',
                     MIN(c.ts + c.dur, top.e) - MAX(c.ts, top.s)
              FROM top JOIN thread_track tt ON tt.utid = top.utid JOIN slice c ON c.track_id = tt.id
              WHERE (c.name GLOB 'monitor contention*' OR c.name GLOB 'Lock contention*') AND c.depth = (
                       SELECT MIN(c2.depth) FROM slice c2 WHERE c2.track_id = c.track_id AND c2.ts <= c.ts AND c2.ts + c2.dur >= c.ts + c.dur
                         AND (c2.name GLOB 'monitor contention*' OR c2.name GLOB 'Lock contention*'))
                AND c.ts < top.e AND c.ts + c.dur > top.s
              UNION ALL
              SELECT 'client', 'binder_overhead (client - server time)', overhead FROM top WHERE overhead > 0)
            SELECT reason_type, reason, COUNT(*) AS intervals, ROUND(SUM(d) / 1e6, 2) AS total_ms
            FROM parts GROUP BY reason_type, reason ORDER BY SUM(d) DESC LIMIT 15
            """, ctx, 15).ConfigureAwait(false);
        return ToolCallResult.Json(report);
    }
}
