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
    public const string Text = """
        Perfetto trace analysis server (Android-focused). Workflow:

        1. `load_trace` first. It returns a `trace_id` and a health/overview summary. Loading several traces
           (e.g. base vs fix) is supported; every other tool takes an optional `trace_id` and defaults to the
           most recently loaded trace.

        2. Prefer the dedicated tools for common questions — they already use the right stdlib modules and joins:
           - `android_startups`        app cold/warm/hot launches and their main-thread breakdown
           - `android_jank_frames`     janky frames per process (FrameTimeline)
           - `thread_state_breakdown`  Running / Runnable / Sleeping / D-state time for a thread or process in a window
           - `cpu_top_threads`         who used the CPU in a window
           - `android_binder_txns`     slowest binder transactions
           - `slice_hotspots`          heaviest slices (by total or max duration) for a process / thread / name
           - `frame_intervals`         fps / freezes of any render thread (eglSwapBuffers, doFrame ...), incl. GL/SurfaceView apps
           - `cpu_profile`             perf callstack hotspots per thread/window; unsymbolized frames as lib.so+0x<rel_pc>
           - `profile_threads`         sample chosen threads on the device now (simpleperf): self / inclusive hot
                                       functions, incl. kernel threads and system_server threads (root)
           - `cpu_frequency`           time-weighted CPU frequency per cluster (throttling check)
           - `android_gc` / `android_logs`  ART GC cost; logcat search aligned with the timeline
           - `memory_summary` / `process_memory`  memory pressure (MemAvailable, swap, LMK, direct reclaim) / one process's RSS
           - `io_summary`              who is blocked in D state and on what; block-device latency; page-cache reads
           - `compare_traces`          base vs fix diff of startup / CPU / main-thread states / binder / GC / memory / slices
           - `android_anrs`            ANRs and what the main thread was doing / stuck in before each
           - `lock_contention`         Java monitor contention by lock holder; links system_server waits to binder clients
           - `wakeup_chain`            who a thread was waiting for: wakers of its sleeps hop by hop, plus the critical path
                                       (which threads held up its progress) for windows up to 30 s
           - `main_thread_blocking_calls`  curated main-thread blocking calls (binder, locks, inflate, draw ...) per process
           - `analyze_bootchart`       Android init bootchart (dir or .tgz, no trace needed): boot phases and who slowed them
           - `boot_timeline`           boot trace: boot_progress milestones, system_server phases, slowest service starts
           - `record_trace`            capture a trace on an adb device (built-in `scenario` / `heap` configs), pull and load it
           - `record_boot`             REBOOT an adb device and capture bootchart + boot trace, then analyze both
           - `java_heap`               Java heap dump: retained classes, UI objects, bitmaps, growth vs a baseline dump,
                                       `class_name` = shortest reference path to a GC root (who keeps it alive)
           - `server_stats`            this server's tool durations and slow queries (performance diagnosis)

           Typical drill-down: frame_intervals finds a freeze -> pass its gap_start_ts/gap_end_ts as ts_start/ts_end to
           thread_state_breakdown, cpu_profile and cpu_frequency to see what the thread did and why; wakeup_chain over the
           same window names the thread it was really waiting on (critical_path).
           Leak check: record_trace(config="heap", process=X) -> repeat the scenario -> heap dump again ->
           java_heap(baseline_trace_id=...) -> java_heap(class_name=<growing class>) for who holds it.
           Performance: tools on one trace run one at a time; call them sequentially, `server_stats` shows where time went.

        3. `execute_sql` for everything else. PREFER PerfettoSQL stdlib over raw `slice` + `LIKE '%x%'` scans:
           `INCLUDE PERFETTO MODULE <name>; SELECT ...` in a single call. Useful modules:
           android.startup.startups, android.frames.timeline, android.binder, android.anrs,
           android.garbage_collection, android.monitor_contention, sched.time_in_state, slices.with_context,
           linux.cpu.frequency. Full list: `list_stdlib_modules`; docs: https://perfetto.dev/docs/analysis/stdlib-docs

        4. Timestamps are nanoseconds. Aggregate in SQL instead of pulling raw rows — results are truncated at
           `max_rows`. Long queries are cancelled after `timeout_s`; the trace processor is then restarted and the
           trace reloaded automatically, so narrow the query (time window, upid/utid filter, LIMIT) and retry.
        """;
}
