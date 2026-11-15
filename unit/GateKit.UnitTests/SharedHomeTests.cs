using System.Text.Json.Nodes;
using GateKit.Home;
using GateKit.Platform;
using GateKit.Copies;

namespace GateKit.UnitTests;

/// <summary>Two GateKit builds - two checkouts, each with its own bridge sources - working at the
/// same time in one home: what they share is written under a lock and never lost, what belongs to
/// one game copy stays that copy's, and a file of a newer format is refused, not rewritten.</summary>
public sealed class SharedHomeTests : IDisposable
{
    readonly FakeGame _game = new();

    public void Dispose() => _game.Dispose();

    /// <summary>A "build" standing in checkout <paramref name="checkout"/>: its own working folder and
    /// bridge sources, the one home every checkout shares.</summary>
    CopySettings Build(string checkout, string? instance = null)
    {
        var mods = _game.Temp[checkout + "/mods"];
        if (!Directory.Exists(mods)) FileTree.CopyTree(_game.Temp["mods"], mods);
        _game.Temp.Write(checkout + "/GateKit.slnx", "<Solution />");
        string? Variable(string name) => name switch
        {
            "BG3_GAME" => _game.Temp["game"],
            "GATEKIT_BRIDGE" => mods,
            _ => null,
        };
        return CopySettings.Resolve(_game.Platform, Variable, _game.Temp[checkout], instance,
            new HomeSearch(Variable, _game.Temp[checkout], null, null, _game.Temp["unused-default"]));
    }

    public SharedHomeTests() => _game.Temp.Write("gatekit.json", """{ "home": "home" }""");

    [Fact]
    public void Both_builds_see_one_state_and_one_list_of_copies()
    {
        var a = Build("a");
        var b = Build("b");
        Assert.Equal(_game.Temp["home/state"], a.StateRoot);
        Assert.Equal(a.StateRoot, b.StateRoot);
        Assert.Equal(a.InstancesRoot, b.InstancesRoot);
        Directory.CreateDirectory(Path.Combine(a.InstancesRoot, "one", "game", "bin"));
        Directory.CreateDirectory(Path.Combine(b.InstancesRoot, "two", "game", "bin"));
        Assert.Equal(["one", "two"], Instance.Names(a.InstancesRoot));
        Assert.Equal(Instance.Names(a.InstancesRoot), Instance.Names(b.InstancesRoot));
    }

    [Fact]
    public async Task Labels_set_by_two_builds_at_once_are_all_kept()
    {
        var a = new DeckRecord(Build("a").StateRoot);
        var b = new DeckRecord(Build("b").StateRoot);
        await Task.WhenAll(
            Task.Run(() => { for (var i = 0; i < 40; i++) a.Label($"a{i}", $"from a {i}"); }),
            Task.Run(() => { for (var i = 0; i < 40; i++) b.Label($"b{i}", $"from b {i}"); }));
        var labels = a.Labels();
        Assert.Equal(80, labels.Count);
        Assert.Equal("from b 39", labels["b39"]);
        Assert.Equal(DeckRecord.Version,SharedFile.VersionOf(JsonNode.Parse(File.ReadAllText(a.LabelsFile))!.AsObject()));
    }

    [Fact]
    public void An_older_file_is_upgraded_once_and_a_newer_one_is_refused_and_left_alone()
    {
        var deck = new DeckRecord(Build("a").StateRoot);
        _game.Temp.Write("home/state/deck/labels.json", """{ "old": "written before formats were numbered" }""");
        deck.Label("new", "x");
        var upgraded = JsonNode.Parse(File.ReadAllText(deck.LabelsFile))!.AsObject();
        Assert.Equal(1, SharedFile.VersionOf(upgraded));
        Assert.Equal("written before formats were numbered", deck.Labels()["old"]);

        var newer = """{ "version": 99, "labels": { "kept": "as it is" } }""";
        File.WriteAllText(deck.LabelsFile, newer);
        var error = Assert.Throws<CopyException>(() => deck.Label("other", "y"));
        Assert.Contains("newer GateKit", error.Message);
        Assert.Equal(newer, File.ReadAllText(deck.LabelsFile));

        var state = Build("b").StateRoot;
        File.WriteAllText(Path.Combine(state, "script-extender.json"), """{ "version": 7, "scriptExtender": "x" }""");
        Assert.Contains("newer GateKit", Assert.Throws<CopyException>(() => Build("b")).Message);
    }

    [Fact]
    public void Each_copy_gets_its_own_packed_bridge_from_its_own_builds_sources()
    {
        var a = new Copies.GameCopy(Build("a", "copy-a"), _game.Platform, new StringWriter(), new CopyPacker());
        var b = new Copies.GameCopy(Build("b", "copy-b"), _game.Platform, new StringWriter(), new CopyPacker());
        Parallel.Invoke(() => a.Build(), () => b.Build());
        var packedA = Path.Combine(_game.Temp["home/cache/build"], "copy-a", "Gwynerva_GateKit.pak");
        var packedB = Path.Combine(_game.Temp["home/cache/build"], "copy-b", "Gwynerva_GateKit.pak");
        Assert.Contains(_game.Temp["a/mods"], File.ReadAllText(packedA));
        Assert.Contains(_game.Temp["b/mods"], File.ReadAllText(packedB));
        Assert.DoesNotContain(_game.Temp["b/mods"], File.ReadAllText(packedA));
    }

    [Fact]
    public async Task A_lock_being_let_go_of_is_waited_for_like_a_held_one()
    {
        // The holder's lock file deletes itself as it closes; while another handle is still open
        // it is "pending delete", and Windows answers an open of it with access denied.
        var path = _game.Temp["home/state/shared.json"];
        var lockFile = Path.GetFullPath(path) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        var holder = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
        var straggler = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        holder.Dispose();
        var taking = Task.Run(() => SharedFile.Lock(path, TimeSpan.FromSeconds(5)));
        await Task.Delay(200);
        straggler.Dispose();
        using var held = await taking;
        Assert.True(File.Exists(lockFile));
    }
}
