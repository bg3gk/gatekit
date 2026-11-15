using GateKit.Wiki;

namespace GateKit.UnitTests;

public sealed class WikiTests : IDisposable
{
    readonly string cache = GateKit.Testing.Scratch.NewPath("wiki");
    readonly List<string> asked = [];

    public void Dispose()
    {
        if (Directory.Exists(cache)) Directory.Delete(cache, true);
    }

    const string Haste = """
        {"parse": {"title": "Haste", "text": {"*": "<div><p>Haste is a level 3 <b>transmutation</b> spell.</p><style>.x{}</style><div>UID</div><div>Target_Haste</div><div>UUID</div><div>0f4ab5a0-1b0f-4f37-9d1a-1c2b3d4e5f60</div><p>Lasts&nbsp;10 turns &amp; needs concentration.</p></div>"}}}
        """;

    WikiReader Reader(Func<string, string> answer) => new(cache, (url, _) =>
    {
        asked.Add(url);
        return Task.FromResult(answer(url));
    });

    [Fact]
    public void A_rendered_page_becomes_readable_text()
    {
        Assert.Equal("Haste is a level 3 transmutation spell.\nA | B |\nLasts 10 turns & more",
            WikiReader.Plain("<p>Haste is a  level 3 <b>transmutation</b> spell.</p><script>x()</script><tr><td>A</td><td>B</td></tr><p>Lasts&nbsp;10 turns &amp; more</p>"));
    }

    [Fact]
    public async Task A_page_gives_its_text_and_the_games_identifiers()
    {
        var reader = Reader(_ => Haste);
        string text = await reader.Page("Haste");
        Assert.StartsWith("Haste is a level 3 transmutation spell.", text);
        Assert.Contains("Lasts 10 turns & needs concentration.", text);
        var ids = await reader.Ids("Haste");   // nothing in the markup: read from the rendering, as shown
        Assert.Equal(["Target_Haste"], ids.Uid);
        Assert.Equal(["0f4ab5a0-1b0f-4f37-9d1a-1c2b3d4e5f60"], ids.Uuid);
        Assert.Equal(2, asked.Count);   // the markup was asked for; the rendering came from the cache
        Assert.Contains("page=Haste", asked[0]);
        Assert.StartsWith(WikiReader.Api, asked[0]);
    }

    [Fact]
    public async Task What_was_asked_once_answers_again_without_the_wiki()
    {
        await Reader(_ => Haste).Page("Haste");
        var offline = Reader(_ => throw new WikiException("no network"));
        Assert.Contains("transmutation", await offline.Page("Haste"));
        await Assert.ThrowsAsync<WikiException>(() => offline.Page("Something else"));
        offline.Refresh = true;
        await Assert.ThrowsAsync<WikiException>(() => offline.Page("Haste"));
    }

    [Fact]
    public async Task A_stats_name_finds_the_page_that_carries_it()
    {
        var reader = Reader(url => url.Contains("list=search")
            ? """{"query": {"search": [{"title": "Rage", "snippet": "about <span>rage</span>"}, {"title": "Haste", "snippet": "x"}]}}"""
            : url.Contains("page=Haste") ? Haste : """{"parse": {"text": {"*": "<p>Rage, nothing else.</p>"}}}""");
        var page = await reader.Uid("Target_Haste");
        Assert.Equal("Haste", page?.Title);
        Assert.Null(await reader.Uid("Shout_Nothing"));
        Assert.Equal("about rage", (await reader.Find("\"Target_Haste\"", 5))[0].Snippet);
    }

    [Fact]
    public async Task A_missing_page_is_empty_and_a_broken_answer_is_said_plainly()
    {
        Assert.Equal("", await Reader(_ => """{"error": {"code": "missingtitle"}}""").Page("Nope"));
        var broken = await Assert.ThrowsAsync<WikiException>(() => Reader(_ => "<html>busy</html>").Page("Other"));
        Assert.Contains("not JSON", broken.Message);
    }

    // ---- the structured side: tables, cards, sections ----

    const string ActionFields = """{"cargofields": {"name": {"type": "String"}, "image": {"type": "File"}, "uid": {"type": "Text"}, "spell_level": {"type": "String"}, "classes": {"type": "String", "isList": ""}, "is_concentration": {"type": "Boolean"}}}""";

    const string ActionRows = """
        [{"page": "Misty Step", "name": "Misty Step", "uid": "Target_MistyStep", "spell level": "2", "classes": ["Sorcerer", "Wizard", ""], "is concentration": 0},
         {"page": "A Coven of Hags", "name": "A Coven of Hags", "uid": "Shout_HAG_Doubles_Phase2:Version used in Act 1,\nShout_LOW_Hag_Doubles", "spell level": null, "classes": [""], "is concentration": 0},
         {"page": "Mother&#039;s Call", "name": "Mother&#039;s Call", "uid": null, "spell level": "", "classes": [""], "is concentration": 1}]
        """;

