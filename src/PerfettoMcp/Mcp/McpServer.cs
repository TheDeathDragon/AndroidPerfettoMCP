using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Logging;
using PerfettoMcp.Tools;

namespace PerfettoMcp.Mcp;

internal sealed class McpServer
{
    // Calls that change the default trace (the one tools use without `trace_id`); record_* only when they load.
    private static readonly HashSet<string> DefaultTraceChangers = new(StringComparer.Ordinal)
    {
        "load_trace", "unload_trace", "record_trace", "record_boot"
    };

    private readonly ToolRegistry tools;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> inFlight = new();
    private readonly object order = new();
    private readonly Dictionary<string, long> arrivals = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, TaskCompletionSource> pendingChangers = new();
    private readonly Dictionary<string, long> changerSeqByKey = new(StringComparer.Ordinal);
    private long arrivalSeq;

    public McpServer(ToolRegistry tools)
    {
        this.tools = tools;
    }

    // Runs in read order before concurrent dispatch, so a call without trace_id can wait for an earlier load.
    public void NoteArrival(string line)
    {
        if (!line.Contains("\"tools/call\"", StringComparison.Ordinal))
        {
            return;
        }
        string? key;
        bool changer;
        try
        {
            JsonObject? msg = JsonNode.Parse(line) as JsonObject;
            if (msg?["method"]?.GetValue<string>() != "tools/call" || msg["id"] is not JsonNode id)
            {
                return;
            }
            key = IdKey(id);
            JsonObject? p = msg["params"] as JsonObject;
            string? name = p?["name"]?.GetValue<string>();
            bool loads = (p?["arguments"] as JsonObject)?["load"] is not JsonValue load || !load.TryGetValue(out bool l) || l;
            changer = name is not null && DefaultTraceChangers.Contains(name) && loads;
        }
        catch (Exception)
        {
            return; // HandleAsync reports the parse error
        }
        lock (order)
        {
            long seq = ++arrivalSeq;
            arrivals[key] = seq;
            if (changer && !changerSeqByKey.ContainsKey(key))
            {
                pendingChangers[seq] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                changerSeqByKey[key] = seq;
            }
        }
    }

    // Waits for every default-changing call that arrived before this one (none when arrival order is unknown).
    private Task WaitForEarlierChangersAsync(long seq, CancellationToken ct)
    {
        List<Task> earlier;
        lock (order)
        {
            earlier = pendingChangers.Where(p => p.Key < seq).Select(p => p.Value.Task).ToList();
        }
        return earlier.Count == 0 ? Task.CompletedTask : Task.WhenAll(earlier).WaitAsync(ct);
    }

    private long TakeArrival(string? key)
    {
        lock (order)
        {
            return key is not null && arrivals.Remove(key, out long seq) ? seq : 0;
        }
    }

    private void CompleteChanger(string? key)
    {
        if (key is null)
        {
            return;
        }
        TaskCompletionSource? tcs = null;
        lock (order)
        {
            if (changerSeqByKey.Remove(key, out long seq))
            {
                pendingChangers.Remove(seq, out tcs);
            }
        }
        tcs?.TrySetResult();
    }

    private static bool TakesTraceId(ToolDescriptor d) =>
        (d.InputSchema as JsonObject)?["properties"] is JsonObject p && p.ContainsKey("trace_id");

    public async Task<string?> HandleAsync(string rawRequest, Func<string, Task> sendNotification)
    {
        JsonRpcRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<JsonRpcRequest>(rawRequest, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            return Serialize(ErrorResponse(null, JsonRpcError.ParseError, $"parse error: {ex.Message}"));
        }
        if (req is null || string.IsNullOrEmpty(req.Method))
        {
            return Serialize(ErrorResponse(req?.Id, JsonRpcError.InvalidRequest, "missing method"));
        }
        bool isNotification = req.Id is null;
        Log.Debug($"-> {req.Method}{(isNotification ? " (notification)" : "")}");
        try
        {
            JsonNode? result = req.Method switch
            {
                "initialize" => HandleInitialize(req.Params),
                "initialized" => null,
                "notifications/initialized" => null,
                "notifications/cancelled" => HandleCancelled(req.Params),
                "ping" => new JsonObject(),
                "tools/list" => HandleToolsList(),
                "tools/call" => await HandleToolsCallAsync(req.Id, req.Params, sendNotification).ConfigureAwait(false),
                _ => throw new McpMethodNotFoundException(req.Method)
            };
            if (isNotification)
            {
                return null;
            }

            return Serialize(new JsonRpcResponse { Id = req.Id, Result = result ?? new JsonObject() });
        }
        catch (McpMethodNotFoundException ex)
        {
            return isNotification ? null : Serialize(ErrorResponse(req.Id, JsonRpcError.MethodNotFound, ex.Message));
        }
        catch (McpInvalidParamsException ex)
        {
            return isNotification ? null : Serialize(ErrorResponse(req.Id, JsonRpcError.InvalidParams, ex.Message));
        }
        catch (Exception ex)
        {
            Log.Error($"handler crashed for {req.Method}: {ex}");
            return isNotification ? null : Serialize(ErrorResponse(req.Id, JsonRpcError.InternalError, ex.Message));
        }
    }

