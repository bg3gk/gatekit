using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Stand;

namespace GateKit.Tests;

/// <summary>
/// `gatekit sound` and the bridge's GK.Sound: what the host sends to the game (against
/// FakeBridge), how it reads an argument before sending anything, and that the Lua side still
/// lists the game's own twelve sound settings.
/// </summary>
public sealed class SoundTests : IAsyncDisposable
{
    readonly FakeGame _game = new();
    readonly List<FakeBridge> _bridges = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var bridge in _bridges) await bridge.DisposeAsync();
        _game.Dispose();
    }

    static string Chunk(string code) => code[BridgeClient.Prelude.Length..];

    /// <summary>A stand whose client side records every chunk and answers with <paramref name="answer"/>.</summary>
    (Stand.Stand Stand, List<string> Sent) Game(Func<string, JsonNode?> answer)
    {
        var stand = _game.Stand();
        var sent = new List<string>();
        _bridges.Add(new FakeBridge(stand.Layout.Channel, "client", code =>
        {
            var chunk = Chunk(code);
            lock (sent) sent.Add(chunk);
            return answer(chunk);
        }));
        return (stand, sent);
    }

    [Fact]
    public async Task Each_call_sends_the_bridge_the_call_a_person_would_type()
    {
        var (stand, sent) = Game(_ => JsonValue.Create(42));
        var sound = stand.Sound();
        await sound.ListAsync();
        await sound.SetAsync("music", 40);
        await sound.SetAsync(null, 7.5);
        await sound.MuteAsync(null);
        await sound.MuteAsync("VoiceVolumeDialog");
        await sound.UnmuteAsync(null);
        await sound.UnmuteAsync("music");
        await sound.WhenUnfocusedAsync(null);
        await sound.WhenUnfocusedAsync(true);
        await sound.WhenUnfocusedAsync(false);
        lock (sent)
        {
            Assert.Equal(
            [
                "return GK.Sound.List()",
                "return GK.Sound.Set(\"music\", 40)",
                "return GK.Sound.Set(\"MasterVolume\", 7.5)",
                "return GK.Sound.Mute()",
                "return GK.Sound.Mute(\"VoiceVolumeDialog\")",
                "return GK.Sound.Unmute()",
                "return GK.Sound.Unmute(\"music\")",
                "return GK.Sound.WhenUnfocused()",
                "return GK.Sound.WhenUnfocused(true)",
                "return GK.Sound.WhenUnfocused(false)",
            ], sent);
        }
    }

    [Fact]
    public void A_category_is_a_name_of_the_game_or_of_gatekit_and_nothing_else()
    {
        Assert.Null(SoundControl.Category(null));
        Assert.Null(SoundControl.Category("  "));
        Assert.Equal("music", SoundControl.Category(" music "));
        Assert.Equal("MusicVolume", SoundControl.Category("MusicVolume"));
        Assert.Equal("RTPC_Volume_VO_Dialog", SoundControl.Category("RTPC_Volume_VO_Dialog"));
        // gatekit:allow-non-english  a category name is ASCII by the game's own rules, so a word in
        // another alphabet belongs in the list of things that must be refused.
        foreach (var bad in new[] { "music\"); Ext.Utils.Exit(); --", "my music", "music)", "музыка", new string('x', 65) })
            Assert.Throws<StandException>(() => SoundControl.Category(bad));
    }

    [Fact]
    public void A_volume_is_a_number_from_0_to_100()
    {
        Assert.Equal(0, SoundControl.Volume("0"));
        Assert.Equal(100, SoundControl.Volume("100"));
        Assert.Equal(12.5, SoundControl.Volume(" 12.5 "));
        Assert.Equal(40, SoundControl.Volume("40%"));
        foreach (var bad in new[] { "", "loud", "-1", "101", "1e9" })
            Assert.Throws<StandException>(() => SoundControl.Volume(bad));
        Assert.Throws<StandException>(() => SoundControl.Volume(double.NaN));
    }

    [Fact]
    public void On_and_off_are_read_the_way_a_person_writes_them()
    {
        Assert.True(SoundControl.OnOff("on"));
        Assert.True(SoundControl.OnOff(" MUTE "));
        Assert.False(SoundControl.OnOff("off"));
        Assert.False(SoundControl.OnOff("hear"));
        Assert.Throws<StandException>(() => SoundControl.OnOff("maybe"));
    }

    [Fact]
    public void The_list_is_printed_as_a_person_reads_it()
    {
        var list = JsonNode.Parse("""
            [
             {"key": "MasterVolume", "name": "master", "rtpc": "RTPC_Volume_MAIN", "value": 0, "muted": 100},
             {"key": "MusicVolume", "name": "music", "rtpc": "RTPC_Volume_Music", "value": 40},
             {"key": "RumbleVolume", "name": "rumble"}
            ]
            """);
        var lines = SoundControl.Lines(list, whenUnfocused: true).ToList();
        Assert.Equal(4, lines.Count);
        Assert.StartsWith("master    MasterVolume", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("0  (muted)", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("40", lines[1], StringComparison.Ordinal);
        Assert.Contains("no RTPC", lines[2], StringComparison.Ordinal);
        Assert.Equal("mute when the window is not in front: on", lines[3]);
        Assert.Equal(3, SoundControl.Lines(list).Count());
        Assert.Empty(SoundControl.Lines(null));
    }

    // --- the Lua side ---

    static string SoundLua()
    {
        var repo = StandSettings.RepositoryRoot();
        Assert.NotNull(repo);
        return File.ReadAllText(Path.Combine(repo, "mods", "GateKit", "Mods", "Gwynerva_GateKit",
            "ScriptExtender", "Lua", "Client", "Sound.lua"));
    }

    [Fact]
    public void The_bridge_lists_the_games_own_twelve_sound_settings()
    {
        var lua = SoundLua();
        string[] keys =
        [
            "MasterVolume", "FxVolume", "AmbientVolume", "MusicVolume", "MusicFightVolume", "CutsceneVolume",
            "UIVolume", "VoiceVolume", "VoiceVolumeDialog", "VoiceVolumeNarrator", "VoiceVolumeOverhead", "RumbleVolume",
        ];
        foreach (var key in keys) Assert.Contains($"key = \"{key}\"", lua, StringComparison.Ordinal);
        Assert.Equal(keys.Length, lua.Split("key = \"").Length - 1);
        // Eleven of the twelve are mixed; RumbleVolume is controller vibration and has no RTPC.
        Assert.Equal(keys.Length - 1, lua.Split("rtpc = \"RTPC_Volume_").Length - 1);
    }

    [Fact]
    public void Every_sound_function_carries_the_comment_bg3_api_reads()
    {
        var lua = SoundLua();
        var lines = lua.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var functions = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("function GK.Sound.", StringComparison.Ordinal)) continue;
            functions++;
            Assert.True(i > 0 && lines[i - 1].StartsWith("---", StringComparison.Ordinal),
                $"{lines[i]} has no --- comment above it");
        }
        Assert.Equal(10, functions);
    }
}
