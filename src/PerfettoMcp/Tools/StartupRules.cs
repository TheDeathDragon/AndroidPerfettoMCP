using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

// "Why was this launch slow": metrics for one startup window, each checked against warning / critical
// thresholds (after SmartPerfetto's startup_slow_reasons).
internal static partial class StartupRules
{
    private sealed record Rule(string Id, string Metric, double Warn, double Crit, string Unit, string Meaning, string Suggestion);

    private static readonly Rule[] Rules =
    [
        new("MAIN_BINDER", "main_binder_ms", 30, 100, "ms", "Sync binder calls on the main thread",
            "Move IPC off the main thread or batch it; check the server side with android_binder_txns."),
        new("MAIN_LOCK", "main_lock_ms", 10, 50, "ms", "Main thread blocked on Java monitors",
            "See lock_contention for the holder; avoid locks shared with background init."),
        new("MAIN_IO", "main_d_io_ms", 30, 100, "ms", "Main thread in IO wait (D-state)",
            "Reads on the main thread (dex/odex, resources, SharedPreferences, databases); see io_summary."),
        new("MAIN_D_OTHER", "main_d_other_ms", 30, 100, "ms", "Main thread in non-IO D-state",
            "Kernel waits (mmap/mprotect, reclaim, driver locks); see thread_state_breakdown blocked_function."),
        new("MAIN_RUNNABLE", "main_runnable_pct", 10, 15, "% of startup", "Main thread runnable but not running",
            "CPU contention or low priority at launch; check cpu_top_threads for competitors."),
        new("OPEN_DEX", "open_dex_ms", 50, 150, "ms", "OpenDexFilesFromOat on the main thread",
            "Large or uncompressed dex / missing odex; check the compiler filter (ODEX_FILTER) and APK alignment."),
        new("APP_IMAGE_MADVISE", "app_image_ms", 50, 150, "ms", "AppImage loading / madvise of APK and dex",
            "Page-in of APK/dex at launch; consider app image + profile, check storage read latency."),
        new("CLASS_LOADING", "class_loads_main", 500, 2000, "classes", "Classes loaded on the main thread",
            "Many classes resolved at runtime: use a baseline/startup profile (speed-profile) and lazy init."),
        new("JIT", "jit_compiles", 20, 50, "compiles", "JIT compilations in the app during launch",
            "Code not AOT-compiled for the launch path: ship a profile / speed-profile compile."),
        new("VERIFY_CLASS", "verify_class", 10, 50, "classes", "Runtime class verification",
            "Classes failing verification at build time are verified at runtime; check dex2oat verify failures."),
        new("INFLATE", "inflate_ms", 200, 450, "ms", "Layout inflation on the main thread",
            "Flatten layouts, inflate lazily (ViewStub), avoid inflating off-screen content at launch."),
        new("DLOPEN", "dlopen_ms", 50, 200, "ms", "dlopen of native libraries on the main thread",
            "Load native libraries lazily or on a background thread; reduce library size / relocations."),
        new("WEBVIEW", "webview_ms", 100, 300, "ms", "WebView initialization during launch",
            "Defer WebView creation until it is shown."),
        new("STORAGE", "storage_ms", 10, 50, "ms", "SharedPreferences / SQLite / fsync on the main thread",
            "Use apply() instead of commit(), load preferences asynchronously, move DB work off the main thread."),
        new("GC", "gc_ms", 50, 150, "ms", "ART GC in the app process during launch",
            "Allocation burst at startup; check android_gc and reduce temporary allocations."),
        new("BIND_APPLICATION", "bind_app_pct", 35, 60, "% of startup", "bindApplication share of the launch",
            "Heavy Application.onCreate / ContentProvider init; defer SDK initialization (App Startup library)."),
        new("CONCURRENT_FORKS", "other_app_starts", 2, 4, "processes", "Other app processes (zygote forks) started within 5 s of the launch",
            "Competing app starts (pushes, boot jobs, providers) steal CPU and IO; check list_processes start_ts."),
        new("SHELL_SPAWNS", "shell_spawns", 20, 100, "processes", "Shell commands spawned within 5 s of the launch",
            "A test harness / script is forking commands (date, cat, getprop ...) during the measurement; it perturbs timing."),
        new("DEX2OAT", "dex2oat_cpu_ms", 50, 200, "ms", "dex2oat / installd running during the launch",
            "Background compilation competing with the launch; reschedule or test after bg-dexopt finished."),
        new("CPU_FREQ_LOW", "big_core_freq_pct", -90, -70, "% of peak", "Big-core average frequency below its peak",
            "Launch boost missing or thermal throttling; check cpu_frequency and the power HAL launch hint."),
        new("DIRECT_RECLAIM", "app_direct_reclaim_ms", 1, 20, "ms", "App threads stalled in direct memory reclaim",
            "Memory pressure at launch; see memory_summary (MemAvailable, kswapd, LMK)."),
        new("KSWAPD", "kswapd_pct", 15, 40, "% of startup", "kswapd CPU during the launch",
            "Background reclaim competing for CPU; the device is short of free memory."),
        new("PROFILER_OVERHEAD", "profiler_cpu_ms", 100, 300, "ms", "traced_perf / heapprofd CPU during the launch",
            "The capture itself perturbs timing; compare with a trace without linux.perf callstack sampling."),
    ];

