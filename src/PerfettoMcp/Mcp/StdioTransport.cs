using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Logging;

namespace PerfettoMcp.Mcp;

internal sealed class StdioTransport
{
    // onArrival runs synchronously in read order before the line is dispatched (requests then run concurrently).
    public async Task RunAsync(Func<string, Func<string, Task>, Task<string?>> handler, Action onEof, CancellationToken ct,
        Action<string>? onArrival = null)
    {
        using TextReader reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using TextWriter writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        SemaphoreSlim writeGate = new(1, 1);

        async Task WriteLineAsync(string line)
        {
            await writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
        }

        List<Task> pending = new();
        Log.Info("stdio transport listening");
        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                Log.Info("stdin closed; cancelling in-flight requests");
                onEof();
                break;
            }
            if (line.Length == 0)
            {
                continue;
            }

            onArrival?.Invoke(line);
            pending.RemoveAll(static t => t.IsCompleted);
            pending.Add(Task.Run(async () =>
            {
                try
                {
                    string? response = await handler(line, WriteLineAsync).ConfigureAwait(false);
                    if (response is not null)
                    {
                        await WriteLineAsync(response).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Error($"stdio dispatch error: {ex.Message}");
                }
            }, ct));
        }
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }
}
