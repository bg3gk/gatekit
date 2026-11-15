using System.Text.Json.Nodes;

namespace GateKit.Bridge;

/// <summary>
/// Reading a Lua table that arrived as JSON. Lua has one kind of table for lists and for records,
/// and Script Extender writes an empty one as <c>[]</c>: whether it was an empty list or a record
/// with no fields set is known only to the reader. Every host reader of an answer from Lua goes
/// through these, so that an empty table is what the reader expects, and a table of the other
/// kind is an error that says what came instead.
/// </summary>
public static class LuaTable
{
    /// <summary>The table as a record of fields: an object, or an empty table as an empty object;
    /// null for nothing. A non-empty list or a plain value is a <see cref="LuaShapeException"/>.</summary>
    public static JsonObject? AsFields(this JsonNode? node) => node switch
    {
        null => null,
        JsonObject fields => fields,
        JsonArray { Count: 0 } => [],
        _ => throw new LuaShapeException("a table of fields", node),
    };

    /// <summary>One field of a record (null when it is not set, or when the table is empty or nothing).</summary>
    public static JsonNode? Field(this JsonNode? node, string name) => node.AsFields()?[name];

    /// <summary>The table as a list: an array, or an empty table as an empty array; null for
    /// nothing. A record with fields or a plain value is a <see cref="LuaShapeException"/>.</summary>
    public static JsonArray? AsItems(this JsonNode? node) => node switch
    {
        null => null,
        JsonArray items => items,
        JsonObject { Count: 0 } => [],
        _ => throw new LuaShapeException("a list", node),
    };
}

/// <summary>An answer from Lua was not the kind of table its reader expects.</summary>
public sealed class LuaShapeException(string expected, JsonNode got)
    : InvalidOperationException($"expected {expected} from Lua, got {Describe(got)}")
{
    static string Describe(JsonNode got) => got switch
    {
        JsonArray list => $"a list of {list.Count}",
        JsonObject fields => $"a table with fields {string.Join(", ", fields.Select(f => f.Key).Take(5))}",
        _ => $"the value {Cut(got.ToString())}",
    };

    static string Cut(string text) => text.Length <= 80 ? text : text[..80] + "...";
}
