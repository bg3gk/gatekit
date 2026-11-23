using System.Text.Json.Nodes;
using GateKit.Cli;

namespace GateKit.UnitTests;

/// <summary>`logic-diff`: two event-log records of one scenario compared with the run's own
/// numbers out, ids made anew numbered, bursts of one tick sorted.</summary>
public sealed class LogicDiffTests
{
    static JsonObject Row(int seq, int tick, string kind, string payload) =>
        (JsonObject)JsonNode.Parse($"{{\"seq\":{seq},\"tick\":{tick},\"t\":{seq * 7},\"kind\":\"{kind}\",\"payload\":{payload}}}")!;

    static List<(string Kind, string Text)> Norm(IEnumerable<JsonObject> rows, IReadOnlyCollection<string>? anchors = null) =>
        LogicDiffCommand.Normalize(rows, [], [], new Dictionary<string, string>(), LogicDiffCommand.DefaultIgnore, 2, true, anchors);

    const string G1 = "11111111-1111-1111-1111-111111111111", G2 = "22222222-2222-2222-2222-222222222222";
    const string H1 = "aaaaaaaa-1111-1111-1111-111111111111", H2 = "bbbbbbbb-2222-2222-2222-222222222222";

    [Fact]
    public void Two_runs_that_differ_only_in_their_clocks_and_new_ids_are_the_same()
    {
        var a = Norm([Row(1, 10, "game_turn", $"{{\"who\":\"{G1}\"}}"), Row(2, 11, "game_hit", $"{{\"by\":\"{G1}\",\"on\":\"{G2}\",\"caller\":123}}")]);
        var b = Norm([Row(5, 90, "game_turn", $"{{\"who\":\"{H1}\"}}"), Row(6, 95, "game_hit", $"{{\"by\":\"{H1}\",\"on\":\"{H2}\",\"caller\":456}}")]);
        Assert.Contains(LogicDiffCommand.Compare(a, b, 5), l => l.StartsWith("THE SAME", StringComparison.Ordinal));
    }

    [Fact]
    public void A_burst_of_one_tick_is_compared_in_any_order_but_a_change_of_order_across_ticks_is_not()
    {
        var a = Norm([Row(1, 10, "game_status", "{\"s\":\"A\"}"), Row(2, 10, "game_status", "{\"s\":\"B\"}")]);
        var b = Norm([Row(1, 10, "game_status", "{\"s\":\"B\"}"), Row(2, 10, "game_status", "{\"s\":\"A\"}")]);
        Assert.Contains(LogicDiffCommand.Compare(a, b, 5), l => l.StartsWith("THE SAME", StringComparison.Ordinal));
        var c = Norm([Row(1, 10, "game_status", "{\"s\":\"B\"}"), Row(2, 11, "game_status", "{\"s\":\"A\"}")]);
        Assert.DoesNotContain(LogicDiffCommand.Compare(a, c, 5), l => l.StartsWith("THE SAME", StringComparison.Ordinal));
    }

    [Fact]
    public void Anchor_rows_fix_which_new_id_gets_which_number()
    {
        // the same creatures act in the same order, but an earlier burst named them the other way round
        var a = Norm([Row(1, 1, "roll_pin", $"{{\"who\":\"{G1}\"}}"), Row(2, 1, "roll_pin", $"{{\"who\":\"{G2}\"}}"),
            Row(3, 5, "game_item", $"{{\"who\":\"{G1}\"}}"), Row(4, 5, "game_item", $"{{\"who\":\"{G2}\"}}"), Row(5, 9, "game_turn", $"{{\"who\":\"{G1}\"}}")], ["roll_pin"]);
        var b = Norm([Row(1, 1, "roll_pin", $"{{\"who\":\"{H1}\"}}"), Row(2, 1, "roll_pin", $"{{\"who\":\"{H2}\"}}"),
            Row(3, 5, "game_item", $"{{\"who\":\"{H2}\"}}"), Row(4, 5, "game_item", $"{{\"who\":\"{H1}\"}}"), Row(5, 9, "game_turn", $"{{\"who\":\"{H1}\"}}")], ["roll_pin"]);
        Assert.Equal(a[^1].Text, b[^1].Text);
    }

    [Fact]
    public void Masked_ids_make_a_burst_of_equal_rows_naming_different_new_ids_the_same_in_any_order()
    {
        static List<(string Kind, string Text)> Masked(IEnumerable<JsonObject> rows) =>
            LogicDiffCommand.Normalize(rows, [], [], new Dictionary<string, string>(), LogicDiffCommand.DefaultIgnore, 2, true, null, maskIds: true);
        var a = Masked([Row(1, 5, "status_applied", $"{{\"who\":\"{G1}\"}}"), Row(2, 5, "status_applied", $"{{\"who\":\"{G2}\"}}"), Row(3, 6, "game_turn", $"{{\"who\":\"{G1}\"}}")]);
        var b = Masked([Row(1, 5, "status_applied", $"{{\"who\":\"{H2}\"}}"), Row(2, 5, "status_applied", $"{{\"who\":\"{H1}\"}}"), Row(3, 6, "game_turn", $"{{\"who\":\"{H2}\"}}")]);
        Assert.Contains(LogicDiffCommand.Compare(a, b, 5), l => l.StartsWith("THE SAME", StringComparison.Ordinal));
    }

    [Fact]
    public void A_window_keeps_the_last_scenario_s_stretch_from_its_first_row_through_the_end_row()
    {
        // an earlier test's scenario in the same Lua state (rows 0-1) is not this one's
        List<JsonObject> rows = [Row(0, 0, "family_on", "{}"), Row(1, 0, "family_off", "{}"),
            Row(1, 1, "game_state", "{}"), Row(2, 2, "family_on", "{}"), Row(3, 3, "game_turn", "{}"),
            Row(4, 4, "family_off", "{}"), Row(5, 5, "game_state", "{}"), Row(6, 6, "family_off", "{}")];
        Assert.Equal([2, 3, 4], LogicDiffCommand.Window(rows, "family_on", "family_off").Select(r => (int)r["seq"]!));
        Assert.Equal([2, 3, 4, 5, 6], LogicDiffCommand.Window(rows, "family_on", null).Select(r => (int)r["seq"]!));
        Assert.Empty(LogicDiffCommand.Window(rows, "combat_started", "family_off"));
        Assert.Equal(8, LogicDiffCommand.Window(rows, null, null).Count);
    }
}
