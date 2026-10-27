using System;
using System.Text.Json.Nodes;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal static class TableFormat
{
    public const int DefaultMaxStringLen = 300;

    // Columnar layout: column names once, rows as arrays — far fewer tokens than row objects.
    public static JsonObject ToJson(QueryTable t, int maxRows, int maxStringLen = DefaultMaxStringLen)
    {
        JsonArray cols = new();
        foreach (string c in t.Columns)
        {
            cols.Add(c);
        }
        JsonArray rows = new();
        int n = Math.Min(maxRows, t.Rows.Count);
        for (int i = 0; i < n; i++)
        {
            JsonArray row = new();
            foreach (object? cell in t.Rows[i])
            {
                row.Add(Cell(cell, maxStringLen));
            }
            rows.Add(row);
        }
        JsonObject o = new()
        {
            ["columns"] = cols,
            ["rows"] = rows,
            ["row_count"] = t.TotalRows
        };
        if (t.TotalRows > n)
        {
            o["truncated"] = true;
            o["note"] = $"Showing first {n} of {t.TotalRows} rows. Aggregate (GROUP BY / ORDER BY ... LIMIT) instead of paging raw rows.";
        }
        return o;
    }

    // Integers beyond 2^53 are emitted as strings (JS clients round them); tool args accept numeric strings.
    private const long MaxSafeInteger = 9007199254740991;

    public static JsonNode? Cell(object? cell, int maxStringLen = DefaultMaxStringLen) => cell switch
    {
        null => null,
        long l when l > MaxSafeInteger || l < -MaxSafeInteger => JsonValue.Create(l.ToString()),
        long l => JsonValue.Create(l),
        double d when double.IsFinite(d) => JsonValue.Create(Math.Round(d, 6)),
        double d => JsonValue.Create(d.ToString()),
        string s when maxStringLen > 0 && s.Length > maxStringLen =>
            JsonValue.Create($"{s[..maxStringLen]}…(+{s.Length - maxStringLen} chars)"),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(cell.ToString())
    };

    public static void ThrowIfError(QueryTable t, string? sql = null)
    {
        if (t.Error is not null)
        {
            throw new SqlQueryException(QueryErrors.Format(t.Error, sql));
        }
    }
}

internal static class SqlText
{
    public static string Literal(string value)
    {
        if (value.Contains('\0'))
        {
            throw new McpInvalidParamsException("string parameters must not contain NUL");
        }
        return "'" + value.Replace("'", "''") + "'";
    }

    public static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    // For GLOB patterns and identifiers that are interpolated into SQL.
    public static string Identifier(string value, string paramName)
    {
        foreach (char c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '*' or '?' or '[' or ']' or '-'))
            {
                throw new McpInvalidParamsException($"'{paramName}' contains unsupported character '{c}'");
            }
        }
        return value;
    }
}
