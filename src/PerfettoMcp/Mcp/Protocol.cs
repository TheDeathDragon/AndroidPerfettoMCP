using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PerfettoMcp.Mcp;

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Package names, thread names and paths are often CJK; keep them readable and short.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

internal sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public JsonNode? Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = string.Empty;
    [JsonPropertyName("params")] public JsonNode? Params { get; set; }
}

internal sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    // JSON-RPC requires "id": null (not an absent id) when the request id is unknown.
    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public JsonNode? Id { get; set; }
    [JsonPropertyName("result")] public JsonNode? Result { get; set; }
    [JsonPropertyName("error")] public JsonRpcError? Error { get; set; }
}

internal sealed class JsonRpcError
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("data")] public JsonNode? Data { get; set; }

    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

internal sealed class ToolDescriptor
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("inputSchema")] public JsonNode InputSchema { get; set; } = new JsonObject();
    [JsonPropertyName("annotations")] public JsonNode? Annotations { get; set; }
}

internal sealed class ToolCallResult
{
    [JsonPropertyName("content")] public List<ToolContent> Content { get; set; } = new();
    [JsonPropertyName("isError")] public bool IsError { get; set; }

    public static ToolCallResult Text(string text) => new()
    {
        Content = { new ToolContent { Type = "text", Text = text } }
    };

    public static ToolCallResult Json(JsonNode payload) => new()
    {
        Content = { new ToolContent { Type = "text", Text = payload.ToJsonString(JsonOpts.Default) } }
    };

    public static ToolCallResult Error(string message) => new()
    {
        Content = { new ToolContent { Type = "text", Text = message } },
        IsError = true
    };
}

internal sealed class ToolContent
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("text")] public string? Text { get; set; }
}

internal static class ProtocolVersions
{
    public const string Fallback = "2024-11-05";

    private static readonly HashSet<string> Supported = new() { "2024-11-05", "2025-03-26", "2025-06-18" };

    public static string Negotiate(string? requested) =>
        requested is not null && Supported.Contains(requested) ? requested : Fallback;
}

internal static class ServerInstructions
{
    // Claude Code truncates server instructions at 4096 characters; scenario playbooks live in the skill.
    public const string Text = """
        Perfetto trace analysis for Android performance.

        1. `load_trace` first: it returns a `trace_id` and an overview (health issues, launches, top CPU, hints).
           Other tools take an optional `trace_id` (default: the latest load, which other agents sharing this server
           can change); pass it explicitly whenever more than one trace is loaded. Load two traces for base vs fix.
        2. Use the dedicated tools before SQL; they already use the right stdlib modules and joins:
           launches `android_startups` | jank `android_jank_frames`, `frame_intervals` | ANR `android_anrs` |
           CPU `cpu_top_threads`, `cpu_frequency`, `cpu_profile` | heaviest slices `slice_hotspots` |
           thread states `thread_state_breakdown` |
           who a thread waited for `wakeup_chain` (critical path) | binder and locks `android_binder_txns`,
           `lock_contention`, `main_thread_blocking_calls` | memory and IO `memory_summary`, `process_memory`,
           `io_summary`, `android_gc` | logs `android_logs` | operation latency `event_latency` | heap `java_heap` |
           boot `analyze_bootchart`, `boot_timeline` | diff `compare_traces` | device capture `record_trace`,
           `record_boot` (REBOOTS the device), `profile_threads`.
        3. Drill down by window: a symptom's time range (e.g. frame_intervals gap_start_ts / gap_end_ts) becomes
           ts_start / ts_end for thread_state_breakdown, cpu_profile, cpu_frequency and wakeup_chain.
        4. `execute_sql` for the rest: prefer `INCLUDE PERFETTO MODULE <module>; SELECT ...` (find modules with
           `list_stdlib_modules`) over raw slice scans with LIKE. Timestamps are nanoseconds. Aggregate in SQL:
           rows are capped at `max_rows`.
        5. A query past `timeout_s` restarts the trace processor and the trace reloads on next use: narrow the
           window or filter and retry. Calls on one trace run one at a time, so issue them sequentially.

        Step-by-step playbooks per scenario: the `perfetto-analysis` skill, when installed.
        """;
}
