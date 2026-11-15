using System.Text.Json.Nodes;
using GateKit.Json;
using GateKit.Copies;

namespace GateKit.Mcp;

/// <summary>A tool's answer held to its schema before it goes out (schemas/index.json binds the
/// tool to a kind): one that does not fit is a failed call that names every misfit, never a
/// silently different answer.</summary>
public static class SchemaCheck
{
    /// <summary>The misfits of a value against a kind, as one line each; empty when it fits.</summary>
    public static List<string> Misfits(string kind, JsonNode? value) =>
        [.. SchemaSet.Embedded.Validate(value, kind).Select(m => $"{(m.Pointer.Length > 0 ? m.Pointer : "/")}: {m.What}")];

    /// <summary>Throws when <paramref name="answer"/> does not fit the schema the registry binds <paramref name="tool"/> to.</summary>
    public static void Answer(string tool, JsonNode answer)
    {
        var binding = SchemaSet.Embedded.ForTool(tool)
            ?? throw new CopyException($"{tool}: schemas/index.json binds no schema to its answer");
        var misfits = Misfits(binding.Schema.Id, answer);
        if (misfits.Count > 0)
            throw new CopyException($"{tool}: the answer does not fit {binding.Schema.Id}: {string.Join("; ", misfits.Take(10))}");
    }
}
