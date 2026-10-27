using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed class CpuProfileTool : TraceToolBase
{
    // Symbol precedence: deobfuscated (Java) > offline symbolization > in-trace name.
    private const string FnExpr =
        "COALESCE(f.deobfuscated_name, (SELECT sym.name FROM stack_profile_symbol sym " +
        "WHERE sym.symbol_set_id = f.symbol_set_id ORDER BY sym.id LIMIT 1), NULLIF(f.name, ''))";

    // Frames present in (nearly) every stack — thread entry, ART invoke stubs — carry no signal.
    private const double RootShare = 0.97;
    private const int RootSlack = 12;

    public CpuProfileTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("cpu_profile",
        """
        CPU hotspots from `linux.perf` callstack samples (perf_sample): self (leaf) and inclusive sample counts per
        function, plus the most frequent full stacks. Filter by thread/process and time window — compare a slow
        window against a normal one by calling twice. Unsymbolized native frames are reported as
        `lib.so+0x<rel_pc>` (feed rel_pc to IDA/addr2line). Samples are a relative measure (perf may drop records;
        check load_trace health_issues). Kernel-mode samples usually carry no user stack and are counted separately.
        """,
        TimeWindow.AddParams(ProcessSelector.AddParams(new SchemaBuilder())
            .Int("utid", "Only this thread (trace-internal id).")
            .Int("tid", "Only this OS thread.")
            .Str("thread_name", "Only threads with this name (* for GLOB), combined with the process filter."))
            .Enum("group_by", "Aggregate unsymbolized frames per pc (function, default) or whole libraries.", "function", "library")
            .Bool("include_stacks", "Also return the most frequent full stacks (default true).")
            .Int("limit", "Rows per section (default 25).")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        TimeWindow w = TimeWindow.Parse(args);
        int limit = Args.OptionalInt(args, "limit", 25, 1, 500);
        bool byLibrary = Args.OptionalString(args, "group_by") == "library";
        bool stacks = Args.OptionalBool(args, "include_stacks", true);
        string filter = SampleFilter(args) + $" AND x.ts >= {w.Start} AND x.ts < {w.End}";
        string samples = $"perf_sample x JOIN thread t USING (utid) LEFT JOIN process p USING (upid) WHERE {filter}";

        JsonObject report = new() { ["window"] = w.Description };
        QueryTable summary = await RunAsync(s, $"""
            {w.Prelude}SELECT COUNT(*) AS samples, SUM(x.callsite_id IS NOT NULL) AS with_stack,
                   SUM(x.cpu_mode = 'kernel' AND x.callsite_id IS NULL) AS kernel_no_stack,
                   SUM(x.unwind_error IS NOT NULL) AS unwind_errors, COUNT(DISTINCT x.utid) AS threads
            FROM {samples}
            """, ctx).ConfigureAwait(false);
        report["summary"] = Row(summary);
        if (summary.Cell(0, "samples") is not long total || total == 0)
        {
            report["note"] = "No perf samples in scope. Needs the `linux.perf` data source with callstack_sampling.";
            return ToolCallResult.Json(report);
        }

        report["by_thread"] = await SectionAsync(s, $"""
            {w.Prelude}SELECT GROUP_CONCAT(DISTINCT t.utid) AS utids, t.tid, MAX(t.name) AS thread,
                   MAX({ProcessSelector.NameOf("t")}) AS process, COUNT(*) AS samples, ROUND(100.0 * COUNT(*) / {total}, 1) AS pct
            FROM {samples} GROUP BY t.tid ORDER BY samples DESC LIMIT 10
            """, ctx, 10).ConfigureAwait(false);

        string leafAgg = $"SELECT x.callsite_id AS leaf, COUNT(*) AS n FROM {samples} AND x.callsite_id IS NOT NULL GROUP BY x.callsite_id";
        string key = byLibrary
            ? "NULL AS fn, f.mapping AS map, NULL AS pc"
            : $"{FnExpr} AS fn, f.mapping AS map, IIF({FnExpr} IS NULL, f.rel_pc, NULL) AS pc";

        QueryTable self = await RunAsync(s, $"""
            {w.Prelude}WITH s AS ({leafAgg}),
            k AS (SELECT {key}, s.n FROM s JOIN stack_profile_callsite c ON c.id = s.leaf JOIN stack_profile_frame f ON f.id = c.frame_id)
            SELECT k.fn, m.name AS mapping, k.pc, SUM(k.n) AS samples
            FROM k LEFT JOIN stack_profile_mapping m ON m.id = k.map
            GROUP BY k.fn, k.map, k.pc ORDER BY samples DESC LIMIT {limit}
            """, ctx).ConfigureAwait(false);
        long withStack = summary.Cell(0, "with_stack") as long? ?? 0;
        report["self"] = FrameTable(self, withStack);

        QueryTable incl = await RunAsync(s, $"""
            {w.Prelude}WITH RECURSIVE s AS ({leafAgg}),
            walk(leaf, cs) AS (
              SELECT leaf, leaf FROM s
              UNION ALL
              SELECT walk.leaf, c.parent_id FROM walk JOIN stack_profile_callsite c ON c.id = walk.cs WHERE c.parent_id IS NOT NULL),
            k AS (SELECT DISTINCT walk.leaf, {key} FROM walk
                  JOIN stack_profile_callsite c ON c.id = walk.cs JOIN stack_profile_frame f ON f.id = c.frame_id)
            SELECT k.fn, m.name AS mapping, k.pc, SUM(s.n) AS samples
            FROM k JOIN s USING (leaf) LEFT JOIN stack_profile_mapping m ON m.id = k.map
            GROUP BY k.fn, k.map, k.pc ORDER BY samples DESC LIMIT {limit + RootSlack}
            """, ctx).ConfigureAwait(false);
        report["inclusive"] = FrameTable(incl, withStack, hideRootsAndLimit: limit);

        if (stacks)
        {
            int stackCount = Math.Min(limit, 8);
            QueryTable frames = await RunAsync(s, $"""
                {w.Prelude}WITH RECURSIVE s AS ({leafAgg} ORDER BY n DESC LIMIT {stackCount}),
                walk(leaf, cs, d) AS (
                  SELECT leaf, leaf, 0 FROM s
                  UNION ALL
                  SELECT walk.leaf, c.parent_id, walk.d + 1 FROM walk JOIN stack_profile_callsite c ON c.id = walk.cs
                  WHERE c.parent_id IS NOT NULL AND walk.d < 40)
                SELECT s.leaf, s.n, walk.d, {FnExpr} AS fn, m.name AS mapping, f.rel_pc AS pc
                FROM walk JOIN s USING (leaf) JOIN stack_profile_callsite c ON c.id = walk.cs
                JOIN stack_profile_frame f ON f.id = c.frame_id LEFT JOIN stack_profile_mapping m ON m.id = f.mapping
                ORDER BY s.n DESC, s.leaf, walk.d
                """, ctx).ConfigureAwait(false);
            report["top_stacks"] = StackList(frames, withStack);
        }
        return ToolCallResult.Json(report);
    }

    private static string SampleFilter(JsonObject args)
    {
        if (Args.OptionalLong(args, "utid") is { } utid)
        {
            return $"t.utid = {utid}";
        }
        if (Args.OptionalLong(args, "tid") is { } tid)
        {
            return $"t.tid = {tid}";
        }
        string pred = ProcessSelector.Predicate(args) ?? "1";
        if (Args.OptionalString(args, "thread_name") is { Length: > 0 } name)
        {
            string lit = SqlText.Literal(name);
            pred += name.Contains('*') ? $" AND t.name GLOB {lit}" : $" AND t.name = {lit}";
        }
        return pred;
    }

    private static JsonObject Row(QueryTable t)
    {
        JsonObject o = new();
        for (int i = 0; i < t.Columns.Count; i++)
        {
            o[t.Columns[i]] = TableFormat.Cell(t.Rows[0][i]);
        }
        return o;
    }

    private static JsonObject FrameTable(QueryTable t, long withStack, int? hideRootsAndLimit = null)
    {
        JsonArray rows = new();
        int hidden = 0;
        foreach (object?[] r in t.Rows)
        {
            long n = r[3] as long? ?? 0;
            if (hideRootsAndLimit is not null && withStack > 0 && n >= RootShare * withStack)
            {
                hidden++;
                continue;
            }
            if (hideRootsAndLimit is { } max && rows.Count >= max)
            {
                break;
            }
            rows.Add(new JsonArray(
                JsonValue.Create(Label(r[0] as string, r[1] as string, r[2] as long?)),
                JsonValue.Create(Library(r[1] as string)),
                n,
                withStack > 0 ? Math.Round(100.0 * n / withStack, 1) : 0));
        }
        JsonObject o = new()
        {
            ["columns"] = new JsonArray("frame", "library", "samples", "pct_of_stacked"),
            ["rows"] = rows
        };
        if (hidden > 0)
        {
            o["common_root_frames_hidden"] = hidden;
        }
        return o;
    }

    private static JsonArray StackList(QueryTable t, long withStack)
    {
        JsonArray result = new();
        foreach (IGrouping<long, object?[]> g in t.Rows.GroupBy(r => (long)r[0]!))
        {
            long n = (long)g.First()[1]!;
            StringBuilder sb = new();
            foreach (object?[] r in g.OrderBy(r => (long)r[2]!))
            {
                if (sb.Length > 0)
                {
                    sb.Append(" <- ");
                }
                sb.Append(Label(r[3] as string, r[4] as string, r[5] as long?));
            }
            result.Add(new JsonObject
            {
                ["samples"] = n,
                ["pct_of_stacked"] = withStack > 0 ? Math.Round(100.0 * n / withStack, 1) : 0,
                ["stack_leaf_first"] = sb.ToString()
            });
        }
        return result;
    }

    // Unsymbolized frames become "lib.so+0x1234" so they can be looked up offline.
    private static string Label(string? fn, string? mapping, long? relPc)
    {
        if (!string.IsNullOrEmpty(fn))
        {
            return Demangle.Simplify(fn);
        }
        string lib = Library(mapping) ?? "?";
        return relPc is { } pc ? $"{lib}+0x{pc:x}" : lib;
    }

    private static string? Library(string? mapping)
    {
        if (string.IsNullOrEmpty(mapping))
        {
            return null;
        }
        int bang = mapping.LastIndexOf('!');
        int slash = mapping.LastIndexOf('/');
        return mapping[(Math.Max(bang, slash) + 1)..];
    }
}