    string Structured(string url)
    {
        if (url.Contains("action=cargotables")) return """{"cargotables": ["actions", "weapons"]}""";
        if (url.Contains("action=cargofields") && url.Contains("table=actions")) return ActionFields;
        if (url.Contains("action=cargofields") && url.Contains("table=weapons")) return """{"cargofields": {"name": {"type": "String"}, "uid": {"type": "String"}, "uuid": {"type": "String"}}}""";
        if (url.Contains("action=cargofields")) return """{"cargofields": []}""";
        if (url.StartsWith(WikiReader.Export) && url.Contains("tables=actions")) return ActionRows;
        if (url.StartsWith(WikiReader.Export) && url.Contains("tables=weapons"))
            return """[{"page": "Returning Pike", "name": "Returning Pike", "uid": "MAG_Returning_Pike", "uuid": "6a7e1d02-1b0f-4f37-9d1a-1c2b3d4e5f60"}]""";
        throw new WikiException("not asked for in this test: " + url);
    }

    [Fact]
    public async Task A_table_is_read_whole_once_and_kept_tidy()
    {
        var reader = Reader(Structured);
        var rows = await reader.Table("actions");
        Assert.Equal(3, rows.Count);
        Assert.Equal("""{"page":"Misty Step","name":"Misty Step","uid":"Target_MistyStep","spell_level":"2","classes":["Sorcerer","Wizard"],"is_concentration":0}""", rows[0].ToJsonString());
        Assert.Equal("Mother's Call", (string?)rows[2]["page"]);   // entities decoded; empty values and empty list items dropped
        Assert.Null(rows[2]["uid"]);
        string export = Assert.Single(asked, url => url.StartsWith(WikiReader.Export));
        Assert.Contains(Uri.EscapeDataString("_pageName=page,name,uid,spell_level,classes,is_concentration"), export);   // no File field is asked for
        int before = asked.Count;
        Assert.Equal(3, (await reader.Table("actions")).Count);
        Assert.Equal(before, asked.Count);   // the second reading asked nothing
        var missing = await Assert.ThrowsAsync<WikiException>(() => reader.Table("nothing"));
        Assert.Contains("actions, weapons", missing.Message);
        await Assert.ThrowsAsync<WikiException>(() => reader.Table("actions; drop"));
    }

    [Fact]
    public async Task A_card_answers_for_a_stats_name_a_guid_or_a_title()
    {
        var reader = Reader(url => WikiReader.CardTables.Any(t => t is not ("actions" or "weapons") && url.Contains("table=" + t)) ? """{"cargofields": {"name": {"type": "String"}}}"""
            : url.StartsWith(WikiReader.Export) && !url.Contains("tables=actions") && !url.Contains("tables=weapons") ? "[]" : Structured(url));
        var spell = Assert.Single(await reader.Card("Target_MistyStep"));
        Assert.Equal(("actions", "Misty Step"), (spell.Table, spell.Page));
        Assert.Equal("2", (string?)spell.Row["spell_level"]);
        Assert.DoesNotContain(asked, url => url.Contains("tables=weapons"));   // found in the first table: the others were not read

        // several identifiers in one field, each with a note after a colon
        Assert.Equal(["Shout_HAG_Doubles_Phase2", "Shout_LOW_Hag_Doubles"], Assert.Single(await reader.Card("Shout_LOW_Hag_Doubles")).Ids);
        Assert.Equal("Returning Pike", Assert.Single(await reader.Card("6a7e1d02-1b0f-4f37-9d1a-1c2b3d4e5f60")).Page);
        Assert.Equal("Misty Step", Assert.Single(await reader.Card("misty step")).Page);
        Assert.Empty(await reader.Card("Target_Nothing"));
        Assert.Contains("ids: Target_MistyStep", WikiPrint.Card(spell));
    }

    [Fact]
    public async Task Rows_are_filtered_by_what_a_field_contains()
    {
        var reader = Reader(Structured);
        Assert.Equal("Misty Step", (string?)Assert.Single(await reader.Rows("actions", WikiPrint.Where(["classes=wizard; spell level=2"])))["page"]);
        Assert.Equal(3, (await reader.Rows("actions", [])).Count);
        Assert.Contains("no field 'school'", (await Assert.ThrowsAsync<WikiException>(() => reader.Rows("actions", [("school", "x")]))).Message);
        Assert.Throws<WikiException>(() => WikiPrint.Where(["classes"]));
    }

