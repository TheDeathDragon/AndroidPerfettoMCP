using System.Text.Json.Nodes;
using PerfettoMcp.Mcp;

namespace PerfettoMcp.Tools;

internal static class ProcessSelector
{
    public static SchemaBuilder AddParams(SchemaBuilder b) => b
        .Int("upid", "Trace-internal process id (from list_processes). Most precise.")
        .Int("pid", "OS process id.")
        .Str("process_name", "Exact process name (e.g. com.android.settings); use * for GLOB (e.g. com.android.*).");

    // trace_processor may split a thread with an unknown tgid into a nameless "shadow" process (pid == tid);
    // these expressions map shadows back to their owner.
    public const string OwnerUpidExpr =
        "IIF(p.name IS NULL AND p.cmdline IS NULL, COALESCE((SELECT t2.upid FROM thread t2 JOIN process p2 ON p2.upid = t2.upid " +
        "WHERE t2.tid = p.pid AND p2.upid != p.upid AND p2.name IS NOT NULL LIMIT 1), p.upid), p.upid)";

    public const string NameExpr =
        "COALESCE(p.name, (SELECT p2.name FROM thread t2 JOIN process p2 ON p2.upid = t2.upid " +
        "WHERE t2.tid = p.pid AND p2.upid != p.upid AND p2.name IS NOT NULL LIMIT 1))";

    // Per thread: unknown-tgid threads may also sit in a placeholder process (pid 0xFFFF0000); resolve the owner via
    // the named process with the same tid, then the pid == tid rule. `t`: thread-like alias, `p`: its process.
    public static string OwnerUpidOf(string t) =>
        $"IIF(p.name IS NULL AND p.cmdline IS NULL, COALESCE((SELECT t2.upid FROM thread t2 JOIN process p2 ON p2.upid = t2.upid " +
        $"WHERE t2.tid = {t}.tid AND t2.upid != p.upid AND (p2.name IS NOT NULL OR p2.cmdline IS NOT NULL) LIMIT 1), " +
        "(SELECT t2.upid FROM thread t2 JOIN process p2 ON p2.upid = t2.upid " +
        "WHERE t2.tid = p.pid AND p2.upid != p.upid AND p2.name IS NOT NULL LIMIT 1), p.upid), p.upid)";

    public static string NameOf(string t) =>
        $"COALESCE(p.name, (SELECT p2.name FROM thread t2 JOIN process p2 ON p2.upid = t2.upid " +
        $"WHERE t2.tid = {t}.tid AND t2.upid != p.upid AND p2.name IS NOT NULL LIMIT 1), " +
        "(SELECT p2.name FROM thread t2 JOIN process p2 ON p2.upid = t2.upid " +
        "WHERE t2.tid = p.pid AND p2.upid != p.upid AND p2.name IS NOT NULL LIMIT 1))";

    // Predicate on alias `p` including shadow processes of the matched ones, or null when no filter was given.
    public static string? Predicate(JsonObject args)
    {
        string? real = RealPredicate(args);
        if (real is null)
        {
            return null;
        }
        string matched = $"SELECT p.upid FROM process p WHERE {real}";
        return $"p.upid IN ({matched} UNION SELECT ps.upid FROM process ps WHERE ps.name IS NULL AND ps.cmdline IS NULL " +
            $"AND ps.pid IN (SELECT st.tid FROM thread st WHERE st.upid IN ({matched})))";
    }

    // Predicate on alias `p` matching only the named processes themselves (no shadows).
    public static string? RealPredicate(JsonObject args)
    {
        long? upid = Args.OptionalLong(args, "upid");
        if (upid is not null)
        {
            return $"p.upid = {upid}";
        }
        long? pid = Args.OptionalLong(args, "pid");
        if (pid is not null)
        {
            return $"p.pid = {pid}";
        }
        string? name = Args.OptionalString(args, "process_name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        string lit = SqlText.Literal(name.Trim());
        return name.Contains('*')
            ? $"(p.name GLOB {lit} OR p.cmdline GLOB {lit})"
            : $"(p.name = {lit} OR p.cmdline = {lit})";
    }

    public static string Describe(JsonObject args) =>
        Args.OptionalLong(args, "upid") is { } u ? $"upid {u}"
        : Args.OptionalLong(args, "pid") is { } p ? $"pid {p}"
        : $"process '{Args.OptionalString(args, "process_name")}'";
}

internal static class ThreadSelector
{
    // This utid plus the utids the same thread was split into (same tid; same or shadow process), so a thread is
    // not judged by its shadow half alone.
    public static string SameThreadAs(long utid) => $"""
        (t.utid = {utid} OR t.utid IN (
          SELECT t2.utid FROM thread t2 JOIN thread t1 ON t1.utid = {utid}
          WHERE t2.tid = t1.tid AND t2.utid != t1.utid
            AND (t2.upid = t1.upid OR EXISTS (SELECT 1 FROM process ps WHERE ps.upid IN (t1.upid, t2.upid)
                 AND ps.name IS NULL AND ps.cmdline IS NULL AND ps.pid = t1.tid))))
        """;

