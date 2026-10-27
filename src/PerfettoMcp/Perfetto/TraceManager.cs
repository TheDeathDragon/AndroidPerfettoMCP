using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Logging;
using PerfettoMcp.Mcp;

namespace PerfettoMcp.Perfetto;

internal sealed class TraceManager : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, TraceSession> sessions = new(StringComparer.Ordinal);
    private readonly Lazy<string> binary = new(TpBinary.Resolve);
    private int nextId = 1;
    private string? activeId;
    private int disposed;

    public TraceManager(Settings settings)
    {
        Settings = settings;
        RestoreState();
    }

    public Settings Settings { get; }

    public string Binary => binary.Value;

    public IReadOnlyList<TraceSession> All
    {
        get
        {
            lock (sync)
            {
                return sessions.Values.ToList();
            }
        }
    }

    public string? ActiveId
    {
        get
        {
            lock (sync)
            {
                return activeId;
            }
        }
    }

    public async Task<TraceSession> LoadAsync(string path, RequestContext ctx)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        if (!File.Exists(full))
        {
            throw new ToolException(Directory.Exists(full)
                ? $"{full} is a directory; pass a trace file."
                : $"Trace file not found: {full}");
        }

        TraceSession session;
        bool isNew = false;
        lock (sync)
        {
            session = sessions.Values.FirstOrDefault(s => PathsEqual(s.Path, full)) ?? Register(full, out isNew);
        }
        EvictForRoom(session);
        try
        {
            await session.EnsureLoadedAsync(ctx).ConfigureAwait(false);
        }
        catch when (isNew)
        {
            // Don't leave a never-loaded trace behind in list_traces.
            Unload(session.Id);
            throw;
        }
        lock (sync)
        {
            activeId = session.Id;
        }
        SaveState();
        return session;
    }

    public TraceSession Resolve(string? traceId)
    {
        lock (sync)
        {
            string? id = string.IsNullOrWhiteSpace(traceId) ? activeId : traceId.Trim();
            if (id is null)
            {
                throw new ToolException("No trace loaded. Call `load_trace` with a trace file path first.");
            }
            if (!sessions.TryGetValue(id, out TraceSession? s))
            {
                string known = sessions.Count == 0 ? "none" : string.Join(", ", sessions.Keys);
                throw new ToolException($"Unknown trace_id '{id}'. Loaded traces: {known}.");
            }
            return s;
        }
    }

    public async Task<QueryTable> QueryAsync(TraceSession session, string sql, RequestContext ctx,
        TimeSpan? timeout = null, int? maxRows = null)
    {
        if (!session.IsLive)
        {
            EvictForRoom(session);
        }
        long started = Environment.TickCount64;
        QueryTable t = await session.QueryAsync(sql, timeout ?? Settings.QueryTimeout,
            maxRows ?? Settings.HardMaxRows, ctx).ConfigureAwait(false);
        PerfLog.RecordQuery(session.Id, sql, Environment.TickCount64 - started, t.ElapsedMs);
        return t;
    }

    public bool Unload(string id)
    {
        TraceSession? s;
        lock (sync)
        {
            if (!sessions.Remove(id, out s))
            {
                return false;
            }
            if (activeId == id)
            {
                activeId = sessions.Values.OrderByDescending(x => x.LastUsedUtc).FirstOrDefault()?.Id;
            }
        }
        s.Dispose();
        SaveState();
        return true;
    }

    // Called from both `using` and ProcessExit; must be idempotent.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        foreach (TraceSession s in All)
        {
            s.Dispose();
        }
    }

    private TraceSession Register(string full, out bool isNew)
    {
        string id = $"t{nextId++}";
        TraceSession s = new(id, full, binary.Value, Settings);
        sessions[id] = s;
        isNew = true;
        return s;
    }

    // At most MaxLiveProcessors children stay alive; evicted sessions reload lazily.
    private void EvictForRoom(TraceSession incoming)
    {
        List<TraceSession> others = All.Where(s => !ReferenceEquals(s, incoming)).ToList();
        int loading = others.Count(s => s.IsLoading);
        List<TraceSession> live = others.Where(s => s.IsLive).OrderBy(s => s.LastUsedUtc).ToList();
        int excess = live.Count + loading + 1 - Settings.MaxLiveProcessors;
        foreach (TraceSession victim in live)
        {
            if (excess <= 0)
            {
                break;
            }
            if (victim.TryUnloadIfIdle($"evicted to stay within {Settings.MaxLiveProcessors} live trace processors"))
            {
                excess--;
            }
        }
    }

    // After a hot update the traces re-register under their ids and load lazily on first use.
    private void RestoreState()
    {
        if (Settings.StateFile is not { } file || !File.Exists(file))
        {
            return;
        }
        try
        {
            JsonObject state = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            lock (sync)
            {
                foreach (JsonNode? t in state["traces"]?.AsArray() ?? new JsonArray())
                {
                    string? id = t?["id"]?.GetValue<string>();
                    string? path = t?["path"]?.GetValue<string>();
                    if (id is null || path is null || !File.Exists(path))
                    {
                        continue;
                    }
                    sessions[id] = new TraceSession(id, path, binary.Value, Settings);
                    if (int.TryParse(id.TrimStart('t'), out int n) && n >= nextId)
                    {
                        nextId = n + 1;
                    }
                }
                string? active = state["active"]?.GetValue<string>();
                activeId = active is not null && sessions.ContainsKey(active) ? active : null;
            }
            Log.Info($"restored {sessions.Count} trace handle(s) from {file}");
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot restore state from {file}: {ex.Message}");
        }
    }

    private void SaveState()
    {
        if (Settings.StateFile is not { } file)
        {
            return;
        }
        try
        {
            JsonObject state;
            lock (sync)
            {
                JsonArray traces = new();
                foreach (TraceSession s in sessions.Values.OrderBy(s => s.Id.Length).ThenBy(s => s.Id, StringComparer.Ordinal))
                {
                    traces.Add(new JsonObject { ["id"] = s.Id, ["path"] = s.Path });
                }
                state = new JsonObject { ["active"] = activeId, ["traces"] = traces };
            }
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, state.ToJsonString());
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot save state to {file}: {ex.Message}");
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
