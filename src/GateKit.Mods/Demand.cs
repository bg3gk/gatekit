using System.Text.Json.Nodes;
using GateKit.Names.Demand;

namespace GateKit.Mods;

/// <summary>
/// The demand over any set of usage documents: each name counted by the mods that use it, in the
/// rows of a demand file (<see cref="DemandFile"/>). A mod counts once per name whatever it does
/// with it; rows are ordered by kind, then most mods first, then name, so the same mods give the
/// same bytes.
/// </summary>
public static class Demand
{
    public const string Schema = "gatekit.mod-demand/1";

    static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(s => (JsonNode)s)]);

    public static List<JsonObject> Rows(IReadOnlyList<ModUsage> mods, SeCatalog? catalog, IReadOnlyDictionary<string, List<(string Kind, int Arity, int Out)>>? osiris)
    {
        var rows = new List<JsonObject>
        {
            new()
            {
                ["kind"] = "source",
                ["schema"] = Schema,
                ["scanner"] = $"gatekit mod usage {ModUsage.ScannerVersion}",
                ["count"] = mods.Count,
                ["osiris_index"] = osiris is not null,
                ["mods"] = new JsonArray([.. mods.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => (JsonNode)new JsonObject
                {
                    ["name"] = m.Name,
                    ["modules"] = new JsonArray([.. m.Modules.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["uuid"] = x.Uuid, ["version"] = x.Version })]),
                })]),
            },
        };

        var components = new List<JsonObject>();
        foreach (var group in mods.SelectMany(m => m.Components.Values.Select(c => (Mod: m.Name, Use: c))).GroupBy(x => x.Use.Type, StringComparer.Ordinal))
        {
            var users = group.GroupBy(x => x.Mod, StringComparer.Ordinal).ToList();
            bool Any(IGrouping<string, (string Mod, ComponentUse Use)> g, Func<ComponentUse, bool> f) => g.Any(x => f(x.Use));
            var fields = group.SelectMany(x => x.Use.Fields.Select(f => (Field: f, x.Mod))).GroupBy(p => p.Field, StringComparer.Ordinal)
                .Select(g => (Name: g.Key, Mods: g.Select(p => p.Mod).Distinct(StringComparer.Ordinal).Count()))
                .OrderByDescending(f => f.Mods).ThenBy(f => f.Name, StringComparer.Ordinal);
            components.Add(new JsonObject
            {
                ["kind"] = "component",
                ["type"] = group.Key,
                ["ext_name"] = group.Select(x => x.Use.Ext).FirstOrDefault(e => e is not null) ?? catalog?.EngineToExt.GetValueOrDefault(group.Key),
                ["mods"] = users.Count,
                ["mod_names"] = Strings(users.Select(u => u.Key).Order(StringComparer.Ordinal)),
                ["uses"] = new JsonObject
                {
                    ["read"] = users.Count(u => Any(u, c => c.Read)),
                    ["write"] = users.Count(u => Any(u, c => c.Write)),
                    ["create"] = users.Count(u => Any(u, c => c.Create)),
                    ["subscribe"] = users.Count(u => Any(u, c => c.Subscribe)),
                },
                ["via"] = new JsonObject
                {
                    ["lua"] = users.Count(u => u.Any(x => x.Use.Via.Contains("lua"))),
                    ["stats"] = users.Count(u => u.Any(x => x.Use.Via.Contains("stats"))),
                },
                ["fields"] = new JsonArray([.. fields.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["mods"] = f.Mods })]),
            });
        }
        rows.AddRange(Ordered(components, "type"));

        var osirisRows = new List<JsonObject>();
        var names = mods.SelectMany(m => m.OsirisCalls.Keys.Concat(m.OsirisListeners.Keys).Concat(m.GoalCalls)).Distinct(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var calling = mods.Where(m => m.OsirisCalls.ContainsKey(name) || m.GoalCalls.Contains(name)).Select(m => m.Name).ToList();
            var listening = mods.Where(m => m.OsirisListeners.ContainsKey(name)).Select(m => m.Name).ToList();
            var all = calling.Concat(listening).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var row = new JsonObject
            {
                ["kind"] = "osiris",
                ["name"] = name,
                ["mods"] = all.Count,
                ["mod_names"] = Strings(all),
                ["called_by"] = calling.Distinct(StringComparer.Ordinal).Count(),
                ["from_goals"] = mods.Count(m => m.GoalCalls.Contains(name)),
                ["listened_by"] = listening.Count,
                ["arities"] = new JsonArray([.. mods.SelectMany(m => m.OsirisListeners.GetValueOrDefault(name) ?? []).Distinct().Order().Select(a => (JsonNode)a)]),
                ["lua_arguments"] = new JsonArray([.. mods.SelectMany(m => m.OsirisLuaArguments.GetValueOrDefault(name) ?? []).Distinct().Order().Select(a => (JsonNode)a)]),
            };
            if (name.StartsWith("DB_", StringComparison.Ordinal)) row["database"] = true;
            if (osiris is not null)
                row["index"] = osiris.TryGetValue(name, out var overloads)
                    ? new JsonObject
                    {
                        ["overloads"] = new JsonArray([.. overloads.Select(o => (JsonNode)new JsonObject { ["kind"] = o.Kind, ["arity"] = o.Arity, ["out"] = o.Out })]),
                    }
                    : null;
            osirisRows.Add(row);
        }
        rows.AddRange(Ordered(osirisRows, "name"));

        rows.AddRange(Ordered(Named(mods, m => m.Api, "se_api"), "name"));
        rows.AddRange(Ordered(Named(mods, m => m.Events, "se_event", catalog is null ? null : e => catalog.Events.Contains(e)), "name"));
        rows.AddRange(Ordered(Named(mods, m => m.Functors.Keys, "functor"), "name"));
        rows.AddRange(Ordered(Named(mods, m => m.Boosts.Keys, "boost"), "name"));
        return rows;
    }

    static IEnumerable<JsonObject> Ordered(IEnumerable<JsonObject> rows, string key) =>
        rows.OrderByDescending(r => (int)r["mods"]!).ThenBy(r => (string)r[key]!, StringComparer.Ordinal);

    static IEnumerable<JsonObject> Named(IReadOnlyList<ModUsage> mods, Func<ModUsage, IEnumerable<string>> pick, string kind, Func<string, bool>? known = null)
    {
        foreach (var name in mods.SelectMany(pick).Distinct(StringComparer.Ordinal))
        {
            var users = mods.Where(m => pick(m).Contains(name)).Select(m => m.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var row = new JsonObject { ["kind"] = kind, ["name"] = name, ["mods"] = users.Count, ["mod_names"] = Strings(users) };
            if (known is not null) row["in_se"] = known(name);
            yield return row;
        }
    }
}
