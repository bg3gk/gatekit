using System.Globalization;
using System.Text.Json.Nodes;
using MoonSharp.Interpreter;

namespace GateKit.Scenarios;

/// <summary>What the game answers (JSON) as Lua values, and back. A JSON object becomes a table
/// with its keys, an array a table from 1, null becomes nil - the same shapes the Python
/// scenarios saw as dicts and lists.</summary>
public static class LuaValues
{
    public static DynValue ToLua(Script script, JsonNode? node)
    {
        switch (node)
        {
            case null:
                return DynValue.Nil;
            case JsonArray array:
                {
                    var table = new Table(script);
                    for (var i = 0; i < array.Count; i++) table.Set(i + 1, ToLua(script, array[i]));
                    return DynValue.NewTable(table);
                }
            case JsonObject map:
                {
                    var table = new Table(script);
                    foreach (var (key, value) in map) table.Set(key, ToLua(script, value));
                    return DynValue.NewTable(table);
                }
            case JsonValue value:
                {
                    if (value.TryGetValue<bool>(out var flag)) return DynValue.NewBoolean(flag);
                    if (value.TryGetValue<double>(out var number)) return DynValue.NewNumber(number);
                    if (value.TryGetValue<string>(out var text)) return DynValue.NewString(text);
                    return DynValue.NewString(value.ToJsonString());
                }
            default:
                return DynValue.NewString(node.ToJsonString());
        }
    }

    /// <summary>A Lua value as text, the way the prelude's fmt() renders it (for messages).</summary>
    public static string Describe(DynValue value) => value.Type switch
    {
        DataType.Nil or DataType.Void => "nil",
        DataType.Boolean => value.Boolean ? "true" : "false",
        DataType.Number => value.Number.ToString("0.######", CultureInfo.InvariantCulture),
        DataType.String => value.String,
        DataType.Table => "{" + string.Join(", ", value.Table.Pairs.Select(p => $"{Describe(p.Key)} = {Describe(p.Value)}")) + "}",
        _ => value.ToPrintString(),
    };

    public static string? OptionalString(Table? options, string key) =>
        options?.Get(key) is { Type: DataType.String } value ? value.String : null;

    public static double? OptionalNumber(Table? options, string key) =>
        options?.Get(key) is { Type: DataType.Number } value ? value.Number : null;

    public static bool Flag(Table? options, string key) =>
        options?.Get(key) is { } value && value.Type != DataType.Nil && value.CastToBool();

    /// <summary>A list of strings from a Lua list, or null when the key is not there.</summary>
    public static IReadOnlyList<string>? OptionalList(Table? options, string key)
    {
        if (options?.Get(key) is not { Type: DataType.Table } value) return null;
        return [.. value.Table.Values.Select(v => v.CastToString() ?? "")];
    }
}
