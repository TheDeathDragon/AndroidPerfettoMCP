using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Boot;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class AnalyzeBootchartTool : IToolHandler
{
    // Processes whose first appearance marks a boot phase (matched against the 15-char comm).
    private static readonly (string Label, string[] Names)[] Milestones =
    [
        ("init_second_stage", ["init"]),
        ("ueventd", ["ueventd"]),
        ("logd", ["logd"]),
        ("servicemanager", ["servicemanager"]),
        ("vold", ["vold"]),
        ("surfaceflinger", ["surfaceflinger"]),
        ("bootanimation", ["bootanimation"]),
        ("zygote", ["zygote64", "zygote"]),
        ("system_server", ["system_server"]),
        ("systemui", ["ndroid.systemui", "com.android.systemui"]),
        ("launcher", ["launcher", "launcher3", "nexuslauncher"]),
    ];

    private static readonly string[] LoggingProcs = ["logd", "ylog", "logcat", "logcatd", "slog", "traced_probes"];

    // (from, to, warn above seconds): typical healthy spacing on mid-range devices.
    private static readonly (string From, string To, double WarnS)[] PhaseGaps =
    [
        ("zygote", "system_server", 3),
        ("surfaceflinger", "bootanimation", 2),
        ("system_server", "systemui", 8),
        ("systemui", "launcher", 4),
    ];

    private static JsonObject TopCpuBetween(Bootchart b, double from, double to, int n)
    {
        PsSample? a = b.Ps.LastOrDefault(s => s.Time <= from) ?? b.Ps.FirstOrDefault();
        PsSample? z = b.Ps.LastOrDefault(s => s.Time <= to);
        JsonArray rows = new();
        if (a is not null && z is not null && z.Time > a.Time)
        {
            foreach (IGrouping<string, (string Name, double Ms)> g in z.Procs.Values
                .Select(p => (Name: KernelWorker(p.Name), Ms: (p.CpuJiffies - (a.Procs.TryGetValue(p.Pid, out ProcStat? q) ? q.CpuJiffies : 0)) * 10.0))
                .Where(x => x.Ms > 0).GroupBy(x => x.Name).OrderByDescending(g => g.Sum(x => x.Ms)).Take(n))
            {
                rows.Add(new JsonArray(g.Key, Math.Round(g.Sum(x => x.Ms), 0)));
            }
        }
        return new JsonObject { ["columns"] = new JsonArray("process", "cpu_ms"), ["rows"] = rows };
    }

    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "analyze_bootchart",
        Description = """
            Analyze Android init bootchart data (the /data/bootchart files header, proc_stat.log, proc_ps.log,
            proc_diskstats.log, or bootchart.tgz made from them) without rendering a chart: bootloader stage times
            from the kernel cmdline (UNISOC tspl / tsml / tuboot), when key processes started (init, ueventd, vold,
            surfaceflinger, bootanimation, zygote, system_server, systemui, launcher), a per-second CPU / iowait /
            runnable / blocked timeline, the processes that used the most CPU during boot, disk read / write volume
            and findings (slow bootloader, IO-bound phases, CPU saturation, heavy logging).
            Capture: adb shell touch /data/bootchart/enabled; adb reboot; after boot adb root; adb pull /data/bootchart;
            adb shell rm /data/bootchart/enabled. Covers boot up to sys.boot_completed.
            """,
        InputSchema = new SchemaBuilder()
            .Str("path", "Directory with the bootchart files, or bootchart.tgz.", isRequired: true)
            .Int("limit", "Rows in the top-CPU list (default 15).")
            .Str("focus_process", "Also return this process's CPU growth sample by sample (substring of the name), e.g. a HAL seen in top_cpu_in_gap.")
            .Build(),
        Annotations = Annotations.ReadOnly()
    };

    public Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        string path = Args.RequireString(args, "path").Trim().Trim('"');
        int limit = Args.OptionalInt(args, "limit", 15, 1, 200);
        Bootchart b;
        try
        {
            b = Bootchart.Load(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or FormatException)
        {
            throw new ToolException($"Cannot read bootchart at {path}: {ex.Message}");
        }
        if (b.Stat.Count < 2 || b.Ps.Count == 0)
        {
            throw new ToolException("Bootchart has too few samples (was it captured up to boot completion?).");
        }

        JsonObject report = new();
        Dictionary<string, double> bl = b.BootloaderMs();
        double kernelStart = 0; // uptime 0 = kernel start
        double first = b.Stat[0].Time;
        double last = b.Stat[^1].Time;
        report["build"] = b.Header.GetValueOrDefault("system.release");
        report["span_s"] = new JsonObject { ["first_sample"] = first, ["last_sample"] = last };
        if (bl.Count > 0)
        {
            JsonObject o = new();
            foreach ((string k, double v) in bl)
            {
                o[k] = v;
            }
            o["sum_ms"] = bl.Values.Sum();
            report["bootloader_ms"] = o;
        }

        // Milestones: earliest process start time per label (starttime is exact, sampling only bounds it).
        Dictionary<int, ProcStat> all = new();
        Dictionary<int, double> lastSeen = new();
        foreach (PsSample s in b.Ps)
        {
            foreach ((int pid, ProcStat p) in s.Procs)
            {
                all[pid] = p;
                lastSeen[pid] = s.Time;
            }
        }
        JsonArray milestones = new();
        Dictionary<string, double> starts = new(StringComparer.Ordinal);
        foreach ((string label, string[] names) in Milestones)
        {
            ProcStat? p = all.Values.Where(x => names.Any(n => x.Name.Equals(n, StringComparison.Ordinal)
                    || x.Name.StartsWith(n + " ", StringComparison.Ordinal)
                    || (n.Length > 6 && x.Name.Contains(n, StringComparison.Ordinal))))
                .OrderBy(x => x.StartSeconds).FirstOrDefault();
            if (p is null)
            {
                continue;
            }
            bool ended = lastSeen[p.Pid] < last - 0.01;
            starts[label] = p.StartSeconds;
            milestones.Add(new JsonArray(label, p.Name, p.Pid, Math.Round(Math.Max(p.StartSeconds, kernelStart), 2),
                ended ? Math.Round(lastSeen[p.Pid], 2) : null));
        }
        report["milestones"] = new JsonObject
        {
            ["columns"] = new JsonArray("phase", "process", "pid", "started_s", "gone_by_s"),
            ["rows"] = milestones
        };

        // Gaps between phases that normally follow each other closely, and who used the CPU meanwhile.
        JsonArray gaps = new();
        foreach ((string from, string to, double warnS) in PhaseGaps)
        {
            if (!starts.TryGetValue(from, out double a) || !starts.TryGetValue(to, out double z) || z <= a)
            {
                continue;
            }
            gaps.Add(new JsonObject
            {
                ["from"] = from,
                ["to"] = to,
                ["gap_s"] = Math.Round(z - a, 2),
                ["expected_below_s"] = warnS,
                ["top_cpu_in_gap"] = TopCpuBetween(b, a, z, 6)
            });
        }
        report["phase_gaps"] = gaps;

        JsonArray timeline = new();
        double busyAll = 0, ioAll = 0, totAll = 0;
        int saturatedRun = 0, maxSaturatedRun = 0;
        foreach (IGrouping<int, (StatSample A, StatSample B)> sec in b.Stat.Zip(b.Stat.Skip(1)).GroupBy(p => (int)Math.Floor(p.Second.Time)))
        {
            double busy = sec.Sum(p => p.B.Busy - p.A.Busy);
            double io = sec.Sum(p => p.B.Iowait - p.A.Iowait);
            double tot = sec.Sum(p => p.B.Total - p.A.Total);
            if (tot <= 0)
            {
                continue;
            }
            busyAll += busy;
            ioAll += io;
            totAll += tot;
            double busyPct = 100 * busy / tot;
            saturatedRun = busyPct >= 90 ? saturatedRun + 1 : 0;
            maxSaturatedRun = Math.Max(maxSaturatedRun, saturatedRun);
            timeline.Add(new JsonArray(sec.Key, Math.Round(busyPct, 0), Math.Round(100 * io / tot, 0),
                Math.Round(sec.Average(p => p.B.Running), 1), sec.Max(p => p.B.Blocked)));
        }
        report["timeline"] = new JsonObject
        {
            ["columns"] = new JsonArray("uptime_s", "cpu_busy_pct", "iowait_pct", "avg_running", "max_blocked"),
            ["rows"] = timeline
        };

        Dictionary<int, long> firstCpu = b.Ps[0].Procs.ToDictionary(kv => kv.Key, kv => kv.Value.CpuJiffies);
        Dictionary<string, (double Ms, int Procs)> byName = new(StringComparer.Ordinal);
        foreach ((int pid, ProcStat p) in all)
        {
            double ms = (p.CpuJiffies - firstCpu.GetValueOrDefault(pid)) * 10.0;
            if (ms <= 0)
            {
                continue;
            }
            string key = KernelWorker(p.Name);
            (double m, int n) = byName.GetValueOrDefault(key);
            byName[key] = (m + ms, n + 1);
        }
        double cpuSum = byName.Values.Sum(v => v.Ms);
        JsonArray top = new();
        foreach ((string name, (double ms, int n)) in byName.OrderByDescending(kv => kv.Value.Ms).Take(limit))
        {
            top.Add(new JsonArray(name, n, Math.Round(ms, 0), Math.Round(100 * ms / Math.Max(1, cpuSum), 1)));
        }
        report["top_cpu"] = new JsonObject
        {
            ["columns"] = new JsonArray("process", "pids", "cpu_ms", "pct_of_boot_cpu"),
            ["rows"] = top
        };

        // Disk volume per block device (whole disks and dm devices; partitions skipped).
        if (b.Disk.Count >= 2)
        {
            DiskSample d0 = b.Disk[0];
            DiskSample d1 = b.Disk[^1];
            double span = Math.Max(0.01, d1.Time - d0.Time);
            JsonArray disks = new();
            foreach ((string dev, (long r, long w, long io)) in d1.Devices
                .Where(kv => IsWholeDisk(kv.Key)).OrderByDescending(kv => kv.Value.Read))
            {
                (long r0, long w0, long io0) = d0.Devices.GetValueOrDefault(dev);
                double readMb = (r - r0) * 512 / 1048576.0;
                if (readMb + ((w - w0) * 512 / 1048576.0) < 0.5)
                {
                    continue;
                }
                disks.Add(new JsonArray(dev, Math.Round(readMb, 1), Math.Round((w - w0) * 512 / 1048576.0, 1),
                    Math.Round(100.0 * (io - io0) / (span * 1000), 0)));
            }
            report["disk"] = new JsonObject
            {
                ["columns"] = new JsonArray("device", "read_mb", "written_mb", "busy_pct"),
                ["rows"] = disks
            };
        }

        JsonArray findings = Findings(bl, totAll > 0 ? 100 * ioAll / totAll : 0, maxSaturatedRun,
            LoggingProcs.Sum(n => byName.GetValueOrDefault(n).Ms) / Math.Max(1, cpuSum) * 100, last);
        foreach (JsonNode? g in gaps)
        {
            double gap = g!["gap_s"]!.GetValue<double>();
            double warn = g["expected_below_s"]!.GetValue<double>();
            if (gap >= warn)
            {
                findings.Insert(0, new JsonObject
                {
                    ["severity"] = gap >= 2 * warn ? "critical" : "warning",
                    ["rule"] = "SLOW_PHASE",
                    ["what"] = $"{g["from"]} -> {g["to"]} took {gap:0.0} s (expected < {warn:0.#} s); see phase_gaps top_cpu_in_gap.",
                    ["next_evidence"] = "If the gap is idle (low CPU in timeline) something waits (property, service, apexd/odsign, fs); "
                        + "if busy, the top_cpu_in_gap processes compete. A Perfetto boot trace started early covers it in detail."
                });
            }
        }
        report["findings"] = findings;

        if (Args.OptionalString(args, "focus_process") is { Length: > 0 } focus)
        {
            JsonArray rows = new();
            Dictionary<int, long> prevCpu = new();
            foreach (PsSample s in b.Ps)
            {
                foreach (ProcStat p in s.Procs.Values.Where(p => p.Name.Contains(focus, StringComparison.Ordinal)))
                {
                    long before = prevCpu.GetValueOrDefault(p.Pid, -1);
                    if (before == p.CpuJiffies)
                    {
                        continue; // only samples where it consumed CPU (or first sight)
                    }
                    rows.Add(new JsonArray(s.Time, p.Pid, p.Name, p.State, p.CpuJiffies * 10,
                        before < 0 ? null : (p.CpuJiffies - before) * 10, p.Threads));
                    prevCpu[p.Pid] = p.CpuJiffies;
                }
            }
            report["focus"] = new JsonObject
            {
                ["columns"] = new JsonArray("uptime_s", "pid", "process", "state", "cpu_ms_total", "cpu_ms_since_prev", "threads"),
                ["rows"] = rows
            };
        }
        return Task.FromResult(ToolCallResult.Json(report));
    }

    private static JsonArray Findings(Dictionary<string, double> bl, double iowaitPct, int saturatedSeconds, double loggingPct, double lastSample)
    {
        JsonArray f = new();
        void Add(string severity, string rule, string what, string next) =>
            f.Add(new JsonObject { ["severity"] = severity, ["rule"] = rule, ["what"] = what, ["next_evidence"] = next });
        if (bl.TryGetValue("tuboot", out double uboot) && uboot >= 2000)
        {
            Add(uboot >= 3000 ? "critical" : "warning", "BOOTLOADER_SLOW",
                $"U-Boot took {uboot:0} ms before the kernel started (bootloader total {bl.Values.Sum():0} ms).",
                "Bootloader log (uart): splash / logo loading, eMMC init, verified-boot hashing, wait loops; compare with a reference build.");
        }
        if (iowaitPct >= 15)
        {
            Add(iowaitPct >= 30 ? "critical" : "warning", "IO_BOUND",
                $"CPU iowait was {iowaitPct:0}% of all CPU time during boot.",
                "Boot trace: io_summary for blocked functions and block-device latency; check readahead / dm-verity / erofs.");
        }
        if (saturatedSeconds >= 3)
        {
            Add("warning", "CPU_SATURATED", $"All CPUs were >= 90% busy for {saturatedSeconds} consecutive seconds.",
                "Boot trace: cpu_top_threads in that window; defer non-critical services (init class / late_start).");
        }
        if (loggingPct >= 10)
        {
            Add(loggingPct >= 20 ? "critical" : "warning", "LOGGING_HEAVY",
                $"Logging processes (logd / ylog / logcat ...) used {loggingPct:0}% of the CPU consumed during boot.",
                "Reduce log levels / disable vendor log capture (e.g. ylog) on performance builds; check log spam with android_logs by_tag.");
        }
        f.Add(new JsonObject { ["info"] = $"Bootchart stops at boot completion; last sample at {lastSample:0.0} s uptime (kernel start = 0)." });
        return f;
    }

    // kworker/u16:3, kworker/2:1H ... -> kworker
    private static string KernelWorker(string name)
    {
        int slash = name.IndexOf('/');
        return slash > 0 && (name.StartsWith("kworker", StringComparison.Ordinal) || name.StartsWith("irq", StringComparison.Ordinal))
            ? name[..slash] : name;
    }

    private static bool IsWholeDisk(string dev) =>
        dev.StartsWith("dm-", StringComparison.Ordinal)
        || (dev.StartsWith("mmcblk", StringComparison.Ordinal) && !dev.Contains('p', StringComparison.Ordinal) && !dev.Contains("boot", StringComparison.Ordinal) && !dev.Contains("rpmb", StringComparison.Ordinal))
        || (dev.StartsWith("sd", StringComparison.Ordinal) && dev.Length == 3);
}

