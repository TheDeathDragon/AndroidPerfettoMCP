using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PerfettoMcp.Mcp;

namespace PerfettoMcp.Tools;

internal static class Args
{
    public static void RejectUnknown(JsonObject args, ToolDescriptor descriptor)
    {
        if (descriptor.InputSchema["properties"] is not JsonObject props)
        {
            return;
        }
        List<string> unknown = args.Select(kv => kv.Key).Where(k => !props.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new McpInvalidParamsException(
                $"unknown parameter(s) {string.Join(", ", unknown)}; accepted: {string.Join(", ", props.Select(p => p.Key))}");
        }
    }

    public static string RequireString(JsonObject args, string name) =>
        OptionalString(args, name) ?? throw new McpInvalidParamsException($"missing '{name}'");

    public static string? OptionalString(JsonObject args, string name)
    {
        JsonNode? node = args[name];
        if (node is null)
        {
            return null;
        }
        if (node is JsonValue v && v.TryGetValue(out string? s))
        {
            return s;
        }
        // Tolerate numbers where a string is expected (e.g. process ids).
        return node.GetValueKind() is JsonValueKind.Number ? node.ToJsonString() : throw new McpInvalidParamsException($"'{name}' must be a string");
    }

    public static List<string>? OptionalStringList(JsonObject args, string name)
    {
        JsonNode? node = args[name];
        if (node is null)
        {
            return null;
        }
        if (node is JsonArray arr)
        {
            return arr.Select(n => n?.GetValue<string>() ?? throw new McpInvalidParamsException($"'{name}' items must be strings")).ToList();
        }
        throw new McpInvalidParamsException($"'{name}' must be an array of strings");
    }

    // LLMs frequently send numbers as strings ("10"); accept both.
    public static long? OptionalLong(JsonObject args, string name)
    {
        JsonNode? node = args[name];
        if (node is null)
        {
            return null;
        }
        string? text = NumericText(node);
        if (text is not null)
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            {
                return l;
            }
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d % 1 == 0 && Math.Abs(d) < 9e18)
            {
                return (long)d;
            }
        }
        throw new McpInvalidParamsException($"'{name}' must be an integer");
    }

    public static double? OptionalDouble(JsonObject args, string name)
    {
        JsonNode? node = args[name];
        if (node is null)
        {
            return null;
        }
        string? text = NumericText(node);
        if (text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return d;
        }
        throw new McpInvalidParamsException($"'{name}' must be a number");
    }

    public static int OptionalInt(JsonObject args, string name, int fallback, int min, int max)
    {
        long? v = OptionalLong(args, name);
        if (v is null)
        {
            return fallback;
        }
        if (v < min || v > max)
        {
            throw new McpInvalidParamsException($"'{name}' must be between {min} and {max}");
        }
        return (int)v.Value;
    }

    public static bool OptionalBool(JsonObject args, string name, bool fallback)
    {
        JsonNode? node = args[name];
        if (node is null)
        {
            return fallback;
        }
        if (node is JsonValue v)
        {
            if (v.TryGetValue(out bool b))
            {
                return b;
            }
            if (v.TryGetValue(out string? s) && bool.TryParse(s.Trim(), out bool parsed))
            {
                return parsed;
            }
        }
        throw new McpInvalidParamsException($"'{name}' must be a boolean");
    }

    public static string? TraceId(JsonObject args) => OptionalString(args, "trace_id");

    // JSON number or numeric string -> its text; works for parsed and constructed JsonValues alike.
    private static string? NumericText(JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.Number => node.ToJsonString(),
        JsonValueKind.String => node.GetValue<string>().Trim(),
        _ => null
    };
}
