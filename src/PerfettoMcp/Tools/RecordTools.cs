using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Device;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed partial class RecordTraceTool : TraceToolBase
{
    // perfetto can only read configs from here (SELinux denies /data/local/tmp) and write traces to perfetto-traces.
    private const string RemoteConfigDir = "/data/misc/perfetto-configs";
    private const string RemoteTraceDir = "/data/misc/perfetto-traces";
    private static readonly TimeSpan ShortCmd = DeviceSteps.ShortCmd;

    private readonly Adb adb;

    public RecordTraceTool(TraceManager traces, Adb? adb = null) : base(traces)
    {
        this.adb = adb ?? new Adb();
    }

    public override ToolDescriptor Descriptor { get; } = Describe("record_trace",
        """
        Record a Perfetto trace on an adb-connected Android device, pull it to this machine and (by default) load it.
        `config` is a built-in name — `scenario` (sched / freq / block / atrace am wm gfx view input dalvik binder ... /
        FrameTimeline / process & sys stats / logcat, no callstack sampling), `idle` (standby / power: sched, wakeups,
        freq, idle, suspend, IRQs; process list once, low own overhead), `heap` (Java heap dump of `process`, for
        java_heap), `boot` — or a local .pbtxt path; or `config_text`: a whole TraceConfig in text format composed for
        the question at hand (data sources, ftrace events, atrace categories / apps, buffers), e.g. a built-in config
        plus `ftrace_events: "block/block_rq_issue"`. `duration_s`
        overrides the config's duration_ms. The device keeps running normally while recording, so drive the scenario
        (launch, scroll, unlock ...) in parallel, e.g. with an Android-automation MCP, after this call has started;
        progress is reported every second. Device-side files are removed after a successful pull. Uses `adb` from PATH
        (or PERFETTO_MCP_ADB); tries `adb root` (userdebug) but works without it for most data sources.
        On user builds the android.log data source gets nothing (SELinux denies traced_probes the logdr socket), so by
        default (`logcat`=auto) `adb logcat` runs on the host during the recording and is merged with the trace into
        <trace>+logcat.zip, which is what gets loaded: log-based analyses keep working.
        """,
        new SchemaBuilder()
            .Str("serial", "adb serial; optional when exactly one device is connected.")
            .Str("config", "Built-in config name (scenario, idle, boot, heap) or a local .pbtxt path (default scenario).")
            .Str("config_text", "Inline TraceConfig (pbtxt text) instead of `config`; needs duration_ms or `duration_s`.")
            .Num("duration_s", "Recording length in seconds (overrides duration_ms in the config; default: the config's).")
            .Str("process", "Target process for configs with a PROCESS placeholder (built-in `heap`: Java heap dump).")
            .Str("out_dir", "Local directory for the trace (default ~/perfetto-traces).")
            .Str("name", "File name prefix (default: the config name).")
            .Bool("load", "Load the pulled trace and return its overview (default true).")
            .Enum("logcat", "Capture `adb logcat` alongside and merge it: auto (user builds whose config asks for android.log), "
                + "on, off. Default auto.", "auto", "on", "off"));

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        CancellationToken ct = ctx.Cancellation;
        string serial = await DeviceSteps.ResolveSerialAsync(adb, Args.OptionalString(args, "serial"), ct).ConfigureAwait(false);
        string? configText = Args.OptionalString(args, "config_text");
        if (configText is not null && Args.OptionalString(args, "config") is not null)
        {
            throw new McpInvalidParamsException("pass `config` or `config_text`, not both");
        }
        string configArg = configText is null ? Args.OptionalString(args, "config") ?? "scenario" : "inline";
        string config = configText ?? LoadConfig(configArg);
        if (config.Contains("\"PROCESS\"", StringComparison.Ordinal))
        {
            string process = Args.OptionalString(args, "process")
                ?? throw new McpInvalidParamsException($"config '{configArg}' needs `process` (e.g. com.android.systemui)");
            config = config.Replace("\"PROCESS\"", "\"" + process.Replace("\"", string.Empty) + "\"", StringComparison.Ordinal);
        }
        if (Args.OptionalDouble(args, "duration_s") is { } d)
        {
            config = WithDuration(config, (long)(Math.Clamp(d, 1, 3600) * 1000));
        }
        long durationMs = DurationMs(config)
            ?? throw new McpInvalidParamsException("the config has no duration_ms; pass duration_s");

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string label = SafeName(Args.OptionalString(args, "name") ?? Path.GetFileNameWithoutExtension(configArg));
        string outDir = Args.OptionalString(args, "out_dir")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "perfetto-traces");
        Directory.CreateDirectory(outDir);
        string local = Path.Combine(outDir, $"{label}_{SafeName(serial)}_{stamp}.pftrace");
        string id = "mcp_" + stamp;
        string remoteCfg = $"{RemoteConfigDir}/{id}.pbtxt";
        string remoteTrace = $"{RemoteTraceDir}/{id}.pftrace";

        await DeviceSteps.RootAsync(adb, serial, ct).ConfigureAwait(false);
        string logcatMode = Args.OptionalString(args, "logcat") ?? "auto";
        bool hostLogcat = logcatMode == "on" || (logcatMode == "auto" && config.Contains("\"android.log\"", StringComparison.Ordinal)
            && (await adb.ShellAsync(serial, "getprop ro.build.type", ShortCmd, ct).ConfigureAwait(false)).Stdout.Trim() == "user");
        string localLogcat = Path.ChangeExtension(local, ".logcat.txt");
        string tmp = Path.Combine(Path.GetTempPath(), id + ".pbtxt");
        try
        {
            await File.WriteAllTextAsync(tmp, config, ct).ConfigureAwait(false);
            DeviceSteps.Check(await adb.RunAsync(["-s", serial, "push", tmp, remoteCfg], ShortCmd, ct).ConfigureAwait(false), "push config");
        }
        finally
        {
            File.Delete(tmp);
        }

        JsonObject result = new() { ["serial"] = serial, ["config"] = configArg, ["duration_s"] = durationMs / 1000.0 };
        // Started first so the log covers the whole trace; `-T 1` skips the backlog already in logd's buffers.
        AdbStream? logcat = hostLogcat
            ? adb.Stream(["-s", serial, "logcat", "-b", "all", "-v", "threadtime", "-v", "year", "-T", "1"], localLogcat)
            : null;
        bool pulled = false;
        try
        {
            AdbResult start = await adb.ShellAsync(serial,
                $"perfetto --background --txt -c {remoteCfg} -o {remoteTrace}", ShortCmd, ct).ConfigureAwait(false);
            // --background prints the pid of the detached tracing session.
            string? pid = start.Stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && l.All(char.IsDigit));
            if (!start.Ok || pid is null)
            {
                throw new ToolException($"perfetto did not start: {start.Output}");
            }
            await WaitAsync(serial, pid, durationMs, ctx).ConfigureAwait(false);
            if (logcat is not null)
            {
                await logcat.DisposeAsync().ConfigureAwait(false);
                logcat = null;
            }

            DeviceSteps.Check(await adb.RunAsync(["-s", serial, "pull", remoteTrace, local], TimeSpan.FromMinutes(5), ct).ConfigureAwait(false),
                "pull trace");
            if (!File.Exists(local) || new FileInfo(local).Length == 0)
            {
                throw new ToolException($"pulled trace is missing or empty: {local}");
            }
            pulled = true;
        }
        finally
        {
            if (logcat is not null)
            {
                await logcat.DisposeAsync().ConfigureAwait(false);
            }
            if (!pulled && hostLogcat)
            {
                File.Delete(localLogcat);
            }
            // Only after the pull (or on failure) — never leave configs/traces behind on the device.
            await adb.ShellAsync(serial, $"rm -f {remoteCfg} {remoteTrace}", ShortCmd, CancellationToken.None).ConfigureAwait(false);
        }
        result["path"] = local;
        result["size_mb"] = Math.Round(new FileInfo(local).Length / 1048576.0, 1);
        if (hostLogcat)
        {
            result["logcat_path"] = localLogcat;
        }

        if (Args.OptionalBool(args, "load", true))
        {
            // The merged zip copies the trace, so it is only built when it is loaded.
            string toLoad = hostLogcat ? TraceBundle.WithLogcat(local, localLogcat) : local;
            ToolCallResult loaded = await new LoadTraceTool(Traces).InvokeAsync(new JsonObject { ["path"] = toLoad }, ctx)
                .ConfigureAwait(false);
            result["load"] = JsonNode.Parse(loaded.Content[0].Text!);
        }
        return ToolCallResult.Json(result);
    }

    // Wait on the host while recording (a device-side sleep loop costs measurable CPU in idle traces); only the
    // final flush is awaited on the device.
    private async Task WaitAsync(string serial, string pid, long durationMs, RequestContext ctx)
    {
        double totalS = durationMs / 1000.0;
        const int TailLimitS = 60;
        Stopwatch sw = Stopwatch.StartNew();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation);
        try
        {
            while (sw.ElapsedMilliseconds < durationMs)
            {
                long left = durationMs - sw.ElapsedMilliseconds;
                await Task.Delay((int)Math.Clamp(left, 1, 1000), ctx.Cancellation).ConfigureAwait(false);
                double s = sw.Elapsed.TotalSeconds;
                await ctx.ReportProgressAsync(Math.Min(s, totalS), totalS, $"recording on {serial}: {s:0} / {totalS:0} s")
                    .ConfigureAwait(false);
            }
            Task<AdbResult> wait = adb.ShellAsync(serial,
                $"i=0; while [ -d /proc/{pid} ] && [ $i -lt {TailLimitS} ]; do sleep 1; i=$((i+1)); done; echo waited=$i",
                TimeSpan.FromSeconds(TailLimitS + 30), stop.Token);
            while (!wait.IsCompleted)
            {
                await Task.WhenAny(wait, Task.Delay(1000, ctx.Cancellation)).ConfigureAwait(false);
                ctx.Cancellation.ThrowIfCancellationRequested();
                await ctx.ReportProgressAsync(totalS, totalS, $"finalizing trace on {serial}").ConfigureAwait(false);
            }
            AdbResult r = await wait.ConfigureAwait(false);
            if (r.Stdout.Contains($"waited={TailLimitS}", StringComparison.Ordinal))
            {
                // Still running a minute past its duration: stop it so the file is finalized.
                await adb.ShellAsync(serial, $"kill -INT {pid}; sleep 3", ShortCmd, ctx.Cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            stop.Cancel();
            // SIGINT makes perfetto stop and finalize the file; the caller's finally removes it.
            await adb.ShellAsync(serial, $"kill -INT {pid}", ShortCmd, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal static string LoadConfig(string config)
    {
        if (config.EndsWith(".pbtxt", StringComparison.OrdinalIgnoreCase) || config.Contains(Path.DirectorySeparatorChar)
            || config.Contains('/'))
        {
            return File.Exists(config) ? File.ReadAllText(config) : throw new ToolException($"config not found: {config}");
        }
        using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"config.{config}.pbtxt");
        if (s is null)
        {
            throw new McpInvalidParamsException($"unknown built-in config '{config}' (available: scenario, idle, boot, heap) and not a .pbtxt path");
        }
        using StreamReader r = new(s);
        return r.ReadToEnd();
    }

    internal static string WithDuration(string config, long ms) => DurationLine().IsMatch(config)
        ? DurationLine().Replace(config, $"duration_ms: {ms}", 1)
        : config.TrimEnd() + $"\nduration_ms: {ms}\n";

    internal static long? DurationMs(string config) =>
        DurationLine().Match(config) is { Success: true } m ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    internal static string SafeName(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    // Top-level duration_ms only (nested messages are indented).
    [GeneratedRegex(@"(?m)^duration_ms:\s*(\d+)")]
    private static partial Regex DurationLine();
}
