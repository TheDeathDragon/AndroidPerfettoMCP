using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Logging;
using PerfettoMcp.Mcp;

namespace PerfettoMcp.Device;

internal sealed record AdbResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;

    public string Output => (Stdout + Stderr).Trim();
}

// adb client wrapper; ArgumentList keeps the host shell (MSYS) from rewriting device paths.
internal sealed class Adb
{
    public Adb(string? executable = null)
    {
        Executable = executable ?? Environment.GetEnvironmentVariable("PERFETTO_MCP_ADB") ?? "adb";
    }

    public string Executable { get; }

    public async Task<AdbResult> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        ProcessStartInfo psi = new(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using Process p = new() { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // An expected setup problem, not a server bug: report it as a normal tool error.
            throw new ToolException($"cannot run '{Executable}' ({ex.Message}); put adb on PATH or set PERFETTO_MCP_ADB");
        }
        Task<string> stdout = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = p.StandardError.ReadToEndAsync(ct);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            throw new ToolException($"adb {string.Join(' ', psi.ArgumentList)} did not finish within {timeout.TotalSeconds:0} s");
        }
        return new AdbResult(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public AdbStream Stream(IEnumerable<string> args, string outFile) => AdbStream.Start(Executable, args, outFile);

    public Task<AdbResult> ShellAsync(string serial, string command, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(["-s", serial, "shell", command], timeout, ct);

    // "adb devices" output -> serials in state "device" (skips unauthorized / offline).
    public static List<string> ParseDevices(string output) => output
        .Split('\n')
        .Skip(1)
        .Select(l => l.Trim().Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(f => f.Length >= 2 && f[1] == "device")
        .Select(f => f[0])
        .ToList();
}

// A long-running adb command (e.g. `logcat`) whose stdout streams into a file until disposed.
internal sealed class AdbStream : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task copy;

    private AdbStream(Process process, Task copy)
    {
        this.process = process;
        this.copy = copy;
    }

    public static AdbStream Start(string executable, IEnumerable<string> args, string outFile)
    {
        ProcessStartInfo psi = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        Process p = new() { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            p.Dispose();
            throw new ToolException($"cannot run '{executable}' ({ex.Message}); put adb on PATH or set PERFETTO_MCP_ADB");
        }
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        Task copy = Task.Run(async () =>
        {
            await using FileStream fs = new(outFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            await p.StandardOutput.BaseStream.CopyToAsync(fs).ConfigureAwait(false);
        });
        return new AdbStream(p, copy);
    }

    // Stops the command and waits until its output is on disk. Never throws: it runs in cleanup paths. Only the client
    // is killed: an adb server it may have spawned is shared with other tools and machines.
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"adb stream: kill failed: {ex.Message}");
        }
        try
        {
            await copy.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"adb stream: copy ended with {ex.GetType().Name}: {ex.Message}");
        }
        process.Dispose();
    }
}
