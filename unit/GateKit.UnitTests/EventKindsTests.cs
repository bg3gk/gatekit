using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Bridge;
using static GateKit.UnitTests.EventLogTests;

namespace GateKit.UnitTests;

/// <summary>The registry of the event log (Shared/EventKinds.json): one place for every family and
/// kind. A family added to it - here a made-up one - is stored, merged, filtered, counted and folded
/// into attention with no change to the reader; a kind it does not have is refused.</summary>
public sealed partial class EventKindsTests : IDisposable
{
    readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GateKit.slnx"))) return dir.FullName;
        throw new InvalidOperationException("the tests run outside a checkout");
    }

    static string Lua => Path.Combine(RepoRoot(), "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua");

    /// <summary>The embedded registry with one family and two kinds more, as a person would add them.</summary>
    static EventKinds WithWeather() => EventKinds.Parse(WeatherText(), "test registry");

    static string WeatherText(Action<JsonObject>? more = null)
    {
        var registry = JsonNode.Parse(File.ReadAllText(Path.Combine(Lua, "Shared", "EventKinds.json")))!.AsObject();
        registry["families"]!["weather"] = new JsonObject { ["about"] = "rain starting and stopping (made up by this test)" };
        registry["kinds"]!["rain_started"] = JsonNode.Parse("""{"family": "weather", "class": "observation", "knows": "player", "attention": "begin", "ends": "rain_ended", "payload": {"heavy": "how hard"}}""");
        registry["kinds"]!["rain_ended"] = JsonNode.Parse("""{"family": "weather", "class": "observation", "knows": "player", "attention": "end", "payload": {"opened": "the row that opened it"}}""");
        more?.Invoke(registry);
        return GateKit.Json.JsonText.Inline(registry);
    }

    /// <summary>The registry a side's game loaded, as the mod copies it into the channel.</summary>
    internal static void WriteGameKinds(string channel, string side, string text) =>
        File.WriteAllText(Path.Combine(channel, $"events_{side}_kinds.json"), text);

    [Fact]
    public void A_registry_newer_than_the_hosts_places_the_rows_by_the_games_and_drops_none()
    {
        // the game's registry: weather added, game_damage always omniscient, tutorial_shown gone
        WriteGameKinds(_folder.Path, "server", WeatherText(r =>
        {
            var damage = r["kinds"]!["game_damage"]!.AsObject();
            damage["knows"] = "omniscient";
            damage.Remove("knowsPerRow");
            r["kinds"]!.AsObject().Remove("tutorial_shown");
        }));
        WriteLog(_folder.Path, "server", 1, 8, Row(1, "game_state", 10),
            Row(2, "rain_started", 20, ""","key":"weather","attention":"begin","payload":{"heavy":true}"""),
            Row(3, "game_damage", 30).Replace("\"player\"", "\"omniscient\"", StringComparison.Ordinal),
            Row(4, "tutorial_shown", 40, ""","attention":"news" """));
        var log = new EventLogReader(_folder.Path);   // built with the embedded registry
        var stream = new EventStream(log);
        var rows = stream.Since(null).Rows;
        Assert.Equal(["game_state", "rain_started", "game_damage", "tutorial_shown"], rows.Select(r => (string)r["kind"]!));
        Assert.All(rows, r => Assert.Null(r["unplaced"]));
        Assert.Empty(stream.Unplaced(null));
        Assert.Equal("weather", log.FamilyOf("rain_started"));
        // said once, naming what differs; information, not a fault
        var line = log.KindsMismatch()!;
        Assert.Contains("kinds added: rain_ended, rain_started", line, StringComparison.Ordinal);
        Assert.Contains("kinds removed: tutorial_shown", line, StringComparison.Ordinal);
        Assert.Contains("game_damage (knows)", line, StringComparison.Ordinal);
        Assert.Contains("the host follows the game", line, StringComparison.Ordinal);
        // the fair filter by the game's registry: its game_damage is omniscient now
        Assert.Equal(["game_state", "rain_started", "tutorial_shown"],
            new EventStream(log) { Fair = true }.Since(null).Rows.Select(r => (string)r["kind"]!));
        // the game reloads with this build's registry: the host follows it
        WriteGameKinds(_folder.Path, "server", File.ReadAllText(Path.Combine(Lua, "Shared", "EventKinds.json")));
        Assert.Null(log.KindsMismatch());
        Assert.Contains("the same as this build's", log.KindsLine(), StringComparison.Ordinal);
        File.Delete(log.KindsPath(Side.Server));
        Assert.Contains("this build's registry", log.KindsLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_no_registry_places_is_delivered_marked_said_and_fails_a_negative_check()
    {
        WriteGameKinds(_folder.Path, "server", WeatherText());
        WriteLog(_folder.Path, "server", 1, 8, Row(1, "game_state", 10), Row(2, "thunder", 25), Row(3, "thunder", 26));
        var stream = new EventStream(new EventLogReader(_folder.Path));
        var mark = "server:1.0";
        var thunder = stream.Since(mark, r => (string)r["kind"]! == "thunder").Rows;
        // "no thunder happened" cannot pass: the rows are there, marked
        Assert.NotEmpty(thunder);
        Assert.All(thunder, r => Assert.Contains("neither", (string)r["unplaced"]!, StringComparison.Ordinal));
        var said = Assert.Single(stream.Unplaced(mark));
        Assert.Equal("thunder", (string)said!["kind"]!);
        Assert.Equal(2, (long)said["rows"]!);
        Assert.Null(said["withheld"]);
        var (rows, next) = stream.Since(null);
        var answer = new JsonObject { ["rows"] = new JsonArray([.. rows]), ["next"] = next, ["unplaced"] = stream.Unplaced(null, next) };
        Assert.Empty(GateKit.Json.SchemaSet.Embedded.Validate(answer, "events/answer"));
        // a fair stream takes it as omniscient, and says it withheld it
        var fair = new EventStream(new EventLogReader(_folder.Path)) { Fair = true };
        Assert.Equal(["game_state"], fair.Since(null).Rows.Select(r => (string)r["kind"]!));
        Assert.True((bool)Assert.Single(fair.Unplaced(null))!["withheld"]!);
    }

    [Fact]
    public void The_registry_holds_together()
    {
        var kinds = EventKinds.Embedded;
        Assert.Contains("attention", kinds.Families.Keys);
        Assert.All(kinds.Kinds.Values.Where(k => k.Attention is "begin" or "hold"), k => Assert.NotNull(k.Ends));
        Assert.Equal("game", kinds.FamilyOf("game_damage"));
        Assert.Null(kinds.FamilyOf("DialogStarted"));
        // a begin whose end is missing is refused when the registry is read
        var broken = File.ReadAllText(Path.Combine(Lua, "Shared", "EventKinds.json")).Replace("\"ends\": \"trade_ended\"", "\"ends\": \"trade_gone\"", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => EventKinds.Parse(broken, "broken"));
    }

    [GeneratedRegex(@"(?:Append|Push)\(\s*""([a-z_]+)""\s*[,)]")]
    private static partial Regex Emitted();

    [Fact]
    public void Every_kind_the_mod_writes_by_name_is_in_the_registry()
    {
        var kinds = EventKinds.Embedded;
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Lua, "*.lua", SearchOption.AllDirectories))
            foreach (Match m in Emitted().Matches(File.ReadAllText(file)))
                if (kinds.FamilyOf(m.Groups[1].Value) is null) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
        Assert.Empty(missing);
    }

    [Fact]
    public void A_family_added_to_the_registry_is_read_filtered_counted_and_waits_with_no_change_to_the_reader()
    {
        var log = new EventLogReader(_folder.Path, WithWeather());
        WriteLog(_folder.Path, "server", 1, 8, Row(1, "game_state", 10),
            Row(2, "rain_started", 20, ""","key":"weather","attention":"begin","payload":{"heavy":true}"""),
            Row(3, "thunder", 25));
        var stream = new EventStream(log);
        // the made-up kinds are rows like any other; a kind the registry lacks is kept, marked and said
        Assert.Equal(["game_state", "rain_started", "thunder"], stream.Since(null).Rows.Select(r => (string)r["kind"]!));
        Assert.Equal(["thunder"], stream.Since(null).Rows.Where(r => r["unplaced"] is not null).Select(r => (string)r["kind"]!));
        Assert.Contains(log.Problems, p => p.Contains("thunder", StringComparison.Ordinal));
        Assert.Equal(["rain_started"], stream.Since(null, r => log.Kinds.FamilyOf((string)r["kind"]!) == "weather").Rows.Select(r => (string)r["kind"]!));
        // it waits for an answer like a window does, and is told when it ends
        var attention = new AttentionReader(log);
        var cursor = new AttentionCursor();
        Assert.Equal(["unplaced:thunder", "rain_started:"], attention.Report(cursor).Select(i => $"{i!["what"]}:{i["kind"]}"));
        WriteLog(_folder.Path, "server", 1, 8, Row(1, "game_state", 10),
            Row(2, "rain_started", 20, ""","key":"weather","attention":"begin","payload":{"heavy":true}"""),
            Row(3, "thunder", 25), Row(4, "rain_ended", 40, ""","key":"weather","attention":"end" """));
        Assert.Equal(["rain_ended"], attention.Report(cursor).Select(i => (string)i!["what"]!));
        // the same rows against the registry of this build, with no game's: the family is unknown, the rows kept and marked
        Assert.Equal(["rain_started", "thunder", "rain_ended"], new EventStream(new EventLogReader(_folder.Path)).Since(null).Rows
            .Where(r => r["unplaced"] is not null).Select(r => (string)r["kind"]!));
    }

    [Fact]
    public void A_row_that_says_other_than_its_kind_declares_is_kept_and_marked()
    {
        var log = new EventLogReader(_folder.Path);
        WriteLog(_folder.Path, "client", 1, 8,
            Row(1, "trade", 10, ""","key":"window:trade","attention":"news" """),
            Row(2, "osiris", 20).Replace("omniscient", "player", StringComparison.Ordinal),
            Row(3, "game_damage", 30).Replace("\"player\"", "\"omniscient\"", StringComparison.Ordinal));
        // a game-log row the party did not perceive may say omniscient (knowsPerRow); the others contradict their kind
        var rows = log.Read(Side.Client)!.Rows;
        Assert.Equal([1L, 2, 3], rows.Select(r => (long)r["seq"]!));
        Assert.Equal([1L, 2], rows.Where(r => r["unplaced"] is not null).Select(r => (long)r["seq"]!));
        Assert.Contains("attention", (string)rows[0]["unplaced"]!, StringComparison.Ordinal);
        // a fair stream gives none of them: the marked trade and osiris say player, and are withheld
        Assert.Empty(new EventStream(log) { Fair = true }.Since(null).Rows);
    }
}
