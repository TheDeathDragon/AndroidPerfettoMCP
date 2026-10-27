using System;
using System.IO;
using PerfettoMcp.Logging;

namespace PerfettoMcp.Perfetto;

internal static class TpBinary
{
    public const string EnvOverride = "PERFETTO_TP_PATH";

    private static string FileName => OperatingSystem.IsWindows() ? "trace_processor_shell.exe" : "trace_processor_shell";

    // Resolution order: $PERFETTO_TP_PATH -> <exe dir>/tools/ -> PATH.
    public static string Resolve()
    {
        string? env = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (!File.Exists(env))
            {
                throw new FileNotFoundException($"{EnvOverride} points to a missing file: {env}");
            }
            return Prepare(env);
        }

        string bundled = Path.Combine(AppContext.BaseDirectory, "tools", FileName);
        if (File.Exists(bundled))
        {
            return Prepare(bundled);
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
            {
                continue;
            }
            string candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate))
            {
                return Prepare(candidate);
            }
        }

        throw new FileNotFoundException(
            $"trace_processor_shell not found. Looked at ${EnvOverride}, {bundled} and PATH.");
    }

    // Archives and sftp uploads often drop the exec bit.
    private static string Prepare(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            const UnixFileMode Exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & UnixFileMode.UserExecute) == 0)
            {
                try
                {
                    File.SetUnixFileMode(path, mode | Exec);
                }
                catch (Exception ex)
                {
                    Log.Warn($"cannot chmod +x {path}: {ex.Message}");
                }
            }
        }
        return path;
    }
}
