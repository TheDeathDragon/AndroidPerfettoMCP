using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PerfettoMcp.Mcp;

namespace PerfettoMcp.Tools;

internal interface IToolHandler
{
    public ToolDescriptor Descriptor { get; }
    public Task<ToolCallResult> InvokeAsync(JsonObject arguments, RequestContext ctx);
}

internal sealed class ToolRegistry
{
    private readonly Dictionary<string, IToolHandler> handlers = new();
    private readonly List<ToolDescriptor> descriptors = new();

    public IReadOnlyList<ToolDescriptor> Descriptors => descriptors;

    public void Register(IToolHandler handler)
    {
        handlers[handler.Descriptor.Name] = handler;
        descriptors.Add(handler.Descriptor);
    }

    public bool TryGet(string name, [NotNullWhen(true)] out IToolHandler? handler) =>
        handlers.TryGetValue(name, out handler);
}

internal sealed class SchemaBuilder
{
    private readonly JsonObject props = new();
    private readonly JsonArray required = new();

    public SchemaBuilder Str(string name, string description, bool isRequired = false) =>
        Add(name, new JsonObject { ["type"] = "string", ["description"] = description }, isRequired);

    public SchemaBuilder Int(string name, string description, bool isRequired = false) =>
        Add(name, new JsonObject { ["type"] = "integer", ["description"] = description }, isRequired);

    public SchemaBuilder Num(string name, string description, bool isRequired = false) =>
        Add(name, new JsonObject { ["type"] = "number", ["description"] = description }, isRequired);

    public SchemaBuilder Bool(string name, string description) =>
        Add(name, new JsonObject { ["type"] = "boolean", ["description"] = description }, false);

    public SchemaBuilder Enum(string name, string description, params string[] values)
    {
        JsonArray arr = new();
        foreach (string v in values)
        {
            arr.Add(v);
        }
        return Add(name, new JsonObject { ["type"] = "string", ["enum"] = arr, ["description"] = description }, false);
    }

    public SchemaBuilder StrArray(string name, string description) =>
        Add(name, new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string" },
            ["description"] = description
        }, false);

    public SchemaBuilder TraceId() =>
        Str("trace_id", "Trace handle from load_trace (e.g. \"t2\"). Defaults to the most recently loaded trace.");

    // Closed schema: hallucinated parameters fail fast instead of being ignored.
    public JsonObject Build()
    {
        JsonObject o = new()
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false
        };
        if (required.Count > 0)
        {
            o["required"] = required;
        }
        return o;
    }

    private SchemaBuilder Add(string name, JsonObject schema, bool isRequired)
    {
        props[name] = schema;
        if (isRequired)
        {
            required.Add(name);
        }
        return this;
    }
}

internal static class Annotations
{
    public static JsonObject ReadOnly() => new()
    {
        ["readOnlyHint"] = true,
        ["destructiveHint"] = false,
        ["idempotentHint"] = true,
        ["openWorldHint"] = false
    };
}
