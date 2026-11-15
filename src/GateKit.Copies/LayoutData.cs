using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Copies;

/// <summary>
/// The components' layouts as data for the bridge, so a running copy reads a component's field by
/// its name - GK.Probe.Field(entity, component, field) - through the raw read the bridge already
/// has, with no extender rebuilt: the registry's types made from layouts (<c>registry/types</c>, the
/// ones that carry a <c>survey</c>), else the layout files of the record. Every field with an offset
/// and a type is given, whatever its rung: the probe reads memory, it names no code. A name reaches
/// a copy when the file is written again: at `up` and at every `reload` / `bridge-reload`. The file
/// is <c>&lt;channel&gt;/layouts.json</c>: <c>{"version": 1, "components": {"&lt;engine name&gt;":
/// {"size": n, "status": "...", "fields": {"&lt;name&gt;": [offset, "type"]}}}}</c>.
/// </summary>
public static class LayoutData
{
    public const string FileName = "layouts.json";

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The bridge's data built from every layout under <paramref name="layouts"/>: a type of
    /// the registry made from a layout, or a layout file; a file that does not read, a system, or a
    /// field without an offset or a type is left out.</summary>
    public static JsonObject Build(string layouts)
    {
        var components = new JsonObject();
        if (Directory.Exists(layouts))
        {
            foreach (var path in Directory.EnumerateFiles(layouts, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                JsonObject? file;
                try { file = JsonNode.Parse(File.ReadAllText(path))?.AsObject(); }
                catch (JsonException) { continue; }
                catch (InvalidOperationException) { continue; }
                if (file is null) continue;
                // a type of the registry: its id, its concept, the layout's own keys kept under survey
                if (Text(file["schema"]) == "registry/type/1")
                {
                    if (file["survey"] is not JsonObject survey) continue;
                    var entry = new JsonObject { ["component"] = Text(file["id"]), ["fields"] = file["fields"]?.DeepClone(), ["size"] = file["size"]?.DeepClone(), ["status"] = survey["status"]?.DeepClone() };
                    if (Text(file["concept"]?["kind"]) == "system") entry["kind"] = "system";
                    file = entry;
                }
                if (Text(file["component"]) is not { } component) continue;
                if (Text(file["kind"]) == "system") continue;
                var fields = new JsonObject();
                foreach (var entry in file["fields"] as JsonArray ?? [])
                {
                    if (entry is not JsonObject field) continue;
                    if (field["name"] is not JsonValue n || !n.TryGetValue<string>(out var name)) continue;
                    if (field["type"] is not JsonValue t || !t.TryGetValue<string>(out var type)) continue;
                    if (field["offset"] is not JsonValue o || !o.TryGetValue<long>(out var offset)) continue;
                    if (name.StartsWith("_Pad", StringComparison.Ordinal) || fields.ContainsKey(name)) continue;
                    fields[name] = new JsonArray(offset, type);
                }
                components[component] = new JsonObject
                {
                    ["size"] = file["size"] is JsonValue s && s.TryGetValue<long>(out var size) ? size : 0,
                    ["status"] = file["status"] is JsonValue st && st.TryGetValue<string>(out var status) ? status : "hypothesis",
                    ["fields"] = fields,
                };
            }
        }
        return new JsonObject { ["version"] = 1, ["components"] = components };
    }

    /// <summary>The folder the layouts are read from: the registry's types, else the record's layout files; null with neither.</summary>
    public static string? Folder(string repository)
    {
        var registry = Path.Combine(repository, "registry", "types");
        if (Directory.Exists(registry)) return registry;
        var layouts = Path.Combine(repository, "research", "coverage", "layouts");
        return Directory.Exists(layouts) ? layouts : null;
    }

    /// <summary>Writes the data into the bridge's channel; returns how many components it holds, or
    /// null when the checkout has no layouts (an installed GateKit: names then come from the extender).</summary>
    public static int? Write(string? repository, string channel)
    {
        if (repository is null || Folder(repository) is not { } layouts) return null;
        var data = Build(layouts);
        Directory.CreateDirectory(channel);
        var target = Path.Combine(channel, FileName);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, data.ToJsonString(), new System.Text.UTF8Encoding(false));
        File.Move(temporary, target, overwrite: true);
        return data["components"]!.AsObject().Count;
    }
}
