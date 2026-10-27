using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed partial class ExecuteSqlTool : TraceToolBase
{
    public ExecuteSqlTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("execute_sql",
        $"""
        Run PerfettoSQL against a loaded trace; returns columns, rows (as arrays) and row_count.

        Use for anything the dedicated tools don't cover. Prefer stdlib modules over raw `slice` LIKE scans:
        put `INCLUDE PERFETTO MODULE <module>;` and the SELECT in the same `sql` (only the last statement may
        return rows). `list_stdlib_modules` maps table/function names to modules; on "no such table" errors the
        response names the module to include. Timestamps/durations are ns: use `dur / 1e6 AS dur_ms`.

        Aggregate in SQL — output is cut at `max_rows` (default 200, max {Settings.HardMaxRows}) and long strings at
        `max_string_len`; when rows are cut, `column_stats` summarizes up to {Settings.HardMaxRows} rows. For exploring a
        large result use `summarize=true` (stats + a few sample rows, optionally the largest by `sort_by`). A query running past `timeout_s` is aborted and the trace processor restarted (the trace
        reloads automatically next call) — narrow by time window / upid / utid instead of retrying as-is.
        Docs: https://perfetto.dev/docs/analysis/stdlib-docs , https://perfetto.dev/docs/analysis/perfetto-sql-syntax
        """,
        new SchemaBuilder()
            .Str("sql", "PerfettoSQL to run (INCLUDE statements + one SELECT).", isRequired: true)
            .TraceId()
            .Int("max_rows", $"Rows to return (default 200, max {Settings.HardMaxRows}). row_count always reports the full count.")
            .Int("timeout_s", "Per-query timeout in seconds (default from PERFETTO_QUERY_TIMEOUT_S, 120).")
            .Int("max_string_len", "Truncate string cells longer than this (default 300, 0 = no limit).")
            .Bool("summarize", "Return per-column stats (min/max/avg/p50/p90/p99 or top values) plus sample rows instead of all rows.")
            .Str("sort_by", "With summarize: pick sample rows with the largest values of this column (e.g. dur).")
            .Int("sample_rows", "With summarize: number of sample rows (default 10)."));

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        string sql = Args.RequireString(args, "sql");
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new McpInvalidParamsException("'sql' is empty");
        }
        int maxRows = Args.OptionalInt(args, "max_rows", Traces.Settings.DefaultMaxRows, 1, Settings.HardMaxRows);
        int timeoutS = Args.OptionalInt(args, "timeout_s", (int)Traces.Settings.QueryTimeout.TotalSeconds, 1, 3600);
        int maxStr = Args.OptionalInt(args, "max_string_len", TableFormat.DefaultMaxStringLen, 0, 1_000_000);

        bool summarize = Args.OptionalBool(args, "summarize", false);
        int sampleRows = Args.OptionalInt(args, "sample_rows", 10, 1, 500);
        string? sortBy = Args.OptionalString(args, "sort_by");
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        QueryTable t;
        try
        {
            // Decode up to the hard cap so truncated results can still be summarized faithfully.
            t = await RunAsync(s, sql, ctx, Settings.HardMaxRows, TimeSpan.FromSeconds(timeoutS)).ConfigureAwait(false);
        }
        catch (SqlQueryException ex) when (ex.Message.Contains("no such column:", StringComparison.Ordinal))
        {
            throw new SqlQueryException(ex.Message + await ColumnHintAsync(s, sql, ex.Message, ctx).ConfigureAwait(false));
        }
        JsonObject o;
        if (summarize)
        {
            o = TableFormat.ToJson(ResultSummary.Sample(t, sortBy, sampleRows), sampleRows, maxStr);
            o.Remove("note");
            o.Remove("truncated");
            o["sample_rows"] = Math.Min(sampleRows, t.Rows.Count);
            o["column_stats"] = ResultSummary.ColumnStats(t);
        }
        else
        {
            o = TableFormat.ToJson(t, maxRows, maxStr);
            if (t.TotalRows > maxRows)
            {
                o["column_stats"] = ResultSummary.ColumnStats(t);
            }
        }
        if (o["column_stats"] is not null)
        {
            o["stats_over_rows"] = t.Rows.Count;
            if (t.Truncated)
            {
                o["stats_note"] = $"Stats cover the first {t.Rows.Count} of {t.TotalRows} rows; aggregate in SQL for exact totals.";
            }
        }
        if (t.ElapsedMs is { } ms)
        {
            o["query_ms"] = Math.Round(ms, 1);
        }
        o["trace_id"] = s.Id;
        return ToolCallResult.Json(o);
    }

    // On a missing column, list the table's real columns and the closest names (saves a round trip).
    private async Task<string> ColumnHintAsync(TraceSession s, string sql, string error, RequestContext ctx)
    {
        string missing = MissingColumn().Match(error) is { Success: true } m ? m.Groups[1].Value : string.Empty;
        string includes = string.Join("\n", IncludeLine().Matches(sql).Select(x => x.Value));
        List<string> tables = TableRef().Matches(sql).Select(x => x.Groups[1].Value)
            .Where(n => !Keywords.Contains(n.ToUpperInvariant())).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        System.Text.StringBuilder sb = new();
        foreach (string table in tables)
        {
            string? module = StdlibIndex.ModuleFor(table);
            string prelude = includes + (module is null ? string.Empty : $"\nINCLUDE PERFETTO MODULE {module};");
            QueryTable cols;
            try
            {
                cols = await RunAsync(s, $"{prelude}\nSELECT name FROM pragma_table_info({SqlText.Literal(table)})", ctx).ConfigureAwait(false);
            }
            catch (SqlQueryException)
            {
                continue;
            }
            List<string> names = cols.Rows.Select(r => (string)r[0]!).ToList();
            if (names.Count == 0)
            {
                continue;
            }
            IEnumerable<string> close = names.OrderBy(n => Distance(Bare(missing), n)).Take(3);
            sb.Append($"\nColumns of {table}: {string.Join(", ", names)}");
            if (missing.Length > 0)
            {
                sb.Append($"\n  closest to '{missing}': {string.Join(", ", close)}");
            }
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "SELECT", "WHERE", "LATERAL", "VALUES", "UNNEST"
    };

    private static string Bare(string column) => column.Contains('.') ? column[(column.LastIndexOf('.') + 1)..] : column;

    // Levenshtein distance with a bonus for substring matches (ts -> client_ts).
    private static int Distance(string a, string b)
    {
        if (a.Length > 0 && b.Contains(a, StringComparison.OrdinalIgnoreCase))
        {
            return b.Length - a.Length - 100;
        }
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    [GeneratedRegex(@"no such column: ([A-Za-z0-9_.]+)")]
    private static partial Regex MissingColumn();

    [GeneratedRegex(@"INCLUDE\s+PERFETTO\s+MODULE\s+[A-Za-z0-9_.]+\s*;", RegexOptions.IgnoreCase)]
    private static partial Regex IncludeLine();

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex TableRef();
}

internal sealed class ListTablesTool : TraceToolBase
{
    public ListTablesTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("list_tables",
        "List tables and views available in the trace (SQLite catalog). Without `pattern`, internal `_*` objects " +
        "are hidden. Stdlib views only appear after their module was INCLUDEd — use list_stdlib_modules to find those.",
        new SchemaBuilder()
            .Str("pattern", "SQLite GLOB filter on the name, e.g. `*frame*`, `android_*`.")
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        string? pattern = Args.OptionalString(args, "pattern");
        string where = string.IsNullOrWhiteSpace(pattern)
            ? "name NOT LIKE 'sqlite_%' AND name NOT LIKE '\\_%' ESCAPE '\\'"
            : $"name GLOB {SqlText.Literal(SqlText.Identifier(pattern.Trim(), "pattern"))}";
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        QueryTable t = await RunAsync(s,
            $"SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND {where} ORDER BY name", ctx)
            .ConfigureAwait(false);
        JsonArray tables = new(t.Rows.Where(r => (string?)r[1] == "table").Select(r => (JsonNode?)JsonValue.Create((string?)r[0])).ToArray());
        JsonArray views = new(t.Rows.Where(r => (string?)r[1] == "view").Select(r => (JsonNode?)JsonValue.Create((string?)r[0])).ToArray());
        return ToolCallResult.Json(new JsonObject { ["tables"] = tables, ["views"] = views });
    }
}

internal sealed class ListTableStructureTool : TraceToolBase
{
    public ListTableStructureTool(TraceManager traces) : base(traces) { }

    public override ToolDescriptor Descriptor { get; } = Describe("list_table_structure",
        "Show the columns (name, type, notnull, pk) of a table or view. Call this after a `no such column` error " +
        "instead of guessing. Stdlib views are INCLUDEd automatically when the name is known to the module index.",
        new SchemaBuilder()
            .Str("table_name", "Exact table/view name, e.g. `thread_state` or `android_startups`.", isRequired: true)
            .TraceId());

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        string name = SqlText.Identifier(Args.RequireString(args, "table_name").Trim(), "table_name");
        string? module = StdlibIndex.ModuleFor(name);
        string prelude = module is null ? string.Empty : $"INCLUDE PERFETTO MODULE {module};\n";
        TraceSession s = Traces.Resolve(Args.TraceId(args));
        QueryTable t = await RunAsync(s,
            $"{prelude}SELECT name, type, \"notnull\", pk FROM pragma_table_info({SqlText.Literal(name)})", ctx)
            .ConfigureAwait(false);
        if (t.Rows.Count == 0)
        {
            throw new ToolException($"Table '{name}' not found (or has no columns). Use list_tables or list_stdlib_modules.");
        }
        JsonObject o = TableFormat.ToJson(t, Settings.HardMaxRows);
        o["table"] = name;
        if (module is not null)
        {
            o["module"] = module;
        }
        return ToolCallResult.Json(o);
    }
}

internal sealed class ListStdlibModulesTool : IToolHandler
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Name = "list_stdlib_modules",
        Description = "Search the PerfettoSQL stdlib shipped with the bundled trace processor: which module defines " +
            "which public tables/views/functions. Without `filter` lists module names only. Use the module name in " +
            "`INCLUDE PERFETTO MODULE <module>;`.",
        InputSchema = new SchemaBuilder()
            .Str("filter", "Substring matched against module and object names, e.g. `startup`, `binder`, `jank`, `android_frames`.")
            .Build(),
        Annotations = Annotations.ReadOnly()
    };

    public Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        string? filter = Args.OptionalString(args, "filter");
        if (string.IsNullOrWhiteSpace(filter))
        {
            JsonArray names = new(StdlibIndex.All.Select(m => (JsonNode?)JsonValue.Create(m.Name)).ToArray());
            return Task.FromResult(ToolCallResult.Json(new JsonObject { ["modules"] = names }));
        }
        JsonObject result = new();
        foreach (StdlibModule m in StdlibIndex.Search(filter))
        {
            result[m.Name] = new JsonArray(m.Objects.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray());
        }
        return Task.FromResult(ToolCallResult.Json(new JsonObject { ["modules"] = result }));
    }
}
