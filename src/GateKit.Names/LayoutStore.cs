using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Names;

/// <summary>One layout file of the record: a component type and what is known of its fields.</summary>
public sealed class LayoutFile(string path, JsonObject json, byte[] bytes)
{
    public string Path { get; } = path;
    public JsonObject Json { get; } = json;
    /// <summary>The file as it was read; what a write compares against before replacing it.</summary>
    public byte[] Bytes { get; } = bytes;

    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>The batch the layout was written in: its folder under <c>layouts</c>, kept in the registry's survey.</summary>
    public string Batch { get; init; } = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
    public string Component => Text(Json["component"]) ?? "?";
    public string? Status => Text(Json["status"]);
    public bool IsSystem => Text(Json["kind"]) == "system";
    /// <summary>In the Script Extender build: confirmed against live instances, or declared from the code.</summary>
    public bool InBuild => Status is "confirmed" or "declared";
    public JsonArray Fields => Json["fields"] as JsonArray ?? [];

    public static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? Integer(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    /// <summary>What Python calls truthy: a layout marks things with <c>true</c>, a reason, or nothing.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        _ => true,
    };
}

/// <summary>
/// Reads the layout files and writes one back. The files are committed and were written by a tool
/// with one fixed shape (two spaces, no escaping of non-ASCII text, line feeds); the writer here
/// reproduces that shape exactly, so naming one field changes the lines of that field and nothing
/// else. A test holds it to that over every committed file.
/// </summary>
public static class LayoutStore
{
    static readonly JsonSerializerOptions Shape = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The registry beside the record when it holds the layouts' types, else null (the layout files are read then).</summary>
    public static GateKit.Symbols.Registry.RegistryStore? Registry(string coverageRoot)
    {
        var store = GateKit.Symbols.Registry.RegistryStore.Of(coverageRoot);
        return Directory.Exists(Path.Combine(store.Root, "types")) ? store : null;
    }

