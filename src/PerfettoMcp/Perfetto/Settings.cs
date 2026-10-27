using System;

namespace PerfettoMcp.Perfetto;

internal sealed class Settings
{
    public const int HardMaxRows = 5000;

    public TimeSpan QueryTimeout { get; set; } = FromEnv("PERFETTO_QUERY_TIMEOUT_S", 120);

    public TimeSpan LoadTimeout { get; set; } = FromEnv("PERFETTO_LOAD_TIMEOUT_S", 900);

    public int MaxLiveProcessors { get; set; } = IntFromEnv("PERFETTO_MAX_INSTANCES", 4);

    public int DefaultMaxRows { get; set; } = 200;

    // Background INCLUDE of expensive stdlib modules after a (re)load, only while the trace is idle.
    public bool Prewarm { get; set; } = Environment.GetEnvironmentVariable("PERFETTO_MCP_PREWARM") != "0";

    // Loaded-trace list persisted across worker restarts (set by the supervisor via --state).
    public string? StateFile { get; set; }

    // Raw /query response cap; protects the server from OOM on `SELECT *` over huge tables.
    public long MaxResultBytes { get; set; } = IntFromEnv("PERFETTO_MAX_RESULT_MB", 256) * 1024L * 1024L;

    private static TimeSpan FromEnv(string name, int fallbackSeconds) =>
        TimeSpan.FromSeconds(IntFromEnv(name, fallbackSeconds));

    private static int IntFromEnv(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v > 0 ? v : fallback;
}
