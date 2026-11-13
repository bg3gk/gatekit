using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Stand;

/// <summary>Small JSON settings files of the game and Script Extender: read leniently, written
/// indented by 4, UTF-8 without a byte order mark.</summary>
public static class JsonFiles
{
    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        IndentSize = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The object in the file; a missing or broken file (or one with a BOM) gives an empty one.</summary>
    public static JsonObject ReadObject(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static string Format(JsonNode node) => node.ToJsonString(Indented);

    public static void WriteObject(string path, JsonObject value) => WriteText(path, Format(value));

    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text, Utf8);
    }

    /// <summary>Reads the object, applies <paramref name="change"/>, writes it back.</summary>
    public static void Edit(string path, Action<JsonObject> change)
    {
        var value = ReadObject(path);
        change(value);
        WriteObject(path, value);
    }
}
