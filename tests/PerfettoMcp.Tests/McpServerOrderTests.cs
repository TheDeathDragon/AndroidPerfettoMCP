using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;
using PerfettoMcp.Tools;
using Xunit;

namespace PerfettoMcp.Tests;

// A load_trace and a default-trace call sent in the same batch run concurrently; the second must see the new trace.
public class McpServerOrderTests
{
    private sealed class GateTool : IToolHandler
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> log;

        public GateTool(string name, bool takesTraceId, List<string> log)
        {
            this.log = log;
            SchemaBuilder schema = new SchemaBuilder().Str("path", "p");
            if (takesTraceId)
            {
                schema.TraceId();
            }
            Descriptor = new ToolDescriptor { Name = name, Description = name, InputSchema = schema.Build() };
        }

        public ToolDescriptor Descriptor { get; }

        public void Open() => gate.TrySetResult();

        public async Task<ToolCallResult> InvokeAsync(JsonObject arguments, RequestContext ctx)
        {
            await gate.Task;
            lock (log)
            {
                log.Add(Descriptor.Name);
            }
            return ToolCallResult.Json(new JsonObject());
        }
    }

    private static string Call(int id, string name, string args = "{}") =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{name}}}","arguments":{{{args}}}}}""";

    [Fact]
    public async Task DefaultTraceCallWaitsForAnEarlierLoad()
    {
        List<string> log = new();
        GateTool load = new("load_trace", false, log);
        GateTool query = new("execute_sql", true, log);
        query.Open();
        ToolRegistry tools = new();
        tools.Register(load);
        tools.Register(query);
        McpServer server = new(tools);

        string l1 = Call(1, "load_trace"), l2 = Call(2, "execute_sql");
        server.NoteArrival(l1);
        server.NoteArrival(l2);
        Task<string?> q = server.HandleAsync(l2, _ => Task.CompletedTask);
        Task<string?> l = server.HandleAsync(l1, _ => Task.CompletedTask);
        await Task.Delay(100);
        Assert.False(q.IsCompleted); // held back behind the load
        load.Open();
        await Task.WhenAll(q, l);
        Assert.Equal(new[] { "load_trace", "execute_sql" }, log);
    }

    [Fact]
    public async Task RecordTraceWithoutLoadDoesNotHoldCallsBack()
    {
        List<string> log = new();
        GateTool record = new("record_trace", false, log);
        GateTool query = new("execute_sql", true, log);
        query.Open();
        ToolRegistry tools = new();
        tools.Register(record);
        tools.Register(query);
        McpServer server = new(tools);

        string r1 = Call(1, "record_trace", """{"load":false}"""), q2 = Call(2, "execute_sql");
        server.NoteArrival(r1);
        server.NoteArrival(q2);
        Task<string?> r = server.HandleAsync(r1, _ => Task.CompletedTask);
        await server.HandleAsync(q2, _ => Task.CompletedTask);
        Assert.Equal(new[] { "execute_sql" }, log);
        record.Open();
        await r;
    }

    [Fact]
    public async Task ExplicitTraceIdAndEarlierCallsDoNotWait()
    {
        List<string> log = new();
        GateTool load = new("load_trace", false, log);
        GateTool query = new("execute_sql", true, log);
        query.Open();
        ToolRegistry tools = new();
        tools.Register(load);
        tools.Register(query);
        McpServer server = new(tools);

        string before = Call(1, "execute_sql"), l1 = Call(2, "load_trace"), pinned = Call(3, "execute_sql", """{"trace_id":"t1"}""");
        server.NoteArrival(before);
        server.NoteArrival(l1);
        server.NoteArrival(pinned);
        Task<string?> l = server.HandleAsync(l1, _ => Task.CompletedTask);
        await server.HandleAsync(before, _ => Task.CompletedTask); // arrived first: no wait
        await server.HandleAsync(pinned, _ => Task.CompletedTask); // explicit trace_id: no wait
        Assert.False(l.IsCompleted);
        load.Open();
        await l;
        Assert.Equal(new[] { "execute_sql", "execute_sql", "load_trace" }, log);
    }
}
