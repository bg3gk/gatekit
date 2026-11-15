using System.Globalization;
using System.Text.Json.Nodes;
using MoonSharp.Interpreter;

namespace GateKit.Testing;

/// <summary>What the game answers (JSON) as Lua values, and back. A JSON object becomes a table
/// with its keys, an array a table from 1, null becomes nil - the same shapes the Python
/// tests saw as dicts and lists.</summary>
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

    /// <summary>A Lua value as JSON: a table with keys 1..n (and nothing else) is an array, any other
    /// table an object; functions and the like become their text.</summary>
    public static JsonNode? ToJson(DynValue value)
    {
        switch (value.Type)
        {
            case DataType.Nil:
            case DataType.Void:
                return null;
            case DataType.Boolean:
                return JsonValue.Create(value.Boolean);
            case DataType.Number:
                return Math.Abs(value.Number % 1) < 1e-12 && Math.Abs(value.Number) < 9e15
                    ? JsonValue.Create((long)value.Number) : JsonValue.Create(value.Number);
            case DataType.String:
                return JsonValue.Create(value.String);
            case DataType.Table:
                {
                    var table = value.Table;
                    var length = table.Length;
                    if (length > 0 && table.Pairs.Count() == length)
                    {
                        var array = new JsonArray();
                        for (var i = 1; i <= length; i++) array.Add(ToJson(table.Get(i)));
                        return array;
                    }
                    var map = new JsonObject();
                    foreach (var pair in table.Pairs) map[Describe(pair.Key)] = ToJson(pair.Value);
                    return map;
                }
            default:
                return JsonValue.Create(value.ToPrintString());
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