    /// <summary>
    /// Every layout, batch by batch and name by name: from the registry's types that carry a survey
    /// when the registry holds them (registry/README.md), else from the files under <c>layouts</c>.
    /// A file that cannot be read is handed to <paramref name="unreadable"/> and left out.
    /// </summary>
    public static List<LayoutFile> All(string coverageRoot, Action<string>? unreadable = null)
    {
        if (Registry(coverageRoot) is { } store)
        {
            var list = new List<(string Batch, LayoutFile File)>();
            foreach (var path in Directory.EnumerateFiles(Path.Combine(store.Root, "types"), "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    var entry = GateKit.Json.JsonText.Parse(bytes, path).Root as JsonObject;
                    if (entry is null || !GateKit.Symbols.Registry.RegistryForms.IsLayout(entry)) continue;
                    var batch = GateKit.Symbols.Registry.RegistryForms.BatchOf(entry);
                    list.Add((batch, new LayoutFile(path, GateKit.Symbols.Registry.RegistryForms.ToLayout(entry), bytes) { Batch = batch }));
                }
                catch (Exception e) when (e is GateKit.Json.JsonProblemException or IOException or UnauthorizedAccessException)
                {
                    unreadable?.Invoke(path);
                }
            }
            return [.. list.OrderBy(l => l.Batch, StringComparer.Ordinal).ThenBy(l => LayoutName(l.File.Component), StringComparer.Ordinal).Select(l => l.File)];
        }
        var folder = Path.Combine(coverageRoot, "layouts");
        var files = new List<LayoutFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
                     .OrderBy(p => Path.GetRelativePath(folder, p).Replace('\\', '/'), StringComparer.Ordinal))
        {
            var file = Read(path);
            if (file is null) unreadable?.Invoke(path);
            else files.Add(file);
        }
        return files;
    }

    public static LayoutFile? Read(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return JsonNode.Parse(bytes) is JsonObject json ? new LayoutFile(path, json, bytes) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The one layout of a component: its exact name, else the only one whose name ends with the text.</summary>
    public static LayoutFile Find(IReadOnlyList<LayoutFile> all, string component)
    {
        var found = all.Where(f => !f.IsSystem && f.Component == component).ToList();
        if (found.Count == 0)
            found = all.Where(f => !f.IsSystem && f.Component.EndsWith("::" + component, StringComparison.Ordinal)).ToList();
        return found.Count switch
        {
            1 => found[0],
            0 => throw new NamesException($"no layout is written down for {component}"),
            _ => throw new NamesException($"{component} names {found.Count} layouts; say which: "
                + string.Join(", ", found.Select(f => $"{f.Component} ({f.Name})").Take(8))),
        };
    }

    /// <summary>The file name a layout had under <c>layouts</c>: its component with dots for <c>::</c>.</summary>
    public static string LayoutName(string component) => component.Replace("::", ".") + ".json";

    /// <summary>Whether a layout was read from the registry (a file under registry/types/).</summary>
    public static bool IsRegistryFile(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').Contains("/" + GateKit.Symbols.Registry.RegistryStore.FolderName + "/types/", StringComparison.Ordinal);

    static string RegistryRootOf(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        var at = full.LastIndexOf("/" + GateKit.Symbols.Registry.RegistryStore.FolderName + "/types/", StringComparison.Ordinal);
        return full[..(at + 1 + GateKit.Symbols.Registry.RegistryStore.FolderName.Length)];
    }

    /// <summary>A new layout into the record: a type entry of the registry when it holds the layouts, else a file of <c>layouts/&lt;batch&gt;</c>.
    /// Returns the path written.</summary>
    public static string Add(string coverageRoot, string batch, JsonObject layout)
    {
        if (Registry(coverageRoot) is { } store)
        {
            var entry = GateKit.Symbols.Registry.RegistryForms.FromLayout(layout, batch);
            if (GateKit.Symbols.Registry.RegistryCheck.Refusals(entry) is { Count: > 0 } refused)
                throw new NamesException($"{GateKit.Json.JsonText.Text(layout["component"])}: not written - " + string.Join("; ", refused));
            return store.Write(GateKit.Symbols.Registry.EntryKind.Type, entry, create: true);
        }
        var into = Path.Combine(coverageRoot, "layouts", batch);
        Directory.CreateDirectory(into);
        var path = Path.Combine(into, LayoutName(GateKit.Json.JsonText.Text(layout["component"])!));
        File.WriteAllBytes(path, Serialize(layout, finalNewline: true));
        return path;
    }

    /// <summary>The text of a layout in the shape the record keeps; a final newline only where the file had one.</summary>
    public static byte[] Serialize(JsonObject json, bool finalNewline)
    {
        var text = json.ToJsonString(Shape).ReplaceLineEndings("\n");
        return Encoding.UTF8.GetBytes(finalNewline ? text + "\n" : text);
    }

    /// <summary>
    /// Puts a changed layout in the place of the file it was read from - whole or not at all, and
    /// only if nobody else changed the file meanwhile. That is detection of a lost update and an
    /// atomic replacement, not a lock: two writers of one file in the same instant can still meet.
    /// Sessions keep to their own files and their own worktrees; this is what is left to guard.
    /// </summary>
    /// <summary>The registry entry a layout read from the registry becomes when written back.</summary>
    static JsonObject Merged(LayoutFile file)
    {
        var entry = GateKit.Json.JsonText.Parse(file.Bytes, file.Path).Root!.AsObject();
        var merged = GateKit.Symbols.Registry.RegistryForms.MergeLayout(entry, file.Json);
        merged.Remove("schema");
        if (GateKit.Symbols.Registry.RegistryCheck.Refusals(merged, entry) is { Count: > 0 } refused)
            throw new NamesException($"{file.Component}: not written - " + string.Join("; ", refused));
        return merged;
    }

    /// <summary>The bytes <see cref="Replace"/> would write for a layout: the registry entry's canonical text, or the layout file's shape.</summary>
    public static byte[] Render(LayoutFile file)
    {
        if (!IsRegistryFile(file.Path)) return Serialize(file.Json, file.Bytes.Length > 0 && file.Bytes[^1] == (byte)'\n');
        var merged = Merged(file);
        var ordered = new JsonObject { ["schema"] = GateKit.Symbols.Registry.RegistryStore.Schema(GateKit.Symbols.Registry.EntryKind.Type) };
        foreach (var (key, value) in merged.ToList())
        {
            merged.Remove(key);
            ordered[key] = value;
        }
        return Encoding.UTF8.GetBytes(GateKit.Json.JsonText.Format(ordered, GateKit.Symbols.Registry.RegistryStore.Schema(GateKit.Symbols.Registry.EntryKind.Type)));
    }

    public static void Replace(LayoutFile file)
    {
        if (IsRegistryFile(file.Path))
        {
            var store = new GateKit.Symbols.Registry.RegistryStore(RegistryRootOf(file.Path));
            using var held = GateKit.Json.JsonLock.Take(file.Path);
            if (!SHA256.HashData(File.ReadAllBytes(file.Path)).AsSpan().SequenceEqual(SHA256.HashData(file.Bytes)))
                throw new NamesException($"{file.Name} changed while this command was running; nothing was written, run it again");
            store.Write(GateKit.Symbols.Registry.EntryKind.Type, Merged(file), create: false);
            return;
        }
        var bytes = Serialize(file.Json, file.Bytes.Length > 0 && file.Bytes[^1] == (byte)'\n');
        var temporary = file.Path + ".tmp-" + Environment.ProcessId;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (!SHA256.HashData(File.ReadAllBytes(file.Path)).AsSpan().SequenceEqual(SHA256.HashData(file.Bytes)))
                throw new NamesException($"{file.Name} changed while this command was running; nothing was written, run it again");
            File.Move(temporary, file.Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
