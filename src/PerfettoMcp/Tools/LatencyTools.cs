using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class EventLatencyTool : TraceToolBase
{
    private const int MaxTargets = 10;
    private const int MaxAnchors = 200;
    private const int MaxKinds = 20;

    public EventLatencyTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("event_latency",
        """
        Trigger -> response latency for user operations: for every anchor (a logcat line containing `anchor_log`, or a
        slice matching `anchor_slice`) find the FIRST slice matching each `target_globs` entry after it (before the next
        anchor, at most window_ms), e.g. tap marker -> `*PreviewFirstFrame*`, power key -> `setPowerMode(ON)`.
        Returns per-anchor hits (ms after the anchor, duration, done_ms = when that slice ended, process, thread) and a
        per-target summary (hits, min / avg / max of start and of done), split by anchor kind when the anchors differ
        (label with numbers normalized). Make targets specific: one anchor kind can
        cover different operations (the power key both wakes and sleeps), and a target only one of them reaches
        shows as hits < anchors. Anchor tips: a `log -t X 'op name'` or trace_marker written by the test script.
        """,
        new SchemaBuilder()
            .Str("anchor_log", "Substring of the logcat message that marks each operation (case-sensitive).")
            .Str("anchor_tag", "With anchor_log: only lines with this logcat tag (adbd also logs the shell commands it runs, "
                + "so a marker text sent via adb shell matches twice without it).")
            .Str("anchor_slice", "Slice name GLOB that marks each operation (alternative to anchor_log).")
            .StrArray("target_globs", $"Slice name GLOBs to time after each anchor (1-{MaxTargets}).")
            .Str("target_process", "Only target slices in processes whose name matches this GLOB.")
            .Int("window_ms", "Max search window after each anchor (default 5000).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        string? anchorLog = Args.OptionalString(args, "anchor_log");
        string? anchorSlice = Args.OptionalString(args, "anchor_slice");
        if (string.IsNullOrEmpty(anchorLog) == string.IsNullOrEmpty(anchorSlice))
        {
            throw new McpInvalidParamsException("pass exactly one of anchor_log or anchor_slice");
        }
        List<string> targets = Args.OptionalStringList(args, "target_globs") ?? [];
        if (targets.Count is 0 or > MaxTargets)
        {
            throw new McpInvalidParamsException($"target_globs needs 1-{MaxTargets} slice name GLOBs");
        }
        long windowNs = Args.OptionalInt(args, "window_ms", 5000, 1, 600_000) * 1_000_000L;
        string? targetProcess = Args.OptionalString(args, "target_process");
        string? anchorTag = Args.OptionalString(args, "anchor_tag");
        TraceSession s = Traces.Resolve(Args.TraceId(args));

        string anchors = anchorLog is { Length: > 0 }
            ? $"SELECT ts, msg AS label, tag AS source FROM android_logs WHERE INSTR(msg, {SqlText.Literal(anchorLog)}) > 0"
                + (anchorTag is { Length: > 0 } ? $" AND tag = {SqlText.Literal(anchorTag)}" : "")
            : $"SELECT ts, name AS label, 'slice' AS source FROM slice WHERE name GLOB {SqlText.Literal(anchorSlice!)}";
        string targetValues = string.Join(", ", targets.Select((g, i) => $"({i}, {SqlText.Literal(g)})"));
        string procFilter = targetProcess is { Length: > 0 } ? $"WHERE process GLOB {SqlText.Literal(targetProcess)}" : "";
        // Candidates are pre-filtered once; anchors are ordered by (ts, label) so numbering is stable, and a window ends
        // at the next anchor with a later ts.
        QueryTable t = await RunAsync(s, $"""
            WITH a0 AS ({anchors} ORDER BY ts, label LIMIT {MaxAnchors}),
            a AS (SELECT ROW_NUMBER() OVER (ORDER BY ts, label) AS n, ts, label,
                         MIN(ts + {windowNs}, COALESCE((SELECT MIN(x.ts) FROM a0 x WHERE x.ts > a0.ts), ts + {windowNs})) AS until
                  FROM a0),
            tg(idx, glob) AS (VALUES {targetValues}),
            c AS (SELECT s.id, s.ts, s.dur, s.name, s.track_id, tg.idx, tg.glob FROM slice s JOIN tg ON s.name GLOB tg.glob
                  WHERE s.ts >= (SELECT MIN(ts) FROM a) AND s.ts < (SELECT MAX(until) FROM a)),
            hp AS (SELECT a.n, a.ts AS anchor_ts, a.label, c.*,
                          COALESCE({ProcessSelector.NameOf("th")}, pp.name) AS process, th.name AS thread
                   FROM a JOIN c ON c.ts >= a.ts AND c.ts < a.until
                   LEFT JOIN thread_track tt ON tt.id = c.track_id LEFT JOIN thread th ON th.utid = tt.utid
                   LEFT JOIN process p ON p.upid = th.upid
                   LEFT JOIN process_track pt ON pt.id = c.track_id LEFT JOIN process pp ON pp.upid = pt.upid),
            h AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY n, idx ORDER BY ts) AS rn FROM hp {procFilter})
            SELECT n AS anchor, anchor_ts, SUBSTR(label, 1, 80) AS anchor_label, glob AS target,
                   ROUND((ts - anchor_ts) / 1e6, 1) AS after_ms, IIF(dur < 0, NULL, ROUND(dur / 1e6, 1)) AS dur_ms,
                   IIF(dur < 0, NULL, ROUND((ts + dur - anchor_ts) / 1e6, 1)) AS done_ms, name AS slice,
                   process, thread, id AS slice_id
            FROM h WHERE rn = 1
            ORDER BY n, idx
            """, ctx).ConfigureAwait(false);
        QueryTable sources = await RunAsync(s, $"""
            SELECT source, COUNT(*) AS anchors FROM ({anchors} ORDER BY ts, label LIMIT {MaxAnchors}) GROUP BY source ORDER BY 2 DESC
            """, ctx).ConfigureAwait(false);
        long nAnchors = sources.Rows.Sum(r => System.Convert.ToInt64(r[1] ?? 0L));
        if (nAnchors == 0)
        {
            throw new ToolException("No anchors found: check anchor_log (android_logs needs the android.log data source) "
                + "or anchor_slice (a GLOB on slice names).");
        }

        // Anchor kinds are summarized separately; digits are normalized ("shutter1" / "shutter2" -> one kind).
        QueryTable labels = await RunAsync(s, $"""
            SELECT ROW_NUMBER() OVER (ORDER BY ts, label) AS n, SUBSTR(label, 1, 80) AS label
            FROM ({anchors} ORDER BY ts, label LIMIT {MaxAnchors})
            """, ctx).ConfigureAwait(false);
        Dictionary<long, string> kindOf = labels.Rows.ToDictionary(r => System.Convert.ToInt64(r[0]),
            r => CompareTracesTool.NormalizeName(r[1]?.ToString() ?? ""));
        List<string> kinds = [.. kindOf.OrderBy(kv => kv.Key).Select(kv => kv.Value).Distinct()];
        bool perKind = kinds.Count is > 1 and <= MaxKinds;
        JsonArray summary = [];
        foreach (string glob in targets)
        {
            List<object?[]> rows = [.. t.Rows.Where(r => (string?)r[3] == glob)];
            if (!perKind)
            {
                summary.Add(Summarize(glob, null, rows, nAnchors));
                continue;
            }
            foreach (string kind in kinds)
            {
                List<object?[]> kr = [.. rows.Where(r => kindOf.GetValueOrDefault(System.Convert.ToInt64(r[0])) == kind)];
                if (kr.Count > 0)
                {
                    summary.Add(Summarize(glob, kind, kr, kindOf.Values.Count(v => v == kind)));
                }
            }
            if (rows.Count == 0)
            {
                summary.Add(Summarize(glob, "(any)", rows, nAnchors));
            }
        }
        JsonObject o = new()
        {
            ["anchors"] = nAnchors,
            ["anchor_sources"] = TableFormat.ToJson(sources, 10),
            ["window_ms"] = windowNs / 1_000_000,
            ["summary"] = summary,
            ["hits"] = TableFormat.ToJson(t, 200)
        };
        if (sources.Rows.Count > 1)
        {
            o["note"] = "Anchors come from several log tags; pass anchor_tag to keep only your marker (an earlier duplicate "
                + "anchor ends the previous anchor's window).";
        }
        return ToolCallResult.Json(o);
    }

    private static JsonObject Summarize(string glob, string? kind, List<object?[]> rows, long anchors)
    {
        List<double> ms = [.. rows.Select(r => System.Convert.ToDouble(r[4] ?? 0.0))];
        // Slices still open at trace end have no end (done_ms null): they do not count as "done".
        List<double> done = [.. rows.Where(r => r[6] is not null).Select(r => System.Convert.ToDouble(r[6]))];
        JsonObject o = new() { ["target"] = glob };
        if (kind is not null)
        {
            o["anchor_kind"] = kind;
        }
        o["hits"] = ms.Count;
        o["of_anchors"] = anchors;
        o["min_ms"] = ms.Count > 0 ? ms.Min() : null;
        o["avg_ms"] = ms.Count > 0 ? System.Math.Round(ms.Average(), 1) : null;
        o["max_ms"] = ms.Count > 0 ? ms.Max() : null;
        o["done_min_ms"] = done.Count > 0 ? done.Min() : null;
        o["done_avg_ms"] = done.Count > 0 ? System.Math.Round(done.Average(), 1) : null;
        o["done_max_ms"] = done.Count > 0 ? done.Max() : null;
        return o;
    }
}
