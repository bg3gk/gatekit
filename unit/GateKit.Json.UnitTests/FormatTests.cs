using System.Text.Json.Nodes;

namespace GateKit.Json.UnitTests;

/// <summary>The one canonical layout.</summary>
public class FormatTests
{
    const string Messy = """
        {"spells":{"cantrips":["EldritchBlast","MageHand"]},"name":"Mirna","level":3,
        "notes":"line one\nline \"two\"\ttab \u2028 sep",
          "inventory":[{"item":"OBJ_Potion_Healing","count":3},{"item":"OBJ_Scroll_Revivify","count":1},{"item":"LOOT_Gold","count":120},{"item":"OBJ_Rope","count":1}],
        "empty":{}, "none":[], "big": 18446744073709551615}
        """;

    [Fact]
    public void The_layout_is_one_line_when_it_fits_and_one_member_a_line_when_not()
    {
        var text = JsonText.Format(JsonText.Parse(Messy, "m").Root);
        Assert.Equal("""
            {
              "spells": {"cantrips": ["EldritchBlast", "MageHand"]},
              "name": "Mirna",
              "level": 3,
              "notes": "line one\nline \"two\"\ttab \u2028 sep",
              "inventory": [
                {"item": "OBJ_Potion_Healing", "count": 3},
                {"item": "OBJ_Scroll_Revivify", "count": 1},
                {"item": "LOOT_Gold", "count": 120},
                {"item": "OBJ_Rope", "count": 1}
              ],
              "empty": {},
              "none": [],
              "big": 18446744073709551615
            }

            """.Replace("\r\n", "\n"), text);
    }

    [Fact]
    public void Formatting_twice_gives_the_same_text_and_the_same_value()
    {
        var once = JsonText.Format(JsonText.Parse(Messy, "m").Root);
        var again = JsonText.Format(JsonText.Parse(once, "m").Root);
        Assert.Equal(once, again);
        Assert.True(JsonNode.DeepEquals(JsonText.Parse(Messy, "a").Root, JsonText.Parse(once, "b").Root));
        Assert.DoesNotContain('\r', once);
        Assert.EndsWith("}\n", once);
    }

    [Fact]
    public void Keys_follow_the_schema_and_others_stay_after_them_as_they_were()
    {
        var row = JsonText.Parse("""{"state":"sighted","capture":"w@unstarted","type":"T::X","side":"server","time":"2026-11-14T09:14:50Z","instance":"w","role":"r","run":"u","stretch":"s","source":"tick"}""", "r").Root;
        Assert.Equal("""{"type":"T::X","side":"server","time":"2026-11-14T09:14:50Z","instance":"w","role":"r","run":"u","stretch":"s","source":"tick","state":"sighted","capture":"w@unstarted"}""",
            JsonText.FormatRow(row, "record/sightings"));
        // nested, through $ref: a lead's creators in the order of $defs/creator
        var lead = JsonText.Parse("""{"creators":[{"confidence":"high","function":"b+0x1","binary":"b"}],"zzz":1,"type":"a::B"}""", "l").Root;
        Assert.Equal("""{"type":"a::B","creators":[{"function":"b+0x1","binary":"b","confidence":"high"}],"zzz":1}""", JsonText.FormatRow(lead, "record/lead"));
    }

    [Fact]
    public void A_container_that_would_pass_the_width_with_its_key_is_broken()
    {
        var wide = new JsonObject { ["k"] = new JsonArray([.. Enumerable.Range(0, 30).Select(i => (JsonNode)i)]) };   // 0..29 inline is 110 wide
        var text = JsonText.Format(wide);
        Assert.Contains("\"k\": [\n    0,\n", text);
        Assert.All(text.Split('\n'), line => Assert.True(line.Length <= JsonText.Width));
    }

    [Fact]
    public void Only_what_must_be_escaped_is()
    {
        Assert.Equal("\"caf\u00e9 + <b> & 'x' \\\\ \\\" \\u0001\"", JsonText.Quote("caf\u00e9 + <b> & 'x' \\ \" \u0001"));
    }
}