    public static SchemaBuilder AddParams(SchemaBuilder b) => ProcessSelector.AddParams(b
        .Int("utid", "Trace-internal thread id. Most precise.")
        .Int("tid", "OS thread id."))
        .Str("thread_name", "Thread name within the selected process (e.g. RenderThread); * for GLOB. Default: the main thread.")
        .Bool("all_threads", "With a process filter: include every thread of the process instead of only the main thread.");

    // Predicate on aliases `t` (thread) and `p` (process).
    public static string Predicate(JsonObject args)
    {
        long? utid = Args.OptionalLong(args, "utid");
        if (utid is not null)
        {
            return SameThreadAs(utid.Value);
        }
        long? tid = Args.OptionalLong(args, "tid");
        if (tid is not null)
        {
            return $"t.tid = {tid}";
        }
        string? proc = ProcessSelector.Predicate(args);
        if (proc is null)
        {
            throw new McpInvalidParamsException("select a thread with utid, tid, or a process filter (upid / pid / process_name)");
        }
        string? threadName = Args.OptionalString(args, "thread_name");
        if (!string.IsNullOrWhiteSpace(threadName))
        {
            string lit = SqlText.Literal(threadName.Trim());
            return $"{proc} AND " + (threadName.Contains('*') ? $"t.name GLOB {lit}" : $"t.name = {lit}");
        }
        return Args.OptionalBool(args, "all_threads", false) ? proc : $"{ProcessSelector.RealPredicate(args)} AND t.tid = p.pid";
    }
}

// Time window resolved inside SQL: explicit ns bounds, a startup's span, or the whole trace.
internal sealed class TimeWindow
{
    private TimeWindow(string prelude, string start, string end, string description)
    {
        Prelude = prelude;
        Start = start;
        End = end;
        Description = description;
    }

    public string Prelude { get; }

    public string Start { get; }

    public string End { get; }

    public string Description { get; }

    public static TimeWindow Of(long start, long end, string description) =>
        new(string.Empty, start.ToString(), end.ToString(), description);

    public static SchemaBuilder AddParams(SchemaBuilder b) => b
        .Int("ts_start", "Window start, absolute trace timestamp in ns (as returned by queries). Default: trace start.")
        .Int("ts_end", "Window end, absolute trace timestamp in ns. Default: trace end.")
        .Int("startup_id", "Use the span of this app startup (from android_startups) as the window.");

    public static TimeWindow Parse(JsonObject args)
    {
        long? startupId = Args.OptionalLong(args, "startup_id");
        if (startupId is not null)
        {
            return new TimeWindow(
                "INCLUDE PERFETTO MODULE android.startup.startups;\n",
                $"(SELECT ts FROM android_startups WHERE startup_id = {startupId})",
                $"(SELECT ts + dur FROM android_startups WHERE startup_id = {startupId})",
                $"startup {startupId}");
        }
        long? s = Args.OptionalLong(args, "ts_start");
        long? e = Args.OptionalLong(args, "ts_end");
        if (s is not null && e is not null && e <= s)
        {
            throw new McpInvalidParamsException("ts_end must be greater than ts_start");
        }
        return new TimeWindow(
            string.Empty,
            s?.ToString() ?? "(SELECT start_ts FROM trace_bounds)",
            e?.ToString() ?? "(SELECT end_ts FROM trace_bounds)",
            s is null && e is null ? "whole trace" : $"[{s?.ToString() ?? "start"}, {e?.ToString() ?? "end"}]");
    }

    // Duration of [ts, ts+dur) clipped to the window; open-ended rows (dur = -1) run to the window end.
    public string Clipped(string ts, string dur) =>
        $"(MIN(IIF({dur} < 0, {End}, {ts} + {dur}), {End}) - MAX({ts}, {Start}))";

    public string Overlaps(string ts, string dur) =>
        $"{ts} < {End} AND IIF({dur} < 0, {End}, {ts} + {dur}) > {Start}";
}
