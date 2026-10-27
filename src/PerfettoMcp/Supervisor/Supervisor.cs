using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Logging;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Supervisor;

// Stable front process for hot updates: the client talks to it, the server runs as a child from versions/<id>/.
// When current.txt changes or the worker crashes, a new worker gets the original handshake, the old one drains and
// the client receives tools/list_changed.
[SuppressMessage("Design", "CA1001", Justification = "One instance for the whole process lifetime.")]
internal sealed class Supervisor
{
    private const string InternalIdPrefix = "__supervisor_";
    private const int MaxCrashRestartsPerMinute = 5;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan FailedVersionBackoff = TimeSpan.FromMinutes(1);

    private readonly string root;
    private readonly string stateFile;
    private readonly List<string> workerArgs;
    private readonly ConcurrentDictionary<string, Worker> routes = new();
    private readonly SemaphoreSlim swapGate = new(1, 1);
    private readonly SemaphoreSlim outGate = new(1, 1);
    private readonly List<DateTime> crashes = new();
    private readonly CancellationTokenSource stopping = new();
    private TextWriter? clientOut;
    private volatile Worker? active;
    private string? currentVersion;
    // Messages are built as text: a failed JsonValue<T> type initializer stays failed for the whole process.
    private string? initializeParams;
    private volatile bool clientInitialized;
    private int internalSeq;
    private string? failedVersion;
    private DateTime failedAt;

    public Supervisor(string root, IEnumerable<string> workerArgs)
    {
        this.root = root;
        this.workerArgs = workerArgs.ToList();
        string stateDir = Path.Combine(root, "state");
        Directory.CreateDirectory(stateDir);
        // Per supervisor instance: concurrent client sessions must not share trace lists.
        stateFile = Path.Combine(stateDir, $"{Environment.ProcessId}.json");
        SweepStaleState(stateDir);
        Log.MirrorTo(Path.Combine(stateDir, "supervisor.log"));
        Log.Info($"supervisor: pid {Environment.ProcessId} started");
    }

    public static string? ReadCurrentVersion(string root)
    {
        string file = Path.Combine(root, "current.txt");
        try
        {
            string v = File.Exists(file) ? File.ReadAllText(file).Trim() : string.Empty;
            return v.Length > 0 && File.Exists(WorkerExe(root, v)) ? v : null;
        }
        catch (IOException)
        {
            return null; // being rewritten; next poll retries
        }
    }