    // Which tool call confirms each finding; the rule itself only shows a metric crossing a threshold.
    private static readonly Dictionary<string, string> NextEvidence = new(StringComparer.Ordinal)
    {
        ["MAIN_BINDER"] = "android_binder_txns side=client with startup_id (slowest_breakdown for server-side reasons)",
        ["MAIN_LOCK"] = "lock_contention with startup_id, main_thread_only=true",
        ["MAIN_IO"] = "io_summary with startup_id and the app's process_name",
        ["MAIN_D_OTHER"] = "thread_state_breakdown with startup_id (blocked_function), wakeup_chain",
        ["MAIN_RUNNABLE"] = "cpu_top_threads with startup_id; list_threads_in_process (prio / nice)",
        ["OPEN_DEX"] = "dex_files in this report (filter / status); io_summary for dex/odex reads",
        ["APP_IMAGE_MADVISE"] = "io_summary page_cache_adds and block_devices with startup_id",
        ["CLASS_LOADING"] = "dex_files (compiler filter); slice_hotspots name_glob='L*;' on the main thread",
        ["JIT"] = "dex_files (compiler filter); slice_hotspots name_glob='JIT compiling*'",
        ["VERIFY_CLASS"] = "slice_hotspots name_glob='VerifyClass*'",
        ["INFLATE"] = "slice_hotspots name_glob='inflate' main_thread_only=true; main_thread_blocking_calls",
        ["DLOPEN"] = "slice_hotspots name_glob='dlopen*'",
        ["WEBVIEW"] = "slice_hotspots name_glob='*WebView*'",
        ["STORAGE"] = "slice_hotspots on the main thread for SharedPreferences / sqlite / fsync",
        ["GC"] = "android_gc with startup_id",
        ["BIND_APPLICATION"] = "main_thread_slices / slice_hotspots under bindApplication (ContentProvider, SDK init)",
        ["CONCURRENT_FORKS"] = "list_processes (start_ts near the launch), cpu_top_threads group_by=process",
        ["SHELL_SPAWNS"] = "list_processes name_filter for the spawned commands; check the capture / test script",
        ["DEX2OAT"] = "cpu_top_threads with startup_id group_by=process",
        ["CPU_FREQ_LOW"] = "cpu_frequency with startup_id",
        ["DIRECT_RECLAIM"] = "memory_summary with startup_id",
        ["KSWAPD"] = "memory_summary with startup_id",
        ["PROFILER_OVERHEAD"] = "re-capture without linux.perf callstack sampling and compare with compare_traces",
        ["ODEX_FILTER"] = "dex_files in this report; device: dumpsys package dexopt <package>",
    };

