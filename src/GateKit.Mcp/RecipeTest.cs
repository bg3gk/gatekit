using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Mcp;

/// <summary>
/// The test <c>bg3_recipe</c> writes: a file <c>gatekit test tests/_coverage -k name</c>
/// replays (docs/contracts/mcp.md, "A recipe is a test that replays").
///
/// A recipe used to be free Lua for the game, which the test runner cannot run: the runner
/// executes a file's top level on the host to read its declaration, and <c>Osi</c> is not there.
/// Now the recipe is built from steps - which side runs which Lua, which key is pressed, what is
/// waited for, in order - and ends by checking that the component came alive on its side, so a
/// recipe that no longer reaches its component fails rather than passes.
/// </summary>
public static class RecipeTest
{
    /// <summary>One step of a recipe.</summary>
    /// <param name="Side">client, server or ui: where <see cref="Lua"/> runs or <see cref="Until"/> is read.</param>
    /// <param name="Lua">A chunk run on <see cref="Side"/>; an error in it fails the replay.</param>
    /// <param name="Key">A key pressed in the game, as `gatekit key` names it.</param>
    /// <param name="Until">A Lua expression waited for on <see cref="Side"/> until it is true.</param>
    /// <param name="Timeout">Seconds a chunk or a wait may take.</param>
    /// <param name="Wait">Seconds to let pass after the step (alone: just a pause).</param>
    /// <param name="Note">A comment written above the step.</param>
    /// <param name="Hold">Seconds <see cref="Key"/> stays down (a press alone is a tap).</param>
    public sealed record Step(string? Side, string? Lua, string? Key, string? Until, double? Timeout, double? Wait, string? Note,
        double? Hold = null);

    static readonly string[] Sides = ["client", "server", "ui"];

    /// <summary>The `level` of a recipe that starts in the main menu (no level loaded).</summary>
    public const string MainMenu = "main menu";

    /// <summary>The steps of a call, refused with what is wrong and where.</summary>
    public static IReadOnlyList<Step> Steps(JsonNode? node)
    {
        if (node is not JsonArray list || list.Count == 0)
            throw new ArgumentException("`steps` must be a non-empty array of steps: {\"side\": \"server\", \"lua\": \"...\"}, "
                + "{\"key\": \"Escape\"}, {\"key\": \"w\", \"hold\": 2}, {\"side\": \"client\", \"until\": \"<expression>\", \"timeout\": 30} or {\"wait\": 2}");
        var steps = new List<Step>();
        for (var i = 0; i < list.Count; i++)
        {
            var where = $"step {i + 1}";
            if (list[i] is not JsonObject step) throw new ArgumentException($"{where} is not an object");
            foreach (var (name, _) in step)
                if (name is not ("side" or "lua" or "key" or "hold" or "until" or "timeout" or "wait" or "note"))
                    throw new ArgumentException($"{where}: unknown field '{name}' (a step has side, lua, key, hold, until, timeout, wait, note)");
            var side = Text(step, "side", where);
            var lua = Text(step, "lua", where);
            var key = Text(step, "key", where);
            var until = Text(step, "until", where);
            var timeout = Number(step, "timeout", where);
            var wait = Number(step, "wait", where);
            var hold = Number(step, "hold", where);
            if (hold is not null && key is null) throw new ArgumentException($"{where}: `hold` goes with `key` (seconds the key stays down)");
            if (hold is < 0 or > 30) throw new ArgumentException($"{where}: `hold` is seconds, 0 to 30");
            var does = (lua is null ? 0 : 1) + (key is null ? 0 : 1) + (until is null ? 0 : 1);
            if (does > 1) throw new ArgumentException($"{where}: one of lua, key or until per step, in the order they happen");
            if (does == 0 && wait is null) throw new ArgumentException($"{where}: says nothing to do (lua, key, until or wait)");
            if ((lua ?? until) is not null && !Sides.Contains(side))
                throw new ArgumentException($"{where}: `side` must be client, server or ui for lua and until (got {side ?? "nothing"})");
            if (wait is < 0 or > 600) throw new ArgumentException($"{where}: `wait` is seconds, 0 to 600");
            if (timeout is <= 0 or > 600) throw new ArgumentException($"{where}: `timeout` is seconds, above 0 and up to 600");
            steps.Add(new Step(side, lua, key, until, timeout, wait, Text(step, "note", where), hold));
        }
        return steps;
    }

