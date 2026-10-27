using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Device;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;

namespace PerfettoMcp.Tools;

internal sealed partial class RecordBootTool : TraceToolBase
{
    // Fixed locations init / perfetto use for boot tracing (persist.debug.perfetto.boottrace) and bootchart.
    private const string BootTraceConfig = "/data/misc/perfetto-configs/boottrace.pbtxt";
    private const string BootTraceOut = "/data/misc/perfetto-traces/boottrace.perfetto-trace";
    private const string BootchartDir = "/data/bootchart";
    private static readonly string[] BootchartFiles = ["header", "proc_stat.log", "proc_ps.log", "proc_diskstats.log"];

    private readonly Adb adb;

    public RecordBootTool(TraceManager traces, Adb? adb = null) : base(traces)
    {
        this.adb = adb ?? new Adb();
    }

    public override ToolDescriptor Descriptor { get; } = Describe("record_boot",
        """
        REBOOTS the device and captures its boot: init bootchart (kernel start -> boot completed) and/or a Perfetto
        boot trace (persist.debug.perfetto.boottrace; built-in config, starts once /data is up). Waits for
        sys.boot_completed and the end of the boot trace, pulls everything to this machine, removes the device files,
        then returns analyze_bootchart and boot_timeline results (the trace is also loaded for further queries).
        Needs a userdebug/eng build (adb root). Takes boot time + boot_trace_duration_s (default 90 s).
        """,
        new SchemaBuilder()
            .Str("serial", "adb serial; optional when exactly one device is connected.")
            .Bool("bootchart", "Collect init bootchart (default true).")
            .Bool("boot_trace", "Collect a Perfetto boot trace (default true).")
            .Num("boot_trace_duration_s", "Boot trace length in seconds (default 90).")
            .Int("boot_timeout_s", "Max seconds to wait for sys.boot_completed (default 240).")
            .Str("out_dir", "Local directory (default ~/perfetto-traces)."));

    public override async Task<ToolCallResult> InvokeAsync(JsonObject args, RequestContext ctx)
    {
        CancellationToken ct = ctx.Cancellation;
        bool chart = Args.OptionalBool(args, "bootchart", true);
        bool trace = Args.OptionalBool(args, "boot_trace", true);
        if (!chart && !trace)
        {
            throw new McpInvalidParamsException("nothing to collect: enable bootchart and/or boot_trace");
        }
        double traceS = Math.Clamp(Args.OptionalDouble(args, "boot_trace_duration_s") ?? 90, 10, 600);
        int bootTimeout = Args.OptionalInt(args, "boot_timeout_s", 240, 30, 1200);
        string serial = await DeviceSteps.ResolveSerialAsync(adb, Args.OptionalString(args, "serial"), ct).ConfigureAwait(false);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string outDir = Path.Combine(Args.OptionalString(args, "out_dir")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "perfetto-traces"),
            $"boot_{RecordTraceTool.SafeName(serial)}_{stamp}");
        Directory.CreateDirectory(outDir);

        await DeviceSteps.RootAsync(adb, serial, ct).ConfigureAwait(false);
        AdbResult whoami = await adb.ShellAsync(serial, "id -u", DeviceSteps.ShortCmd, ct).ConfigureAwait(false);
        if (whoami.Stdout.Trim() != "0")
        {
            throw new ToolException("adb root is not available (user build?): bootchart and boot traces need userdebug/eng");
        }

        if (trace)
        {
            string config = RecordTraceTool.WithDuration(RecordTraceTool.LoadConfig("boot"), (long)(traceS * 1000));
            string tmp = Path.Combine(Path.GetTempPath(), $"boottrace_{stamp}.pbtxt");
            try
            {
                await File.WriteAllTextAsync(tmp, config, ct).ConfigureAwait(false);
                DeviceSteps.Check(await adb.RunAsync(["-s", serial, "push", tmp, BootTraceConfig], DeviceSteps.ShortCmd, ct)
                    .ConfigureAwait(false), "push boot trace config");
            }
            finally
            {
                File.Delete(tmp);
            }
            DeviceSteps.Check(await adb.ShellAsync(serial, $"rm -f {BootTraceOut}; setprop persist.debug.perfetto.boottrace 1",
                DeviceSteps.ShortCmd, ct).ConfigureAwait(false), "enable boot trace");
        }
        if (chart)
        {
            DeviceSteps.Check(await adb.ShellAsync(serial, $"rm -rf {BootchartDir}/*; touch {BootchartDir}/enabled",
                DeviceSteps.ShortCmd, ct).ConfigureAwait(false), "enable bootchart");
        }

        await adb.RunAsync(["-s", serial, "reboot"], DeviceSteps.ShortCmd, ct).ConfigureAwait(false);
        await Task.Delay(5000, ct).ConfigureAwait(false); // let the device actually go away before waiting for it
        // The boot trace is already recording: poll every 3 s so the getprop / sleep forks perturb it less.
        int waited = await DeviceSteps.WaitWhileAsync(adb, serial, "[ \"$(getprop sys.boot_completed)\" != 1 ]",
            bootTimeout, ctx, $"rebooting {serial}, waiting for boot_completed", intervalS: 3).ConfigureAwait(false);
        if (waited < 0)
        {
            throw new ToolException($"{serial} did not report sys.boot_completed within {bootTimeout} s; files stay on the device");
        }
        await DeviceSteps.RootAsync(adb, serial, ct).ConfigureAwait(false);
        // Uptime when boot_completed was seen (polled): an upper bound; exact milestones are in the bootchart / trace.
        AdbResult uptime = await adb.ShellAsync(serial, "cat /proc/uptime", DeviceSteps.ShortCmd, ct).ConfigureAwait(false);
        JsonObject result = new() { ["serial"] = serial, ["out_dir"] = outDir, ["uptime_when_ready_s"] = FirstNumber(uptime.Stdout) };

