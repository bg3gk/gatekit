using System.Globalization;
using System.Text.Json.Nodes;

namespace GateKit.Stand;

/// <summary>
/// How loud one running game is, through the bridge's <c>GK.Sound</c> (docs/contracts/lua-api.md).
/// Every copy of the game mixes its own sound, so this is per instance and needs no audio API of
/// the operating system: the chunks below run on the client and write the game's own Wwise RTPCs.
/// </summary>
public sealed class SoundControl(GameSession session)
{
    /// <summary>How long the game is given to answer a sound call.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Volumes run from silence to the slider's full position, as the options screen shows them.</summary>
    public const double Quiet = 0, Loud = 100;

    /// <summary>
    /// A category as the bridge takes it: the game's own key (<c>MusicVolume</c>), GateKit's short
    /// name (<c>music</c>) or the RTPC (<c>RTPC_Volume_Music</c>). Every one of them is letters,
    /// digits and underscores, which is also what makes it safe to put into a Lua chunk; anything
    /// else is refused here rather than in the game. Null or empty means the master.
    /// </summary>
    public static string? Category(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        if (trimmed.Length > 64 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new StandException($"sound category '{trimmed}': a category is letters, digits and '_' "
                + "(a key such as MusicVolume, a short name such as music, or an RTPC name)");
        return trimmed;
    }

    /// <summary>A volume from the command line: a number from 0 to 100, with or without a '%'.</summary>
    public static double Volume(string? text)
    {
        var trimmed = (text ?? "").Trim().TrimEnd('%').Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new StandException($"volume '{text}': expected a number from {Quiet:0} to {Loud:0}");
        return Volume(value);
    }

    /// <summary>The same range check for a volume that arrived as a number.</summary>
    public static double Volume(double value)
    {
        if (double.IsNaN(value) || value < Quiet || value > Loud)
            throw new StandException(string.Create(CultureInfo.InvariantCulture,
                $"volume {value:0.###}: out of range, expected {Quiet:0} to {Loud:0}"));
        return value;
    }

    /// <summary>Whether a word from the command line turns the flag on or off.</summary>
    public static bool OnOff(string text) => text.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "mute" or "1" => true,
        "off" or "false" or "no" or "hear" or "0" => false,
        _ => throw new StandException($"'{text}': expected on or off"),
    };

    /// <summary>A category as a Lua argument; nothing at all when none was named, which the
    /// bridge reads as "the master" for Get, Set and Mute and as "all of them" for Unmute.</summary>
    static string Argument(string? category) => category is null ? "" : $"\"{category}\"";

    /// <summary>Every sound category with its key, short name, RTPC and current volume.</summary>
    public Task<JsonNode?> ListAsync() => session.ClientAsync("return GK.Sound.List()", Patience);

    /// <summary>Sets one category (null: the master) to <paramref name="value"/>, 0 to 100.</summary>
    public Task<JsonNode?> SetAsync(string? category, double value)
    {
        var volume = value.ToString("0.###", CultureInfo.InvariantCulture);
        return session.ClientAsync($"return GK.Sound.Set(\"{category ?? "MasterVolume"}\", {volume})", Patience);
    }

    /// <summary>Silences one category (null: the master, which silences this copy), remembering
    /// how loud it was.</summary>
    public Task<JsonNode?> MuteAsync(string? category) =>
        session.ClientAsync($"return GK.Sound.Mute({Argument(category)})", Patience);

    /// <summary>Puts back what <see cref="MuteAsync"/> took away: one category, or every muted one.</summary>
    public Task<JsonNode?> UnmuteAsync(string? category) =>
        session.ClientAsync($"return GK.Sound.Unmute({Argument(category)})", Patience);

    /// <summary>The game's own MuteSoundWhenNotFocused: null reads it, true or false sets it.</summary>
    public Task<JsonNode?> WhenUnfocusedAsync(bool? on) =>
        session.ClientAsync($"return GK.Sound.WhenUnfocused({(on is null ? "" : on.Value ? "true" : "false")})", Patience);

    /// <summary>The list as a person reads it: short name, the game's key, the volume.</summary>
    public static IEnumerable<string> Lines(JsonNode? list, bool? whenUnfocused = null)
    {
        if (list is not JsonArray entries) yield break;
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var name = entry["name"]?.GetValue<string>() ?? "";
            var key = entry["key"]?.GetValue<string>() ?? "";
            var muted = entry["muted"] is not null;
            var volume = entry["value"] is { } value
                ? value.GetValue<double>().ToString("0.#", CultureInfo.InvariantCulture).PadLeft(5)
                : "    -";
            var note = entry["value"] is null ? "  (no RTPC: controller vibration, not sound)"
                : muted ? "  (muted)" : "";
            yield return $"{name,-9} {key,-20} {volume}{note}";
        }
        if (whenUnfocused is { } flag)
            yield return $"mute when the window is not in front: {(flag ? "on" : "off")}";
    }
}
