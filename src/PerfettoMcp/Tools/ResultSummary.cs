using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

// Per-column statistics (numeric: min / max / avg / percentiles; text: distinct count, top values).
internal static class ResultSummary
{
    public static JsonObject ColumnStats(QueryTable t)
    {
        JsonObject stats = new();
        for (int c = 0; c < t.Columns.Count; c++)
        {
            List<object?> values = t.Rows.Select(r => r[c]).ToList();
            int nulls = values.Count(v => v is null);
            List<double> nums = values.Where(v => v is long or double).Select(v => Convert.ToDouble(v)).ToList();
            JsonObject s = new() { ["nulls"] = nulls };
            if (nums.Count > 0 && nums.Count == values.Count - nulls)
            {
                nums.Sort();
                s["min"] = Num(nums[0]);
                s["max"] = Num(nums[^1]);
                s["avg"] = Num(nums.Average());
                s["p50"] = Num(Percentile(nums, 50));
                s["p90"] = Num(Percentile(nums, 90));
                s["p99"] = Num(Percentile(nums, 99));
            }
            else
            {
                List<IGrouping<string, object?>> groups = values.Where(v => v is not null)
                    .GroupBy(v => v!.ToString()!).OrderByDescending(g => g.Count()).ToList();
                s["distinct"] = groups.Count;
                JsonArray top = new();
                foreach (IGrouping<string, object?> g in groups.Take(5))
                {
                    string key = g.Key.Length > 80 ? g.Key[..80] + "…" : g.Key;
                    top.Add(new JsonArray(key, g.Count()));
                }
                s["top"] = top;
            }
            stats[t.Columns[c]] = s;
        }
        return stats;
    }

    // Sample rows: the first ones, or the largest by `sortBy` (a column name).
    public static QueryTable Sample(QueryTable t, string? sortBy, int n)
    {
        int col = sortBy is null ? -1 : t.Columns.IndexOf(sortBy);
        IEnumerable<object?[]> rows = col < 0
            ? t.Rows
            : t.Rows.OrderByDescending(r => r[col] is long or double ? Convert.ToDouble(r[col]) : double.MinValue);
        QueryTable sample = new() { TotalRows = t.TotalRows, Error = t.Error, ElapsedMs = t.ElapsedMs };
        sample.Columns.AddRange(t.Columns);
        sample.Rows.AddRange(rows.Take(n));
        return sample;
    }

    private static JsonValue Num(double v) =>
        Math.Abs(v) <= 9007199254740991 && v == Math.Floor(v) ? JsonValue.Create((long)v) : JsonValue.Create(Math.Round(v, 3));

    private static double Percentile(List<double> sorted, double p)
    {
        double rank = p / 100 * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }
}
