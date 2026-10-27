using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Logging;
using PerfettoMcp.Mcp;

namespace PerfettoMcp.Perfetto;

// A loaded trace. All access to the child goes through `gate` (queries run serially); a timed-out query kills the
// child, which reloads on next use.
internal sealed class TraceSession : IDisposable
{
    private const int ChunkSize = 16 * 1024 * 1024;

    private readonly string binary;
    private readonly Settings settings;
    private readonly SemaphoreSlim gate = new(1, 1);
    // Cancelled on Dispose so an in-flight load stops instead of resurrecting a child.
    private readonly CancellationTokenSource lifetime = new();
    private TraceProcessor? tp;
    private long loadedSize;
    private DateTime loadedMtimeUtc;
    private volatile bool disposed;
    private int gateWaiters;

    public TraceSession(string id, string path, string binary, Settings settings)
    {
        Id = id;
        Path = path;
        this.binary = binary;
        this.settings = settings;
    }

    public string Id { get; }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    public double LastLoadSeconds { get; private set; }

    public int LoadCount { get; private set; }

    public bool IsLive => tp is { HasExited: false };

    // Counts toward the resident-processor cap while a child is being spawned/fed.
    public bool IsLoading { get; private set; }

    public async Task EnsureLoadedAsync(RequestContext ctx)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation, lifetime.Token);
        await WaitGateAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ctx).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<QueryTable> QueryAsync(string sql, TimeSpan timeout, int maxRows, RequestContext ctx)
    {
        using (CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation, lifetime.Token))
        {
            waitCts.CancelAfter(timeout);
            try
            {
                await WaitGateAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ctx.Cancellation.IsCancellationRequested && !disposed)
            {
                throw new ToolException(
                    $"Trace {Id} is busy: another query on it did not finish within {timeout.TotalSeconds:0}s. Retry later.");
            }
        }

        try
        {
            await EnsureLoadedLockedAsync(ctx).ConfigureAwait(false);
            TraceProcessor live = tp!;
            using CancellationTokenSource qcts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation, lifetime.Token);
            qcts.CancelAfter(timeout);
            try
            {
                return await live.QueryAsync(sql, maxRows, settings.MaxResultBytes, qcts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ctx.Cancellation.IsCancellationRequested && !disposed)
            {
                KillLocked($"query exceeded {timeout.TotalSeconds:0}s");
                throw new ToolException(
                    $"Query exceeded the {timeout.TotalSeconds:0}s timeout and was aborted (trace_processor restarted; " +
                    $"the trace reloads automatically on the next call, last load took {LastLoadSeconds:0.0}s). " +
                    "Narrow it: restrict ts to a window, filter by upid/utid, aggregate with GROUP BY, add LIMIT, " +
                    "or pass a larger `timeout_s`.");
            }
            catch (OperationCanceledException)
            {
                KillLocked("query cancelled");
                throw;
            }
            catch (ResultTooLargeException ex)
            {
                KillLocked("result too large");
                throw new ToolException(
                    $"Query result exceeded {ex.LimitBytes / 1048576} MB and was aborted (trace_processor restarted). " +
                    "Aggregate in SQL or add LIMIT instead of selecting raw rows.");
            }
            catch (HttpRequestException ex)
            {
                string tail = live.StderrTail;
                bool crashed = live.HasExited;
                KillLocked($"rpc failure: {ex.Message}");
                throw new ToolException(crashed
                    ? $"trace_processor crashed while running the query (often out of memory). It will reload on the next call.\n{tail}"
                    : $"trace_processor RPC failed: {ex.Message}");
            }
        }
        finally
        {
            LastUsedUtc = DateTime.UtcNow;
            gate.Release();
        }
    }

    // Frees the child process but keeps the session; next use reloads.
    public bool TryUnloadIfIdle(string reason)
    {
        if (disposed || !gate.Wait(0))
        {
            return false;
        }
        try
        {
            KillLocked(reason);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        lifetime.Cancel();
        // Give an in-flight query/load a moment to observe cancellation; kill regardless.
        bool held = gate.Wait(TimeSpan.FromSeconds(2));
        try
        {
            KillLocked("disposed");
        }
        finally
        {
            if (held)
            {
                gate.Release();
            }
        }
    }

    private async Task WaitGateAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref gateWaiters);
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref gateWaiters);
        }
        if (disposed)
        {
            gate.Release();
            ThrowIfDisposed();
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ToolException($"Trace {Id} was unloaded. Call load_trace again.");
        }
    }

    private async Task EnsureLoadedLockedAsync(RequestContext ctx)
    {
        FileInfo fi = new(Path);
        if (!fi.Exists)
        {
            KillLocked("file removed");
            throw new ToolException($"Trace file not found: {Path}");
        }
        if (tp is { HasExited: false } && fi.Length == loadedSize && fi.LastWriteTimeUtc == loadedMtimeUtc)
        {
            return;
        }
        if (tp is not null)
        {
            Log.Info($"reloading {Id} ({(tp.HasExited ? "processor exited" : "file changed")})");
        }
        KillLocked("reload");

        using CancellationTokenSource loadCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation, lifetime.Token);
        loadCts.CancelAfter(settings.LoadTimeout);
        Stopwatch sw = Stopwatch.StartNew();
        TraceProcessor? fresh = null;
        IsLoading = true;
        try
        {
            fresh = await TraceProcessor.StartAsync(binary, loadCts.Token).ConfigureAwait(false);
            await StreamTraceAsync(fresh, fi, ctx, loadCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ctx.Cancellation.IsCancellationRequested && !disposed)
        {
            fresh?.Dispose();
            throw new ToolException(
                $"Loading {FileName} exceeded {settings.LoadTimeout.TotalSeconds:0}s (PERFETTO_LOAD_TIMEOUT_S).");
        }
        catch (OperationCanceledException) when (disposed)
        {
            fresh?.Dispose();
            ThrowIfDisposed();
            throw;
        }
        catch (TraceParseException ex)
        {
            fresh?.Dispose();
            throw new ToolException($"trace_processor could not parse {FileName}: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            string tail = fresh?.StderrTail ?? string.Empty;
            bool crashed = fresh?.HasExited ?? false;
            fresh?.Dispose();
            throw new ToolException(crashed
                ? $"trace_processor crashed while loading {FileName} (often out of memory):\n{tail}"
                : $"trace_processor RPC failed while loading {FileName}: {ex.Message}");
        }
        catch
        {
            fresh?.Dispose();
            throw;
        }
        finally
        {
            IsLoading = false;
        }

        tp = fresh;
        if (disposed)
        {
            // Dispose ran without the gate while this load was finishing.
            KillLocked("disposed during load");
            ThrowIfDisposed();
        }
        loadedSize = fi.Length;
        loadedMtimeUtc = fi.LastWriteTimeUtc;
        LastLoadSeconds = sw.Elapsed.TotalSeconds;
        LoadCount++;
        PrewarmedModules = 0;
        Log.Info($"loaded {Id} {FileName} ({fi.Length / 1048576.0:0.0} MB) in {LastLoadSeconds:0.0}s on port {fresh.Port}");
        if (settings.Prewarm)
        {
            int generation = LoadCount;
            _ = Task.Run(() => PrewarmAsync(generation));
        }
    }

    // Heavy stdlib modules (materialized on first INCLUDE), each with a cheap probe for whether the trace has the data.
    private static readonly (string Module, string Probe)[] PrewarmModules =
    [
        ("android.critical_blocking_calls", "SELECT EXISTS(SELECT 1 FROM slice WHERE name GLOB 'binder transaction*' OR name = 'inflate')"),
        ("android.binder", "SELECT EXISTS(SELECT 1 FROM slice WHERE name GLOB 'binder transaction*')"),
        ("android.monitor_contention", "SELECT EXISTS(SELECT 1 FROM slice WHERE name GLOB 'monitor contention*')"),
    ];

    public int PrewarmedModules { get; private set; }

    // Prewarms one module at a time, only while the trace is idle (1.5 s without queries); never blocks user queries.
    private async Task PrewarmAsync(int generation)
    {
        try
        {
            foreach ((string module, string probe) in PrewarmModules)
            {
                if (!await WaitIdleAsync(generation).ConfigureAwait(false))
                {
                    return;
                }
                try
                {
                    TraceProcessor live = tp!;
                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    cts.CancelAfter(TimeSpan.FromSeconds(60));
                    QueryTable has = await live.QueryAsync(probe, 1, settings.MaxResultBytes, cts.Token).ConfigureAwait(false);
                    if (has.Error is null && has.Rows.Count > 0 && Convert.ToInt64(has.Rows[0][0] ?? 0L) == 1)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        QueryTable r = await live.QueryAsync($"INCLUDE PERFETTO MODULE {module}; SELECT 1", 1, settings.MaxResultBytes,
                            cts.Token).ConfigureAwait(false);
                        PerfLog.RecordQuery(Id, $"prewarm: INCLUDE PERFETTO MODULE {module}", sw.Elapsed.TotalMilliseconds, r.ElapsedMs);
                        if (r.Error is null)
                        {
                            PrewarmedModules++;
                        }
                    }
                }
                catch (OperationCanceledException) when (!disposed)
                {
                    KillLocked("prewarm query exceeded 60s");
                    return;
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or HttpRequestException)
        {
            // Best effort: unload / reload / crash ends prewarming quietly; the next real query reports problems.
        }
    }

    // True once the gate is held for prewarming (caller releases it); false when prewarming should stop.
    private async Task<bool> WaitIdleAsync(int generation)
    {
        DateTime giveUp = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < giveUp)
        {
            await Task.Delay(500, lifetime.Token).ConfigureAwait(false);
            if (disposed || LoadCount != generation || tp is not { HasExited: false })
            {
                return false;
            }
            if (DateTime.UtcNow - LastUsedUtc < TimeSpan.FromSeconds(1.5) || Volatile.Read(ref gateWaiters) > 0 || !gate.Wait(0))
            {
                continue;
            }
            if (disposed || LoadCount != generation || tp is not { HasExited: false })
            {
                gate.Release();
                return false;
            }
            return true;
        }
        return false;
    }

    private static async Task StreamTraceAsync(TraceProcessor target, FileInfo fi, RequestContext ctx, CancellationToken ct)
    {
        const double ParseShare = 90;
        long total = Math.Max(1, fi.Length);
        long sent = 0;
        byte[] buffer = new byte[ChunkSize];
        await using FileStream fs = new(fi.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        while (true)
        {
            int n = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            await target.ParseAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            sent += n;
            await ctx.ReportProgressAsync(ParseShare * sent / total, 100,
                $"parsed {sent / 1048576.0:0} / {total / 1048576.0:0} MB").ConfigureAwait(false);
        }
        await ctx.ReportProgressAsync(ParseShare, 100, "finalizing (sorting events)").ConfigureAwait(false);
        await target.NotifyEofAsync(ct).ConfigureAwait(false);
        await ctx.ReportProgressAsync(100, 100, "trace loaded").ConfigureAwait(false);
    }

    private void KillLocked(string reason)
    {
        if (tp is null)
        {
            return;
        }
        Log.Info($"stopping trace_processor for {Id} (pid {tp.Pid}): {reason}");
        tp.Dispose();
        tp = null;
    }
}

internal sealed class ResultTooLargeException : Exception
{
    public ResultTooLargeException(long limitBytes) : base($"result exceeded {limitBytes} bytes")
    {
        LimitBytes = limitBytes;
    }

    public long LimitBytes { get; }
}