    const string Rules = """
        {"parse": {"title": "Resting", "revid": 4242, "wikitext": {"*": "{{PageSeo\n| description = x\n}}\n'''Resting''' restores [[hit points]].<!-- hidden -->\n\n== Long rest ==\n[[File:Rest.webp|thumb|A [[camp]] at night]]\nTaken [[Campsite|at camp]]; costs 40 {{SmRarityItem|Camp Supplies}}.{{ref|a note}} Has {{Advantage}}.{{verify|What ends it?}}\n\n=== Partial rest ===\nRestores half.\n\n== Empty ==\n{{NavGameplay}}\n[[Category:Gameplay mechanics]]"}}}
        """;

    [Fact]
    public async Task A_rules_page_is_its_sections_in_order_with_links_and_doubts()
    {
        var page = await Reader(_ => Rules).Sections("Resting");
        Assert.NotNull(page);
        Assert.Equal(("Resting", 4242L), (page.Title, page.Revision));
        Assert.Equal(["", "Long rest", "Partial rest"], page.Sections.Select(s => s.Heading));
        Assert.Equal([1, 2, 3], page.Sections.Select(s => s.Level));
        Assert.Equal("Resting restores hit points.", page.Sections[0].Text);
        Assert.Equal("Taken at camp; costs 40 Camp Supplies. Has Advantage.", page.Sections[1].Text);
        Assert.Equal(["Campsite", "Camp Supplies"], page.Sections[1].Links);
        Assert.Equal(["What ends it?"], page.Sections[1].Unsure);
        Assert.Contains("[the wiki is not sure: What ends it?]", WikiPrint.Page(page));
        Assert.Null(await Reader(_ => """{"error": {"code": "missingtitle"}}""").Sections("Nope"));
    }

    [Fact]
    public async Task Identifiers_are_read_from_the_pages_own_parameters()
    {
        var ids = await Reader(_ => """
            {"parse": {"title": "Dammon", "revid": 7, "wikitext": {"*": "{{Infobox creature\n| name = Dammon\n| uuid = e2ad06ec-8034-479a-9f69-b86faea6dc79\n| uid = S_DEN_Weaponsmith\n| stats = Tiefling_Commoner\n| treasure = DEN_Weaponsmith_Trade\n| treasure2 = HAV_Weaponsmith_Trade\n| stack id = \n}}\nText."}}}
            """).Ids("Dammon");
        Assert.Equal(["S_DEN_Weaponsmith"], ids.Uid);
        Assert.Equal(["e2ad06ec-8034-479a-9f69-b86faea6dc79"], ids.Uuid);
        Assert.Equal(["Tiefling_Commoner"], ids.Stats);
        Assert.Equal(["DEN_Weaponsmith_Trade", "HAV_Weaponsmith_Trade"], ids.Treasure);
        Assert.Empty(ids.StackId);
        var condition = await Reader(_ => """{"parse": {"title": "Mage Armour (condition)", "revid": 8, "wikitext": {"*": "{{condition page\n| stack id = MAGE_ARMOR\n}}"}}}""").Ids("Mage Armour (condition)");
        Assert.Equal(["MAGE_ARMOR"], condition.StackId);
        Assert.Equal("UID  \nUUID \nstack id MAGE_ARMOR", WikiPrint.Ids(condition));
    }

    [Fact]
    public async Task A_refusal_is_remembered_and_nothing_is_asked_until_it_has_passed()
    {
        var clock = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var reader = new WikiReader(cache, (url, _) =>
        {
            asked.Add(url);
            if (url.StartsWith(WikiReader.Export)) throw new WikiRefusedException();
            return Task.FromResult(ActionFields);
        }) { Now = () => clock };
        var first = await Assert.ThrowsAsync<WikiException>(() => reader.Table("actions"));
        Assert.Contains("refused", first.Message);
        int afterRefusal = asked.Count;
        var second = await Assert.ThrowsAsync<WikiException>(() => reader.Table("actions"));
        Assert.Contains("12:15", second.Message);
        Assert.Equal(afterRefusal, asked.Count);   // the wiki was not asked again
        await reader.Fields("actions");            // the API is another endpoint and was not refused
        clock += WikiReader.Quiet + TimeSpan.FromSeconds(1);
        await Assert.ThrowsAsync<WikiException>(() => reader.Table("actions"));
        Assert.Equal(afterRefusal + 1, asked.Count);   // asked once more, after the quiet time
    }

    [Fact]
    public void The_command_has_the_subcommands_the_prompts_name()
    {
        var command = WikiCommands.CreateCommand();
        Assert.Equal("wiki", command.Name);
        Assert.Equal(["card", "find", "ids", "page", "rules", "snapshot", "table", "tables", "uid"], command.Subcommands.Select(c => c.Name).Order());
    }
}
