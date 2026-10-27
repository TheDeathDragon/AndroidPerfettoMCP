using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PerfettoMcp.Perfetto;

// A zip of a trace and a `logcat -v threadtime -v year` file loads as one timeline (user-build traces have no logcat).
internal static class TraceBundle
{
    public static string WithLogcat(string tracePath, string logcatPath)
    {
        if (!File.Exists(tracePath))
        {
            throw new Mcp.ToolException($"Trace file not found: {tracePath}");
        }
        if (!File.Exists(logcatPath))
        {
            throw new Mcp.ToolException($"logcat file not found: {logcatPath}");
        }
        string dir = Path.GetDirectoryName(Path.GetFullPath(tracePath))!;
        string zip = Path.Combine(dir, Path.GetFileNameWithoutExtension(tracePath) + "+logcat.zip");
        if (Matches(zip, tracePath, logcatPath))
        {
            return zip;
        }
        string tmp = $"{zip}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (ZipArchive archive = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(tracePath, Path.GetFileName(tracePath), CompressionLevel.Fastest);
                archive.CreateEntryFromFile(logcatPath, Path.GetFileName(logcatPath), CompressionLevel.Fastest);
            }
            File.Move(tmp, zip, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
        return zip;
    }

    // Reused only when it holds exactly these two files (name and size) and is newer than both.
    private static bool Matches(string zip, string tracePath, string logcatPath)
    {
        if (!File.Exists(zip) || File.GetLastWriteTimeUtc(zip) < File.GetLastWriteTimeUtc(tracePath)
            || File.GetLastWriteTimeUtc(zip) < File.GetLastWriteTimeUtc(logcatPath))
        {
            return false;
        }
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zip);
            return archive.Entries.Count == 2
                && new[] { tracePath, logcatPath }.All(f => archive.GetEntry(Path.GetFileName(f)) is { } e && e.Length == new FileInfo(f).Length);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