    private static JsonObject HandleInitialize(JsonNode? paramsNode)
    {
        string? requested = (paramsNode as JsonObject)?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersions.Negotiate(requested),
            // The supervisor emits tools/list_changed after a hot update.
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
            ["serverInfo"] = new JsonObject { ["name"] = "perfetto-mcp", ["version"] = BuildInfo.Version },
            ["instructions"] = ServerInstructions.Text
        };
    }

    private JsonNode? HandleCancelled(JsonNode? paramsNode)
    {
        JsonNode? id = (paramsNode as JsonObject)?["requestId"];
        if (id is not null && inFlight.TryGetValue(IdKey(id), out CancellationTokenSource? cts))
        {
            Log.Info($"client cancelled request {IdKey(id)}");
            TryCancel(cts);
        }
        return null;
    }

    // Client went away: abort everything still running so the server can exit promptly.
    public void CancelAll()
    {
        foreach (CancellationTokenSource cts in inFlight.Values)
        {
            TryCancel(cts);
        }
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // request completed concurrently
        }
    }

    private JsonObject HandleToolsList()
    {
        JsonArray array = new();
        foreach (ToolDescriptor d in tools.Descriptors)
        {
            array.Add(JsonSerializer.SerializeToNode(d, JsonOpts.Default));
        }
        return new JsonObject { ["tools"] = array };
    }

    private async Task<JsonNode> HandleToolsCallAsync(JsonNode? id, JsonNode? paramsNode, Func<string, Task> sendNotification)
    {
        string? arrivalKey = id is null ? null : IdKey(id);
        long seq = TakeArrival(arrivalKey);
        try
        {
            return await HandleToolsCallCoreAsync(id, seq, paramsNode, sendNotification).ConfigureAwait(false);
        }
        finally
        {
            // Every path (bad params, unknown tool, failure) releases the calls queued behind a load.
            CompleteChanger(arrivalKey);
        }
    }

    private async Task<JsonNode> HandleToolsCallCoreAsync(JsonNode? id, long seq, JsonNode? paramsNode, Func<string, Task> sendNotification)
    {
        if (paramsNode is not JsonObject obj)
        {
            throw new McpInvalidParamsException("params must be an object");
        }
        string? name = obj["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
        {
            throw new McpInvalidParamsException("missing 'name' field");
        }
        JsonObject args = obj["arguments"] as JsonObject ?? new JsonObject();
        if (!tools.TryGet(name, out IToolHandler? handler))
        {
            return JsonSerializer.SerializeToNode(ToolCallResult.Error($"unknown tool: {name}"), JsonOpts.Default)!;
        }

        JsonNode? progressToken = (obj["_meta"] as JsonObject)?["progressToken"];
        using CancellationTokenSource cts = new();
        string? key = id is null ? null : IdKey(id);
        if (key is not null && !inFlight.TryAdd(key, cts))
        {
            key = null; // duplicate id still in flight; leave its entry alone
        }

        ToolCallResult result;
        long started = Environment.TickCount64;
        try
        {
            Args.RejectUnknown(args, handler.Descriptor);
            if (seq > 0 && TakesTraceId(handler.Descriptor) && string.IsNullOrWhiteSpace((args["trace_id"] as JsonValue)?.ToString()))
            {
                await WaitForEarlierChangersAsync(seq, cts.Token).ConfigureAwait(false);
            }
            RequestContext ctx = new(progressToken, sendNotification, cts.Token);
            result = await handler.InvokeAsync(args, ctx).ConfigureAwait(false);
        }
        catch (McpInvalidParamsException ex)
        {
            result = ToolCallResult.Error($"invalid arguments: {ex.Message}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            result = ToolCallResult.Error("cancelled by client");
        }
        catch (ToolException ex)
        {
            result = ToolCallResult.Error(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn($"tool '{name}' threw: {ex}");
            result = ToolCallResult.Error($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (key is not null)
            {
                inFlight.TryRemove(key, out _);
            }
        }
        Log.Info($"tool {name} {(result.IsError ? "failed" : "ok")} in {Environment.TickCount64 - started} ms");
        PerfLog.RecordCall(name, Environment.TickCount64 - started, !result.IsError);
        return JsonSerializer.SerializeToNode(result, JsonOpts.Default)!;
    }

    private static string IdKey(JsonNode id) => id.ToJsonString();

    private static JsonRpcResponse ErrorResponse(JsonNode? id, int code, string message) =>
        new() { Id = id, Error = new JsonRpcError { Code = code, Message = message } };

    private static string Serialize(JsonRpcResponse response) =>
        JsonSerializer.Serialize(response, JsonOpts.Default);
}

internal sealed class McpMethodNotFoundException : Exception
{
    public McpMethodNotFoundException(string method) : base($"method not found: {method}") { }
}

internal sealed class McpInvalidParamsException : Exception
{
    public McpInvalidParamsException(string message) : base(message) { }
}

// Expected, user-facing failure: message is returned to the LLM verbatim, no stack trace logged.
internal class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}

// The SQL itself was rejected by trace_processor; the processor is still healthy.
internal sealed class SqlQueryException : ToolException
{
    public SqlQueryException(string message) : base(message) { }
}
