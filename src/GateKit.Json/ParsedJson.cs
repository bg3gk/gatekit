using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Json;

/// <summary>
/// A document as it was read: its value, where it came from, and its text, so that anything said
/// about a value later - a schema's complaint, a caller's own check - can name the line and column
/// it stands on. Positions are worked out only when asked for (reading pays nothing for them).
/// </summary>
public sealed class ParsedJson
{
    readonly byte[] _bytes;

    internal ParsedJson(JsonNode root, string source, byte[] bytes, int firstLine)
    {
        Root = root;
        Source = source;
        _bytes = bytes;
        FirstLine = firstLine;
    }

    /// <summary>The value. Never null: a document that is only <c>null</c> is refused.</summary>
    public JsonNode Root { get; }

    /// <summary>What the text was called in messages: a path, or a name the caller gave.</summary>
    public string Source { get; }

    /// <summary>The line of the source the text starts on (a ledger row is one line of its file).</summary>
    public int FirstLine { get; }

    /// <summary>The line and column of the value at <paramref name="pointer"/> (RFC 6901, "" for the
    /// whole document); for a member of an object, of its key. The nearest enclosing value that
    /// exists when the pointer names something absent (a missing field is reported at its object).</summary>
    public (int Line, int Column) PositionOf(string pointer)
    {
        var target = JsonPointer.Split(pointer);
        while (true)
        {
            if (Locate(target) is { } at) return LineColumn(_bytes, at, FirstLine);
            if (target.Count == 0) return (FirstLine, 1);
            target = target.Take(target.Count - 1).ToList();
        }
    }

    /// <summary>A problem at a pointer of this document, placed at its line and column.</summary>
    public JsonProblem Problem(string pointer, string rule, string what)
    {
        var (line, column) = PositionOf(pointer);
        return new JsonProblem(Source, line, column, pointer, rule, what);
    }

    long? Locate(IReadOnlyList<string> target)
    {
        var reader = new Utf8JsonReader(_bytes, JsonText.ReaderOptions);
        var path = new List<string>();
        var stack = new Stack<(bool Array, int Index)>();
        string? key = null;
        long keyAt = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    key = reader.GetString();
                    keyAt = reader.TokenStartIndex;
                    continue;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    stack.Pop();
                    if (stack.Count > 0) path.RemoveAt(path.Count - 1);
                    continue;
            }
            // the start of a value
            string? segment = null;
            long at = reader.TokenStartIndex;
            if (stack.Count > 0)
            {
                var (array, index) = stack.Pop();
                if (array)
                {
                    segment = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    stack.Push((true, index + 1));
                }
                else
                {
                    segment = key;
                    at = keyAt;
                    stack.Push((false, 0));
                }
            }
            var depth = path.Count + (segment is null ? 0 : 1);
            if (depth == target.Count && Matches(path, segment, target)) return at;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                if (segment is not null) path.Add(segment);
                stack.Push((reader.TokenType == JsonTokenType.StartArray, 0));
            }
        }
        return null;
    }

    static bool Matches(List<string> path, string? last, IReadOnlyList<string> target)
    {
        for (var i = 0; i < path.Count; i++)
            if (path[i] != target[i]) return false;
        return last is null || target[^1] == last;
    }

    /// <summary>The 1-based line and column (in characters) of a byte offset of UTF-8 text.</summary>
    internal static (int Line, int Column) LineColumn(byte[] bytes, long offset, int firstLine)
    {
        var line = firstLine;
        var lineStart = 0;
        var end = (int)Math.Min(offset, bytes.Length);
        for (var i = 0; i < end; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        return (line, Encoding.UTF8.GetCharCount(bytes, lineStart, end - lineStart) + 1);
    }
}

/// <summary>JSON pointers (RFC 6901): <c>/inventory/0/item</c>.</summary>
public static class JsonPointer
{
    public static string Append(string pointer, string key) => pointer + "/" + key.Replace("~", "~0").Replace("/", "~1");

    public static string Append(string pointer, int index) => pointer + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static List<string> Split(string pointer)
    {
        if (pointer.Length == 0) return [];
        if (pointer[0] != '/') throw new ArgumentException($"a JSON pointer starts with '/', not {pointer}");
        return [.. pointer[1..].Split('/').Select(s => s.Replace("~1", "/").Replace("~0", "~"))];
    }

    /// <summary>The value at <paramref name="pointer"/>, or null when nothing is there.</summary>
    public static JsonNode? Find(JsonNode? root, string pointer)
    {
        var node = root;
        foreach (var segment in Split(pointer))
        {
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(segment, out var v) ? v : null,
                JsonArray a => int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) && i < a.Count ? a[i] : null,
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>The pointer with every array index written as <c>*</c>: the same place in every row.</summary>
    public static string Generalize(string pointer) =>
        string.Join("/", pointer.Split('/').Select(s => s.Length > 0 && s.All(char.IsAsciiDigit) ? "*" : s));
}