    static string? Text(JsonObject step, string name, string where) => step[name] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => throw new ArgumentException($"{where}: `{name}` must be a string"),
    };

    static double? Number(JsonObject step, string name, string where) => step[name] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<double>(),
        _ => throw new ArgumentException($"{where}: `{name}` must be a number of seconds"),
    };

    /// <summary>The side a component lives on: said, or read off the engine's prefix.</summary>
    public static string ComponentSide(string component, string? said)
    {
        if (said is not null)
            return said is "client" or "server" ? said : throw new ArgumentException($"`side` is client or server, where the component lives (got {said})");
        if (component.StartsWith("esv::", StringComparison.Ordinal)) return "server";
        if (component.StartsWith("ecl::", StringComparison.Ordinal)) return "client";
        throw new ArgumentException($"say `side` (client or server): {component} carries no esv:: or ecl:: prefix to tell where it lives");
    }

    /// <summary>The test: the declaration, the watch, the steps in order, and the check. With
    /// <paramref name="mayExist"/> the check takes <c>{ may_exist = true }</c>: the type may be held
    /// before the steps, and the replay then needs more holders or a new one; without it a holder
    /// before the steps fails the replay.</summary>
    public static string Write(string header, string level, string component, string side, IReadOnlyList<Step> steps, double within,
        bool mayExist = false) => Write(header, level, [(side, component)], steps, within, mayExist);

    /// <summary>The test of a recipe that makes several types appear at once (a status that
    /// carries two boosts): each is watched from before the first step and must come alive, on its
    /// own side. <paramref name="cleanup"/> steps run after the checks, so that the replay leaves
    /// nothing behind for the next one.</summary>
    public static string Write(string header, string level, IReadOnlyList<(string Side, string Component)> components, IReadOnlyList<Step> steps,
        double within, bool mayExist = false, IReadOnlyList<Step>? cleanup = null)
    {
        if (components.Count == 0) throw new ArgumentException("a recipe makes at least one component appear");
        var menu = level == MainMenu;
        if (!menu && !level.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException($"`level` is MODULE:LEVEL, the start the replay needs (e.g. GustavX:WLD_Main_A), or \"{MainMenu}\", not {level}");
        var lua = new StringBuilder(header)
            .Append(menu ? "test { menu = true }\n\n" : "test { level = " + Quote(level) + " }\n\n")
            .Append("function run(game)\n")
            .Append(menu ? "    game.wait_state(\"Menu\", \"client\", 120)\n" : "    game.wait_state(\"Running\", \"server\", 120)\n")
            .Append("    -- Watched from before the first step, so a component that lives one frame is seen too.\n");
        foreach (var (side, component) in components)
            lua.Append("    watch(").Append(Quote(side)).Append(", ").Append(Quote(component)).Append(")\n");
        AppendSteps(lua, steps);
        lua.Append('\n');
        var first = true;
        foreach (var (side, component) in components)
        {
            // the first check waits up to `within`; by then the others have had their time too
            lua.Append(first ? "    local seen = alive(" : "    seen = alive(").Append(Quote(side)).Append(", ").Append(Quote(component)).Append(", ")
                .Append(Seconds(first ? within : 1))
                .Append(mayExist ? ", { may_exist = true })\n" : ")\n")
                .Append("    print(").Append(Quote(component + " alive on " + side + ": ")).Append(" .. fmt(seen))\n");
            first = false;
        }
        if (cleanup is { Count: > 0 }) AppendSteps(lua, cleanup);
        return lua.Append("end\n").ToString();
    }

    static void AppendSteps(StringBuilder lua, IReadOnlyList<Step> steps)
    {
        foreach (var step in steps)
        {
            lua.Append('\n');
            if (step.Note is { Length: > 0 } note) lua.Append("    -- ").Append(note.ReplaceLineEndings("\n    -- ")).Append('\n');
            if (step.Lua is not null)
                lua.Append("    game.lua(").Append(Quote(step.Side!)).Append(", ").Append(Long(step.Lua))
                    .Append(step.Timeout is { } t ? ", " + Seconds(t) : "").Append(")\n");
            else if (step.Key is not null)
                lua.Append("    game.key(").Append(Quote(step.Key))
                    .Append(step.Hold is { } held && held > 0 ? ", " + Seconds(held) : "").Append(")\n");
            else if (step.Until is not null)
                lua.Append("    game.wait(").Append(Quote(step.Side!)).Append(", ").Append(Long(step.Until))
                    .Append(", ").Append(Seconds(step.Timeout ?? 30)).Append(")\n");
            if (step.Wait is { } pause and > 0) lua.Append("    game.sleep(").Append(Seconds(pause)).Append(")\n");
        }
    }

    static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A Lua string literal; text other than ASCII is kept as it is (Lua strings are bytes).</summary>
    static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    /// <summary>A long-bracket literal whose closing bracket the text does not contain.</summary>
    static string Long(string text)
    {
        var level = 0;
        while (text.Contains("]" + new string('=', level) + "]", StringComparison.Ordinal)) level++;
        var equals = new string('=', level);
        return $"[{equals}[\n{text.TrimEnd().ReplaceLineEndings("\n")}\n]{equals}]";
    }
}
