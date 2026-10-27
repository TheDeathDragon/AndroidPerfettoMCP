using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Logging;

namespace PerfettoMcp.Mcp;

internal sealed class RequestContext
{
    private readonly Func<string, Task>? sendNotification;
    private readonly JsonNode? progressToken;

    public RequestContext(JsonNode? progressToken, Func<string, Task>? sendNotification, CancellationToken cancellation)
    {
        this.progressToken = progressToken;
        this.sendNotification = sendNotification;
        Cancellation = cancellation;
    }

    public static RequestContext None { get; } = new(null, null, CancellationToken.None);

    public CancellationToken Cancellation { get; }

    public async Task ReportProgressAsync(double progress, double? total, string message)
    {
        Log.Debug($"progress {progress}/{total}: {message}");
        if (progressToken is null || sendNotification is null)
        {
            return;
        }

        JsonObject p = new()
        {
            ["progressToken"] = progressToken.DeepClone(),
            ["progress"] = progress,
            ["message"] = message
        };
        if (total is not null)
        {
            p["total"] = total.Value;
        }

        JsonObject note = new()
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/progress",
            ["params"] = p
        };
        try
        {
            await sendNotification(note.ToJsonString(JsonOpts.Default)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"progress notification dropped: {ex.Message}");
        }
    }
}