    public async Task<int> RunAsync()
    {
        string? version = ReadCurrentVersion(root);
        if (version is null)
        {
            Log.Error($"supervisor: no valid version in {Path.Combine(root, "current.txt")} (install with scripts/install.*)");
            return 1;
        }
        using TextReader reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        clientOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        if (!await SwapAsync(version, "startup").ConfigureAwait(false))
        {
            return 1;
        }
        _ = Task.Run(PollLoopAsync);

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length > 0)
            {
                await OnClientLineAsync(line).ConfigureAwait(false);
            }
        }

        Log.Info("supervisor: client closed stdin; stopping workers");
        stopping.Cancel();
        foreach (Worker w in AllWorkers())
        {
            w.CloseInput();
        }
        await Task.WhenAll(AllWorkers().Select(w => w.WaitForExitAsync(TimeSpan.FromSeconds(10)))).ConfigureAwait(false);
        TryDelete(stateFile);
        return 0;
    }

    private async Task OnClientLineAsync(string line)
    {
        JsonObject? msg = TryParse(line);
        string? method = msg?["method"]?.GetValue<string>();
        JsonNode? id = msg?["id"];
        if (method == "initialize")
        {
            initializeParams = msg!["params"]?.ToJsonString();
        }
        else if (method is "notifications/initialized" or "initialized")
        {
            clientInitialized = true;
        }

        Worker? target = active;
        if (method == "notifications/cancelled" && msg!["params"]?["requestId"] is { } rid && routes.TryGetValue(Key(rid), out Worker? owner))
        {
            target = owner;
        }
        if (target is null)
        {
            if (id is not null)
            {
                await WriteClientAsync(ErrorResponse(id, "perfetto-mcp worker is unavailable (restarting). Retry shortly.")).ConfigureAwait(false);
            }
            return;
        }
        if (id is not null && method is not null)
        {
            routes[Key(id)] = target;
        }
        try
        {
            await target.SendAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (id is not null && routes.TryRemove(Key(id), out _))
            {
                await WriteClientAsync(ErrorResponse(id, "perfetto-mcp worker exited; it is being restarted. Retry the call.")).ConfigureAwait(false);
            }
        }
    }

    private async Task OnWorkerLineAsync(Worker w, string line)
    {
        JsonObject? msg = TryParse(line);
        if (msg is null)
        {
            // Never forward non-JSON-RPC output: one stray line breaks the client's stdio stream.
            Log.Warn($"supervisor: worker {w.Version} wrote non-JSON to stdout: {line[..Math.Min(line.Length, 200)]}");
            return;
        }
        JsonNode? id = msg["id"];
        bool isResponse = id is not null && msg["method"] is null;
        if (isResponse && IsInternal(id!))
        {
            if (w.Pending.TryRemove(Key(id!), out TaskCompletionSource<JsonObject>? tcs))
            {
                tcs.TrySetResult(msg);
            }
            return;
        }
        await WriteClientAsync(line).ConfigureAwait(false);
        if (isResponse)
        {
            routes.TryRemove(Key(id!), out _);
            if (w.Retiring && !routes.Values.Contains(w))
            {
                w.CloseInput();
            }
        }
    }

    private async Task OnWorkerExitedAsync(Worker w)
    {
        if (w.Retiring || stopping.IsCancellationRequested)
        {
            return;
        }
        if (w != active)
        {
            // Died while being started: SwapAsync reports it. Restarting here would replace the healthy active worker.
            Log.Warn($"supervisor: new worker {w.Version} (pid {w.Pid}) exited during startup");
            return;
        }
        Log.Warn($"supervisor: worker {w.Version} (pid {w.Pid}) exited unexpectedly");
        foreach (KeyValuePair<string, Worker> kv in routes.Where(kv => kv.Value == w).ToList())
        {
            if (routes.TryRemove(kv.Key, out _))
            {
                JsonNode id = JsonNode.Parse(kv.Key)!;
                await WriteClientAsync(ErrorResponse(id, "perfetto-mcp worker crashed and was restarted. Retry the call (loaded traces reload automatically).")).ConfigureAwait(false);
            }
        }
        lock (crashes)
        {
            crashes.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-1));
            crashes.Add(DateTime.UtcNow);
            if (crashes.Count > MaxCrashRestartsPerMinute)
            {
                Log.Error("supervisor: worker keeps crashing; not restarting (fix the install, then /mcp reconnect)");
                if (active == w)
                {
                    active = null;
                }
                return;
            }
        }
        await SwapAsync(currentVersion ?? w.Version, "crash restart").ConfigureAwait(false);
    }

    private async Task<bool> SwapAsync(string version, string reason)
    {
        await swapGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Worker? fresh = null;
            try
            {
                fresh = StartWorker(version);
                await InitializeAsync(fresh).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A worker left running here leaked one process per poll (184 seen) while the old one kept serving.
                if (fresh is not null)
                {
                    fresh.Retiring = true;
                    fresh.Kill();
                }
                failedVersion = version;
                failedAt = DateTime.UtcNow;
                Log.Error($"supervisor: starting worker {version} failed ({reason}): {ex.GetType().Name}: {ex.Message}"
                          + (ex.InnerException is { } inner ? $" <- {inner.GetType().Name}: {inner.Message}" : ""));
                return false;
            }

            Worker? old = active;
            active = fresh;
            currentVersion = version;
            if (old is not null && old != fresh)
            {
                Retire(old);
            }
            Log.Info($"supervisor: worker {version} (pid {fresh.Pid}) active ({reason})");
            if (reason != "startup" && clientInitialized)
            {
                await WriteClientAsync("""{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""").ConfigureAwait(false);
            }
            return true;
        }
        finally
        {
            swapGate.Release();
        }
    }

    private async Task InitializeAsync(Worker w)
    {
        if (initializeParams is null)
        {
            return; // startup: the client's own initialize will reach this worker
        }
        string id = $"{InternalIdPrefix}{Interlocked.Increment(ref internalSeq)}";
        TaskCompletionSource<JsonObject> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Pending[$"\"{id}\""] = tcs;
        await w.SendAsync($$$"""{"jsonrpc":"2.0","id":"{{{id}}}","method":"initialize","params":{{{initializeParams}}}}""").ConfigureAwait(false);
        Task done = await Task.WhenAny(tcs.Task, Task.Delay(InitTimeout)).ConfigureAwait(false);
        if (done != tcs.Task)
        {
            throw new TimeoutException($"worker did not answer initialize within {InitTimeout.TotalSeconds:0} s");
        }
        if (!tcs.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("worker exited before answering initialize");
        }
        if (tcs.Task.Result["result"] is null)
        {
            throw new InvalidOperationException($"worker rejected initialize: {tcs.Task.Result["error"]?.ToJsonString()}");
        }
        if (clientInitialized)
        {
            await w.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""").ConfigureAwait(false);
        }
    }

    private Worker StartWorker(string version)
    {
        string exe = WorkerExe(root, version);
        ProcessStartInfo psi = new(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        foreach (string a in workerArgs)
        {
            psi.ArgumentList.Add(a);
        }
        psi.ArgumentList.Add("--state");
        psi.ArgumentList.Add(stateFile);

        Process p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        ChildProcessGuard.Attach(p);
        Worker w = new(p, version);
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Log.Raw(e.Data);
            }
        };
        p.BeginErrorReadLine();
        _ = Task.Run(async () =>
        {
            try
            {
                while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    await OnWorkerLineAsync(w, line).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"supervisor: worker {version} stdout: {ex.Message}");
            }
            foreach (TaskCompletionSource<JsonObject> t in w.Pending.Values)
            {
                t.TrySetCanceled();
            }
            await OnWorkerExitedAsync(w).ConfigureAwait(false);
        });
        return w;
    }

    private void Retire(Worker old)
    {
        old.Retiring = true;
        if (!routes.Values.Contains(old))
        {
            old.CloseInput();
            return;
        }
        Log.Info($"supervisor: worker {old.Version} drains {routes.Values.Count(v => v == old)} in-flight request(s)");
        _ = Task.Run(async () =>
        {
            await Task.Delay(DrainTimeout).ConfigureAwait(false);
            old.Kill();
        });
    }

    private async Task PollLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // A crash restart whose new worker died during startup leaves `active` dead: retry after the backoff.
            if (active is { Retiring: false, HasExited: true } && currentVersion is { } cur
                && !(cur == failedVersion && DateTime.UtcNow - failedAt < FailedVersionBackoff))
            {
                try
                {
                    await SwapAsync(cur, "restart after a failed crash restart").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error($"supervisor: restarting {cur} failed: {ex}");
                }
                continue;
            }
            string? v = ReadCurrentVersion(root);
            if (v is null || v == currentVersion || (v == failedVersion && DateTime.UtcNow - failedAt < FailedVersionBackoff))
            {
                continue;
            }
            try
            {
                await SwapAsync(v, $"update {currentVersion} -> {v}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An escaped exception would end polling silently: no further update would ever be picked up.
                Log.Error($"supervisor: update to {v} failed: {ex}");
            }
        }
    }

    private async Task WriteClientAsync(string line)
    {
        await outGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await clientOut!.WriteLineAsync(line).ConfigureAwait(false);
        }
        finally
        {
            outGate.Release();
        }
    }

    private List<Worker> AllWorkers() =>
        routes.Values.Append(active).OfType<Worker>().Distinct().ToList();

    private static string WorkerExe(string root, string version) =>
        Path.Combine(root, "versions", version, OperatingSystem.IsWindows() ? "perfetto-mcp.exe" : "perfetto-mcp");

    internal static string ErrorResponse(JsonNode id, string message) =>
        $$$"""{"jsonrpc":"2.0","id":{{{Key(id)}}},"error":{"code":-32603,"message":"{{{JsonEncodedText.Encode(message)}}}"}}""";

    internal static bool IsInternal(JsonNode id) => Key(id).StartsWith($"\"{InternalIdPrefix}", StringComparison.Ordinal);

    private static string Key(JsonNode id) => id.ToJsonString();

    private static JsonObject? TryParse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static void SweepStaleState(string dir)
    {
        foreach (string f in Directory.GetFiles(dir, "*.json"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(f), out int pid) && pid != Environment.ProcessId && !IsAlive(pid))
            {
                TryDelete(f);
            }
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    [SuppressMessage("Design", "CA1001", Justification = "Lives until its process exits; the semaphore holds no OS handle.")]
    private sealed class Worker
    {
        private readonly Process process;
        private readonly SemaphoreSlim inGate = new(1, 1);

        public Worker(Process process, string version)
        {
            this.process = process;
            Version = version;
            Pid = process.Id;
        }

        public string Version { get; }

        public int Pid { get; }

        public bool Retiring { get; set; }

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

        public ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> Pending { get; } = new();

        public async Task SendAsync(string line)
        {
            await inGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                inGate.Release();
            }
        }

        // The worker treats stdin EOF as "client gone": it cancels its work and exits.
        public void CloseInput()
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // already closed
            }
            _ = Task.Run(async () =>
            {
                await WaitForExitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                Kill();
            });
        }

        public async Task WaitForExitAsync(TimeSpan timeout)
        {
            using CancellationTokenSource cts = new(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // already gone
            }
        }
    }
}
