using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PerfettoMcp.Perfetto;

// Public stdlib symbols per module; regenerate with scripts/gen_stdlib_index.py when bumping Perfetto.
internal static class StdlibIndex
{
    private static readonly Lazy<IReadOnlyList<StdlibModule>> Modules = new(Load);

    public static IReadOnlyList<StdlibModule> All => Modules.Value;

    // prelude.* is always loaded by trace_processor, so it never needs an INCLUDE.
    public static string? ModuleFor(string objectName)
    {
        StdlibModule? m = All.FirstOrDefault(x => !x.Name.StartsWith("prelude.", StringComparison.Ordinal)
            && x.Objects.Contains(objectName, StringComparer.OrdinalIgnoreCase));
        return m?.Name;
    }

    public static bool IsModule(string name) => All.Any(m => m.Name == name);

    public static IEnumerable<StdlibModule> Search(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return All;
        }
        string f = filter.Trim();
        return All.Where(m => m.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || m.Objects.Any(o => o.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<StdlibModule> Load()
    {
        using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("stdlib_index.tsv");
        if (s is null)
        {
            return Array.Empty<StdlibModule>();
        }
        using StreamReader r = new(s);
        List<StdlibModule> list = new();
        while (r.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            string[] parts = line.Split('\t');
            string[] objects = parts.Length > 1 && parts[1].Length > 0 ? parts[1].Split(',') : Array.Empty<string>();
            list.Add(new StdlibModule(parts[0], objects));
        }
        return list;
    }
}

internal sealed record StdlibModule(string Name, string[] Objects);