        if (chart)
        {
            string dir = Path.Combine(outDir, "bootchart");
            Directory.CreateDirectory(dir);
            // Remove the device copy only after every file arrived: a failed pull must not lose the only copy.
            foreach (string f in BootchartFiles)
            {
                DeviceSteps.Check(await adb.RunAsync(["-s", serial, "pull", $"{BootchartDir}/{f}", Path.Combine(dir, f)],
                    DeviceSteps.ShortCmd, ct).ConfigureAwait(false), $"pull bootchart {f}");
            }
            await adb.ShellAsync(serial, $"rm -rf {BootchartDir}/*", DeviceSteps.ShortCmd, CancellationToken.None).ConfigureAwait(false);
            JsonNode? analysis;
            try
            {
                ToolCallResult a = await new AnalyzeBootchartTool().InvokeAsync(new JsonObject { ["path"] = dir }, ctx).ConfigureAwait(false);
                analysis = a.IsError ? a.Content[0].Text : JsonNode.Parse(a.Content[0].Text!);
            }
            catch (ToolException ex)
            {
                // Keep going: the boot trace still has to be collected and cleaned up.
                analysis = $"analyze_bootchart failed: {ex.Message}";
            }
            result["bootchart"] = new JsonObject { ["dir"] = dir, ["analysis"] = analysis };
        }

        if (trace)
        {
            // Sleep on the host until the boot trace's planned end, then wait briefly on the device for the flush.
            await WaitForBootTraceEndAsync(serial, traceS, ctx).ConfigureAwait(false);
            int done = await DeviceSteps.WaitWhileAsync(adb, serial, "pidof perfetto >/dev/null", (int)traceS + 60, ctx,
                "waiting for the boot trace to finish").ConfigureAwait(false);
            if (done < 0)
            {
                // Still recording past its duration: SIGINT makes perfetto finalize the file, then pull what it wrote.
                await adb.ShellAsync(serial, "kill -INT $(pidof perfetto)", DeviceSteps.ShortCmd, ct).ConfigureAwait(false);
                if (await DeviceSteps.WaitWhileAsync(adb, serial, "pidof perfetto >/dev/null", 20, ctx, "stopping the boot trace")
                        .ConfigureAwait(false) < 0)
                {
                    throw new ToolException($"the boot trace on {serial} is still being written; {BootTraceOut} is left on the device");
                }
            }
            string local = Path.Combine(outDir, "boottrace.perfetto-trace");
            try
            {
                DeviceSteps.Check(await adb.RunAsync(["-s", serial, "pull", BootTraceOut, local], TimeSpan.FromMinutes(5), ct)
                    .ConfigureAwait(false), "pull boot trace");
            }
            finally
            {
                await adb.ShellAsync(serial, $"rm -f {BootTraceOut} {BootTraceConfig}", DeviceSteps.ShortCmd, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            TraceSession s = await Traces.LoadAsync(local, ctx).ConfigureAwait(false);
            ToolCallResult timeline = await new BootTimelineTool(Traces).InvokeAsync(new JsonObject { ["trace_id"] = s.Id }, ctx)
                .ConfigureAwait(false);
            result["boot_trace"] = new JsonObject
            {
                ["path"] = local,
                ["trace_id"] = s.Id,
                ["size_mb"] = Math.Round(new FileInfo(local).Length / 1048576.0, 1),
                ["timeline"] = timeline.IsError ? timeline.Content[0].Text : JsonNode.Parse(timeline.Content[0].Text!)
            };
        }
        return ToolCallResult.Json(result);
    }

    // Planned end = perfetto's start (field 22 of /proc/<pid>/stat, ticks since boot) + the trace duration.
    private async Task WaitForBootTraceEndAsync(string serial, double traceS, RequestContext ctx)
    {
        AdbResult r = await adb.ShellAsync(serial,
            "p=$(pidof perfetto | cut -d' ' -f1); [ -n \"$p\" ] && echo start=$(cut -d' ' -f22 /proc/$p/stat) "
            + "tck=$(getconf CLK_TCK) up=$(cut -d' ' -f1 /proc/uptime)", DeviceSteps.ShortCmd, ctx.Cancellation).ConfigureAwait(false);
        double left = SecondsUntilTraceEnd(r.Stdout, traceS);
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < left)
        {
            await Task.Delay((int)Math.Clamp((left - sw.Elapsed.TotalSeconds) * 1000, 1, 1000), ctx.Cancellation).ConfigureAwait(false);
            await ctx.ReportProgressAsync(Math.Min(sw.Elapsed.TotalSeconds, left), left,
                $"boot trace on {serial} still recording: {Math.Max(0, left - sw.Elapsed.TotalSeconds):0} s left").ConfigureAwait(false);
        }
    }

    // 0 when perfetto is gone or the output is unreadable: the device-side wait then handles the rest.
    internal static double SecondsUntilTraceEnd(string stdout, double traceS)
    {
        Match m = BootTraceTimes().Match(stdout);
        if (!m.Success)
        {
            return 0;
        }
        double tck = Math.Max(1, double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        double end = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / tck + traceS;
        return Math.Max(0, end - double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"start=(\d+) tck=(\d+) up=([\d.]+)")]
    private static partial Regex BootTraceTimes();

    private static double? FirstNumber(string s) =>
        double.TryParse(s.Trim().Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
