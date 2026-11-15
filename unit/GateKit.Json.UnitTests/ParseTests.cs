using System.Text.Json.Nodes;

namespace GateKit.Json.UnitTests;

/// <summary>Strict reading: the typical slips of a hand-written file (M1-M8), exact numbers, and
/// where errors point.</summary>
public class ParseTests
{
    /// <summary>A hand-written roster entry.</summary>
    const string Roster = """
        {
          "id": "test-warlock-3",
          "name": "Mirna",
          "race": "Tiefling",
          "subrace": "Asmodeus",
          "class": "Warlock",
          "subclass": "TheFiend",
          "level": 3,
          "background": "Acolyte",
          "abilities": {
            "Strength": 8,
            "Dexterity": 14,
            "Constitution": 14,
            "Intelligence": 10,
            "Wisdom": 12,
            "Charisma": 17
          },
          "spells": {
            "cantrips": ["EldritchBlast", "MageHand"],
            "known": ["Hex", "ArmorOfAgathys", "HoldPerson", "MistyStep"]
          },
          "invocations": ["AgonizingBlast", "RepellingBlast"],
          "equipment": {
            "MeleeMainHand": "WPN_Dagger",
            "RangedMainHand": "WPN_LightCrossbow",
            "Breast": "ARM_Leather_Body",
            "Boots": "ARM_Boots_Leather"
          },
          "inventory": [
            {"item": "OBJ_Potion_Healing", "count": 3},
            {"item": "OBJ_Scroll_Revivify", "count": 1},
            {"item": "LOOT_Gold", "count": 120}
          ],
          "notes": "Spends both Pact Magic slots on Hex before the short rest.\nKeep Charisma at 17: the Eldritch Blast damage test reads +3."
        }

        """;

    static string Slip(string from, string to)
    {
        Assert.Contains(from, Roster);
        return Roster.Replace(from, to);
    }

    static JsonProblem Refused(string text)
    {
        var e = Assert.Throws<JsonProblemException>(() => JsonText.Parse(text, "roster.json"));
        return Assert.Single(e.Problems);
    }

    [Fact]
    public void M1_a_missing_closing_quote_is_refused_at_its_line()
    {
        var p = Refused(Slip("\"race\": \"Tiefling\",", "\"race\": \"Tiefling,"));
        Assert.Equal(4, p.Line);
        Assert.Contains("closing quote is missing", p.What);
        Assert.StartsWith("roster.json:4:", p.ToString());
    }

    [Fact]
    public void M2_a_line_indented_wrong_reads_as_meant() =>
        Assert.True(JsonNode.DeepEquals(JsonText.Parse(Roster, "a").Root, JsonText.Parse(Slip("    \"known\":", "  \"known\":"), "b").Root));

    [Fact]
    public void M3_a_trailing_comma_is_refused_at_its_line()
    {
        var p = Refused(Slip("\"MistyStep\"]", "\"MistyStep\",]"));
        Assert.Equal(20, p.Line);
        Assert.Contains("trailing commas", p.What);
    }

    [Fact]
    public void M4_a_tab_in_the_indentation_reads_as_meant() =>
        Assert.True(JsonNode.DeepEquals(JsonText.Parse(Roster, "a").Root, JsonText.Parse(Slip("    \"known\":", "\t\"known\":"), "b").Root));

    [Fact]
    public void M5_a_missing_separator_is_refused_at_its_line()
    {
        var p = Refused(Slip("\"EldritchBlast\", \"MageHand\"", "\"EldritchBlast\" \"MageHand\""));
        Assert.Equal(19, p.Line);
        Assert.Contains("expected ','", p.What);
    }

    [Fact]
    public void M6_a_quote_typed_as_is_is_refused_at_its_line()
    {
        var p = Refused(Slip("\"subclass\": \"TheFiend\"", "\"subclass\": \"The \"Fiend\" pact #2\""));
        Assert.Equal(7, p.Line);
        Assert.Contains("found 'F'", p.What);
    }

    [Fact]
    public void M7_a_file_cut_off_is_refused_at_its_end()
    {
        var cut = Roster[..(int)(Roster.Length * 0.7)];
        var p = Refused(cut);
        Assert.Equal(cut.Split('\n').Length, p.Line);
    }

    [Fact]
    public void M8_a_key_written_twice_is_refused_at_the_second()
    {
        var p = Refused(Slip("\"level\": 3,", "\"level\": 3,\n  \"level\": 4,"));
        Assert.Equal(9, p.Line);
        Assert.Equal(3, p.Column);
        Assert.Equal("duplicate", p.Rule);
    }

    [Fact]
    public void A_comment_is_refused_and_told_where_notes_go()
    {
        var p = Refused("{\n  // the level\n  \"level\": 3\n}");
        Assert.Equal(2, p.Line);
        Assert.Contains("`about`", p.What);
    }

    [Fact]
    public void A_byte_order_mark_an_empty_text_and_a_lone_null_are_refused()
    {
        Assert.Contains("byte order mark", Refused("﻿{}").What);
        Assert.Contains("empty", Refused("  \n").What);
        Assert.Contains("only `null`", Refused("null").What);
    }

    [Fact]
    public void Numbers_stay_exactly_as_written()
    {
        var text = "{\"max\": 18446744073709551615, \"min\": -9223372036854775808, \"big\": 123456789012345678901234567890, "
            + "\"micro\": 1763026000123456, \"pi\": 3.14159265358979323846, \"exp\": 1e400}";
        var root = JsonText.Parse(text, "n").Root;
        Assert.Equal(ulong.MaxValue, root["max"]!.GetValue<ulong>());
        Assert.Equal(long.MinValue, root["min"]!.GetValue<long>());
        Assert.Equal(1763026000123456L, root["micro"]!.GetValue<long>());
        var formatted = JsonText.Format(root);
        foreach (var raw in new[] { "18446744073709551615", "-9223372036854775808", "123456789012345678901234567890", "3.14159265358979323846", "1e400" })
            Assert.Contains(raw, formatted);
    }

    [Fact]
    public void A_ledger_row_is_placed_on_its_line_of_the_file()
    {
        var e = Assert.Throws<JsonProblemException>(() => JsonText.Parse("{\"a\": 1,}", "x.jsonl", firstLine: 412));
        Assert.Equal(412, e.Problems[0].Line);
        Assert.StartsWith("x.jsonl:412:", e.Message);
    }

    [Fact]
    public void Positions_of_values_are_found_by_pointer()
    {
        var parsed = JsonText.Parse(Roster, "roster.json");
        Assert.Equal((19, 5), parsed.PositionOf("/spells/cantrips"));
        Assert.Equal((31, 6), parsed.PositionOf("/inventory/1/item"));
        Assert.Equal((10, 3), parsed.PositionOf("/abilities/Luck"));   // absent: its object
        Assert.Equal((1, 1), parsed.PositionOf(""));
    }

    [Fact]
    public void Text_and_unicode_columns_are_counted_in_characters()
    {
        // two of the letters are two bytes each in UTF-8; the column counts them once
        var e = Assert.Throws<JsonProblemException>(() => JsonText.Parse("{\"name\": \"Mïrnå\" \"x\"}", "u"));
        Assert.Equal(18, e.Problems[0].Column);
    }
}
