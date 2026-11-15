using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Json;

/// <summary>
/// The one way GateKit reads and writes its own JSON (docs/contracts/data-format.md):
/// strict text (no comments, no trailing commas, no duplicate keys, no byte order mark), numbers
/// kept exactly as written, errors as <c>file:line:column: what was expected</c>, one canonical
/// layout, whole-file writes that are atomic and ledger appends under a lock. Every file of a known
/// kind is held to its schema (<see cref="SchemaSet"/>) on the way in and on the way out.
/// </summary>
public static partial class JsonText
{
    /// <summary>Strict: the reader's defaults (comments and trailing commas refused), deep enough for
    /// any file of ours.</summary>
    internal static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 256 };

    static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    /// <summary>
    /// Reads <paramref name="text"/> strictly. <paramref name="source"/> names it in messages (a
    /// path, or "the answer of bg3_x"); <paramref name="firstLine"/> is the line of the source it
    /// starts on. Throws <see cref="JsonProblemException"/> with one problem at the first fault.
    /// </summary>
    public static ParsedJson Parse(string text, string source, int firstLine = 1) =>
        Parse(Encoding.UTF8.GetBytes(text), source, firstLine);

    /// <summary>Reads UTF-8 bytes strictly; see <see cref="Parse(string, string, int)"/>.</summary>
    public static ParsedJson Parse(byte[] bytes, string source, int firstLine = 1)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new JsonProblemException(new JsonProblem(source, firstLine, 1, "", "syntax",
                "a byte order mark: GateKit's files are UTF-8 without one (`gatekit json fmt` writes them so)"));
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        JsonNode? root = null;
        var stack = new Stack<JsonNode>();
        string? key = null;
        long keyAt = 0;
        try
        {
            while (reader.Read())
            {
                JsonNode? value;
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        key = reader.GetString()!;
                        keyAt = reader.TokenStartIndex;
                        if (((JsonObject)stack.Peek()).ContainsKey(key))
                            throw At(bytes, keyAt, source, firstLine, "duplicate",
                                $"the key \"{key}\" is written twice in one object; a reader would keep one of them silently - remove the other");
                        continue;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        stack.Pop();
                        continue;
                    case JsonTokenType.StartObject:
                        value = new JsonObject(NodeOptions);
                        break;
                    case JsonTokenType.StartArray:
                        value = new JsonArray(NodeOptions);
                        break;
                    case JsonTokenType.String:
                        value = JsonValue.Create(reader.GetString()!, NodeOptions);
                        break;
                    case JsonTokenType.Number:
                        // kept as its own text: a 64-bit integer, or one beyond, never passes through a double
                        value = JsonValue.Create(JsonElement.ParseValue(ref reader), NodeOptions);
                        break;
                    case JsonTokenType.True:
                        value = JsonValue.Create(true, NodeOptions);
                        break;
                    case JsonTokenType.False:
                        value = JsonValue.Create(false, NodeOptions);
                        break;
                    case JsonTokenType.Null:
                        value = null;
                        break;
                    default:
                        continue;
                }
                if (stack.Count == 0)
                {
                    root = value;
                    if (value is null)
                        throw At(bytes, reader.TokenStartIndex, source, firstLine, "type", "the document is only `null`; a file of ours holds an object or an array");
                }
                else if (stack.Peek() is JsonObject o) o.Add(key!, value);
                else ((JsonArray)stack.Peek()).Add(value);
                if (value is JsonObject or JsonArray) stack.Push(value);
            }
        }
        catch (JsonException e)
        {
            throw Translate(e, bytes, source, firstLine);
        }
        if (root is null)
            throw new JsonProblemException(new JsonProblem(source, firstLine, 1, "", "syntax", "the text is empty: expected an object or an array"));
        return new ParsedJson(root, source, bytes, firstLine);
    }

    static JsonProblemException At(byte[] bytes, long offset, string source, int firstLine, string rule, string what)
    {
        var (line, column) = ParsedJson.LineColumn(bytes, offset, firstLine);
        return new JsonProblemException(new JsonProblem(source, line, column, "", rule, what));
    }

    static readonly Regex Found = new(@"^'(?<c>[^']*)' is", RegexOptions.CultureInvariant);

    /// <summary>The reader's complaint in our words, at its line and column.</summary>
    static JsonProblemException Translate(JsonException e, byte[] bytes, string source, int firstLine)
    {
        var line = (int)(e.LineNumber ?? 0);
        var bytesIn = (int)(e.BytePositionInLine ?? 0);
        // the reader counts lines and bytes from 0; we say lines and characters from 1
        var lineStart = 0;
        for (int i = 0, seen = 0; i < bytes.Length && seen < line; i++)
            if (bytes[i] == (byte)'\n') { seen++; lineStart = i + 1; }
        var column = Encoding.UTF8.GetCharCount(bytes, lineStart, Math.Clamp(bytesIn, 0, bytes.Length - lineStart)) + 1;
        var message = e.Message;
        var cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        if (cut >= 0) message = message[..cut].TrimEnd();
        var found = Found.Match(message) is { Success: true } m ? m.Groups["c"].Value : null;
        string what;
        if (message.Contains("does not contain any JSON token", StringComparison.Ordinal))
            what = "the text is empty: expected an object or an array";
        else if (message.Contains("trailing comma", StringComparison.Ordinal))
            what = "a comma before the closing bracket: strict JSON has no trailing commas - remove it";
        else if (found == "/" || message.Contains("comment", StringComparison.OrdinalIgnoreCase))
            what = "a comment: strict JSON has none - write the note as a field (`about`, `rule`, `note`, `why`)";
        else if (found == "0x0A" || found == "0x0D")
            what = "a line break inside a string: the closing quote is missing, or write the break as \\n";
        else if (message.Contains("reached end of data", StringComparison.Ordinal) || message.Contains("Expected depth to be zero", StringComparison.Ordinal))
            what = "the text ends inside an unfinished object or array: the file is cut off, or a closing bracket is missing";
        else if (message.Contains("is invalid after a value", StringComparison.Ordinal))
            what = $"expected ',' or a closing bracket after a value, found '{found}' (a missing comma, or a quote inside a string that is not written as \\\")";
        else if (message.Contains("after a single JSON value", StringComparison.Ordinal))
            what = $"a second value after the end of the document, found '{found}': one value per file (per line in a .jsonl)";
        else if (message.Contains("Expected a ':'", StringComparison.Ordinal))
            what = $"expected ':' after the key, found '{found}'";
        else if (message.Contains("start of a property name", StringComparison.Ordinal))
            what = $"expected a key in double quotes, found '{found}'";
        else if (message.Contains("invalid start of a value", StringComparison.Ordinal))
            what = $"expected a value (a string in double quotes, a number, true, false, an object or an array), found '{found}'";
        else if (message.Contains("maximum configured depth", StringComparison.Ordinal))
            what = $"nested deeper than {ReaderOptions.MaxDepth} levels";
        else
            what = message;
        return new JsonProblemException(new JsonProblem(source, firstLine + line, column, "", "syntax", what));
    }

    /// <summary>The text of a string, if the node is one.</summary>
    public static string? Text(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The kind of a node as a schema names it: object, array, string, integer, number, boolean, null.</summary>
    public static string KindOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => IsInteger(NumberText(v)) ? "integer" : "number",
            _ => "null",
        },
        _ => "null",
    };

    /// <summary>A number as written: the text it was read from, or the shortest that round-trips.</summary>
    internal static string NumberText(JsonValue v) => v.ToJsonString();

    internal static bool IsInteger(string number) => number.Length > 0 && !number.Contains('.') && !number.Contains('e') && !number.Contains('E');

    /// <summary>Compares two JSON numbers by value, exactly: integers of any size as integers, others as decimals where they fit.</summary>
    internal static int CompareNumbers(string a, string b)
    {
        if (IsInteger(a) && IsInteger(b))
            return System.Numerics.BigInteger.Parse(a, CultureInfo.InvariantCulture).CompareTo(System.Numerics.BigInteger.Parse(b, CultureInfo.InvariantCulture));
        if (decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var da) && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            return da.CompareTo(db);
        return double.Parse(a, CultureInfo.InvariantCulture).CompareTo(double.Parse(b, CultureInfo.InvariantCulture));
    }
}
