using System.Text.Json;
using GateKit.Copies;

namespace GateKit.Mcp;

/// <summary>
/// What a tool call is refused for before it runs.
///
/// Only one thing so far, and it earned its place: an argument the tool does not know used to be
/// dropped without a word. A run asked `bg3_coverage_todo` for one namespace, spelled the
/// argument `namespace` where the tool calls it `namespaces`, and got a cheerful answer about
/// everything - which it then reasoned from. A tool that ignores what it does not understand
/// teaches whoever is calling it that the question was asked properly, and an agent has no way of
/// finding out otherwise. Saying no costs one line and saves a wrong conclusion.
/// </summary>
static class Refuse
{
    /// <summary>What the schema lists as required and the call left out, refused before the tool runs.</summary>
    public static void MissingArguments(GateKitTool tool, Args args)
    {
        var schema = tool.Definition.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array) return;
        var missing = required.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String)
            .Select(r => r.GetString()!).Where(n => !args.Has(n)).ToList();
        if (missing.Count > 0) throw Missing(tool, missing);
    }

    /// <summary>The refusal for arguments a tool needs and was not given: what it needs, and all it takes.</summary>
    public static CopyException Missing(GateKitTool tool, IReadOnlyList<string> missing)
    {
        var schema = tool.Definition.InputSchema;
        var known = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object ? properties.EnumerateObject().Select(p => p.Name).ToList() : [];
        return new CopyException($"{tool.Name} needs {string.Join(", ", missing.Select(m => $"'{m}'"))}, which the call did not give. "
            + (known.Count > 0 ? $"It takes: {string.Join(", ", known)}. " : "")
            + "Nothing was done.");
    }

    public static void UnknownArguments(GateKitTool tool, Args args)
    {
        var schema = tool.Definition.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object) return;
        // A schema that takes anything is taken at its word.
        if (schema.TryGetProperty("additionalProperties", out var open)
            && open.ValueKind == JsonValueKind.True) return;
        if (!schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object) return;

        var known = properties.EnumerateObject().Select(p => p.Name).ToList();
        if (known.Count == 0) return;
        var strangers = args.Names.Where(n => !known.Contains(n, StringComparer.Ordinal)).ToList();
        if (strangers.Count == 0) return;

        var said = new List<string>();
        foreach (var stranger in strangers)
        {
            // The near miss is almost always the point - a plural, a singular, a different word
            // for the same thing - so it is named rather than left to be guessed at.
            var near = known.FirstOrDefault(k => k.StartsWith(stranger, StringComparison.OrdinalIgnoreCase)
                || stranger.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            said.Add(near is null ? $"'{stranger}'" : $"'{stranger}' (did you mean '{near}'?)");
        }
        throw new CopyException($"{tool.Name} does not take {string.Join(", ", said)}. "
            + $"It takes: {string.Join(", ", known)}. Nothing was done, because an argument that is "
            + "quietly ignored reads as an answer to the question you asked.");
    }
}
