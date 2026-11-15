using System.CommandLine;
using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Symbols.Registry;

/// <summary>
/// The cut between what may be given to the decompiler as a real name or type and what is only
/// shown beside the code (registry/README.md, "Rungs"): for one binary and build, the functions of
/// its table on an applied rung, the types on an applied rung, and the members of any type on one.
/// Nothing below the line is in it.
/// </summary>
public static class Applied
{
    public static IEnumerable<JsonObject> Of(RegistryStore store, string binary, string build)
    {
        var places = store.Table(build, binary).ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var entry in store.All().OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var json = entry.Json;
            if (entry.Kind is EntryKind.Function or EntryKind.Global)
            {
                if (!Rungs.IsApplied(JsonText.Text(json["rung"])) || !places.TryGetValue(entry.Id, out var place)) continue;
                var line = new JsonObject { ["kind"] = RegistryStore.Name(entry.Kind), ["id"] = entry.Id, ["rva"] = RegistryStore.Hex(place.Rva), ["rung"] = JsonText.Text(json["rung"]) };
                var variables = (json["parameters"] as JsonArray ?? []).Concat(json["locals"] as JsonArray ?? []).OfType<JsonObject>()
                    .Where(v => Rungs.IsApplied(JsonText.Text(v["rung"]) ?? JsonText.Text(json["rung"])) && JsonText.Text(v["storage"]) != "unresolved")
                    .Select(v =>
                    {
                        var o = new JsonObject { ["storage"] = JsonText.Text(v["storage"]), ["name"] = JsonText.Text(v["name"]), ["type"] = v["type"]?.DeepClone() };
                        if (JsonText.Text(v["type_id"]) is { } typeId) o["type_id"] = typeId;
                        return (JsonNode)o;
                    })
                    .ToList();
                if (variables.Count > 0) line["variables"] = new JsonArray([.. variables]);
                var ties = (json["ties"] as JsonArray ?? []).OfType<JsonObject>().Where(t => Rungs.IsApplied(JsonText.Text(t["rung"]) ?? JsonText.Text(json["rung"])))
                    .Select(t => (JsonNode)new JsonObject { ["type"] = JsonText.Text(t["type"]), ["via"] = JsonText.Text(t["via"]) }).ToList();
                if (ties.Count > 0) line["ties"] = new JsonArray([.. ties]);
                yield return line;
                continue;
            }
            var typeApplied = Rungs.IsApplied(JsonText.Text(json["rung"]));
            var fields = (json["fields"] as JsonArray ?? []).OfType<JsonObject>().Where(f => Rungs.IsApplied(JsonText.Text(f["rung"])))
                .Select(f =>
                {
                    var o = new JsonObject { ["offset"] = f["offset"]!.DeepClone(), ["name"] = JsonText.Text(f["name"]), ["rung"] = JsonText.Text(f["rung"]) };
                    if (f["type"] is not null) o["type"] = f["type"]!.DeepClone();
                    return (JsonNode)o;
                }).ToList();
            if (!typeApplied && fields.Count == 0) continue;
            var type = new JsonObject { ["kind"] = "type", ["id"] = entry.Id };
            if (json["size"] is JsonValue size) type["size"] = size.DeepClone();
            type["rung"] = JsonText.Text(json["rung"]) ?? "members only";
            if (fields.Count > 0) type["fields"] = new JsonArray([.. fields]);
            yield return type;
        }
    }
}

/// <summary>`gatekit refs <address>`: every place in the repository that cites an id or a test.</summary>
public static class RefsCommand
{
    public static Command CreateCommand()
    {
        var address = new Argument<string>("address") { Description = "a registry id (esv::RollSystem::RollDice), a test's path (combat/attack/ranged), with or without code: or test:" };
        var coverage = new Option<string?>("--coverage") { Description = "the coverage record beside the registry (default: the repository's)" };
        var c = new Command("refs", "every place that cites an id of the registry or a test: prefixed mentions in the repository's text and typed fields of the registry's entries; an id's members count") { address, coverage };
        c.SetAction(p =>
        {
            if (CopyFree.Refuse(p, "refs") is not 0 and var refused) return refused;
            var output = p.InvocationConfiguration.Output;
            try
            {
                var store = RegistryCommands.Store(p.GetValue(coverage));
                var text = p.GetValue(address)!;
                var kind = text.StartsWith("test:", StringComparison.Ordinal) ? "test" : text.StartsWith("code:", StringComparison.Ordinal) ? "code" : null;
                var bare = kind is null ? text : text[5..];
                kind ??= RegistryId.IsValid(bare) && bare.Contains("::", StringComparison.Ordinal) ? "code" : "test";
                var found = RegistryRefs.All(store).Where(c => c.Kind == kind && (kind == "code" ? RegistryRefs.Names(c.Address, bare) : RegistryRefs.NamesTest(c.Address, bare))).ToList();
                foreach (var cite in found) output.WriteLine($"{cite.Where}  {cite.Kind}:{cite.Address}");
                output.WriteLine($"{found.Count} citation{(found.Count == 1 ? "" : "s")} of {kind}:{bare}");
                return 0;
            }
            catch (RegistryException e)
            {
                p.InvocationConfiguration.Error.WriteLine(e.Message);
                return 1;
            }
        });
        return c;
    }
}
