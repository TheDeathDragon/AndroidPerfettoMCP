using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Logging;

namespace PerfettoMcp.Perfetto;

// One trace_processor_shell in HTTP-RPC mode; the trace is streamed via /parse (progress, timeouts, non-ASCII paths).
internal sealed class TraceProcessor : IDisposable
{
    private const int StderrTailLines = 40;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // Users here export http_proxy globally; loopback RPC must never be proxied.
        UseProxy = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly Process process;
    private readonly Queue<string> stderrTail = new();
    private readonly string baseUrl;
    private int disposed;

    private TraceProcessor(Process process, int port)
    {
        this.process = process;
        Port = port;
        baseUrl = $"http://127.0.0.1:{port}";
    }

    public int Port { get; }

    public int Pid => process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public string StderrTail
    {
        get
        {
            lock (stderrTail)
            {
                return string.Join('\n', stderrTail);
            }
        }
    }

    public static async Task<TraceProcessor> StartAsync(string binary, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int port = FreeLoopbackPort();
            TraceProcessor tp = Spawn(binary, port);
            try
            {
                TpStatus status = await tp.WaitReadyAsync(ct).ConfigureAwait(false);
                Log.Info($"trace_processor pid={tp.Pid} port={port} ready ({status.Version}, api {status.ApiVersion})");
                return tp;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
                Log.Warn($"trace_processor start attempt {attempt + 1} failed on port {port}: {ex.Message}");
                tp.Dispose();
            }
            catch
            {
                tp.Dispose();
                throw;
            }
        }
        throw new InvalidOperationException($"trace_processor_shell failed to start: {last?.Message}", last);
    }

    public async Task<TpStatus> StatusAsync(CancellationToken ct)
    {
        using HttpResponseMessage resp = await Http.GetAsync($"{baseUrl}/status", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        byte[] body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return TpMessages.DecodeStatus(body);
    }

    public async Task ParseAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        using ReadOnlyMemoryContent content = new(chunk);
        using HttpResponseMessage resp = await Http.PostAsync($"{baseUrl}/parse", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        byte[] body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        string? error = TpMessages.DecodeAppendResultError(body);
        if (!string.IsNullOrEmpty(error))
        {
            throw new TraceParseException(error);
        }
    }

    public async Task NotifyEofAsync(CancellationToken ct)
    {
        using ByteArrayContent empty = new(Array.Empty<byte>());
        using HttpResponseMessage resp = await Http.PostAsync($"{baseUrl}/notify_eof", empty, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<QueryTable> QueryAsync(string sql, int maxRows, long maxBytes, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, $"{baseUrl}/query")
        {
            Content = new ByteArrayContent(TpMessages.EncodeQueryArgs(sql))
        };
        using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using Stream body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[1 << 16];
        int n;
        while ((n = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > maxBytes)
            {
                throw new ResultTooLargeException(maxBytes);
            }
            buffer.Write(chunk, 0, n);
        }
        return TpMessages.DecodeQueryResult(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), maxRows);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        int pid = -1;
        try
        {
            pid = process.Id;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"kill trace_processor: {ex.Message}");
        }
        finally
        {
            OrphanRegistry.Unregister(pid);
            process.Dispose();
        }
    }

    private static TraceProcessor Spawn(string binary, int port)
    {
        ProcessStartInfo psi = new(binary)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-D");
        psi.ArgumentList.Add("--http-port");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--http-ip-address");
        psi.ArgumentList.Add("127.0.0.1");

        Process p = new() { StartInfo = psi, EnableRaisingEvents = true };
        TraceProcessor tp = new(p, port);
        // Both pipes must be drained continuously or the child blocks on write.
        p.OutputDataReceived += (_, e) => tp.AppendOutput(e.Data);
        p.ErrorDataReceived += (_, e) => tp.AppendOutput(e.Data);
        if (!p.Start())
        {
            throw new InvalidOperationException($"failed to start {binary}");
        }
        ChildProcessGuard.Attach(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        OrphanRegistry.Register(p.Id);
        return tp;
    }

    private async Task<TpStatus> WaitReadyAsync(CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + ReadyTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (HasExited)
            {
                throw new InvalidOperationException($"exited with code {SafeExitCode()} during startup. stderr:\n{StderrTail}");
            }
            try
            {
                using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probe.CancelAfter(TimeSpan.FromSeconds(2));
                TpStatus status = await StatusAsync(probe.Token).ConfigureAwait(false);
                // Lost the port race: another process answered /status; don't attach to it.
                await Task.Delay(150, ct).ConfigureAwait(false);
                if (HasExited)
                {
                    throw new InvalidOperationException($"exited with code {SafeExitCode()} right after startup. stderr:\n{StderrTail}");
                }
                return status;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"not ready after {ReadyTimeout.TotalSeconds:0}s. stderr:\n{StderrTail}");
                }
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private void AppendOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        // Progress lines use '\r' to redraw in place; keep only the last frame.
        int cr = line.LastIndexOf('\r');
        string clean = cr >= 0 ? line[(cr + 1)..] : line;
        Log.Debug($"[tp:{Port}] {clean}");
        lock (stderrTail)
        {
            stderrTail.Enqueue(clean);
            while (stderrTail.Count > StderrTailLines)
            {
                stderrTail.Dequeue();
            }
        }
    }

    private string SafeExitCode()
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch
        {
            return "?";
        }
    }

    private static int FreeLoopbackPort()
    {
        using TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}

internal sealed class TraceParseException : Exception
{
    public TraceParseException(string message) : base(message) { }
}

// Child pids are kept on disk so a later instance can reap orphans (Linux has no kill-on-parent-death).
internal static class OrphanRegistry
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "perfetto-mcp");

    public static void Register(int childPid)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, $"{childPid}.pid"), Environment.ProcessId.ToString());
        }
        catch (Exception ex)
        {
            Log.Debug($"orphan registry write failed: {ex.Message}");
        }
    }

    public static void Unregister(int childPid)
    {
        if (childPid <= 0)
        {
            return;
        }
        try
        {
            File.Delete(Path.Combine(Dir, $"{childPid}.pid"));
        }
        catch
        {
        }
    }

    public static void Sweep()
    {
        if (!Directory.Exists(Dir))
        {
            return;
        }
        foreach (string file in Directory.GetFiles(Dir, "*.pid"))
        {
            try
            {
                int child = int.Parse(Path.GetFileNameWithoutExtension(file));
                int owner = int.Parse(File.ReadAllText(file).Trim());
                if (owner != Environment.ProcessId && IsAlive(owner))
                {
                    continue;
                }
                using Process? p = TryGet(child);
                if (p is not null && p.ProcessName.StartsWith("trace_processor", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"reaping orphaned trace_processor pid={child} (owner {owner} is gone)");
                    p.Kill();
                }
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Debug($"orphan sweep {file}: {ex.Message}");
            }
        }
    }

    private static bool IsAlive(int pid)
    {
        using Process? p = TryGet(pid);
        return p is not null && !p.HasExited;
    }

    private static Process? TryGet(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch
        {
            return null;
        }
    }
}
