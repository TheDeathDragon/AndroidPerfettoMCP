using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class FrameIntervalsTool : TraceToolBase
{
    private const int MaxFrames = 50_000;

    // Tried in order when slice_name is not given; first with enough hits wins.
    private static readonly string[] CandidateNames =
        ["eglSwapBuffers*", "Choreographer#doFrame*", "DrawFrame*", "queueBuffer*"];

    public FrameIntervalsTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("frame_intervals",
        """
        Frame cadence of one thread from a per-frame slice (eglSwapBuffers, Choreographer#doFrame, DrawFrame,
        queueBuffer ... auto-detected, or `slice_name`): fps, interval percentiles, janky intervals and freezes
        (gaps >= freeze_ms) with their timestamps. Works for custom GL/SurfaceView render threads that
        FrameTimeline does not cover. Feed a freeze's [gap_start_ts, gap_end_ts] into cpu_profile /
        thread_state_breakdown to see what the thread did meanwhile.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder())
            .Int("utid", "Render thread (trace-internal id).")
            .Int("tid", "Render thread OS id.")
            .Str("thread_name", "Render thread name within the process (* for GLOB). Default: the thread with the most frame slices."))
            .Str("slice_name", "Per-frame slice name GLOB (default: auto-detect).")
            .Num("jank_ms", "Intervals longer than this count as janky (default 2x the median interval).")
            .Num("freeze_ms", "Intervals at least this long are reported as freezes (default 500).")
            .Int("limit", "Longest intervals to list (default 15).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        int limit = Args.OptionalInt(args, "limit", 15, 1, 500);
        double freezeMs = Args.OptionalDouble(args, "freeze_ms") ?? 500;
        string threadPred = ThreadFilter(args);
        string from = $"""
            slice x JOIN thread_track tt ON x.track_id = tt.id JOIN thread t ON t.utid = tt.utid
            LEFT JOIN process p ON p.upid = t.upid
            WHERE {threadPred} AND x.ts >= {w.Start} AND x.ts < {w.End}
            """;

        string? requested = Args.OptionalString(args, "slice_name");
        string[] names = string.IsNullOrWhiteSpace(requested) ? CandidateNames : [requested];
        string nameCases = string.Join(" ", names.Select((n, i) => $"WHEN x.name GLOB {SqlText.Literal(n)} THEN {i}"));
        QueryTable pick = await RunAsync(s, $"""
            {w.Prelude}SELECT CASE {nameCases} END AS rank, t.utid, t.tid, t.name AS thread, p.name AS process, COUNT(*) AS n
            FROM {from} AND rank IS NOT NULL
            GROUP BY rank, t.utid HAVING n >= 3 ORDER BY rank, n DESC LIMIT 1
            """, ctx).ConfigureAwait(false);
        if (pick.Rows.Count == 0)
        {
            throw new ToolException(
                $"No per-frame slices ({string.Join(", ", names)}) found for that selection. Pass `slice_name` " +
                "(see slice_hotspots for candidates) and/or narrow the thread.");
        }
        long rank = (long)pick.Rows[0][0]!;
        long utid = (long)pick.Rows[0][1]!;
        string glob = names[rank];

        QueryTable frames = await RunAsync(s, $"""
            {w.Prelude}SELECT x.ts FROM slice x JOIN thread_track tt ON x.track_id = tt.id
            WHERE tt.utid = {utid} AND x.name GLOB {SqlText.Literal(glob)} AND x.ts >= {w.Start} AND x.ts < {w.End}
            ORDER BY x.ts
            """, ctx, MaxFrames).ConfigureAwait(false);
        List<long> ts = frames.Rows.Select(r => (long)r[0]!).ToList();

        JsonObject report = new()
        {
            ["window"] = w.Description,
            ["thread"] = new JsonObject
            {
                ["utid"] = utid,
                ["tid"] = TableFormat.Cell(pick.Rows[0][2]),
                ["name"] = TableFormat.Cell(pick.Rows[0][3]),
                ["process"] = TableFormat.Cell(pick.Rows[0][4])
            },
            ["frame_slice"] = glob
        };
        if (ts.Count < 2)
        {
            report["note"] = "Fewer than 2 frames in the window.";
            return ToolCallResult.Json(report);
        }
        if (frames.Truncated)
        {
            report["note"] = $"Only the first {MaxFrames} frames were analyzed; narrow the window.";
        }

        List<(long Start, long End, double Ms)> gaps = new(ts.Count - 1);
        for (int i = 1; i < ts.Count; i++)
        {
            gaps.Add((ts[i - 1], ts[i], (ts[i] - ts[i - 1]) / 1e6));
        }
        double[] sorted = gaps.Select(g => g.Ms).OrderBy(v => v).ToArray();
        double median = Percentile(sorted, 50);
        double jankMs = Args.OptionalDouble(args, "jank_ms") ?? Math.Round(2 * median, 2);
        double spanS = (ts[^1] - ts[0]) / 1e9;
        List<(long Start, long End, double Ms)> freezes = gaps.Where(g => g.Ms >= freezeMs).ToList();
        double freezeS = freezes.Sum(f => f.Ms) / 1000;

        report["summary"] = new JsonObject
        {
            ["frames"] = ts.Count,
            ["span_s"] = Math.Round(spanS, 3),
            ["fps"] = Math.Round((ts.Count - 1) / spanS, 2),
            ["interval_ms"] = new JsonObject
            {
                ["avg"] = Math.Round(sorted.Average(), 2),
                ["p50"] = Math.Round(median, 2),
                ["p90"] = Math.Round(Percentile(sorted, 90), 2),
                ["p99"] = Math.Round(Percentile(sorted, 99), 2),
                ["max"] = Math.Round(sorted[^1], 2)
            },
            ["jank_threshold_ms"] = jankMs,
            ["janky_intervals"] = gaps.Count(g => g.Ms > jankMs),
            ["freeze_threshold_ms"] = freezeMs,
            ["freezes"] = freezes.Count,
            ["freeze_total_s"] = Math.Round(freezeS, 3),
            ["freeze_pct_of_span"] = Math.Round(100 * freezeS / spanS, 1),
            ["fps_excluding_freezes"] = spanS - freezeS > 0
                ? Math.Round((ts.Count - 1 - freezes.Count) / (spanS - freezeS), 2)
                : null
        };

        JsonArray longest = new();
        foreach ((long start, long end, double ms) in gaps.OrderByDescending(g => g.Ms).Take(limit))
        {
            longest.Add(new JsonArray(start, end, Math.Round(ms, 2), ms >= freezeMs));
        }
        report["longest_intervals"] = new JsonObject
        {
            ["columns"] = new JsonArray("gap_start_ts", "gap_end_ts", "interval_ms", "is_freeze"),
            ["rows"] = longest
        };
        if (freezes.Count > 0)
        {
            report["freeze_summary"] = await FreezeSummaryAsync(s, utid, freezes, ctx).ConfigureAwait(false);
        }
        return ToolCallResult.Json(report);
    }

    // Render-thread activity during freezes, their regularity and its priority: busy vs waiting vs starved.
    private async Task<JsonObject> FreezeSummaryAsync(TraceSession s, long utid, List<(long Start, long End, double Ms)> freezes,
        RequestContext ctx)
    {
        string windows = string.Join(", ", freezes.Select(f => $"({f.Start}, {f.End})"));
        QueryTable states = await RunAsync(s, $"""
            WITH f(s, e) AS (VALUES {windows}),
            th AS (SELECT t.utid FROM thread t WHERE {ThreadSelector.SameThreadAs(utid)}),
            -- As in thread_state_breakdown: an older utid of the split thread ends where the next one starts, otherwise
            -- its last open state overlaps the real one (runnable came out at 120%).
            seg AS (SELECT x.utid, LEAD(MIN(x.ts)) OVER (ORDER BY MIN(x.ts)) AS next_ts
                    FROM thread_state x WHERE x.utid IN th GROUP BY x.utid),
            st AS (SELECT x.ts, x.state,
                          IIF(seg.next_ts IS NOT NULL AND (x.dur < 0 OR x.ts + x.dur > seg.next_ts), seg.next_ts - x.ts, x.dur) AS dur
                   FROM thread_state x JOIN seg USING (utid) WHERE seg.next_ts IS NULL OR x.ts < seg.next_ts)
            SELECT CASE WHEN x.state = 'Running' THEN 'running' WHEN x.state IN ('R', 'R+') THEN 'runnable'
                        WHEN x.state GLOB 'D*' THEN 'd_state' ELSE 'sleeping' END AS k,
                   SUM(MIN(IIF(x.dur < 0, f.e, x.ts + x.dur), f.e) - MAX(x.ts, f.s)) / 1e6 AS ms
            FROM f JOIN st x ON x.ts < f.e AND IIF(x.dur < 0, f.e, x.ts + x.dur) > f.s
            GROUP BY k
            """, ctx).ConfigureAwait(false);
        QueryTable prio = await RunAsync(s, $"""
            SELECT x.priority FROM sched x JOIN thread t USING (utid) WHERE {ThreadSelector.SameThreadAs(utid)}
            GROUP BY x.priority ORDER BY SUM(x.dur) DESC LIMIT 1
            """, ctx).ConfigureAwait(false);

        double total = freezes.Sum(f => f.Ms);
        Dictionary<string, double> by = states.Rows.ToDictionary(r => (string)r[0]!, r => Convert.ToDouble(r[1] ?? 0.0));
        double Pct(string k) => total > 0 ? Math.Round(100 * by.GetValueOrDefault(k) / total, 1) : 0;
        JsonObject o = new()
        {
            ["freezes"] = freezes.Count,
            ["render_thread_pct"] = new JsonObject
            {
                ["running"] = Pct("running"),
                ["runnable"] = Pct("runnable"),
                ["sleeping"] = Pct("sleeping"),
                ["d_state"] = Pct("d_state")
            }
        };
        if (freezes.Count >= 2)
        {
            double[] periods = freezes.Zip(freezes.Skip(1), (a, b) => (b.Start - a.Start) / 1e9).ToArray();
            o["period_s"] = new JsonObject
            {
                ["min"] = Math.Round(periods.Min(), 2),
                ["avg"] = Math.Round(periods.Average(), 2),
                ["max"] = Math.Round(periods.Max(), 2)
            };
        }
        long? priority = prio.Rows.Count > 0 && prio.Rows[0][0] is long pr ? pr : null;
        if (priority is >= 100 and < 140)
        {
            o["render_thread_nice"] = priority.Value - 120;
        }

        List<string> likely = new();
        if (Pct("running") >= 50)
        {
            likely.Add("The render thread kept running through the freezes: it computes instead of drawing — cpu_profile "
                + "over a freeze window (gap_start_ts .. gap_end_ts) shows the work; move it off the render thread.");
        }
        else if (Pct("sleeping") + Pct("d_state") >= 50)
        {
            likely.Add("The render thread mostly waited during the freezes: wakeup_chain over a freeze window shows on whom.");
        }
        if (Pct("runnable") >= 10 && priority is >= 120)
        {
            likely.Add($"It also waited {Pct("runnable")}% of the freeze time for a CPU at default priority (nice {priority - 120}): "
                + "raise it (Process.THREAD_PRIORITY_DISPLAY) and/or lower competing app threads (cpu_top_threads).");
        }
        if (o["period_s"] is JsonObject p && p["max"]!.GetValue<double>() <= 1.5 * p["min"]!.GetValue<double>())
        {
            likely.Add("Freezes recur at a regular period: look for a timer / periodic recomputation.");
        }
        o["likely"] = new JsonArray(likely.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
        return o;
    }

    private static string ThreadFilter(JsonObject args)
    {
        if (Args.OptionalLong(args, "utid") is { } utid)
        {
            return $"t.utid = {utid}";
        }
        if (Args.OptionalLong(args, "tid") is { } tid)
        {
            return $"t.tid = {tid}";
        }
        string pred = ProcessSelector.Predicate(args)
            ?? throw new McpInvalidParamsException("select the render thread with utid / tid, or a process (upid / pid / process_name)");
        if (Args.OptionalString(args, "thread_name") is { Length: > 0 } name)
        {
            string lit = SqlText.Literal(name);
            pred += name.Contains('*') ? $" AND t.name GLOB {lit}" : $" AND t.name = {lit}";
        }
        return pred;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }
        double rank = p / 100 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }
}

internal sealed class CpuFrequencyTool : TraceToolBase
{
    public CpuFrequencyTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("cpu_frequency",
        """
        CPU frequency in a window (power/cpu_frequency ftrace or cpufreq polling): time-weighted average, min and
        max per CPU, grouped into clusters by their highest observed frequency. Use it to rule thermal/power
        throttling in or out — compare a slow window against a normal one. Always reason from the time-weighted
        average: cpufreq events fire on every change, so a plain AVG over counter samples over-weights brief dips
        and can understate the real frequency by 20-30%.
        """,
        TimeWindow.AddParams(new SchemaBuilder()).TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        string clipped = w.Clipped("f.ts", "f.dur");
        QueryTable t = await RunAsync(s, $"""
            {w.Prelude}INCLUDE PERFETTO MODULE linux.cpu.frequency;
            WITH peak AS (SELECT cpu, MAX(freq) AS max_khz FROM cpu_frequency_counters GROUP BY cpu),
            win AS (
              SELECT f.cpu, SUM(f.freq * {clipped}) AS weighted, SUM({clipped}) AS covered,
                     MIN(f.freq) AS min_khz, MAX(f.freq) AS max_khz
              FROM cpu_frequency_counters f
              WHERE {w.Overlaps("f.ts", "f.dur")}
              GROUP BY f.cpu)
            SELECT win.cpu, peak.max_khz / 1000 AS cluster_peak_mhz,
                   ROUND(win.weighted / win.covered / 1000.0, 0) AS avg_mhz,
                   win.min_khz / 1000 AS min_mhz, win.max_khz / 1000 AS max_mhz,
                   ROUND(100.0 * win.weighted / win.covered / peak.max_khz, 1) AS avg_pct_of_peak
            FROM win JOIN peak USING (cpu)
            WHERE win.covered > 0
            ORDER BY win.cpu
            """, ctx).ConfigureAwait(false);
        if (t.TotalRows == 0)
        {
            throw new ToolException("No CPU frequency data in the window (needs power/cpu_frequency ftrace or sys_stats cpufreq polling).");
        }

        JsonArray clusters = new();
        foreach (IGrouping<long, object?[]> g in t.Rows.GroupBy(r => (long)r[1]!).OrderBy(g => g.Key))
        {
            clusters.Add(new JsonObject
            {
                ["cluster_peak_mhz"] = g.Key,
                ["cpus"] = string.Join(",", g.Select(r => r[0])),
                ["avg_mhz"] = Math.Round(g.Average(r => Convert.ToDouble(r[2])), 0),
                ["min_mhz"] = g.Min(r => (long)r[3]!),
                ["max_mhz"] = g.Max(r => (long)r[4]!)
            });
        }
        return ToolCallResult.Json(new JsonObject
        {
            ["window"] = w.Description,
            ["clusters"] = clusters,
            ["per_cpu"] = TableFormat.ToJson(t, 64)
        });
    }
}