    public static async Task<JsonObject> EvaluateAsync(long startupId, Func<string, Task<QueryTable>> query)
    {
        string id = startupId.ToString();
        QueryTable m = await query($"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            INCLUDE PERFETTO MODULE android.binder;
            INCLUDE PERFETTO MODULE android.monitor_contention;
            INCLUDE PERFETTO MODULE android.garbage_collection;
            INCLUDE PERFETTO MODULE android.memory.lmk;
            WITH st AS (SELECT ts AS s, ts + dur AS e, dur FROM android_startups WHERE startup_id = {id}),
            up AS (SELECT upid FROM android_startup_processes WHERE startup_id = {id}),
            mt AS (SELECT t.utid FROM thread t JOIN process p USING (upid) WHERE p.upid IN up AND t.tid = p.pid),
            at AS (SELECT t.utid FROM thread t WHERE t.upid IN up),
            ms AS (SELECT x.name, x.dur FROM slice x JOIN thread_track tt ON x.track_id = tt.id, st
                   WHERE tt.utid IN mt AND x.ts >= st.s AND x.ts < st.e AND x.dur > 0),
            aps AS (SELECT x.name, x.dur, tt.utid FROM slice x JOIN thread_track tt ON x.track_id = tt.id, st
                    WHERE tt.utid IN at AND x.ts >= st.s AND x.ts < st.e),
            cpu AS (SELECT t.name AS thread, p.name AS process, x.dur, x.ts FROM sched x JOIN thread t USING (utid)
                    LEFT JOIN process p USING (upid), st WHERE x.ts < st.e AND x.ts + x.dur > st.s)
            SELECT
              (SELECT dur FROM st) / 1e6 AS startup_ms,
              (SELECT SUM(b.client_dur) FROM android_binder_txns b, st WHERE b.is_sync AND b.client_utid IN mt AND b.client_ts BETWEEN st.s AND st.e) / 1e6 AS main_binder_ms,
              (SELECT SUM(c.dur) FROM android_monitor_contention c, st WHERE c.blocked_utid IN mt AND c.ts BETWEEN st.s AND st.e) / 1e6 AS main_lock_ms,
              (SELECT SUM(dur) FROM ms WHERE name GLOB 'OpenDexFilesFromOat*') / 1e6 AS open_dex_ms,
              (SELECT SUM(dur) FROM ms WHERE name GLOB 'AppImage:*' OR name GLOB 'madvising *') / 1e6 AS app_image_ms,
              (SELECT COUNT(*) FROM ms WHERE name GLOB 'L*;') AS class_loads_main,
              (SELECT COUNT(*) FROM aps WHERE name GLOB 'JIT compiling *') AS jit_compiles,
              (SELECT COUNT(*) FROM aps WHERE name GLOB 'VerifyClass *') AS verify_class,
              (SELECT SUM(dur) FROM ms WHERE name = 'inflate') / 1e6 AS inflate_ms,
              (SELECT SUM(dur) FROM ms WHERE name GLOB 'dlopen*') / 1e6 AS dlopen_ms,
              (SELECT SUM(dur) FROM aps WHERE name GLOB '*WebView*' AND dur > 0) / 1e6 AS webview_ms,
              (SELECT SUM(dur) FROM ms WHERE name GLOB '*SharedPreferences*' OR name GLOB '*sqlite*' OR name GLOB '*SQLite*'
                 OR name GLOB 'QueuedWork*' OR name GLOB '*fsync*') / 1e6 AS storage_ms,
              (SELECT SUM(g.gc_dur) FROM android_garbage_collection_events g, st WHERE g.upid IN up AND g.gc_ts < st.e AND g.gc_ts + g.gc_dur > st.s) / 1e6 AS gc_ms,
              100.0 * (SELECT MAX(dur) FROM ms WHERE name = 'bindApplication') / (SELECT dur FROM st) AS bind_app_pct,
              (SELECT COUNT(*) FROM process p JOIN process pp ON pp.upid = p.parent_upid, st
                 WHERE p.start_ts BETWEEN st.s - 5e9 AND st.e + 5e9 AND p.upid NOT IN up
                   AND (pp.name GLOB 'zygote*' OR pp.name GLOB 'usap*')) AS other_app_starts,
              (SELECT COUNT(*) FROM process p JOIN process pp ON pp.upid = p.parent_upid, st
                 WHERE p.start_ts BETWEEN st.s - 5e9 AND st.e + 5e9 AND (pp.name GLOB '*sh' OR pp.name GLOB '*adbd')) AS shell_spawns,
              (SELECT SUM(dur) FROM cpu WHERE process GLOB '*dex2oat*' OR process GLOB '*installd*') / 1e6 AS dex2oat_cpu_ms,
              (SELECT SUM(dur) FROM cpu WHERE process GLOB '*traced_perf*' OR process GLOB '*heapprofd*') / 1e6 AS profiler_cpu_ms,
              100.0 * (SELECT SUM(dur) FROM cpu WHERE thread GLOB 'kswapd*') / (SELECT dur FROM st) AS kswapd_pct,
              (SELECT SUM(dur) FROM aps WHERE name = 'mm_vmscan_direct_reclaim') / 1e6 AS app_direct_reclaim_ms,
              (SELECT COUNT(*) FROM android_lmk_events l, st WHERE l.ts BETWEEN st.s AND st.e) AS lmk_kills
            """).ConfigureAwait(false);
        Dictionary<string, double?> v = new();
        if (m.Rows.Count > 0)
        {
            for (int i = 0; i < m.Columns.Count; i++)
            {
                v[m.Columns[i]] = m.Rows[0][i] is null ? null : Convert.ToDouble(m.Rows[0][i]);
            }
        }

        QueryTable states = await query($"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            WITH st AS (SELECT ts AS s, ts + dur AS e FROM android_startups WHERE startup_id = {id})
            SELECT x.state, x.io_wait, SUM(MIN(IIF(x.dur < 0, st.e, x.ts + x.dur), st.e) - MAX(x.ts, st.s)) / 1e6 AS ms
            FROM thread_state x JOIN android_startup_threads t ON t.utid = x.utid AND t.startup_id = {id} AND t.is_main_thread, st
            WHERE x.ts < st.e AND IIF(x.dur < 0, st.e, x.ts + x.dur) > st.s
            GROUP BY 1, 2
            """).ConfigureAwait(false);
        double startupMs = v.GetValueOrDefault("startup_ms") ?? 0;
        double StateMs(Func<string, long?, bool> pick) => states.Rows
            .Where(r => pick((string?)r[0] ?? string.Empty, r[1] as long?)).Sum(r => Convert.ToDouble(r[2] ?? 0));
        v["main_d_io_ms"] = StateMs((st, io) => st.StartsWith('D') && io == 1);
        v["main_d_other_ms"] = StateMs((st, io) => st.StartsWith('D') && io != 1);
        v["main_runnable_pct"] = startupMs > 0 ? 100 * StateMs((st, _) => st is "R" or "R+") / startupMs : null;

        QueryTable freq = await query($"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            INCLUDE PERFETTO MODULE linux.cpu.frequency;
            WITH st AS (SELECT ts AS s, ts + dur AS e FROM android_startups WHERE startup_id = {id}),
            peak AS (SELECT cpu, MAX(freq) AS pk FROM cpu_frequency_counters GROUP BY cpu),
            big AS (SELECT cpu FROM peak WHERE pk = (SELECT MAX(pk) FROM peak))
            SELECT 100.0 * SUM(f.freq * (MIN(f.ts + f.dur, st.e) - MAX(f.ts, st.s))) / SUM(MIN(f.ts + f.dur, st.e) - MAX(f.ts, st.s))
                   / (SELECT MAX(pk) FROM peak) AS pct
            FROM cpu_frequency_counters f, st WHERE f.cpu IN big AND f.ts < st.e AND f.ts + f.dur > st.s
            """).ConfigureAwait(false);
        if (freq.Rows.Count > 0 && freq.Rows[0][0] is { } pct)
        {
            v["big_core_freq_pct"] = -Convert.ToDouble(pct); // negated so "lower than" fits the >= thresholds
        }

        JsonArray findings = new();
        foreach (Rule r in Rules)
        {
            if (v.GetValueOrDefault(r.Metric) is not { } val || val < r.Warn)
            {
                continue;
            }
            double shown = r.Metric == "big_core_freq_pct" ? -val : val;
            findings.Add(new JsonObject
            {
                ["severity"] = val >= r.Crit ? "critical" : "warning",
                ["rule"] = r.Id,
                ["value"] = Math.Round(shown, 1),
                ["unit"] = r.Unit,
                ["threshold"] = r.Metric == "big_core_freq_pct" ? $"< {-r.Warn} / < {-r.Crit}" : $">= {r.Warn} / >= {r.Crit}",
                ["what"] = r.Meaning,
                ["suggestion"] = r.Suggestion,
                ["next_evidence"] = NextEvidence.GetValueOrDefault(r.Id)
            });
            if (r.Id == "DEX2OAT")
            {
                await ExplainInstalldAsync(id, query, (JsonObject)findings[^1]!).ConfigureAwait(false);
            }
        }

        // The stdlib clamps TTFD to TTID: reportFullyDrawn() before the first frame hides the real TTFD.
        QueryTable rfd = await query($"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            INCLUDE PERFETTO MODULE android.startup.time_to_display;
            WITH st AS (SELECT s.ts, d.time_to_initial_display AS ttid FROM android_startups s
                        JOIN android_startup_time_to_display d USING (startup_id) WHERE s.startup_id = {id})
            SELECT (MIN(x.ts) - st.ts) / 1e6 AS rfd_ms, st.ttid / 1e6 AS ttid_ms
            FROM slice x JOIN thread_track tt ON x.track_id = tt.id JOIN thread t USING (utid), st
            WHERE t.upid IN (SELECT upid FROM android_startup_processes WHERE startup_id = {id})
              AND x.name GLOB 'reportFullyDrawn*' AND x.ts BETWEEN st.ts AND st.ts + 30e9
            GROUP BY st.ts, st.ttid
            """).ConfigureAwait(false);
        if (rfd.Rows.Count > 0 && rfd.Rows[0] is [object rfdMs, object ttidMs]
            && Convert.ToDouble(rfdMs) < Convert.ToDouble(ttidMs))
        {
            findings.Add(new JsonObject
            {
                ["severity"] = "info",
                ["rule"] = "REPORT_FULLY_DRAWN_EARLY",
                ["value"] = Math.Round(Convert.ToDouble(rfdMs), 1),
                ["what"] = $"reportFullyDrawn() at {Convert.ToDouble(rfdMs):0} ms, before the first frame (TTID "
                    + $"{Convert.ToDouble(ttidMs):0} ms): TTFD is clamped to TTID and does not measure full content",
                ["suggestion"] = "Call reportFullyDrawn() when the real content is shown (after data loading / the "
                    + "list is populated), e.g. via FullyDrawnReporter, not in onCreate / onResume.",
                ["next_evidence"] = "slice_hotspots name_glob='reportFullyDrawn*' (its thread and caller slice)"
            });
        }

        JsonArray odex = await OdexAsync(id, query).ConfigureAwait(false);
        foreach (JsonNode? o in odex)
        {
            if (o?["flag"] is null)
            {
                continue;
            }
            bool app = o["kind"]!.GetValue<string>() == "app";
            findings.Add(new JsonObject
            {
                ["severity"] = "warning",
                ["rule"] = "ODEX_FILTER",
                ["value"] = o["filter"]?.GetValue<string>(),
                ["what"] = $"{(app ? "App code" : "Shared library")} {o["dex"] ?? o["location"]} runs with filter={o["filter"]} status={o["status"]}",
                ["suggestion"] = app
                    ? "Launch path runs interpreted/JIT. For a preinstalled app set dex_preopt compiler_filter to "
                        + "speed-profile with a profile (or speed); for downloaded apps ship a baseline profile."
                    : "Every process loading this jar interprets/JITs it. Add it to dexpreopt (speed, or a profile), "
                        + "or to the boot / system_server classpath if it is loaded everywhere. If it is already on "
                        + "BOOTCLASSPATH, this is a redundant second copy opened via <uses-library>.",
                ["next_evidence"] = NextEvidence["ODEX_FILTER"]
            });
        }

        List<JsonNode?> ordered = findings.OrderBy(f => f!["severity"]!.GetValue<string>() == "critical" ? 0 : 1).ToList();
        findings.Clear();
        foreach (JsonNode? f in ordered)
        {
            findings.Add(f);
        }
        JsonObject metrics = new();
        foreach (KeyValuePair<string, double?> kv in v.OrderBy(k => k.Key))
        {
            metrics[kv.Key] = kv.Value is null ? null : Math.Round(kv.Key == "big_core_freq_pct" ? -kv.Value.Value : kv.Value.Value, 1);
        }
        return new JsonObject { ["findings"] = findings, ["metrics"] = metrics, ["dex_files"] = odex };
    }

