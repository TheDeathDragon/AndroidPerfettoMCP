using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PerfettoMcp.Logging;
using PerfettoMcp.Mcp;
using PerfettoMcp.Perfetto;
using PerfettoMcp.Tools;

namespace PerfettoMcp;

internal static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--supervise"))
        {
            if (args.Contains("--version"))
            {
                Console.Out.WriteLine($"perfetto-mcp supervisor {BuildInfo.Version}, worker {Supervisor.Supervisor.ReadCurrentVersion(AppContext.BaseDirectory) ?? "none"}");
                return 0;
            }
            return await new Supervisor.Supervisor(AppContext.BaseDirectory, args.Where(a => a != "--supervise"))
                .RunAsync().ConfigureAwait(false);
        }

        Settings settings = new();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--debug":
                    Log.MinLevel = LogLevel.Debug;
                    break;
                case "--quiet":
                    Log.MinLevel = LogLevel.Warn;
                    break;
                case "--query-timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out int q):
                    settings.QueryTimeout = TimeSpan.FromSeconds(q);
                    i++;
                    break;
                case "--load-timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out int l):
                    settings.LoadTimeout = TimeSpan.FromSeconds(l);
                    i++;
                    break;
                case "--state" when i + 1 < args.Length:
                    settings.StateFile = args[i + 1];
                    i++;
                    break;
                case "--max-instances" when i + 1 < args.Length && int.TryParse(args[i + 1], out int m) && m > 0:
                    settings.MaxLiveProcessors = m;
                    i++;
                    break;
                case "--version":
                    Console.Out.WriteLine($"perfetto-mcp {BuildInfo.Version}");
                    return 0;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    Log.Warn($"unknown arg: {args[i]}");
                    break;
            }
        }

        OrphanRegistry.Sweep();
        using TraceManager traces = new(settings);
        try
        {
            Log.Info($"trace_processor_shell: {traces.Binary}");
        }
        catch (Exception ex)
        {
            // Keep serving so the client sees a clear tool error instead of a dead server.
            Log.Error(ex.Message);
        }

        ToolRegistry tools = new();
        tools.Register(new LoadTraceTool(traces));
        tools.Register(new ListTracesTool(traces));
        tools.Register(new UnloadTraceTool(traces));
        tools.Register(new ExecuteSqlTool(traces));
        tools.Register(new ListTablesTool(traces));
        tools.Register(new ListTableStructureTool(traces));
        tools.Register(new ListStdlibModulesTool());
        tools.Register(new ListProcessesTool(traces));
        tools.Register(new ListThreadsInProcessTool(traces));
        tools.Register(new AndroidStartupsTool(traces));
        tools.Register(new AndroidJankFramesTool(traces));
        tools.Register(new AndroidBinderTxnsTool(traces));
        tools.Register(new ThreadStateBreakdownTool(traces));
        tools.Register(new CpuTopThreadsTool(traces));
        tools.Register(new SliceHotspotsTool(traces));
        tools.Register(new CpuProfileTool(traces));
        tools.Register(new FrameIntervalsTool(traces));
        tools.Register(new CpuFrequencyTool(traces));
        tools.Register(new AndroidLogsTool(traces));
        tools.Register(new AndroidGcTool(traces));
        tools.Register(new MemorySummaryTool(traces));
        tools.Register(new ProcessMemoryTool(traces));
        tools.Register(new IoSummaryTool(traces));
        tools.Register(new CompareTracesTool(traces));
        tools.Register(new LockContentionTool(traces));
        tools.Register(new AndroidAnrsTool(traces));
        tools.Register(new WakeupChainTool(traces));
        tools.Register(new EventLatencyTool(traces));
        tools.Register(new MainThreadBlockingCallsTool(traces));
        tools.Register(new AnalyzeBootchartTool());
        tools.Register(new BootTimelineTool(traces));
        tools.Register(new RecordTraceTool(traces));
        tools.Register(new RecordBootTool(traces));
        tools.Register(new ProfileThreadsTool(traces));
        tools.Register(new JavaHeapTool(traces));
        tools.Register(new ServerStatsTool());

        McpServer server = new(tools);
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => traces.Dispose();

        Log.Info($"perfetto-mcp v{BuildInfo.Version} starting (tools={tools.Descriptors.Count}, query_timeout={settings.QueryTimeout.TotalSeconds}s, " +
            $"load_timeout={settings.LoadTimeout.TotalSeconds}s, max_instances={settings.MaxLiveProcessors})");
        try
        {
            await new StdioTransport().RunAsync(server.HandleAsync, server.CancelAll, cts.Token, server.NoteArrival).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        Log.Info("perfetto-mcp stopped");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: perfetto-mcp [--query-timeout <s>] [--load-timeout <s>] [--max-instances <n>] [--debug | --quiet]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("MCP server over stdio for Perfetto trace analysis.");
        Console.Error.WriteLine("  --query-timeout <s>   per-query timeout (default 120, env PERFETTO_QUERY_TIMEOUT_S)");
        Console.Error.WriteLine("  --load-timeout <s>    trace load timeout (default 900, env PERFETTO_LOAD_TIMEOUT_S)");
        Console.Error.WriteLine("  --max-instances <n>   trace processors kept resident (default 4, env PERFETTO_MAX_INSTANCES)");
        Console.Error.WriteLine("  --supervise           hot-update front process: runs versions/<current.txt>/ as a child");
        Console.Error.WriteLine("  env PERFETTO_TP_PATH  use this trace_processor_shell instead of the bundled one");
    }
}
