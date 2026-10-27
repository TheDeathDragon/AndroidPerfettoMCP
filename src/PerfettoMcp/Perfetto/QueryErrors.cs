using System;
using System.Text.RegularExpressions;

namespace PerfettoMcp.Perfetto;

internal enum QueryErrorKind
{
    MissingTable,
    MissingModule,
    MissingColumn,
    AmbiguousColumn,
    MissingFunction,
    MultipleOutputStatements,
    Other
}

internal static partial class QueryErrors
{
    // Casing is significant: SQLite says "no such table:", the stdlib loader "Module not found:".
    public static QueryErrorKind Classify(string message)
    {
        if (message.Contains("no such table:"))
        {
            return QueryErrorKind.MissingTable;
        }
        if (message.Contains("Module not found:") || message.Contains("INCLUDE: unknown module"))
        {
            return QueryErrorKind.MissingModule;
        }
        if (message.Contains("no such column:"))
        {
            return QueryErrorKind.MissingColumn;
        }
        if (message.Contains("ambiguous column name:"))
        {
            return QueryErrorKind.AmbiguousColumn;
        }
        if (message.Contains("no such function:"))
        {
            return QueryErrorKind.MissingFunction;
        }
        if (message.Contains("Result rows were returned for multiples queries") || message.Contains("multiple output statements"))
        {
            return QueryErrorKind.MultipleOutputStatements;
        }
        return QueryErrorKind.Other;
    }

    public static string Format(string message, string? sql = null)
    {
        string hint = Classify(message) switch
        {
            // Already included: the object is a table function / macro used without its arguments.
            QueryErrorKind.MissingTable when StdlibIndex.ModuleFor(Name(message, "no such table:")) is { } module
                && sql is not null && sql.Contains($"INCLUDE PERFETTO MODULE {module}", StringComparison.OrdinalIgnoreCase) =>
                $"Hint: `{Name(message, "no such table:")}` comes from `{module}`, which is already included: it is probably a " +
                "table function — call it with its arguments, e.g. `SELECT * FROM name(arg)`; see the stdlib docs for the signature.",
            QueryErrorKind.MissingTable when StdlibIndex.ModuleFor(Name(message, "no such table:")) is { } module =>
                $"Hint: `{Name(message, "no such table:")}` is defined by a stdlib module; prepend `INCLUDE PERFETTO MODULE {module};`.",
            QueryErrorKind.MissingTable =>
                $"Hint: `{Name(message, "no such table:")}` does not exist. If it is a stdlib table/view, prepend " +
                "`INCLUDE PERFETTO MODULE <module>;` (find the module with `list_stdlib_modules`). Use `list_tables` to see what exists.",
            QueryErrorKind.MissingFunction when StdlibIndex.ModuleFor(Name(message, "no such function:")) is { } module =>
                $"Hint: `{Name(message, "no such function:")}` is defined by a stdlib module; prepend `INCLUDE PERFETTO MODULE {module};`.",
            QueryErrorKind.MissingModule =>
                "Hint: unknown stdlib module. Check the exact name with `list_stdlib_modules`.",
            QueryErrorKind.MissingColumn =>
                $"Hint: column `{Name(message, "no such column:")}` does not exist. Call `list_table_structure` on the table " +
                "instead of guessing; stdlib views and base tables have different schemas.",
            QueryErrorKind.AmbiguousColumn =>
                "Hint: qualify the column with its table alias (e.g. `s.ts`).",
            QueryErrorKind.MissingFunction =>
                "Hint: stdlib functions need their module INCLUDEd first; check the name with `list_stdlib_modules`.",
            QueryErrorKind.MultipleOutputStatements =>
                "Hint: only the last statement may return rows. Split the query or turn earlier SELECTs into CTEs.",
            _ => string.Empty
        };
        return hint.Length == 0 ? $"SQL error: {message}" : $"SQL error: {message}\n{hint}";
    }

    private static string Name(string message, string marker)
    {
        int i = message.IndexOf(marker, System.StringComparison.Ordinal);
        if (i < 0)
        {
            return "?";
        }
        Match m = IdentRegex().Match(message, i + marker.Length);
        return m.Success ? m.Value : "?";
    }

    [GeneratedRegex(@"[A-Za-z0-9_.]+")]
    private static partial Regex IdentRegex();
}
