using System.Text.Json.Nodes;
using GateKit.Testing;

namespace GateKit.UnitTests;

/// <summary>What the event log holds that a test did not ask for: blocking states, creatures
/// outside the cast, rows no registry places - and the one line that allows each.</summary>
public sealed class SurprisesTests
{
    const string Host = "c7c13742-bacd-460a-8f65-f864fe41f255";
    const string Goblin = "11111111-2222-3333-4444-555555555555";
    const string Stranger = "99999999-8888-7777-6666-555555555555";

    static JsonObject Row(long seq, string kind, string? attention = null, JsonObject? payload = null, string side = "server")
    {
        var row = new JsonObject { ["seq"] = seq, ["kind"] = kind, ["side"] = side, ["payload"] = payload ?? new JsonObject() };
        if (attention is not null) row["attention"] = attention;
        return row;
    }

    static readonly HashSet<string> None = new(StringComparer.Ordinal);

    static HashSet<string> Set(params string[] entries) => new(entries, StringComparer.Ordinal);

    [Fact]
    public void A_blocking_state_nobody_asked_for_is_listed_with_whether_it_ended()
    {
        var rows = new List<JsonObject>
        {
            Row(1, "combat_started", "begin"),
            Row(2, "tutorial", "begin", new JsonObject { ["window"] = "tutorial_box" }, "client"),
            Row(3, "tutorial_ended", "end", new JsonObject { ["opened"] = 2 }, "client"),
            Row(4, "death", "news"),
        };
        var found = Surprises.Find(rows, rows, null, None, None);
        Assert.Equal(["combat_started", "tutorial"], found.Select(s => s.What));
        Assert.All(found, s => Assert.Equal("blocking", s.Sort));
        Assert.Contains("1 not ended", found[0].Detail);
        Assert.Contains("began and ended inside the test", found[1].Detail);
        Assert.Contains("tutorial_box", found[1].Detail);
    }

    [Fact]
    public void An_end_on_the_other_side_does_not_close_a_state()
    {
        var rows = new List<JsonObject>
        {
            Row(2, "roll", "begin", side: "client"),
            Row(3, "combat_ended", "end", new JsonObject { ["opened"] = 2 }, "server"),
        };
        Assert.Contains("1 not ended", Assert.Single(Surprises.Find(rows, rows, null, None, None)).Detail);
    }

    [Fact]
    public void What_the_test_named_or_its_header_allows_is_no_surprise()
    {
        var rows = new List<JsonObject> { Row(1, "combat_started", "begin"), Row(2, "roll", "begin", side: "client") };
        Assert.Equal(["roll"], Surprises.Find(rows, rows, null, Set("combat_started"), None).Select(s => s.What));
        Assert.Equal(["combat_started"], Surprises.Find(rows, rows, null, None, Set("roll")).Select(s => s.What));
        Assert.Empty(Surprises.Find(rows, rows, null, Set("roll"), Set("combat_started")));
    }

    [Fact]
    public void A_creature_outside_the_party_and_the_ledger_is_a_participant()
    {
        var before = new List<JsonObject>
        {
            Row(1, "scope_begin", null, new JsonObject { ["party"] = new JsonArray("S_Player_" + Host) }),
            Row(2, "scope_track", null, new JsonObject { ["kind"] = "spawn", ["entity"] = Goblin }),
        };
        var rows = new List<JsonObject>
        {
            Row(3, "game_damage", null, new JsonObject { ["who"] = Goblin, ["by"] = Host }),
            Row(4, "game_damage", null, new JsonObject { ["who"] = Host, ["by"] = "S_GOB_" + Stranger.ToUpperInvariant() }),
            Row(5, "game_turn", null, new JsonObject { ["who"] = Stranger }),
            Row(6, "game_fight", null, new JsonObject { ["who"] = "" }),
            Row(7, "status_applied", null, new JsonObject { ["who"] = Goblin, ["by"] = "00000000-0000-0000-0000-000000000000" }),
        };
        var found = Assert.Single(Surprises.Find([.. before, .. rows], rows, null, None, None));
        Assert.Equal(("participant", Stranger, 2), (found.Sort, found.What, found.Rows));
        Assert.Contains("game_damage, game_turn", found.Detail);
        Assert.Empty(Surprises.Find([.. before, .. rows], rows, null, None, Set(Surprises.Participants)));
    }

    [Fact]
    public void A_pinned_roller_is_of_the_cast()
    {
        var rows = new List<JsonObject>
        {
            Row(1, "roll_pin", null, new JsonObject { ["who"] = Goblin, ["kind"] = "Attack" }),
            Row(2, "game_roll", null, new JsonObject { ["who"] = Goblin }),
        };
        Assert.Empty(Surprises.Find(rows, rows, null, None, None));
    }

    [Fact]
    public void An_unplaced_row_is_listed_and_cannot_be_allowed()
    {
        var unplaced = new JsonArray(new JsonObject { ["kind"] = "made_up", ["rows"] = 3, ["why"] = "kind made_up is not in the registry" });
        var found = Assert.Single(Surprises.Find([], [], unplaced, None, Set("made_up", Surprises.Participants)));
        Assert.Equal(("unplaced", "made_up", 3), (found.Sort, found.What, found.Rows));
    }

    [Fact]
    public void A_folder_allows_a_surprise_to_every_test_under_it()
    {
        var root = GateKit.Testing.Scratch.NewPath("surprises");
        try
        {
            var deep = Path.Combine(root, "combat", "attack");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(root, "_suite.json"), """{"schema": "tests/suite/1", "about": "the tree", "surprises": ["tutorial"]}""");
            File.WriteAllText(Path.Combine(root, "combat", "_suite.json"), """{"schema": "tests/suite/1", "about": "fights", "surprises": ["combat_started", "participants"]}""");
            File.WriteAllText(Path.Combine(deep, "melee.lua"), """
                test { surprises = { "roll" } }
                function run(game) end
                """);
            File.WriteAllText(Path.Combine(root, "plain.lua"), "function run(game) end");
            var found = GameTest.Discover(root);
            Assert.Equal(["combat_started", "participants", "roll", "tutorial"], found[0].AllowedSurprises().Order(StringComparer.Ordinal));
            Assert.Equal(["tutorial"], found[1].AllowedSurprises());
            // a file loaded alone: its own folder is the folder given, nothing above it is read
            Assert.Equal(["roll"], GameTest.Load(Path.Combine(deep, "melee.lua")).AllowedSurprises().Order(StringComparer.Ordinal));
            // a folder given below the root: the root's _suite.json is not read
            Assert.Equal(["combat_started", "participants", "roll"],
                GameTest.Discover(Path.Combine(root, "combat"))[0].AllowedSurprises().Order(StringComparer.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }
}
