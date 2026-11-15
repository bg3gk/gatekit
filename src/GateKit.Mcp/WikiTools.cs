using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Wiki;
using ModelContextProtocol.Protocol;

namespace GateKit.Mcp;

/// <summary>bg3.wiki as a tool: the same reader as <c>gatekit wiki</c>. No game is needed.</summary>
static class WikiTools
{
    public static IReadOnlyList<GateKitTool> Create() => [Wiki()];

    static GateKitTool Wiki() => new(
        new Tool
        {
            Name = "bg3_wiki",
            Description =
                "bg3.wiki as context: what players and modders know about a spell, a status, an item, a quest or a rule. Use it to "
                + "decide what to TRY - a rules page describes the order in which the game resolves things, which is the logic the "
                + "systems implement, and a page on a mechanic nobody here has exercised is a list of situations to stage. Spell, "
                + "condition and passive pages carry the game's own identifier (UID), which ties a page to the data index.\n"
                + "It is NOT a source of layouts or of the game's own member names: a name taken from a wiki description is "
                + "inferred, never the game's, and the record has to say so.\n"
                + "Actions: card (START HERE for a spell, action, passive, condition or item: `text` is a stats name such as "
                + "Target_MistyStep, a template name or GUID, a stack id, or a page title; the answer is the wiki's table row as "
                + "fields - level, cost, range, concentration, classes, conditions applied, flags - with the identifiers that lead "
                + "on to `gatekit data stat`), rules (the page `text` as its sections in order, links kept as titles, the wiki's own "
                + "\"needs verifying\" marks listed; `section` picks sections by heading and gives their whole text), table (rows of "
                + "the table `text`, filtered by `where` \"field=text; field=text\"), tables (the tables there are), find (titles "
                + "matching `text`), page (the page `text` as plain text), ids (the game's identifiers on the page `text`), uid (the "
                + "page that carries the stats name `text`). A page that says a thing is cut, unobtainable or NPC-only is telling "
                + "you not to aim a stretch at it.\n"
                + "What the wiki says is CONTEXT: it may go into a `context` or `needs` line with the page's title, never into "
                + "`evidence`. Answers are cached, so a question asked once is free afterwards and still answers without a network; "
                + "the reader paces itself and stops asking for a quarter of an hour when the wiki refuses - never work around that.",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""
                {
                 "type": "object",
                 "properties": {
                  "action": {"type": "string", "enum": ["find", "page", "ids", "uid", "card", "rules", "table", "tables"], "description": "Default find."},
                  "text": {"type": "string", "description": "Search words, a page title, a stats name or GUID, or a table name. Not needed for tables."},
                  "section": {"type": "string", "description": "rules: only sections whose heading contains this, with their whole text."},
                  "where": {"type": "string", "description": "table: conditions \"field=text; field=text\" - a row passes when the field contains the text."},
                  "fields": {"type": "string", "description": "table: the fields to return, comma-separated (default page,name,uid,uuid)."},
                  "limit": {"type": "integer", "description": "table: rows to return (default 30)."},
                  "chars": {"type": "integer", "description": "page, uid: cut the text to this many characters (default 6000)."},
                  "refresh": {"type": "boolean", "description": "Ask the wiki again even when the cache has the answer."}
                 }
                }
                """),
        },
        async (args, cancel) =>
        {
            var reader = new WikiReader { Refresh = args.Flag("refresh") };
            string action = args.Str("action") ?? "find";
            string text = action == "tables" ? "" : args.Need("text");
            int chars = args.Int("chars") ?? 6000;
            string Cut(string s) => s.Length > chars ? s[..chars] : s;
            try
            {
                switch (action)
                {
                    case "find":
                        return Results.Text(new JsonArray((await reader.Find(text, cancel: cancel))
                            .Select(r => (JsonNode)new JsonObject { ["title"] = r.Title, ["snippet"] = r.Snippet }).ToArray()));
                    case "page":
                        return Results.Text(new JsonObject { ["title"] = text, ["text"] = Cut(await reader.Page(text, cancel)) });
                    case "ids":
                        return Results.Text(WikiPrint.Json(await reader.Ids(text, cancel)));
                    case "card":
                        return Results.Text(new JsonArray((await reader.Card(text, cancel)).Select(c => (JsonNode)WikiPrint.Json(c)).ToArray()));
                    case "rules":
                    {
                        var page = await reader.Sections(text, cancel);
                        if (page is null) return Results.Text(new JsonObject { ["title"] = text, ["sections"] = new JsonArray() });
                        var node = WikiPrint.Json(page);
                        if (args.Str("section") is { } wanted)
                            node["sections"] = new JsonArray(page.Sections.Where(s => s.Heading.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                                .Select(s => (JsonNode)new JsonObject { ["level"] = s.Level, ["heading"] = s.Heading, ["text"] = Cut(s.Text),
                                    ["links"] = new JsonArray(s.Links.Select(l => (JsonNode)l).ToArray()), ["unsure"] = new JsonArray(s.Unsure.Select(l => (JsonNode)l).ToArray()) }).ToArray());
                        else
                            foreach (var section in node["sections"]!.AsArray())   // the whole page: headings, links and doubts, the text cut short
                                section!["text"] = ((string)section["text"]!) is { Length: > 400 } long_ ? long_[..400] + "..." : (string)section["text"]!;
                        return Results.Text(node);
                    }
                    case "table":
                    {
                        var rows = await reader.Rows(text, WikiPrint.Where([args.Str("where") ?? ""]), cancel);
                        int limit = args.Int("limit") ?? 30;
                        var fields = (args.Str("fields") ?? "page,name,uid,uuid").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        return Results.Text(new JsonObject
                        {
                            ["rows"] = rows.Count,
                            ["shown"] = new JsonArray(rows.Take(limit).Select(row =>
                            {
                                var kept = new JsonObject();
                                foreach (string field in fields)
                                    if (row[field] is { } value) kept[field] = value.DeepClone();
                                return (JsonNode)kept;
                            }).ToArray()),
                        });
                    }
                    case "tables":
                        return Results.Text(new JsonArray((await reader.Tables(cancel)).Select(t => (JsonNode)t).ToArray()));
                    case "uid":
                    {
                        var page = await reader.Uid(text, cancel);
                        return Results.Text(new JsonObject { ["title"] = page?.Title, ["text"] = page is null ? "" : Cut(page.Value.Text) });
                    }
                    default:
                        throw new InvalidOperationException($"no action '{args.Str("action")}': find, page, ids, uid, card, rules, table or tables");
                }
            }
            catch (WikiException e)
            {
                throw new InvalidOperationException(e.Message);
            }
        });
}
