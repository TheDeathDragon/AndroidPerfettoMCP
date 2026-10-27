using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class LoadTraceTool : TraceToolBase
{
    private const int MaxBatch = 8;
    private static readonly TimeSpan OverviewTimeout = TimeSpan.FromSeconds(60);

    public LoadTraceTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("load_trace",
        """
        Load a local Perfetto trace (.pftrace / .perfetto-trace / .pb / .gz ...) and return its `trace_id`
        plus an overview: duration, device metadata, data-source row counts, trace health issues (data loss /
        import errors), detected app startups and the busiest processes. Call this first.

        Several traces can be loaded (e.g. base vs fix) — pass `paths` or call again; the last one loaded becomes
        the default for other tools, which also accept `trace_id`. Re-loading an unchanged file is instant.
        Large traces stream with progress notifications; loading has its own generous timeout.
        `logcat`: a text log captured alongside (`adb logcat -b all -v threadtime -v year`) is merged into the same
        timeline (as <trace>+logcat.zip next to the trace) — for user builds, whose traces carry no logcat.
        """,
        new SchemaBuilder()
            .Str("path", "Absolute path of the trace file on the machine running this server.")
            .Str("logcat", "Logcat text file (`adb logcat -v threadtime -v year`) to merge into `path`'s timeline.")
            .StrArray("paths", $"Several trace files to load in one call (max {MaxBatch}).")
            .Bool("overview", "Include the overview summary (default true)."));

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        List<string> paths = Args.OptionalStringList(args, "paths") ?? new List<string>();
        string? single = Args.OptionalString(args, "path");
        if (!string.IsNullOrWhiteSpace(single))
        {
            paths.Insert(0, single);
        }
        if (paths.Count == 0)
        {
            throw new McpInvalidParamsException("pass `path` or `paths`");
        }
        if (paths.Count > MaxBatch)
        {
            throw new McpInvalidParamsException($"at most {MaxBatch} traces per call");
        }
        bool overview = Args.OptionalBool(args, "overview", true);
        if (Args.OptionalString(args, "logcat") is { } logcat)
        {
            if (paths.Count != 1)
            {
                throw new McpInvalidParamsException("`logcat` goes with a single `path`");
            }
            paths[0] = TraceBundle.WithLogcat(Path.GetFullPath(Environment.ExpandEnvironmentVariables(paths[0].Trim().Trim('"'))),
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(logcat.Trim().Trim('"'))));
        }

        JsonArray results = new();
        foreach (string path in paths)
        {
            TraceSession s = await Traces.LoadAsync(path, ctx).ConfigureAwait(false);
            JsonObject entry = new()
            {
                ["trace_id"] = s.Id,
                ["file"] = s.FileName,
                ["path"] = s.Path,
                ["size_mb"] = Math.Round(new FileInfo(s.Path).Length / 1048576.0, 1),
                ["load_seconds"] = Math.Round(s.LastLoadSeconds, 2)
            };
            if (overview)
            {
                entry["overview"] = await OverviewAsync(s, ctx).ConfigureAwait(false);
            }
            results.Add(entry);
        }
        JsonObject result = paths.Count == 1 ? (JsonObject)results[0]!.DeepClone() : new JsonObject { ["traces"] = results };
        result["active_trace_id"] = Traces.ActiveId;
        return ToolCallResult.Json(result);
    }

    private async Task<JsonObject> OverviewAsync(TraceSession s, RequestContext ctx)
    {
        JsonObject o = new();
        List<string> hints = new();
        OverviewState state = new();

        QueryTable? bounds = await TryAsync(state, s, "SELECT start_ts, end_ts, end_ts - start_ts AS dur FROM trace_bounds", ctx);
        if (bounds is { Rows.Count: > 0 })
        {
            o["start_ts"] = TableFormat.Cell(bounds.Rows[0][0]);
            o["end_ts"] = TableFormat.Cell(bounds.Rows[0][1]);
            if (bounds.Rows[0][2] is long dur)
            {
                o["duration_s"] = Math.Round(dur / 1e9, 3);
            }
        }

        QueryTable? meta = await TryAsync(state, s, """
            SELECT name, COALESCE(str_value, CAST(int_value AS TEXT)) AS value FROM metadata
            WHERE name IN ('android_build_fingerprint', 'android_sdk_version', 'android_device_manufacturer',
                           'android_soc_model', 'android_hardware_revision', 'trace_type', 'system_release',
                           'system_machine', 'trace_size_bytes')
            """, ctx);
        if (meta is { Rows.Count: > 0 })
        {
            JsonObject m = new();
            foreach (object?[] r in meta.Rows)
            {
                m[r[0]?.ToString() ?? "?"] = TableFormat.Cell(r[1]);
            }
            o["metadata"] = m;
        }

        QueryTable? counts = await TryAsync(state, s, """
            SELECT (SELECT COUNT(*) FROM process) AS processes, (SELECT COUNT(*) FROM thread) AS threads,
                   (SELECT COUNT(*) FROM slice) AS slices, (SELECT COUNT(*) FROM sched) AS sched_slices,
                   (SELECT COUNT(*) FROM thread_state) AS thread_states, (SELECT COUNT(*) FROM counter) AS counters,
                   (SELECT COUNT(*) FROM actual_frame_timeline_slice) AS frame_timeline_slices,
                   (SELECT COUNT(*) FROM perf_sample) AS perf_samples,
                   (SELECT COUNT(*) FROM android_logs) AS android_logs
            """, ctx);
        if (counts is { Rows.Count: > 0 })
        {
            JsonObject c = new();
            for (int i = 0; i < counts.Columns.Count; i++)
            {
                c[counts.Columns[i]] = TableFormat.Cell(counts.Rows[0][i]);
            }
            o["counts"] = c;
            if (counts.Cell(0, "frame_timeline_slices") is long f && f > 0)
            {
                hints.Add("FrameTimeline present: use android_jank_frames.");
            }
            if (counts.Cell(0, "sched_slices") is long sc && sc > 0)
            {
                hints.Add("Scheduling data present: cpu_top_threads / thread_state_breakdown.");
                if (counts.Cell(0, "thread_states") is long tsc && tsc == 0)
                {
                    hints.Add("No thread states although sched data exists: thread_state_breakdown / wakeup_chain need "
                        + "sched_waking / sched_wakeup ftrace events too.");
                }
            }
            else
            {
                // A capture whose ftrace part failed still loads: say so instead of every CPU / thread tool coming back empty.
                hints.Add("NO scheduling data (sched_switch) in this trace: cpu_top_threads, thread_state_breakdown, "
                    + "wakeup_chain, critical paths and startup state splits will be empty. The capture's linux.ftrace "
                    + "sched events are missing — check the trace config (and that ftrace was allowed: userdebug / root).");
            }
            if (counts.Cell(0, "perf_samples") is long ps && ps > 0)
            {
                hints.Add("CPU profile samples present: INCLUDE PERFETTO MODULE linux.perf.samples (or callstacks.stack_profile).");
            }
            // android.log works on userdebug only (user builds: SELinux denies traced_probes the logdr socket).
            if (counts.Cell(0, "android_logs") is long lg && lg == 0)
            {
                QueryTable? cfg = await TryAsync(state, s, """
                    SELECT (SELECT str_value FROM metadata WHERE name = 'trace_config_pbtxt') GLOB '*"android.log"*' AS wanted,
                           (SELECT str_value FROM metadata WHERE name = 'android_build_fingerprint') GLOB '*:user/*' AS user_build
                    """, ctx);
                if (cfg is { Rows.Count: > 0 } && Convert.ToInt64(cfg.Rows[0][0] ?? 0L) == 1)
                {
                    hints.Add("The trace config asked for android.log but no logcat line arrived"
                        + (Convert.ToInt64(cfg.Rows[0][1] ?? 0L) == 1
                            ? " (user build: Perfetto supports android.log on userdebug only; SELinux denies traced_probes /dev/socket/logdr)"
                            : " (check `adb logcat | grep logdr` for an avc denial or a connect error from traced_probes)")
                        + ": log-based sections (important_kills killers, process_churn kill / restart sources, app_compactions, "
                        + "noisy_log_tags, boot milestones) are empty. Record with record_trace (logcat=auto merges a host "
                        + "logcat) or pass `adb logcat -b all -v threadtime -v year` output as load_trace `logcat`.");
                }
            }
        }

        // Grade what each issue affects: unparsed vendor atrace lines are harmless, a ftrace overrun is not.
        QueryTable? health = await TryAsync(state, s, """
            WITH prints AS (SELECT COUNT(*) AS n FROM ftrace_event WHERE name = 'print'),
                 samples AS (SELECT COUNT(*) AS n FROM perf_sample),
                 -- Per-CPU / per-buffer counters (idx) are folded: one row per problem, not eight perf_cpu_lost_records.
                 st AS (SELECT name, severity, source, SUM(value) AS value, COUNT(*) AS entries,
                               GROUP_CONCAT(DISTINCT idx) AS idx FROM stats
                        WHERE value > 0 AND severity IN ('error', 'data_loss') GROUP BY name, severity, source),
                 r AS (SELECT st.*,
                         CASE WHEN name = 'systrace_parse_failure' THEN 100.0 * value / MAX(1, (SELECT n FROM prints))
                              WHEN name GLOB 'perf_*' THEN 100.0 * value / MAX(1, value + (SELECT n FROM samples))
                         END AS pct,
                         CASE WHEN name GLOB 'perf_*' OR name GLOB 'symbolization_*' THEN 'profile'
                              WHEN name = 'systrace_parse_failure' THEN 'slices'
                              WHEN name GLOB 'android_log_*' THEN 'logs'
                              WHEN name GLOB 'config_*' THEN 'config'
                              WHEN name GLOB 'ftrace_*' OR name GLOB '*overrun*' THEN 'ftrace'
                              WHEN name = 'traced_flushes_failed' THEN 'trace_end'
                              ELSE 'unknown'
                         END AS affects FROM st)
            SELECT name, severity, source, value, entries,
              CASE
                WHEN name = 'systrace_parse_failure' THEN 'unparseable atrace lines (' || IIF(pct < 0.01, '<0.01', ROUND(pct, 2))
                     || '% of print events): a few slices / counters missing, scheduling unaffected'
                WHEN affects = 'profile' AND pct IS NOT NULL THEN 'perf samples lost / skipped (' || IIF(pct < 0.01, '<0.01', ROUND(pct, 2))
                     || '% of samples): cpu_profile undercounts; scheduling unaffected'
                WHEN affects = 'profile' THEN 'some frames not symbolized: cpu_profile shows lib+offset; scheduling unaffected'
                WHEN name = 'ftrace_cpu_has_data_loss' THEN 'ftrace lost events on CPU ' || COALESCE(idx, '?')
                     || ': gaps in sched / thread states / slices there; check the timeline around your window'
                WHEN affects = 'ftrace' THEN 'ftrace buffer overran: events missing for some CPUs / periods (gaps)'
                WHEN name = 'traced_flushes_failed' THEN 'a flush failed: the end of the trace may lack data'
                WHEN affects = 'logs' THEN 'logcat lines lost or unparsed: android_logs incomplete; scheduling unaffected'
                WHEN name = 'config_write_into_file_no_flush' THEN 'config advice: write_into_file without flush_period_ms; '
                     || 'nothing lost here, but long traces risk losing their end'
              END AS impact,
              IIF(pct < 5 OR affects = 'config' OR name GLOB 'symbolization_*'
                  OR (affects = 'logs' AND value < 100), 1, 0) AS minor,
              affects, idx
            FROM r
            ORDER BY minor, value DESC LIMIT 12
            """, ctx);
        if (health is { Rows.Count: > 0 })
        {
            o["health_issues"] = TableFormat.ToJson(health, 12);
            List<string> serious = [.. health.Rows.Where(r => Convert.ToInt64(r[6] ?? 0L) == 0)
                .Select(r => r[7]?.ToString() ?? "unknown").Distinct()];
            if (serious.Count == 0)
            {
                hints.Add("Only minor import issues (see health_issues.impact): unparseable vendor atrace lines, a few lost "
                    + "logcat lines, lost perf samples and / or config advice; scheduling and timing data are complete.");
            }
            else if (serious.All(a => a is "profile" or "slices" or "logs"))
            {
                hints.Add($"Data loss limited to: {string.Join(", ", serious)} (see health_issues.impact); scheduling and "
                    + "thread-state data are complete.");
            }
            else
            {
                hints.Add($"Trace has data loss affecting: {string.Join(", ", serious)} (health_issues, see impact); "
                    + "conclusions about missing events may be unreliable.");
            }
        }

        // A log burst wrapping the shared buffer loses the process scan: most processes end up nameless.
        QueryTable? nameless = await TryAsync(state, s, """
            WITH ran AS (SELECT DISTINCT utid FROM sched WHERE utid != 0)
            SELECT COUNT(*) AS n,
                   (SELECT SUM(value) FROM stats WHERE name = 'traced_buf_chunks_overwritten') AS chunks_overwritten
            FROM process p
            WHERE p.pid > 0 AND p.name IS NULL AND p.cmdline IS NULL
              AND EXISTS (SELECT 1 FROM thread t JOIN ran USING (utid) WHERE t.upid = p.upid
                          AND NOT EXISTS (SELECT 1 FROM thread t2 JOIN process p2 ON p2.upid = t2.upid
                                          WHERE t2.tid = t.tid AND t2.upid != p.upid AND p2.name IS NOT NULL))
            """, ctx);
        if (nameless is { Rows.Count: > 0 } && Convert.ToInt64(nameless.Rows[0][0] ?? 0L) >= 20)
        {
            long n = Convert.ToInt64(nameless.Rows[0][0]);
            long chunks = Convert.ToInt64(nameless.Rows[0][1] ?? 0L);
            o["nameless_processes"] = n;
            hints.Add($"{n} processes that ran have no name: the start-of-trace process scan is missing"
                + (chunks > 0 ? $" (trace ring buffer wrapped, {chunks} chunks overwritten)" : "")
                + ". Per-process results show pids only. For captures, give linux.process_stats its own buffer and keep "
                + "android.log in a separate one.");
        }

        // Share of SurfaceFlinger frames composed on the GPU (client composition): costly and invisible app-side.
        // Counted by drawLayers on SF's main thread; region sampling and screenshots also use RenderEngine.
        QueryTable? sf = await TryAsync(state, s, """
            WITH sl AS (SELECT x.name, x.dur, t.name AS thread FROM slice x JOIN thread_track tt ON x.track_id = tt.id
                        JOIN thread t USING (utid) JOIN process p USING (upid)
                        WHERE p.name GLOB '*surfaceflinger' AND x.name IN ('drawLayers', 'REThreaded::drawLayers')),
                 f AS (SELECT COUNT(*) AS n FROM slice WHERE name GLOB 'composite [0-9]*'),
                 g AS (SELECT COUNT(*) AS n FROM sl WHERE name = 'drawLayers' AND thread = 'surfaceflinger'),
                 r AS (SELECT COUNT(*) AS n FROM sl WHERE name = 'drawLayers' AND thread = 'RegionSampling'),
                 e AS (SELECT SUM(dur) AS d FROM sl WHERE name = 'REThreaded::drawLayers')
            SELECT f.n AS frames, g.n AS gpu_frames, ROUND(100.0 * g.n / MAX(f.n, 1), 1) AS gpu_pct,
                   ROUND(COALESCE(e.d, 0) / 1e6, 1) AS render_engine_ms, r.n AS region_sampling_renders
            FROM f, g, r, e
            """, ctx);
        if (sf is { Rows.Count: > 0 } && Convert.ToInt64(sf.Rows[0][0] ?? 0L) > 0)
        {
            o["sf_composition"] = TableFormat.ToJson(sf, 1);
            if (Convert.ToInt64(sf.Rows[0][0]) >= 100 && Convert.ToDouble(sf.Rows[0][2] ?? 0.0) >= 30)
            {
                hints.Add($"SurfaceFlinger composed {sf.Rows[0][2]}% of {sf.Rows[0][0]} frames on the GPU (client composition, "
                    + $"RenderEngine {sf.Rows[0][3]} ms): some layer is rejected by the HWC (format, rotation, scaling, "
                    + "too many layers) or composition is forced. Check `dumpsys SurfaceFlinger` composition types; "
                    + "slice_hotspots process_name=surfaceflinger thread_name=RenderEngine.");
            }
        }

        // waitForService logs once per second forever for a service that never registers: a permanent ~1 Hz wakeup chain.
        QueryTable? missing = await TryAsync(state, s, """
            SELECT SUBSTR(l.msg, 23, INSTR(SUBSTR(l.msg, 23) || ' ', ' ') - 1) AS service,
                   COALESCE(p.name, p.cmdline) AS waiting_process, l.tag, COUNT(*) AS waits,
                   ROUND((MAX(l.ts) - MIN(l.ts)) / 1e9, 1) AS span_s
            FROM android_logs l LEFT JOIN thread t USING (utid) LEFT JOIN process p USING (upid)
            WHERE l.msg GLOB 'Waited one second for *'
            GROUP BY 1, 2 ORDER BY waits DESC LIMIT 5
            """, ctx);
        if (missing is { Rows.Count: > 0 })
        {
            o["missing_services"] = TableFormat.ToJson(missing, 5);
        }
        // A few waits are normal while services come up during boot; a client retrying all trace long is not.
        if (missing is { Rows.Count: > 0 } && Convert.ToInt64(missing.Rows[0][3] ?? 0L) >= 10)
        {
            hints.Add($"{missing.Rows[0][1] ?? "A client"} waits forever for service '{missing.Rows[0][0]}' (missing_services): each 1 s retry "
                + "wakes the client, servicemanager (new thread) and init. Fix the HAL registration or drop the client / feature.");
        }

        // Processes killed and restarted repeatedly. Kill sources come from the killers' logs; type "restart" is an
        // immediate AMS restart (e.g. UNIPNP restart_list).
        QueryTable? churn = await TryAsync(state, s, """
            WITH st AS (SELECT name, COUNT(*) AS starts,
                               AVG(COALESCE(end_ts, (SELECT end_ts FROM trace_bounds)) - start_ts) AS life
                        FROM process WHERE start_ts IS NOT NULL AND pid > 0 AND name GLOB '*.*' AND name NOT GLOB '/*'
                        GROUP BY name HAVING starts >= 3 ORDER BY starts DESC LIMIT 8),
            lg AS MATERIALIZED (SELECT tag, msg FROM android_logs
                                WHERE tag IN ('lowmemorykiller', 'am_kill', 'am_proc_start')
                                   OR (tag = 'ActivityManager' AND msg GLOB '* restart by unipnp')
                                   OR msg GLOB 'processName is *to be kill*')
            SELECT st.name AS process, st.starts, ROUND(st.life / 1e9, 1) AS avg_life_s,
                   (SELECT COUNT(*) FROM lg WHERE tag = 'lowmemorykiller' AND msg GLOB 'Kill ''' || st.name || '''*') AS lmkd_kills,
                   (SELECT COUNT(*) FROM lg WHERE tag = 'am_kill' AND msg GLOB '*,' || st.name || ',*') AS am_kills,
                   (SELECT COUNT(*) FROM lg WHERE msg GLOB 'processName is ' || st.name || ',*to be kill*') AS unistab_kills,
                   (SELECT COUNT(*) FROM lg WHERE tag = 'am_proc_start'
                      AND msg GLOB '*Process Name="' || st.name || '" Type="restart"*') AS am_restarts,
                   (SELECT COUNT(*) FROM lg WHERE tag = 'ActivityManager' AND msg GLOB '*:' || st.name || '/* restart by unipnp')
                     AS unipnp_restarts
            FROM st ORDER BY st.starts DESC
            """, ctx);
        if (churn is { Rows.Count: > 0 })
        {
            o["process_churn"] = TableFormat.ToJson(churn, 8);
            object?[] top = churn.Rows[0];
            if (Convert.ToInt64(top[1] ?? 0L) >= 5 && Convert.ToDouble(top[2] ?? 0.0) < 10)
            {
                string by = string.Join(", ", new[] { ("lmkd", top[3]), ("am_kill", top[4]), ("UniStab", top[5]) }
                    .Where(k => Convert.ToInt64(k.Item2 ?? 0L) > 0).Select(k => $"{k.Item1} {k.Item2}"));
                string back = string.Join(", ", new[] { ("immediate AMS restarts", top[6]), ("UNIPNP restart_list", top[7]) }
                    .Where(k => Convert.ToInt64(k.Item2 ?? 0L) > 0).Select(k => $"{k.Item1} {k.Item2}"));
                hints.Add($"{top[0]} started {top[1]} times (average life {top[2]} s){(by.Length > 0 ? $"; kills: {by}" : "")}"
                    + $"{(back.Length > 0 ? $"; restarts: {back}" : "")}: "
                    + "a kill / restart loop (process_churn). Each restart costs a fork and bindApplication; find what keeps "
                    + "restarting it (receivers, sticky services, restart lists) or exclude it from the killer.");
            }
        }

        // Apps that died at oom_adj 100-899 (still important): returning is a cold start. Adj 0 = killed while starting.
        QueryTable? kills = await TryAsync(state, s, """
            WITH d AS (
              SELECT ts, SUBSTR(msg, INSTR(msg, 'PID=') + 4, INSTR(SUBSTR(msg, INSTR(msg, 'PID=') + 4), ' ') - 1) AS pid,
                     SUBSTR(msg, INSTR(msg, 'Process Name="') + 14, INSTR(SUBSTR(msg, INSTR(msg, 'Process Name="') + 14), '"') - 1) AS process,
                     CAST(SUBSTR(msg, INSTR(msg, 'OomAdj=') + 7, INSTR(SUBSTR(msg, INSTR(msg, 'OomAdj=') + 7) || ' ', ' ') - 1) AS INT) AS adj
              FROM android_logs WHERE tag = 'am_proc_died' AND msg GLOB '* OomAdj=*'),
            top AS (SELECT * FROM d WHERE adj BETWEEN 100 AND 899 AND process GLOB '*.*' ORDER BY adj, ts LIMIT 10),
            k AS MATERIALIZED (SELECT ts, tag, msg FROM android_logs
                               WHERE tag IN ('lowmemorykiller', 'am_kill') OR msg GLOB '*to be kill, this Tgid is *')
            SELECT d.ts, d.process, d.adj,
                   CASE WHEN EXISTS (SELECT 1 FROM k WHERE k.tag = 'lowmemorykiller'
                                       AND k.msg GLOB 'Kill * (' || d.pid || '),*' AND k.ts BETWEEN d.ts - 5e9 AND d.ts) THEN 'lmkd'
                        WHEN EXISTS (SELECT 1 FROM k WHERE k.msg GLOB '*to be kill, this Tgid is ' || d.pid
                                       AND k.ts BETWEEN d.ts - 5e9 AND d.ts) THEN 'UniStab'
                        WHEN EXISTS (SELECT 1 FROM k WHERE k.tag = 'am_kill' AND k.msg GLOB '* PID=' || d.pid || ' *'
                                       AND k.ts BETWEEN d.ts - 5e9 AND d.ts) THEN 'am_kill' END AS killer
            FROM top d ORDER BY d.adj, d.ts
            """, ctx);
        if (kills is { Rows.Count: > 0 })
        {
            o["important_kills"] = TableFormat.ToJson(kills, 10);
            // Without a killer's log line the process may simply have exited (gms.unstable restarts itself): no hint.
            object?[]? k = kills.Rows.FirstOrDefault(r => r[3] is not null && Convert.ToInt64(r[2] ?? 0L) <= 700);
            if (k is not null)
            {
                long adj = Convert.ToInt64(k[2] ?? 0L);
                string level = adj switch { < 200 => "visible", < 500 => "perceptible", < 600 => "service", < 700 => "home", _ => "previous app" };
                hints.Add($"{k[1]} was killed at oom_adj {adj} ({level}) by {k[3]} (important_kills): "
                    + "returning to it is a cold start. Check the killer's policy (lmkd minfree / UniStab cleanup levels) for apps "
                    + "the user just used.");
            }
        }

        // Platform dex verified at run time: repeatedly (a private class loader, no odex applies) or once for long
        // (an APEX app not compiled yet).
        QueryTable? reverify = await TryAsync(state, s, """
            SELECT SUBSTR(name, 17) AS dex, COUNT(*) AS verifies, ROUND(SUM(dur) / 1e6, 1) AS total_ms,
                   ROUND(MAX(dur) / 1e6, 1) AS max_ms
            FROM slice
            WHERE name GLOB 'Verify dex file /*'
              AND (name GLOB 'Verify dex file /system*' OR name GLOB 'Verify dex file /apex/*'
                   OR name GLOB 'Verify dex file /product/*' OR name GLOB 'Verify dex file /vendor/*')
            GROUP BY name HAVING verifies >= 3 OR SUM(dur) >= 200e6 ORDER BY total_ms DESC LIMIT 3
            """, ctx);
        if (reverify is { Rows.Count: > 0 })
        {
            o["dex_reverify"] = TableFormat.ToJson(reverify, 3);
            if (Convert.ToInt64(reverify.Rows[0][1] ?? 0L) < 3)
            {
                hints.Add($"Platform dex {reverify.Rows[0][0]} was verified at run time ({reverify.Rows[0][2]} ms) (dex_reverify): it has "
                    + "no usable odex / vdex. APEX apps cannot be preopted and stay run-from-apk until the first background dexopt "
                    + "(fresh flash / OTA; `pm art dump <pkg>`); until then each process start pays this.");
            }
            else if (Convert.ToDouble(reverify.Rows[0][2] ?? 0.0) >= 100)
            {
                hints.Add($"Platform dex {reverify.Rows[0][0]} was verified {reverify.Rows[0][1]} times ({reverify.Rows[0][2]} ms) "
                    + "(dex_reverify): something opens it through its own class loader in each process, where no odex "
                    + "applies. If the jar is on BOOTCLASSPATH, load the class through the boot class loader instead.");
            }
        }

        // CachedAppOptimizer compactions per app with the app's own GC time (a running background app pages it back in).
        QueryTable? compact = await TryAsync(state, s, """
            WITH c AS (SELECT msg, INSTR(msg, 'Process Name="') + 14 AS pn, INSTR(msg, ' Time=') + 6 AS tp
                       FROM android_logs WHERE tag = 'am_compact' AND msg GLOB '*Process Name="*' AND msg GLOB '* Time=*'),
            x AS (SELECT SUBSTR(msg, pn, INSTR(SUBSTR(msg, pn), '"') - 1) AS process,
                         CAST(SUBSTR(msg, tp, INSTR(SUBSTR(msg, tp) || ' ', ' ') - 1) AS INT) AS ms FROM c)
            SELECT process, COUNT(*) AS compactions, SUM(ms) AS total_ms, MAX(ms) AS max_ms,
                   (SELECT ROUND(SUM(sl.dur) / 1e6) FROM slice sl JOIN thread_track tt ON sl.track_id = tt.id
                      JOIN thread t USING (utid) JOIN process p USING (upid)
                    WHERE p.name = x.process AND t.name = 'HeapTaskDaemon' AND sl.depth = 0) AS app_gc_ms
            FROM x GROUP BY process ORDER BY total_ms DESC LIMIT 5
            """, ctx);
        if (compact is { Rows.Count: > 0 })
        {
            o["app_compactions"] = TableFormat.ToJson(compact, 5);
            object?[] top = compact.Rows[0];
            if (Convert.ToInt64(top[3] ?? 0L) >= 2000)
            {
                hints.Add($"Compacting {top[0]} took up to {top[3]} ms ({top[1]} compactions, {top[2]} ms; its own GC ran {top[4] ?? 0} ms) "
                    + "(app_compactions): an app that keeps running in the background pages its heap back in while system_server "
                    + "pages it out. Check why it is not frozen (am_compact setAdj) and A/B device_config activity_manager use_compaction.");
            }
        }

        // Noisiest log tags (E / W lines pass any log.tag filter); Unisoc kernel tags' "[T633@C4] " prefix is stripped.
        QueryTable? noisy = await TryAsync(state, s, """
            SELECT IIF(tag GLOB '[[]T*@C*] *', SUBSTR(tag, INSTR(tag, '] ') + 2), tag) AS tag, CASE prio WHEN 2 THEN 'V' WHEN 3 THEN 'D' WHEN 4 THEN 'I' WHEN 5 THEN 'W' WHEN 6 THEN 'E' ELSE 'F' END AS level,
                   COUNT(*) AS lines, ROUND(COUNT(*) * 1e9 / (SELECT MAX(end_ts - start_ts, 1) FROM trace_bounds), 1) AS per_s
            FROM android_logs GROUP BY 1, prio ORDER BY lines DESC LIMIT 5
            """, ctx);
        if (noisy is { Rows.Count: > 0 })
        {
            o["noisy_log_tags"] = TableFormat.ToJson(noisy, 5);
            if (Convert.ToDouble(noisy.Rows[0][3] ?? 0.0) >= 50)
            {
                hints.Add($"{noisy.Rows[0][0]} logs {noisy.Rows[0][3]} lines/s at level {noisy.Rows[0][1]} (noisy_log_tags): every line costs "
                    + "the caller, logd and each log reader (logcat, ylog, the trace). Drop or rate-limit the call site; "
                    + "W / E lines are not filtered by log.tag.");
            }
        }

        // Main thread woken by vsync (SF "app" thread) while RenderThread hardly runs: animating without drawing
        // (stopped activity, or screen off where SF keeps a synthetic vsync).
        QueryTable? vsync = await TryAsync(state, s, """
            WITH sf AS (SELECT t.utid FROM thread t JOIN process p USING (upid) WHERE p.name GLOB '*/surfaceflinger' AND t.name = 'app'),
            main AS (SELECT t.utid, t.upid FROM thread t JOIN process p USING (upid)
                     WHERE t.tid = p.pid AND p.name GLOB '*.*' AND p.name NOT GLOB '/*'),
            w AS (SELECT m.upid, COUNT(*) AS n FROM thread_state ts JOIN main m USING (utid)
                  WHERE ts.state = 'R' AND ts.waker_utid IN (SELECT utid FROM sf) GROUP BY m.upid),
            rt AS (SELECT t.upid, COUNT(*) AS n FROM sched s JOIN thread t USING (utid) WHERE t.name = 'RenderThread' GROUP BY t.upid),
            mc AS (SELECT m.upid, SUM(s.dur) AS d FROM sched s JOIN main m USING (utid) GROUP BY m.upid)
            SELECT p.name AS process, w.n AS vsync_wakeups,
                   ROUND(w.n * 1e9 / (SELECT MAX(end_ts - start_ts, 1) FROM trace_bounds), 1) AS per_s,
                   COALESCE(rt.n, 0) AS render_thread_runs, ROUND(mc.d / 1e6, 1) AS main_cpu_ms
            FROM w JOIN process p USING (upid) LEFT JOIN rt USING (upid) LEFT JOIN mc USING (upid)
            ORDER BY w.n DESC LIMIT 5
            """, ctx);
        if (vsync is { Rows.Count: > 0 })
        {
            o["vsync_consumers"] = TableFormat.ToJson(vsync, 5);
            foreach (object?[] r in vsync.Rows)
            {
                long wakeups = Convert.ToInt64(r[1] ?? 0L);
                if (Convert.ToDouble(r[2] ?? 0.0) >= 20 && Convert.ToInt64(r[3] ?? 0L) * 5 < wakeups)
                {
                    hints.Add($"{r[0]} takes vsync {r[2]}/s but its RenderThread ran {r[3]} times (vsync_consumers): Choreographer "
                        + $"callbacks without drawing ({r[4]} ms main-thread CPU, plus SurfaceFlinger) - an animation left running "
                        + "in a stopped activity or with the screen off. Stop it in onStop / when the display turns off.");
                    break;
                }
            }
        }

        QueryTable? doctor = await TryAsync(state, s, """
            SELECT key, title, confidence, remediation FROM __intrinsic_trace_diagnostics LIMIT 10
            """, ctx);
        if (doctor is { Rows.Count: > 0 })
        {
            o["capture_diagnostics"] = TableFormat.ToJson(doctor, 10, 200);
            hints.Add("trace_processor flagged capture-config problems (capture_diagnostics); fix them for the next capture.");
        }

        QueryTable? startups = await TryAsync(state, s, """
            INCLUDE PERFETTO MODULE android.startup.startups;
            SELECT startup_id, package, startup_type, ts, ROUND(dur / 1e6, 1) AS dur_ms
            FROM android_startups ORDER BY ts LIMIT 30
            """, ctx);
        if (startups is { Rows.Count: > 0 })
        {
            o["startups"] = TableFormat.ToJson(startups, 30);
            hints.Add("App startups detected: android_startups (pass startup_id for a breakdown).");
        }

        // Shadow processes are folded into their owner so busy threads are not attributed to nameless pids.
        QueryTable? cpu = await TryAsync(state, s, $"""
            WITH owner AS (SELECT t.utid, {ProcessSelector.OwnerUpidOf("t")} AS owner FROM thread t LEFT JOIN process p USING (upid)),
            per AS (SELECT o.owner AS upid, SUM(s.dur) AS d
                    FROM sched s JOIN owner o USING (utid) WHERE s.utid != 0 GROUP BY 1)
            SELECT per.upid, p.pid, p.name AS process, ROUND(per.d / 1e6, 1) AS cpu_ms
            FROM per LEFT JOIN process p USING (upid) ORDER BY per.d DESC LIMIT 10
            """, ctx);
        if (cpu is { Rows.Count: > 0 })
        {
            o["top_cpu_processes"] = TableFormat.ToJson(cpu, 10);
        }

        // Capture (traced_probes) and log daemons, incl. their children, often top idle traces: report their share.
        QueryTable? overhead = await TryAsync(state, s, """
            WITH RECURSIVE
            roots(upid, kind) AS (
              SELECT upid, IIF(name GLOB '*traced*' OR name GLOB '*perfetto' OR name GLOB '*heapprofd*', 'capture', 'logging')
              FROM process WHERE name GLOB '*traced*' OR name GLOB '*perfetto' OR name GLOB '*heapprofd*'
                 OR name GLOB '*/logd' OR name GLOB '*ylog*' OR name GLOB '*slog*' OR name GLOB '*logcat*' OR name GLOB '*logkat*'),
            tree(upid, kind) AS (
              SELECT upid, kind FROM roots
              UNION SELECT p.upid, tree.kind FROM process p JOIN tree ON p.parent_upid = tree.upid),
            k AS (SELECT upid, MIN(kind) AS kind FROM tree GROUP BY upid)
            SELECT ROUND(SUM(s.dur) / 1e6, 1) AS total_cpu_ms,
                   ROUND(SUM(IIF(k.kind = 'capture', s.dur, 0)) / 1e6, 1) AS capture_ms,
                   ROUND(SUM(IIF(k.kind = 'logging', s.dur, 0)) / 1e6, 1) AS logging_ms
            FROM sched s JOIN thread t USING (utid) LEFT JOIN k ON k.upid = t.upid WHERE s.utid != 0
            """, ctx);
        if (overhead is { Rows.Count: > 0 } && overhead.Rows[0][0] is not null)
        {
            double total = Convert.ToDouble(overhead.Rows[0][0]);
            double capture = Convert.ToDouble(overhead.Rows[0][1] ?? 0.0);
            double logging = Convert.ToDouble(overhead.Rows[0][2] ?? 0.0);
            double Pct(double v) => total > 0 ? Math.Round(100 * v / total, 1) : 0;
            o["cpu_overhead"] = new JsonObject
            {
                ["total_cpu_ms"] = total,
                ["capture_ms"] = capture,
                ["capture_pct"] = Pct(capture),
                ["logging_ms"] = logging,
                ["logging_pct"] = Pct(logging)
            };
            if (Pct(capture) >= 10)
            {
                hints.Add($"The capture itself (traced / traced_probes / perfetto) is {Pct(capture)}% of all CPU in this trace: "
                    + "discount it. For idle / power measurements record with record_trace config=idle (process list once, "
                    + "no atrace, 5 s memory counters: ~60-70 % less capture CPU); what remains there is ftrace draining.");
            }
            if (Pct(logging) >= 15)
            {
                hints.Add($"Log daemons (logd / ylog / slog / logcat, incl. their child processes) are {Pct(logging)}% of all CPU: check android_logs for "
                    + "the noisiest tags and the vendor log collectors.");
            }
        }

        QueryTable? heap = await TryAsync(state, s, "SELECT COUNT(*) FROM heap_graph_object", ctx);
        if (heap is { Rows.Count: > 0 } && Convert.ToInt64(heap.Rows[0][0] ?? 0L) > 0)
        {
            hints.Add($"Java heap dump present ({heap.Rows[0][0]} objects): java_heap.");
            // A heap-dump capture has no sched data by design.
            hints.RemoveAll(h => h.StartsWith("NO scheduling data", StringComparison.Ordinal));
        }

        if (state.Aborted is not null)
        {
            o["overview_incomplete"] = state.Aborted;
        }
        // Where the time went (load = parse; overview = these queries): makes slow traces diagnosable from the output.
        o["timing"] = new JsonObject
        {
            ["load_s"] = Math.Round(s.LastLoadSeconds, 2),
            ["overview_ms"] = Math.Round(state.Steps.Sum(x => x.Ms), 1),
            ["slowest_overview_steps"] = new JsonArray(state.Steps.OrderByDescending(x => x.Ms).Take(3)
                .Select(x => (JsonNode?)new JsonObject { ["step"] = x.Label, ["ms"] = Math.Round(x.Ms, 1) }).ToArray())
        };
        if (hints.Count > 0)
        {
            o["hints"] = new JsonArray(hints.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
        }
        return o;
    }

    // Overview parts are best-effort; after a timeout/crash stop issuing queries
    // so we don't trigger a reload per remaining section.
    private async Task<QueryTable?> TryAsync(OverviewState state, TraceSession s, string sql, RequestContext ctx)
    {
        if (state.Aborted is not null)
        {
            return null;
        }
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            QueryTable t = await Traces.QueryAsync(s, sql, ctx, OverviewTimeout, 100).ConfigureAwait(false);
            return t.Error is null ? t : null;
        }
        catch (ToolException ex)
        {
            state.Aborted = ex.Message;
            return null;
        }
        finally
        {
            state.Steps.Add((Label(sql), sw.Elapsed.TotalMilliseconds));
        }
    }

    // First meaningful words of the query, enough to tell the overview steps apart.
    private static string Label(string sql)
    {
        string flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 70 ? flat : flat[..70] + "...";
    }

    private sealed class OverviewState
    {
        public string? Aborted { get; set; }

        public List<(string Label, double Ms)> Steps { get; } = new();
    }
}

