using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Mcp;

/// <summary>
/// A tool call's arguments. The JSON schemas of the tools are advisory, as they were in the old
/// server: what is missing is null and what is required is asked for by name, which fails with
/// <c>KeyError: 'side'</c> - the same text the Python server produced.
/// </summary>
public sealed class Args
{
    readonly Dictionary<string, JsonElement> _values;

    public Args(IEnumerable<KeyValuePair<string, JsonElement>>? values = null) =>
        _values = values is null ? [] : new Dictionary<string, JsonElement>(values, StringComparer.Ordinal);

    public static Args From(JsonObject? json) => new(json is null ? null :
        json.Select(p => new KeyValuePair<string, JsonElement>(p.Key,
            JsonSerializer.Deserialize<JsonElement>(p.Value is null ? "null" : p.Value.ToJsonString()))));

    /// <summary>The names the call carried, in the order they arrived.</summary>
    public IEnumerable<string> Names => _values.Keys;

    public bool Has(string name) => _values.TryGetValue(name, out var v) && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    public JsonElement? Raw(string name) => Has(name) ? _values[name] : null;

    public JsonNode? Node(string name) => Raw(name) is { } e ? JsonNode.Parse(e.GetRawText()) : null;

    public string? Str(string name) => Raw(name) is { } e
        ? e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()
        : null;

    public string Need(string name) => Str(name) ?? throw new KeyNotFoundException($"'{name}'");

    public double? Num(string name) => Raw(name) is { } e
        ? e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.String => double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : throw new FormatException($"{name}: '{e.GetString()}' is not a number"),
            _ => throw new FormatException($"{name}: expected a number"),
        }
        : null;

    public int? Int(string name) => Num(name) is { } d ? (int)d : null;

    public bool Flag(string name) => Raw(name) is { } e && e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.GetDouble() != 0,
        JsonValueKind.String => bool.TryParse(e.GetString(), out var b) && b,
        _ => false,
    };

    public IReadOnlyList<string>? Strings(string name)
    {
        if (Raw(name) is not { } e) return null;
        if (e.ValueKind == JsonValueKind.String) return [e.GetString()!];
        if (e.ValueKind != JsonValueKind.Array) throw new FormatException($"{name}: expected an array of strings");
        return [.. e.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText())];
    }

    public TimeSpan Seconds(string name, double fallback) => TimeSpan.FromSeconds(Num(name) ?? fallback);
}
