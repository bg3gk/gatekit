using System.Text.Json;
using GateKit.Stand;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>
/// The window tools: what is running, and how a deck shows it. They are the MCP half of
/// `gatekit window ...` (<see cref="DeckApi"/>), so an agent can say "keep my run where I can see
/// it" while a person rearranges the rest without the agent noticing.
///
/// None of them touches the game: reading it, driving it and taking its picture work the same
/// whether a copy is in a tile, behind another window or in no deck at all.
/// </summary>
public static class DeckTools
{
    public static IReadOnlyList<GateKitTool> Create(ToolHost host) =>
    [
        new(Define("bg3_windows",
                "Every Baldur's Gate 3 copy running on this machine: instance name, the label a person gave it, its process id "
                + "and its tile in the deck (`gatekit deck`, one window that holds them all). Tiles that are waiting for a game "
                + "to come back are listed too. Needs no deck: without one it still names every copy.",
                Empty),
            (_, _) =>
            {
                var (stand, _) = host.Begin();
                return Task.FromResult(Results.Text(DeckApi.Report(stand.Settings, stand.Platform)));
            }),

        new(Define("bg3_window",
                "Arrange the deck: `maximize` gives one copy the whole deck and keeps its tile while it restarts, `grid` puts "
                + "everything back side by side, `label` names a copy so no tile has to be guessed at, `release` gives a copy its "
                + "own window back. `mirror` draws a copy from the frames it shares on the graphics card, so its own window need "
                + "never be seen; `adopt` puts that window back in the tile. `fps` is how fast a copy may draw - 0 for no cap, "
                + "which also lifts the throttle the engine puts a copy under when its window is not in front; a low cap on a copy "
                + "nobody is watching leaves the machine to the one that is. `instance` is the instance name bg3_windows lists. "
                + "Nothing here starts, stops or disturbs a game, and no window has to be visible for the other tools to work.",
                """
                {
                 "type": "object",
                 "required": ["action"],
                 "properties": {
                  "action": {"type": "string", "enum": ["maximize", "grid", "label", "release", "mirror", "adopt", "fps"]},
                  "instance": {"type": "string", "description": "The copy to act on; not needed by `grid`."},
                  "label": {"type": "string", "description": "For `label`: what this copy is for, e.g. \"Nautiloid run\". Leave it out to take the name away."},
                  "fps": {"type": "integer", "minimum": 0, "maximum": 1000, "description": "For `fps`: frames a second, 0 for no cap. Leave it out to read what the copy has now."}
                 }
                }
                """),
            (args, _) =>
            {
                var (stand, _) = host.Begin();
                var action = args.Need("action");
                var instance = action == "grid" ? "" : args.Need("instance");
                return Task.FromResult(Results.Text(action switch
                {
                    "maximize" => DeckApi.Maximize(stand.Settings, instance),
                    "grid" => DeckApi.Grid(stand.Settings),
                    "label" => DeckApi.Label(stand.Settings, instance, args.Str("label")),
                    "release" => DeckApi.Release(stand.Settings, instance),
                    "mirror" or "adopt" => DeckApi.Backing(stand.Settings, instance, action),
                    "fps" => Fps(stand, instance, args.Int("fps")),
                    _ => throw new StandException($"unknown action '{action}'; one of maximize, grid, label, release, mirror, adopt, fps"),
                }));
            }),
    ];

    /// <summary>`fps` with a number sets the cap, without one reads it.</summary>
    static string Fps(Stand.Stand stand, string instance, int? fps)
    {
        if (fps is null)
        {
            var state = FrameCap.Read(stand.Settings, stand.Platform, instance);
            return state is null
                ? $"{instance}: this copy cannot say what its frame cap is (it needs GateKit's Script Extender build)"
                : $"{instance}: {state}";
        }
        return FrameCap.Set(stand.Settings, stand.Platform, instance, fps.Value)
            ? fps.Value == 0
                ? $"{instance} draws as fast as it can, in front or not"
                : $"{instance} draws at most {fps.Value} frames a second, in front or not"
            : $"{instance}: this copy would not take a frame cap (it needs GateKit's Script Extender build)";
    }

    const string Empty = """{"type": "object", "properties": {}}""";

    static Tool Define(string name, string description, string schema) =>
        new() { Name = name, Description = description, InputSchema = JsonDocument.Parse(schema).RootElement };
}
