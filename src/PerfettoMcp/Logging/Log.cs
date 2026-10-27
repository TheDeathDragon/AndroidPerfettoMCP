using System;
using System.IO;
using System.Text;

namespace PerfettoMcp.Logging;

// stdout is the JSON-RPC channel under stdio transport; never write to it.
internal static class Log
{
    private static readonly object Gate = new();
    // UTF-8 regardless of the console codepage: trace paths and thread names are often CJK.
    private static readonly TextWriter Sink = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
    private static StreamWriter? mirror;
    private static string? mirrorPath;
    private static long mirrorMax;
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    // Claude Code keeps only a server's startup stderr, so the supervisor also logs to a file.
    // Rotates to <path>.1 at maxBytes, also while running (a supervisor lives for days).
    public static void MirrorTo(string path, long maxBytes = 1 << 20)
    {
        string? error;
        lock (Gate)
        {
            mirrorPath = path;
            mirrorMax = maxBytes;
            error = OpenMirror();
        }
        if (error is not null)
        {
            Warn($"log file {path} unavailable: {error}");
        }
    }

    internal static void StopMirror()
    {
        lock (Gate)
        {
            mirror?.Dispose();
            mirror = null;
        }
    }

    // Caller holds Gate.
    private static string? OpenMirror()
    {
        mirror?.Dispose();
        mirror = null;
        try
        {
            if (File.Exists(mirrorPath) && new FileInfo(mirrorPath).Length > mirrorMax)
            {
                File.Move(mirrorPath, mirrorPath + ".1", overwrite: true);
            }
        }
        catch (IOException)
        {
            // Another supervisor (second client session) holds it: keep appending.
        }
        try
        {
            FileStream fs = new(mirrorPath!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            mirror = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    // Already-formatted line from a child process (supervisor forwarding worker stderr).
    public static void Raw(string line)
    {
        lock (Gate)
        {
            Sink.WriteLine(line);
            TryMirror(line);
        }
    }

    private static void Write(LogLevel level, string message)
    {
        if (level < MinLevel)
        {
            return;
        }

        DateTime now = DateTime.Now;
        string line = $"{now:HH:mm:ss.fff} {LevelTag(level)} {message}";
        lock (Gate)
        {
            Sink.WriteLine(line);
            TryMirror($"{now:yyyy-MM-dd} {line}");
        }
    }

    private static void TryMirror(string line)
    {
        if (mirror is null)
        {
            return;
        }
        try
        {
            mirror.WriteLine(line);
            if (mirror.BaseStream.Length > mirrorMax)
            {
                OpenMirror();
            }
        }
        catch
        {
            mirror = null; // disk full or file gone: stderr still works
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "?    "
    };
}

internal enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}
