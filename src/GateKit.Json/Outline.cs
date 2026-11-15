using System.Text.Json.Nodes;

namespace GateKit.Json;

/// <summary>
/// Any JSON value laid out for a person: an object as aligned <c>key  value</c> lines with its
/// containers below their key, an array of objects as a table (columns in the order keys first
/// appear), other arrays one item a line. Nothing here knows a kind of file.
/// </summary>
public static class Outline
{
    /// <summary>The widest a table cell grows before it is cut with "...".</summary>
    public const int Cell = 40;

    public static void Print(TextWriter output, JsonNode? node, int rows = 50) => Write(output, node, "", rows);

    static void Write(TextWriter output, JsonNode? node, string indent, int rows)
    {
        switch (node)
        {
            case JsonObject o when o.Count == 0:
                output.WriteLine(indent + "{}");
                break;
            case JsonObject o:
                var width = o.Where(p => IsScalar(p.Value)).Select(p => p.Key.Length).DefaultIfEmpty(0).Max();
                foreach (var (key, value) in o)
                {
                    if (IsScalar(value))
                    {
                        output.WriteLine(indent + key.PadRight(width) + "  " + Scalar(value));
                        continue;
                    }
                    output.WriteLine(indent + key + Count(value));
                    Write(output, value, indent + "  ", rows);
                }
                break;
            case JsonArray a when a.Count == 0:
                output.WriteLine(indent + "[]");
                break;
            case JsonArray a when a.All(i => i is JsonObject):
                Table(output, a, indent, rows);
                break;
            case JsonArray a:
                var shown = 0;
                foreach (var item in a)
                {
                    if (shown++ == rows) { output.WriteLine($"{indent}... {a.Count - rows} more"); break; }
                    if (IsScalar(item)) output.WriteLine(indent + "- " + Scalar(item));
                    else
                    {
                        output.WriteLine(indent + "-" + Count(item));
                        Write(output, item, indent + "  ", rows);
                    }
                }
                break;
            default:
                output.WriteLine(indent + Scalar(node));
                break;
        }
    }

    static void Table(TextWriter output, JsonArray rows, string indent, int limit)
    {
        var columns = new List<string>();
        foreach (var row in rows.OfType<JsonObject>())
            foreach (var (key, _) in row)
                if (!columns.Contains(key)) columns.Add(key);
        var shown = rows.OfType<JsonObject>().Take(limit).ToList();
        var cells = shown.Select(r => columns.Select(c => Cut(r.TryGetPropertyValue(c, out var v) ? CellText(v) : "")).ToArray()).ToList();
        var widths = columns.Select((c, i) => Math.Max(c.Length, cells.Select(r => r[i].Length).DefaultIfEmpty(0).Max())).ToArray();
        string Line(IEnumerable<string> values) => indent + string.Join("  ", values.Select((v, i) => v.PadRight(widths[i]))).TrimEnd();
        output.WriteLine(Line(columns));
        output.WriteLine(Line(widths.Select(w => new string('-', w))));
        foreach (var row in cells) output.WriteLine(Line(row));
        if (rows.Count > shown.Count) output.WriteLine($"{indent}... {rows.Count - shown.Count} more rows ({rows.Count} in all)");
    }

    static string CellText(JsonNode? value) => IsScalar(value) ? Scalar(value) : JsonText.Inline(value, compact: false);

    static string Cut(string text)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length > Cell ? text[..(Cell - 3)] + "..." : text;
    }

    static bool IsScalar(JsonNode? node) => node is not (JsonObject or JsonArray) || (node is JsonObject { Count: 0 } or JsonArray { Count: 0 });

    static string Count(JsonNode? node) => node switch
    {
        JsonArray a => $"  ({a.Count})",
        _ => "",
    };

    /// <summary>A scalar as a person reads it: text without quotes, the rest as JSON.</summary>
    static string Scalar(JsonNode? node) => JsonText.Text(node) is { } text ? text.ReplaceLineEndings(" / ") : JsonText.Inline(node, compact: false);
}
