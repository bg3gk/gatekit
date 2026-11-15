using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Json;

public static partial class JsonText
{
    /// <summary>The width a container must fit in to be written on one line.</summary>
    public const int Width = 100;

    /// <summary>UTF-8 without a byte order mark: how every file of ours is written.</summary>
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The canonical text of a document: a container that fits in <see cref="Width"/> columns on
    /// one line is written on one line (never the document itself, unless empty), anything else one
    /// member per line with an indent of two; keys in the order the schema of <paramref name="kind"/>
    /// lists them, others after them as they were; LF line ends and a final newline. Formatting the
    /// result again gives the same text.
    /// </summary>
    public static string Format(JsonNode node, string? kind = null, SchemaSet? set = null)
    {
        set ??= kind is null ? null : SchemaSet.Embedded;
        var schema = kind is null ? (SchemaSet.Node?)null : set!.RootOf(kind);
        var sb = new StringBuilder();
        Write(sb, node, 0, 0, last: true, schema, set, top: true);
        return sb.Append('\n').ToString();
    }

    /// <summary>The canonical text of a ledger row: one line, no spaces, keys in the schema's order; no newline.</summary>
    public static string FormatRow(JsonNode row, string? kind = null, SchemaSet? set = null)
    {
        set ??= kind is null ? null : SchemaSet.Embedded;
        var schema = kind is null ? (SchemaSet.Node?)null : set!.RootOf(kind);
        var sb = new StringBuilder();
        Inline(sb, row, compact: true, schema, set);
        return sb.ToString();
    }

    /// <summary>A value on one line however long: compact (<c>{"a":1}</c>, a ledger row) or spaced (<c>{"a": 1}</c>).</summary>
    public static string Inline(JsonNode? node, bool compact = true)
    {
        var sb = new StringBuilder();
        Inline(sb, node, compact, null, null);
        return sb.ToString();
    }

    static void Inline(StringBuilder sb, JsonNode? node, bool compact, SchemaSet.Node? schema, SchemaSet? set)
    {
        switch (node)
        {
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var key in set?.Order(schema, o) ?? o.Select(p => p.Key))
                {
                    if (!first) sb.Append(compact ? "," : ", ");
                    first = false;
                    Quote(sb, key);
                    sb.Append(compact ? ":" : ": ");
                    Inline(sb, o[key], compact, set?.Member(schema, o, key), set);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                var items = set?.Items(schema);
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(compact ? "," : ", ");
                    Inline(sb, a[i], compact, items, set);
                }
                sb.Append(']');
                break;
            default:
                Scalar(sb, node);
                break;
        }
    }

    static void Write(StringBuilder sb, JsonNode? node, int indent, int column, bool last, SchemaSet.Node? schema, SchemaSet? set, bool top)
    {
        if (node is not (JsonObject or JsonArray))
        {
            Scalar(sb, node);
            return;
        }
        var line = new StringBuilder();
        Inline(line, node, compact: false, schema, set);
        var count = node is JsonObject co ? co.Count : ((JsonArray)node).Count;
        if (count == 0 || (!top && column + line.Length + (last ? 0 : 1) <= Width))
        {
            sb.Append(line);
            return;
        }
        var pad = new string(' ', indent + 2);
        if (node is JsonObject o)
        {
            sb.Append("{\n");
            var keys = set?.Order(schema, o) ?? [.. o.Select(p => p.Key)];
            for (var i = 0; i < keys.Count; i++)
            {
                var start = sb.Length;
                sb.Append(pad);
                Quote(sb, keys[i]);
                sb.Append(": ");
                var isLast = i == keys.Count - 1;
                Write(sb, o[keys[i]], indent + 2, sb.Length - start, isLast, set?.Member(schema, o, keys[i]), set, top: false);
                if (!isLast) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(' ', indent).Append('}');
        }
        else
        {
            var a = (JsonArray)node;
            var items = set?.Items(schema);
            sb.Append("[\n");
            for (var i = 0; i < a.Count; i++)
            {
                sb.Append(pad);
                var isLast = i == a.Count - 1;
                Write(sb, a[i], indent + 2, indent + 2, isLast, items, set, top: false);
                if (!isLast) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(' ', indent).Append(']');
        }
    }

    static void Scalar(StringBuilder sb, JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            sb.Append("null");
            return;
        }
        switch (v.GetValueKind())
        {
            case JsonValueKind.String:
                Quote(sb, v.TryGetValue<string>(out var s) ? s : JsonSerializer.Deserialize<string>(v.ToJsonString())!);
                break;
            case JsonValueKind.Number:
                sb.Append(NumberText(v));
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    /// <summary>A string as JSON: only what must be escaped is (the quote, the backslash, control
    /// characters, the two line separators JavaScript breaks on, lone surrogates); everything else
    /// as itself.</summary>
    public static string Quote(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        Quote(sb, text);
        return sb.ToString();
    }

    static void Quote(StringBuilder sb, string text)
    {
        sb.Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        sb.Append(c).Append(text[++i]);
                    else if (char.IsSurrogate(c))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