internal sealed class BootTimelineTool : TraceToolBase
{
    // Event-log tags that mark boot milestones (boot_progress_* plus animation end and user unlock).
    private const string MilestoneTags = """
        tag GLOB 'boot_progress_*' OR tag IN ('sf_stop_bootanim', 'wm_boot_animation_done', 'ssm_user_starting',
        'uc_finish_user_boot', 'ssm_user_unlocking', 'uc_finish_user_unlocked_completed', 'ssm_user_unlocked')
        """;

    public BootTimelineTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("boot_timeline",
        """
        Boot timeline from a Perfetto boot trace (persist.debug.perfetto.boottrace): boot_progress_* / boot animation /
        user unlock milestones with the gap to the previous one, key intervals (PMS, AMS ready -> enable screen ->
        animation end -> unlocked), system_server main-thread phases (SystemServerTiming: startBootstrapServices,
        StartPackageManagerService, startOtherServices, Phase*), the slowest service starts, and long work on other
        system_server threads (storage mount, broadcast receivers, handlers). Timestamps are seconds since boot.
        A boot trace usually starts after /data is mounted; earlier phases need analyze_bootchart.
        """,
        new SchemaBuilder()
            .Num("min_ms", "Minimum slice duration for phases / other-thread work (default 200).")
            .Int("limit", "Rows per section (default 15).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        int limit = Args.OptionalInt(args, "limit", 15, 1, 200);
        long minNs = (long)(Math.Clamp(Args.OptionalDouble(args, "min_ms") ?? 200, 0, 1e6) * 1e6);
        const string ssMain = """
            ss AS (SELECT t.utid FROM thread t JOIN process p USING (upid) WHERE p.name = 'system_server' AND t.tid = p.pid)
            """;

        JsonObject report = new();
        QueryTable start = await RunAsync(s, "SELECT MIN(ts) AS s FROM (SELECT ts FROM sched UNION ALL SELECT ts FROM slice UNION ALL SELECT ts FROM android_logs)", ctx).ConfigureAwait(false);
        double startS = start.Rows.Count > 0 && start.Rows[0][0] is { } st ? Convert.ToDouble(st) / 1e9 : 0;
        report["trace_start_s"] = Math.Round(startS, 2);

        QueryTable ms = await RunAsync(s, $"""
            WITH m AS (
              SELECT MIN(ts) AS ts, tag AS event FROM android_logs WHERE {MilestoneTags} GROUP BY tag
              UNION ALL
              -- Short-lived helpers forked later (e.g. surfaceflinger children) are not boot milestones.
              SELECT MIN(p.start_ts), 'process_start: ' || p.name FROM process p
              WHERE p.start_ts IS NOT NULL AND (p.end_ts IS NULL OR p.end_ts - p.start_ts > 5e9)
                AND p.start_ts <= (SELECT MAX(ts) FROM android_logs WHERE {MilestoneTags}) AND (p.name IN ('zygote64', 'zygote', 'system_server', 'com.android.systemui',
                    '/system/bin/bootanimation', '/system/bin/surfaceflinger') OR p.name GLOB '*launcher*')
              GROUP BY p.name)
            SELECT event, ROUND(ts / 1e9, 3) AS at_s, ROUND((ts - LAG(ts) OVER (ORDER BY ts)) / 1e6, 1) AS since_prev_ms
            FROM m ORDER BY ts
            """, ctx).ConfigureAwait(false);
        report["milestones"] = TableFormat.ToJson(ms, 100);

        Dictionary<string, double> at = new(StringComparer.Ordinal);
        foreach (object?[] r in ms.Rows)
        {
            if (r[0] is string e && r[1] is not null)
            {
                at[e] = Convert.ToDouble(r[1]);
            }
        }
        JsonObject intervals = new();
        void Interval(string name, string from, string to)
        {
            if (at.TryGetValue(from, out double a) && at.TryGetValue(to, out double b))
            {
                intervals[name] = Math.Round((b - a) * 1000, 1);
            }
        }
        Interval("pms_ms", "boot_progress_pms_start", "boot_progress_pms_ready");
        Interval("pms_system_scan_ms", "boot_progress_pms_system_scan_start", "boot_progress_pms_data_scan_start");
        Interval("pms_ready_to_ams_ready_ms", "boot_progress_pms_ready", "boot_progress_ams_ready");
        Interval("ams_ready_to_enable_screen_ms", "boot_progress_ams_ready", "boot_progress_enable_screen");
        // sf_stop_bootanim is not logged on every boot; wm_boot_animation_done is the WindowManager side of it.
        string animEnd = at.ContainsKey("sf_stop_bootanim") ? "sf_stop_bootanim" : "wm_boot_animation_done";
        Interval("enable_screen_to_bootanim_end_ms", "boot_progress_enable_screen", animEnd);
        Interval("bootanim_end_to_user_unlocked_ms", animEnd, "ssm_user_unlocked");
        report["intervals_ms"] = intervals;

        report["system_server_phases"] = await SectionAsync(s, $"""
            WITH {ssMain}
            SELECT ROUND(x.ts / 1e9, 3) AS at_s, ROUND(x.dur / 1e6, 1) AS dur_ms, x.depth, x.name
            FROM slice x JOIN thread_track tt ON x.track_id = tt.id
            WHERE tt.utid IN ss AND x.depth <= 2 AND x.dur >= {minNs}
              AND x.ts <= IFNULL((SELECT MAX(ts) FROM android_logs WHERE {MilestoneTags}), x.ts)
              AND x.name NOT GLOB 'monitor contention*' AND x.name NOT GLOB 'Lock contention*'
              AND x.name NOT GLOB 'binder*' AND x.name NOT GLOB 'serviceBind*' AND x.name NOT GLOB 'bindService*'
              AND x.name NOT GLOB 'broadcastReceive*' AND x.name NOT GLOB 'android.os.Handler*'
            ORDER BY x.ts LIMIT {limit * 2}
            """, ctx, limit * 2).ConfigureAwait(false);
        report["slowest_service_starts"] = await SectionAsync(s, $"""
            WITH {ssMain}
            SELECT x.name, ROUND(x.dur / 1e6, 1) AS dur_ms, ROUND(x.ts / 1e9, 3) AS at_s, x.depth
            FROM slice x JOIN thread_track tt ON x.track_id = tt.id
            WHERE tt.utid IN ss AND x.depth >= 2 AND (x.name GLOB 'Start*' OR x.name GLOB 'OnBootPhase*'
                  OR x.name GLOB 'ssm.*' OR x.name GLOB 'Make*Ready' OR x.name GLOB '*Ready')
            ORDER BY x.dur DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        report["other_system_server_work"] = await SectionAsync(s, $"""
            WITH {ssMain}
            SELECT t.name AS thread, x.name, ROUND(x.dur / 1e6, 1) AS dur_ms, ROUND(x.ts / 1e9, 3) AS at_s
            FROM slice x JOIN thread_track tt ON x.track_id = tt.id JOIN thread t USING (utid) JOIN process p USING (upid)
            WHERE p.name = 'system_server' AND tt.utid NOT IN ss AND x.depth = 0 AND x.dur >= MAX({minNs}, 300e6)
              AND x.name NOT IN ('binder reply', 'binder transaction', 'binder transaction async')
              AND x.name NOT GLOB 'monitor contention*' AND x.name NOT GLOB 'Lock contention*'
              -- Long boot traces run well past boot; keep this section about boot (last milestone + 10 s).
              AND x.ts <= IFNULL((SELECT MAX(ts) FROM android_logs WHERE {MilestoneTags}) + 10e9, x.ts)
            ORDER BY x.dur DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);

        // init logs "Command '<cmd>' ... took <n>ms" for slow commands; while one runs, init's main loop is blocked.
        QueryTable slowInit = await RunAsync(s, $"""
            WITH c AS (
              SELECT ts, msg, INSTR(msg, ' took ') AS p FROM android_logs
              -- Long kernel-log lines are cut ("took 29449m"): match the number, not the unit.
              WHERE msg GLOB '*Command ''*'' *took [0-9]*')
            SELECT ROUND(ts / 1e9, 3) AS logged_at_s, CAST(SUBSTR(msg, p + 6) AS INTEGER) AS took_ms,
                   SUBSTR(msg, INSTR(msg, 'Command ''') + 9, INSTR(msg, ''' action=') - INSTR(msg, 'Command ''') - 9) AS command,
                   SUBSTR(msg, INSTR(msg, ' action=') + 8, p - INSTR(msg, ' action=') - 8) AS trigger_and_source
            FROM c WHERE p > 0 AND CAST(SUBSTR(msg, p + 6) AS INTEGER) >= 500
            ORDER BY took_ms DESC LIMIT {limit}
            """, ctx).ConfigureAwait(false);
        if (slowInit.Rows.Count > 0)
        {
            report["slow_init_commands"] = TableFormat.ToJson(slowInit, limit);
        }

        JsonArray hints = new();
        if (slowInit.Rows.Count > 0 && Convert.ToInt64(slowInit.Rows[0][1] ?? 0L) >= 2000)
        {
            hints.Add($"init was blocked {slowInit.Rows[0][1]} ms by `{slowInit.Rows[0][2]}` (slow_init_commands): property "
                + "triggers and ctl.start / ctl.stop requests queue behind it; move it off init's main thread.");
        }
        if (startS > 10)
        {
            hints.Add($"The trace starts at {startS:0.0} s after boot: bootloader, kernel, init and zygote are not covered; "
                + "use analyze_bootchart (or an earlier boot-trace start) for them.");
        }
        if (!at.ContainsKey("boot_progress_enable_screen"))
        {
            hints.Add("No boot_progress events: the trace lacks the android.log events buffer (LID_EVENTS) or is not a boot trace.");
        }
        hints.Add("Drill down: slice_hotspots / cpu_top_threads with ts_start/ts_end around a slow phase; "
            + "android_binder_txns for long binder waits inside system_server work.");
        report["hints"] = hints;
        return ToolCallResult.Json(report);
    }
}