    // installd CPU is not always compilation (e.g. getUserSize for Settings): name the busiest call.
    private static async Task ExplainInstalldAsync(string id, Func<string, Task<QueryTable>> query, JsonObject finding)
    {
        QueryTable t = await query($"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            WITH st AS (SELECT ts AS s, ts + dur AS e FROM android_startups WHERE startup_id = {id})
            SELECT x.name, SUM(MIN(x.ts + x.dur, st.e) - MAX(x.ts, st.s)) / 1e6 AS ms,
                   (SELECT COUNT(*) FROM sched c JOIN thread ct USING (utid) JOIN process cp USING (upid)
                    WHERE cp.name GLOB '*dex2oat*' AND c.ts < st.e AND c.ts + c.dur > st.s) AS dex2oat_slices
            FROM slice x JOIN thread_track tt ON x.track_id = tt.id JOIN thread t USING (utid) JOIN process p USING (upid), st
            WHERE p.name GLOB '*installd*' AND x.name GLOB 'AIDL::*::server' AND x.name NOT GLOB '*::cppServer'
              AND x.ts < st.e AND x.ts + x.dur > st.s
            GROUP BY x.name ORDER BY ms DESC LIMIT 1
            """).ConfigureAwait(false);
        if (t.Rows.Count == 0 || t.Rows[0][0] is not string call || Convert.ToInt64(t.Rows[0][2] ?? 0L) > 0)
        {
            return;
        }
        string method = call.Split("::", StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts ? parts[^2] : call;
        if (method.Contains("dexopt", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        double ms = Math.Round(Convert.ToDouble(t.Rows[0][1]), 1);
        finding["what"] = $"installd busy in {method} ({ms} ms in the launch window), not compiling";
        finding["suggestion"] = method == "getUserSize"
            ? "installd recomputes storage stats for every app (per-uid quota queries plus walks of data / profile / "
                + "dalvik dirs) on each query, e.g. Settings' storage summary via StorageStatsManager.getFreeBytes; "
                + "cache or defer the query on the caller side."
            : "An installd call competes with the launch for CPU/IO; find its client and defer it.";
        finding["next_evidence"] = "execute_sql on android_binder_txns WHERE server_process GLOB '*installd*' (client), then the "
            + "system_server transaction enclosing it";
    }

    // ART's OpenDexFilesFromOat child slice carries location / status / filter; the dex path is on the parent.
    private static async Task<JsonArray> OdexAsync(string id, Func<string, Task<QueryTable>> query)
    {
        QueryTable t = await query($"""
            INCLUDE PERFETTO MODULE android.startup.startups;
            WITH st AS (SELECT ts AS s, ts + dur AS e FROM android_startups WHERE startup_id = {id}),
            up AS (SELECT upid FROM android_startup_processes WHERE startup_id = {id})
            SELECT DISTINCT x.name, (SELECT a.name FROM ancestor_slice(x.id) a WHERE a.name GLOB 'OpenDexFilesFromOat(*'
                ORDER BY a.depth DESC LIMIT 1) AS opener
            FROM slice x JOIN thread_track tt ON x.track_id = tt.id JOIN thread t USING (utid), st
            WHERE t.upid IN up AND x.ts >= st.s AND x.ts < st.e AND x.name GLOB 'location=* status=* filter=*'
            LIMIT 20
            """).ConfigureAwait(false);
        JsonArray arr = new();
        foreach (object?[] r in t.Rows)
        {
            Match m = OdexLine().Match((string)r[0]!);
            if (!m.Success)
            {
                continue;
            }
            string location = m.Groups["loc"].Value;
            string status = m.Groups["status"].Value;
            string filter = m.Groups["filter"].Value;
            string? dex = r[1] is string opener && opener.Length > "OpenDexFilesFromOat()".Length
                ? opener["OpenDexFilesFromOat(".Length..^1]
                : null;
            string path = dex ?? location;
            bool app = path.Contains("/app/", StringComparison.Ordinal) || path.Contains("/priv-app/", StringComparison.Ordinal)
                || path.EndsWith(".apk", StringComparison.Ordinal);
            bool weak = filter is "verify" or "quicken" or "extract" or "run-from-apk" or "assume-verified" or "space"
                || status != "up-to-date";
            JsonObject o = new()
            {
                ["dex"] = dex,
                ["kind"] = app ? "app" : "shared_library",
                ["location"] = location,
                ["status"] = status,
                ["filter"] = filter,
                ["reason"] = m.Groups["reason"].Value
            };
            // "verify" on a prebuilt framework jar is the platform default; a missing oat is actionable.
            if (weak && (app || filter == "run-from-apk" || status != "up-to-date"))
            {
                o["flag"] = app ? "app code not AOT-compiled" : "shared library not AOT-compiled";
            }
            arr.Add(o);
        }
        return arr;
    }

    [GeneratedRegex(@"location=(?<loc>\S+) status=(?<status>\S+) filter=(?<filter>\S+)(?: reason=(?<reason>\S+))?")]
    private static partial Regex OdexLine();
}
