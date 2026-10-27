using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;

namespace PerfettoMcp.Device;

internal static class DeviceSteps
{
    public static readonly TimeSpan ShortCmd = TimeSpan.FromSeconds(30);

    public static async Task<string> ResolveSerialAsync(Adb adb, string? serial, CancellationToken ct)
    {
        AdbResult r = await adb.RunAsync(["devices"], ShortCmd, ct).ConfigureAwait(false);
        Check(r, "adb devices");
        List<string> devices = Adb.ParseDevices(r.Stdout);
        if (serial is not null)
        {
            return devices.Contains(serial)
                ? serial
                : throw new ToolException($"device {serial} not connected (connected: {string.Join(", ", devices)})");
        }
        return devices.Count switch
        {
            1 => devices[0],
            0 => throw new ToolException("no adb device connected (or unauthorized / offline)"),
            _ => throw new ToolException($"several devices connected, pass serial: {string.Join(", ", devices)}"),
        };
    }

    public static void Check(AdbResult r, string what)
    {
        if (!r.Ok)
        {
            throw new ToolException($"{what} failed: {r.Output}");
        }
    }

    // Awaits a long adb call while sending a progress notification every second.
    public static async Task<T> WithProgressAsync<T>(Task<T> work, double expectS, RequestContext ctx, string label)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (!work.IsCompleted)
        {
            await Task.WhenAny(work, Task.Delay(1000, ctx.Cancellation)).ConfigureAwait(false);
            ctx.Cancellation.ThrowIfCancellationRequested();
            double s = sw.Elapsed.TotalSeconds;
            await ctx.ReportProgressAsync(Math.Min(s, expectS), expectS, $"{label}: {s:0} s").ConfigureAwait(false);
        }
        return await work.ConfigureAwait(false);
    }

    // Waits on the device while `condition` holds (at most limitS), retrying dropped connections. Returns the seconds
    // waited, or -1 on timeout. Each round forks on the device: avoid it while a trace records.
    public static async Task<int> WaitWhileAsync(Adb adb, string serial, string condition, int limitS, RequestContext ctx,
        string label, int intervalS = 1)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < limitS)
        {
            int left = Math.Max(1, limitS - (int)sw.Elapsed.TotalSeconds);
            try
            {
                // `done=` only when the condition no longer holds; `still=` when the device-side loop ran out of time.
                Task<AdbResult> run = adb.RunAsync(["-s", serial, "wait-for-device", "shell",
                    $"i=0; while {condition} && [ $i -lt {left} ]; do sleep {intervalS}; i=$((i+{intervalS})); done; "
                    + $"if {condition}; then echo still=$i; else echo done=$i; fi"],
                    TimeSpan.FromSeconds(left + 30), ctx.Cancellation);
                AdbResult r = await WithProgressAsync(run, limitS, ctx, label).ConfigureAwait(false);
                int at = r.Stdout.IndexOf("done=", StringComparison.Ordinal);
                if (r.Ok && at >= 0 && int.TryParse(r.Stdout[(at + 5)..].Trim(), out int waited))
                {
                    return waited;
                }
            }
            catch (ToolException)
            {
                // adb timed out (device gone / not back yet): retry until the deadline.
            }
            await Task.Delay(2000, ctx.Cancellation).ConfigureAwait(false);
        }
        return -1;
    }

    // adb root restarts adbd; the device is briefly gone afterwards.
    public static async Task RootAsync(Adb adb, string serial, CancellationToken ct)
    {
        AdbResult r = await adb.RunAsync(["-s", serial, "root"], ShortCmd, ct).ConfigureAwait(false);
        if (!r.Output.Contains("already running as root", StringComparison.Ordinal))
        {
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
        await adb.RunAsync(["-s", serial, "wait-for-device"], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
    }
}
