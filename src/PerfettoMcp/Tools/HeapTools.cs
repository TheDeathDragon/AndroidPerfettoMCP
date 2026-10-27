using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class JavaHeapTool : TraceToolBase
{
    // Lifecycle-bound UI classes: more live instances than the screen shows usually means a leak.
    private const string UiRoots = """
        'android.app.Activity', 'androidx.fragment.app.Fragment', 'android.app.Fragment', 'android.app.Dialog',
        'android.view.ViewRootImpl'
        """;

    public JavaHeapTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("java_heap",
        """
        Java heap dump analysis (android.java_hprof / ART heap graph): per dump the heap / reachable / native sizes and
        OOM state; classes by retained (dominated) size incl. native; classes by instance count; live UI objects
        (Activity / Fragment / Dialog / ViewRootImpl subclasses); bitmaps (size, dimensions); findings (several live
        instances of one Activity, large bitmaps, suspended-coroutine backlog). With `baseline_trace_id` (an earlier
        dump of the same process, loaded as another trace) it lists the classes whose reachable instances grew — the
        usual leak check: dump, repeat the scenario N times, dump again, compare. `class_name` traces the largest instances
        of a class to a GC root (shortest reference path, with the app classes on it: who keeps them alive). Capture with
        record_trace(config="heap", process="<name>"). Primitive field values are not in Perfetto heap dumps.
        """,
        ProcessSelector.AddParams(new SchemaBuilder())
            .Str("baseline_trace_id", "trace_id of an earlier heap dump of the same process to diff against.")
            .Str("class_name", "Show what keeps instances of this class alive: shortest reference paths to a GC root.")
            .Int("paths", "With class_name: instances (largest retained first) to trace to a root (default 3, max 10).")
            .Int("limit", "Rows per section (default 15).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        int limit = Args.OptionalInt(args, "limit", 15, 1, 200);
        string proc = ProcessSelector.RealPredicate(args) is { } p ? $"upid IN (SELECT p.upid FROM process p WHERE {p})" : "1";

        QueryTable n = await RunAsync(s, $"SELECT COUNT(*) FROM heap_graph_object WHERE {proc}", ctx).ConfigureAwait(false);
        if (n.Rows.Count == 0 || Convert.ToInt64(n.Rows[0][0]) == 0)
        {
            throw new ToolException("no Java heap graph in this trace (for this process): record with config \"heap\" "
                + "(android.java_hprof data source)");
        }

        JsonObject report = new();
        report["dumps"] = await SectionAsync(s, $"""
            INCLUDE PERFETTO MODULE android.memory.heap_graph.heap_graph_stats;
            SELECT s.upid, p.name AS process, s.graph_sample_ts AS ts, ROUND(s.total_heap_size / 1048576.0, 1) AS heap_mb,
                   ROUND(s.reachable_heap_size / 1048576.0, 1) AS reachable_mb,
                   ROUND(s.total_native_alloc_registry_size / 1048576.0, 1) AS native_registry_mb,
                   s.total_obj_count AS objects, s.reachable_obj_count AS reachable_objects, s.oom_score_adj,
                   s.is_out_of_memory_error AS oome
            FROM android_heap_graph_stats s JOIN process p USING (upid) WHERE {proc.Replace("upid IN", "s.upid IN")}
            ORDER BY s.graph_sample_ts
            """, ctx, 20).ConfigureAwait(false);
        const string agg = "INCLUDE PERFETTO MODULE android.memory.heap_graph.heap_graph_class_aggregation;\n";
        report["top_retained_classes"] = await SectionAsync(s, $"""
            {agg}SELECT type_name AS class, obj_count AS objects, reachable_obj_count AS reachable,
                   ROUND(size_bytes / 1048576.0, 2) AS self_mb, ROUND(dominated_size_bytes / 1048576.0, 2) AS retained_mb,
                   ROUND(dominated_native_size_bytes / 1048576.0, 2) AS retained_native_mb
            FROM android_heap_graph_class_aggregation WHERE {proc}
            ORDER BY dominated_size_bytes + dominated_native_size_bytes DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);
        report["top_instance_counts"] = await SectionAsync(s, $"""
            {agg}SELECT type_name AS class, reachable_obj_count AS reachable, obj_count AS objects,
                   ROUND(reachable_size_bytes / 1048576.0, 2) AS reachable_mb
            FROM android_heap_graph_class_aggregation WHERE {proc}
            ORDER BY reachable_obj_count DESC LIMIT {limit}
            """, ctx, limit).ConfigureAwait(false);

        QueryTable ui = await RunAsync(s, $"""
            WITH RECURSIVE sub(id, root) AS (
              SELECT id, name FROM heap_graph_class WHERE name IN ({UiRoots})
              UNION SELECT c.id, sub.root FROM heap_graph_class c JOIN sub ON c.superclass_id = sub.id)
            SELECT sub.root AS kind, c.name AS class, COUNT(o.id) AS objects, SUM(o.reachable) AS reachable
            FROM heap_graph_object o JOIN heap_graph_class c ON o.type_id = c.id JOIN sub ON sub.id = c.id
            WHERE {proc.Replace("upid IN", "o.upid IN")}
            GROUP BY sub.root, c.name ORDER BY reachable DESC LIMIT 50
            """, ctx).ConfigureAwait(false);
        report["ui_objects"] = TableFormat.ToJson(ui, 50);

        QueryTable bitmaps = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE android.memory.heap_graph.bitmap;
            SELECT width, height, ROUND((self_size + native_size) / 1048576.0, 2) AS mb, reachable, bitmap_storage_type AS storage
            FROM heap_graph_bitmaps WHERE {proc} ORDER BY self_size + native_size DESC LIMIT {limit}
            """, ctx).ConfigureAwait(false);
        QueryTable bitmapTotal = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE android.memory.heap_graph.bitmap;
            SELECT COUNT(*) AS n, ROUND(SUM(self_size + native_size) / 1048576.0, 2) AS mb FROM heap_graph_bitmaps WHERE {proc}
            """, ctx).ConfigureAwait(false);
        report["bitmaps"] = new JsonObject
        {
            ["count"] = TableFormat.Cell(bitmapTotal.Rows[0][0]),
            ["total_mb"] = TableFormat.Cell(bitmapTotal.Rows[0][1]),
            ["largest"] = TableFormat.ToJson(bitmaps, limit)
        };

        JsonArray findings = new();
        foreach (object?[] r in ui.Rows.Where(r => (string?)r[0] == "android.app.Activity" && Convert.ToInt64(r[3] ?? 0L) > 1))
        {
            findings.Add(Finding("warning", "MULTIPLE_ACTIVITY_INSTANCES",
                $"{r[3]} reachable instances of {r[1]}: a destroyed Activity is probably still referenced",
                "baseline diff after rotating / reopening; heap path to GC root in the Perfetto UI heap graph"));
        }
        foreach (object?[] r in bitmaps.Rows.Where(r => Convert.ToDouble(r[2] ?? 0.0) >= 8))
        {
            findings.Add(Finding("warning", "LARGE_BITMAP", $"bitmap {r[0]}x{r[1]} holds {r[2]} MB",
                "decode at the displayed size (inSampleSize / target size); check who keeps it"));
        }
        QueryTable coroutines = await RunAsync(s, $"""
            {agg}SELECT SUM(reachable_obj_count) FROM android_heap_graph_class_aggregation
            WHERE {proc} AND type_name = 'kotlinx.coroutines.CancellableContinuationImpl'
            """, ctx).ConfigureAwait(false);
        long suspended = coroutines.Rows.Count > 0 && coroutines.Rows[0][0] is { } c ? Convert.ToInt64(c) : 0;
        if (suspended >= 2000)
        {
            findings.Add(Finding("info", "COROUTINE_BACKLOG",
                $"{suspended} reachable suspended coroutines (CancellableContinuationImpl): flow collectors / waiters",
                "class_name=kotlinx.coroutines.CancellableContinuationImpl shows which flows / callbacks hold them; dump again "
                    + "after repeating the scenario and pass baseline_trace_id: growth means collectors are not cancelled"));
        }

        if (Args.OptionalString(args, "baseline_trace_id") is { } baseId)
        {
            TraceSession b = Traces.Resolve(baseId);
            string sql = $"""
                {agg}SELECT type_name, SUM(reachable_obj_count), SUM(reachable_size_bytes)
                FROM android_heap_graph_class_aggregation WHERE {proc} GROUP BY type_name
                ORDER BY SUM(reachable_obj_count) DESC LIMIT 3000
                """;
            Dictionary<string, (long N, long Bytes)> before = ToMap(await RunAsync(b, sql, ctx).ConfigureAwait(false));
            Dictionary<string, (long N, long Bytes)> after = ToMap(await RunAsync(s, sql, ctx).ConfigureAwait(false));
            List<Growth> growth = after
                .Select(kv => new Growth(kv.Key, before.GetValueOrDefault(kv.Key), kv.Value))
                .Where(x => x.Delta > 0)
                .OrderByDescending(x => x.Delta)
                .Take(limit)
                .ToList();
            JsonArray rows = new();
            foreach (Growth x in growth)
            {
                rows.Add(new JsonArray(x.Class, x.Before.N, x.After.N, x.Delta,
                    Math.Round((x.After.Bytes - x.Before.Bytes) / 1048576.0, 2)));
            }
            report["growth_vs_baseline"] = new JsonObject
            {
                ["baseline_trace_id"] = baseId,
                ["columns"] = new JsonArray("class", "reachable_before", "reachable_after", "delta", "delta_mb"),
                ["rows"] = rows
            };
            // Per-class growth inside a flat heap is usually bounded caches / log ring buffers filling up.
            long totalBefore = before.Values.Sum(v => v.N);
            long totalAfter = after.Values.Sum(v => v.N);
            double totalPct = totalBefore > 0 ? 100.0 * (totalAfter - totalBefore) / totalBefore : 0;
            report["growth_vs_baseline"]!["reachable_objects_before"] = totalBefore;
            report["growth_vs_baseline"]!["reachable_objects_after"] = totalAfter;
            report["growth_vs_baseline"]!["reachable_growth_pct"] = Math.Round(totalPct, 2);
            bool flat = totalPct < 2;
            foreach (Growth x in growth.Where(x => x.Delta >= Math.Max(200, x.Before.N / 5)).Take(5))
            {
                findings.Add(Finding(flat ? "info" : "warning", "CLASS_GROWTH",
                    $"{x.Class}: {x.Before.N} -> {x.After.N} reachable (+{x.Delta})"
                        + (flat ? $"; overall reachable objects changed only {totalPct:0.0}% (bounded cache / log buffer?)" : ""),
                    "repeat once more: steady growth per repetition is a leak; class_name=<class> shows who holds them"));
            }
        }
        if (Args.OptionalString(args, "class_name") is { } cls)
        {
            report["retaining_paths"] = await RetainingPathsAsync(s, proc, cls, Args.OptionalInt(args, "paths", 3, 1, 10), ctx)
                .ConfigureAwait(false);
        }
        report["findings"] = findings;
        return ToolCallResult.Json(report);
    }

    private const int MaxHops = 64;

    // Library packages; the first classes outside them on a path are usually the real owners.
    private static readonly string[] LibraryPrefixes =
        ["java.", "javax.", "kotlin.", "kotlinx.", "android.", "androidx.", "dalvik.", "libcore.", "sun.", "com.android.internal."];

    // Shortest GC-root path to the largest instances of `cls`, one root_distance step per hop (the dominator tree
    // alone fails for objects reachable from several roots).
    private async Task<JsonNode> RetainingPathsAsync(TraceSession s, string proc, string cls, int n, RequestContext ctx)
    {
        QueryTable starts = await RunAsync(s, $"""
            INCLUDE PERFETTO MODULE android.memory.heap_graph.dominator_tree;
            SELECT o.id, o.root_distance, d.dominated_size_bytes + IFNULL(d.dominated_native_size_bytes, 0) AS retained,
                   (SELECT COUNT(*) FROM heap_graph_object o2 WHERE o2.type_id = o.type_id AND o2.reachable) AS instances
            FROM heap_graph_object o JOIN heap_graph_class c ON o.type_id = c.id
            LEFT JOIN heap_graph_dominator_tree d ON d.id = o.id
            WHERE c.name = {SqlText.Literal(cls)} AND o.reachable AND {proc.Replace("upid IN", "o.upid IN")}
            ORDER BY retained DESC LIMIT {n}
            """, ctx).ConfigureAwait(false);
        if (starts.Rows.Count == 0)
        {
            return new JsonObject { ["error"] = $"no reachable instance of {cls} (check the exact class name in top_instance_counts)" };
        }
        JsonArray paths = new();
        foreach (object?[] r in starts.Rows)
        {
            long id = Convert.ToInt64(r[0]);
            QueryTable hops = await RunAsync(s, $"""
                WITH RECURSIVE path(hop, id, rd) AS (
                  SELECT 0, {id}, {Convert.ToInt64(r[1] ?? 0L)}
                  UNION ALL
                  SELECT path.hop + 1,
                         (SELECT x.owner_id FROM heap_graph_reference x JOIN heap_graph_object ow ON ow.id = x.owner_id
                          WHERE x.owned_id = path.id AND ow.root_distance = path.rd - 1 LIMIT 1), path.rd - 1
                  FROM path WHERE path.rd > 0 AND path.hop < {MaxHops} AND path.id IS NOT NULL)
                SELECT p.hop, c.name AS class, o.root_type,
                       (SELECT x.field_name FROM heap_graph_reference x
                        WHERE x.owner_id = p.id AND x.owned_id = (SELECT p2.id FROM path p2 WHERE p2.hop = p.hop - 1) LIMIT 1) AS field
                FROM path p JOIN heap_graph_object o ON o.id = p.id JOIN heap_graph_class c ON c.id = o.type_id
                ORDER BY p.hop DESC
                """, ctx).ConfigureAwait(false);
            JsonArray chain = new();
            List<string> owners = new();
            foreach (object?[] h in hops.Rows)
            {
                string name = h[1] as string ?? "?";
                // Field names come fully qualified ("pkg.Owner.field"); the owner is the previous hop already.
                string? field = h[3] as string;
                JsonObject hop = new() { ["class"] = name };
                if (!string.IsNullOrEmpty(field))
                {
                    hop["field"] = field[(field.LastIndexOf('.') + 1)..];
                }
                if (h[2] is string rootType)
                {
                    hop["root_type"] = rootType;
                }
                chain.Add(hop);
                if (!LibraryPrefixes.Any(name.StartsWith) && !owners.Contains(name) && name != cls)
                {
                    owners.Add(name);
                }
            }
            paths.Add(new JsonObject
            {
                ["object_id"] = id,
                ["retained_kb"] = Math.Round(Convert.ToDouble(r[2] ?? 0L) / 1024, 1),
                ["root_distance"] = TableFormat.Cell(r[1]),
                ["reached_root"] = hops.Rows.Count > 0 && hops.Rows[0][2] is not null,
                ["app_classes_on_path"] = new JsonArray(owners.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray()),
                ["path_from_root"] = chain
            });
        }
        return new JsonObject { ["class"] = cls, ["reachable_instances"] = TableFormat.Cell(starts.Rows[0][3]), ["paths"] = paths };
    }

    private sealed record Growth(string Class, (long N, long Bytes) Before, (long N, long Bytes) After)
    {
        public long Delta => After.N - Before.N;
    }

    private static Dictionary<string, (long N, long Bytes)> ToMap(QueryTable t) => t.Rows
        .Where(r => r[0] is string)
        .GroupBy(r => (string)r[0]!)
        .ToDictionary(g => g.Key, g => (Convert.ToInt64(g.First()[1] ?? 0L), Convert.ToInt64(g.First()[2] ?? 0L)), StringComparer.Ordinal);

    private static JsonObject Finding(string severity, string rule, string what, string next) => new()
    {
        ["severity"] = severity,
        ["rule"] = rule,
        ["what"] = what,
        ["next_evidence"] = next
    };
}
