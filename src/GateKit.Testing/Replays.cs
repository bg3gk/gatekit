using System.Reflection;
using System.Text.Json.Nodes;
using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.Testing;

/// <summary>
/// Every replay of a coverage recipe (a test under <c>tests/_coverage</c>) leaves a row in
/// <c>research/coverage/record/replays.jsonl</c> of the checkout the recipe belongs to: which
/// recipe, when, on which copy, by which role and run, under which game build and GateKit version,
/// whether it passed, and what its <c>alive</c> saw (holders before the steps, the most at once).
/// A passed replay also writes a <c>repeated</c> row into sightings.jsonl: the type was confirmed
/// again.
/// </summary>
public static class Replays
{
    /// <summary>What <c>alive</c> saw in this script (its RECIPE_ALIVE), or null when it never ran.</summary>
    public static JsonObject? Alive(Script script) =>
        script.Globals.Get("RECIPE_ALIVE") is { Type: DataType.Table } seen ? LuaValues.ToJson(seen) as JsonObject : null;

    /// <summary>Writes the rows for one replay. Never throws: a record that cannot be written is
    /// said on <paramref name="output"/>, and the verdict stands.</summary>
    public static void Note(string recipePath, bool ok, TimeSpan took, JsonObject? alive, Copies.GameCopy copy, GameSession session,
        TextWriter output, string via = "test")
    {
        if (Record.CoverageRootOfRecipe(recipePath) is not { } root) return;
        try
        {
            var (role, run, stretch) = Record.Who();
            var instance = copy.Instance?.Name ?? "installed";
            var recipe = "tests/_coverage/" + Path.GetFileName(recipePath);
            var time = Record.Now();
            var row = new JsonObject
            {
                ["recipe"] = recipe,
                ["time"] = time,
                ["instance"] = instance,
                ["by"] = role,
                ["run"] = run,
                ["game"] = GameVersion(session),
                ["gatekit"] = typeof(Replays).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?",
                ["ok"] = ok,
                ["before"] = alive?["before"]?.DeepClone(),
                ["most"] = alive?["most"]?.DeepClone(),
                ["seconds"] = Math.Round(took.TotalSeconds, 1),
                ["via"] = via,
            };
            using var held = Record.Lock(root);
            Record.Append(root, "replays", row, held: true);
            if (ok && alive?["component"] is JsonValue c && c.TryGetValue<string>(out var type)
                && alive["side"] is JsonValue s && s.TryGetValue<string>(out var side))
            {
                Record.Append(root, "sightings", new JsonObject
                {
                    ["type"] = type,
                    ["side"] = side,
                    ["time"] = time,
                    ["instance"] = instance,
                    ["role"] = role,
                    ["run"] = run,
                    ["stretch"] = stretch,
                    ["level"] = "",
                    ["label"] = "replay of " + recipe,
                    ["source"] = "tick",
                    ["state"] = "repeated",
                    ["capture"] = "replay",
                }, held: true);
            }
        }
        catch (Exception e) when (e is IOException or CopyException or ArgumentException or UnauthorizedAccessException)
        {
            output.WriteLine($"note: the replay of {Path.GetFileName(recipePath)} was not written into the record: {e.Message}");
        }
    }

    static string? GameVersion(GameSession session)
    {
        try
        {
            return session.LuaAsync("server", "return Ext.Utils.GameVersion()", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() is JsonValue v
                && v.TryGetValue<string>(out var text) ? text : null;
        }
        catch (CopyException) { return null; }
    }
}
