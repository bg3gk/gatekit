using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>
/// What `gatekit window ...` and the MCP tools of the same name do: name the running copies, and
/// ask a deck to rearrange them. It needs no window of its own, so it answers whether a deck is
/// running or not - without one, `list` still names every copy and `label` still remembers names.
/// </summary>
public static class DeckApi
{
    /// <summary>Every running game and its tile, as JSON.</summary>
    public static JsonObject Report(StandSettings settings, IGamePlatform platform)
    {
        var record = new DeckRecord(settings.StateRoot);
        var deck = record.Running();
        var labels = record.Labels();
        var tiles = record.Tiles().ToDictionary(t => t.Instance, StringComparer.OrdinalIgnoreCase);
        var copies = new JsonArray();
        foreach (var copy in DeckRecord.Copies(settings, platform))
        {
            tiles.TryGetValue(copy.Instance, out var tile);
            copies.Add(new JsonObject
            {
                ["instance"] = copy.Instance,
                ["label"] = labels.TryGetValue(copy.Instance, out var label) ? label : null,
                ["pid"] = copy.Pid,
                ["window"] = (long)copy.Window,
                // Whether this stand is the one that started it. A deck takes hold only of its
                // own unless it is told otherwise, so this is why a copy may have no tile.
                ["mine"] = copy.Mine,
                ["tile"] = tile is null ? null : new JsonObject
                {
                    ["state"] = tile.State,
                    ["column"] = tile.Column,
                    ["row"] = tile.Row,
                    ["why"] = tile.Why,
                },
            });
        }
        // A tile whose game is away keeps its place, and is worth seeing: that is what it is for.
        var waiting = new JsonArray();
        foreach (var tile in record.Tiles().Where(t => t.Pid == 0))
            waiting.Add(new JsonObject { ["instance"] = tile.Instance, ["label"] = tile.Label, ["state"] = tile.State });

        return new JsonObject
        {
            ["deck"] = deck is null ? null : new JsonObject { ["pid"] = deck.Pid, ["window"] = (long)deck.Host },
            ["copies"] = copies,
            ["waiting"] = waiting,
        };
    }

    /// <summary>The same as lines to print.</summary>
    public static IReadOnlyList<string> List(StandSettings settings, IGamePlatform platform)
    {
        var report = Report(settings, platform);
        var lines = new List<string>
        {
            report["deck"] is JsonObject deck
                ? $"deck: running (pid {deck["pid"]})"
                : "deck: none (`gatekit deck` opens one; the games run in their own windows meanwhile)",
        };
        foreach (var copy in report["copies"]!.AsArray().OfType<JsonObject>())
        {
            var tile = copy["tile"] as JsonObject;
            var mine = copy["mine"]?.GetValue<bool>() ?? false;
            var where = tile is not null
                ? $"tile {tile["column"]},{tile["row"]} {tile["state"]}"
                : mine ? "no tile" : "no tile (not this stand's)";
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{copy["instance"],-16} pid {copy["pid"],-8} {where,-28} {copy["label"]}").TrimEnd());
            // A tile that is not showing its game says why, in its own line under it.
            if (tile?["why"]?.ToString() is { Length: > 0 } why) lines.Add($"{"",-16} {why}");
        }
        foreach (var tile in report["waiting"]!.AsArray().OfType<JsonObject>())
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{tile["instance"],-16} {"(no game)",-12} tile kept, {tile["state"]}").TrimEnd());
        }
        if (lines.Count == 1) lines.Add("no game is running");
        return lines;
    }

    public static string Maximize(StandSettings settings, string instance)
    {
        Ask(settings, new DeckRequest("maximize", instance));
        return $"{instance} fills the deck and keeps its place while it restarts";
    }

    public static string Grid(StandSettings settings)
    {
        Ask(settings, new DeckRequest("grid"));
        return "every tile is back in the grid";
    }

    /// <summary>Closes a running deck; every window goes back to the desktop, no game is touched.</summary>
    public static string Close(StandSettings settings)
    {
        var record = new DeckRecord(settings.StateRoot);
        var deck = record.Running()
            ?? throw new StandException("no deck is running; `gatekit deck` opens one (the games themselves are unaffected)");
        record.Send(new DeckRequest("quit"));
        // Waited for, rather than announced. A deck has windows to give back and a drawing thread
        // to stop, so closing is not instant; and a close that reported success while the deck
        // stayed up left one behind every time, which is how an unattended night ends up with
        // several. If it does not go, this says so and names the process, which can be ended.
        var until = DateTime.UtcNow + ClosePatience;
        while (DateTime.UtcNow < until)
        {
            if (record.Running() is null) return "the deck is closed; every window is back on the desktop";
            Thread.Sleep(200);
        }
        throw new StandException($"the deck (pid {deck.Pid}) was asked to close {ClosePatience.TotalSeconds:0} s ago "
            + "and is still running; close its window, or end that process");
    }

    /// <summary>How long a deck is given to put every window back and go.</summary>
    static readonly TimeSpan ClosePatience = TimeSpan.FromSeconds(20);

    /// <summary>Which backing fills that copy's tile from now on: "adopt" (its own window, taken
    /// into the deck) or "mirror" (the frames it shares on the graphics card, its window never
    /// seen). The tile is given back by the one that held it and filled by the other.</summary>
    public static string Backing(StandSettings settings, string instance, string backing)
    {
        Ask(settings, new DeckRequest("backing", instance, backing));
        return backing == "mirror"
            ? $"{instance} is drawn from the frames it shares; its own window goes out of sight"
            : $"{instance} has its own window in its tile again";
    }

    public static string Release(StandSettings settings, string instance)
    {
        Ask(settings, new DeckRequest("release", instance));
        return $"{instance} gets its own window back and leaves the deck";
    }

    /// <summary>Names a copy. The name is kept in the state folder, so it survives a deck's restart
    /// and is there before the game is.</summary>
    public static string Label(StandSettings settings, string instance, string? text)
    {
        new DeckRecord(settings.StateRoot).Label(instance, text);
        return text is { Length: > 0 } ? $"{instance} is now \"{text}\"" : $"{instance} has no label any more";
    }

    static void Ask(StandSettings settings, DeckRequest request)
    {
        var record = new DeckRecord(settings.StateRoot);
        if (record.Running() is null)
            throw new StandException("no deck is running; `gatekit deck` opens one (the games themselves are unaffected)");
        record.Send(request);
    }
}