internal sealed class ListTracesTool : TraceToolBase
{
    public ListTracesTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("list_traces",
        "List traces loaded in this session with their trace_id, file, whether the trace processor is currently " +
        "resident (evicted ones reload on demand) and which one is the default.",
        new SchemaBuilder());

    public override Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        JsonArray arr = new();
        foreach (TraceSession s in Traces.All)
        {
            arr.Add(new JsonObject
            {
                ["trace_id"] = s.Id,
                ["file"] = s.FileName,
                ["path"] = s.Path,
                ["resident"] = s.IsLive,
                ["last_load_seconds"] = Math.Round(s.LastLoadSeconds, 2),
                ["prewarmed_modules"] = s.PrewarmedModules,
                ["active"] = s.Id == Traces.ActiveId
            });
        }
        return Task.FromResult(ToolCallResult.Json(new JsonObject { ["traces"] = arr }));
    }
}

internal sealed class UnloadTraceTool : TraceToolBase
{
    public UnloadTraceTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "unload_trace",
        Description = "Forget a loaded trace and stop its trace processor to free memory.",
        InputSchema = new SchemaBuilder().Str("trace_id", "Trace handle to unload.", isRequired: true).Build()
    };

    public override Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        string id = Args.RequireString(args, "trace_id");
        return Task.FromResult(Traces.Unload(id)
            ? ToolCallResult.Json(new JsonObject { ["unloaded"] = id, ["active_trace_id"] = Traces.ActiveId })
            : ToolCallResult.Error($"Unknown trace_id '{id}'."));
    }
}
