using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Copies;

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

    /// <summary>Writes beside the file and moves over it: a reader in another process sees the old
    /// text or the new, never half of one.</summary>
    public static void WriteText(string path, string text)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = $"{full}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, text, Utf8);
        try { File.Move(temporary, full, overwrite: true); }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Reads the object, applies <paramref name="change"/>, writes it back - under the
    /// file's cross-process lock, so two processes editing it at once both keep their change.</summary>
    public static void Edit(string path, Action<JsonObject> change)
    {
        using var held = GateKit.Home.SharedFile.Lock(path);
        var value = ReadObject(path);
        change(value);
        WriteObject(path, value);
    }
}
