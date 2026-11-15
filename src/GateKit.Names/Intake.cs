using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace GateKit.Names;

/// <summary>A candidate layout left out, and why.</summary>
public sealed record Refused(string File, string Component, string Reason);

/// <summary>What <see cref="Intake.Take"/> did with a folder of candidates.</summary>
public sealed record IntakeResult(List<string> Taken, List<Refused> Refused, List<string> AlreadyThere);

/// <summary>
/// Taking candidate layouts into the record: the files <c>gatekit symbols se-layouts</c> writes
/// from Script Extender's own declarations, for the types the record has no layout of yet.
///
/// A candidate goes in only as it would stand in the build: status <c>declared</c>, every field at
/// rung <c>extender</c> (or <c>unknown</c> where the extender's own name is a machine's, field_24), an <c>ext_name</c> (the in-tick comparison reads the type by it), the size
/// the game registers where <c>coverage.json</c> knows it, and the same checks <c>names generate</c>
/// holds every layout to - fields in order, no overlap, alignment, the size covered exactly. One
/// that fails any of them stays out and is named with the reason: a layout in the build that does
/// not hold together would stop the generator for everybody.
/// </summary>
public static class Intake
{
    public static IntakeResult Take(string coverageRoot, string candidates, string batch, SqliteConnection? symbols, bool write)
    {
        if (!Directory.Exists(candidates)) throw new NamesException($"no folder {candidates}");
        if (batch.Length == 0 || batch.IndexOfAny(['/', '\\', '.']) >= 0) throw new NamesException($"'{batch}' is not a batch folder name");
        var have = LayoutStore.All(coverageRoot).Select(f => f.Component).ToHashSet(StringComparer.Ordinal);
        var registered = RegisteredSizes(coverageRoot);
        var check = new Declarations("", symbols);
        string into = Path.Combine(coverageRoot, "layouts", batch);
        var result = new IntakeResult([], [], []);
        foreach (var path in Directory.EnumerateFiles(candidates, "*.json").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            JsonObject? json;
            try { json = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject; }
            catch (JsonException) { json = null; }
            if (json is null)
            {
                result.Refused.Add(new Refused(name, "?", "not a JSON object"));
                continue;
            }
            string component = LayoutFile.Text(json["component"]) ?? "?";
            if (have.Contains(component) || File.Exists(Path.Combine(into, name)))
            {
                result.AlreadyThere.Add(component);
                continue;
            }
            if (Problem(json, registered, check) is { } reason)
            {
                result.Refused.Add(new Refused(name, component, reason));
                continue;
            }
            result.Taken.Add(component);
            if (write) LayoutStore.Add(coverageRoot, batch, json);
        }
        return result;
    }

    static string? Problem(JsonObject json, Dictionary<string, long> registered, Declarations check)
    {
        string component = LayoutFile.Text(json["component"]) ?? "";
        if (component.Length == 0) return "no component";
        if (LayoutFile.Text(json["kind"]) == "system") return "a system, not a component";
        if (LayoutFile.Text(json["ext_name"]) is not { Length: > 0 }) return "no ext_name: the in-tick comparison could not read it";
        if (LayoutFile.Text(json["status"]) != "declared") return $"status {LayoutFile.Text(json["status"]) ?? "(none)"}, not declared";
        foreach (var field in json["fields"] as JsonArray ?? [])
            if (LayoutFile.Text(field?["source"]) is var source && source != "extender"
                && !(source == "unknown" && Tally.IsMachine(LayoutFile.Text(field?["name"]) ?? "")))
                return $"field {LayoutFile.Text(field?["name"])} has rung {source ?? "(none)"}, not extender";
        if (registered.TryGetValue(component, out long size) && size >= 0 && LayoutFile.Integer(json["size"]) != size)
            return $"size {LayoutFile.Integer(json["size"])}, the game registers {size}";
        try
        {
            check.Validate(json);
        }
        catch (LayoutError e)
        {
            return e.Message;
        }
        return null;
    }

    /// <summary>The sizes the game registers, from <c>coverage.json</c> of the record; none without it.</summary>
    static Dictionary<string, long> RegisteredSizes(string coverageRoot)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var index = Path.Combine(coverageRoot, "coverage.json");
        if (!File.Exists(index)) return sizes;
        foreach (var row in JsonNode.Parse(File.ReadAllBytes(index))?["components"] as JsonArray ?? [])
            if (LayoutFile.Text(row?["name"]) is { } name && LayoutFile.Integer(row?["totalSize"]) is { } size)
                sizes.TryAdd(name, size);
        return sizes;
    }
}
