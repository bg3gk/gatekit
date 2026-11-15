using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Wiki;

/// <summary>The <c>wiki</c> command.</summary>
public static class WikiCommands
{
    const string About =
        "bg3.wiki as context: what players and modders know about a spell, a status, an item or a rule\n\n"
        + "Its spell, action, passive and item pages carry the game's own identifier (UID), which ties a page to the data index, "
        + "and the wiki keeps those pages as tables too: `card` answers for an identifier with fields - level, cost, classes, "
        + "the conditions applied - instead of a page of text. Its rules pages describe the order in which the game resolves "
        + "things; `rules` gives one as its sections. It says what players can do and what a thing needs; it is not a source of layouts or of the game's "
        + "own member names: a name taken from a wiki description is inferred, never the game's. Answers are cached outside "
        + "the repository (GATEKIT_WIKI_CACHE), so a question asked once answers again without a network.";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Command CreateCommand()
    {
        var root = new Command("wiki", About);
        var json = new Option<bool>("--json") { Description = "the answer as JSON", Recursive = true };
        var refresh = new Option<bool>("--refresh") { Description = "ask the wiki again even when the cache has the answer", Recursive = true };
        var chars = new Option<int>("--chars") { Description = "cut a page to this many characters", DefaultValueFactory = _ => 6000 };
        root.Options.Add(json);
        root.Options.Add(refresh);

        Command Verb(string name, string help, string argument, Func<WikiReader, string, ParseResult, CancellationToken, Task<(string Text, JsonNode Json, bool Found)>> body, bool cut = false)
        {
            var words = new Argument<string[]>(argument) { Arity = ArgumentArity.OneOrMore };
            var command = new Command(name, help) { words };
            if (cut) command.Options.Add(chars);
            command.SetAction(async (p, cancel) =>
            {
                var output = p.InvocationConfiguration.Output;
                try
                {
                    var reader = new WikiReader { Refresh = p.GetValue(refresh) };
                    var (text, node, found) = await body(reader, string.Join(" ", p.GetValue(words)!), p, cancel);
                    output.WriteLine(p.GetValue(json) ? node.ToJsonString(Indented).ReplaceLineEndings("\n") : text);
                    return found ? 0 : 1;
                }
                catch (WikiException e)
                {
                    p.InvocationConfiguration.Error.WriteLine(e.Message);
                    return 1;
                }
            });
            return command;
        }
        static string Cut(string text, int limit) => text.Length > limit ? text[..limit] : text;

        root.Subcommands.Add(Verb("find", "titles that match (full-text search)", "words", async (reader, words, _, cancel) =>
        {
            var rows = await reader.Find(words, cancel: cancel);
            return (rows.Count == 0 ? "nothing matches" : string.Join("\n", rows.Select(r => $"{r.Title}: {Cut(r.Snippet, 160)}")),
                new JsonArray(rows.Select(r => (JsonNode)new JsonObject { ["title"] = r.Title, ["snippet"] = r.Snippet }).ToArray()), rows.Count > 0);
        }));
        root.Subcommands.Add(Verb("page", "a page as plain text", "title", async (reader, title, p, cancel) =>
        {
            string text = Cut(await reader.Page(title, cancel), p.GetValue(chars));
            return (text.Length > 0 ? text : "no such page", new JsonObject { ["title"] = title, ["text"] = text }, text.Length > 0);
        }, cut: true));
        root.Subcommands.Add(Verb("ids", "the game's identifiers on a page: UID (stats or template names), UUID (GUIDs), stats entry, a status's stack id, treasure tables", "title", async (reader, title, _, cancel) =>
        {
            var ids = await reader.Ids(title, cancel);
            return (WikiPrint.Ids(ids), WikiPrint.Json(ids), ids.Count > 0);
        }));
        root.Subcommands.Add(Verb("uid", "the page of a spell, status or passive by its name in the game's stats (\"Shout_Rage\", \"HASTE\")", "name", async (reader, name, p, cancel) =>
        {
            var page = await reader.Uid(name, cancel);
            if (page is null) return ("no page carries this identifier", new JsonObject { ["title"] = null, ["text"] = "" }, false);
            string text = Cut(page.Value.Text, p.GetValue(chars));
            return ($"== {page.Value.Title}\n{text}", new JsonObject { ["title"] = page.Value.Title, ["text"] = text }, true);
        }, cut: true));
        root.Subcommands.Add(Verb("card", "a thing of the game as fields, from the wiki's tables: by stats name, template name or GUID, stack id, or page title", "key", async (reader, key, _, cancel) =>
        {
            var cards = await reader.Card(key, cancel);
            return (cards.Count == 0 ? "no row of the wiki's tables answers for this" : string.Join("\n\n", cards.Select(WikiPrint.Card)),
                new JsonArray(cards.Select(c => (JsonNode)WikiPrint.Json(c)).ToArray()), cards.Count > 0);
        }));
        root.Subcommands.Add(Verb("rules", "a page as its sections in order, links kept as titles, the wiki's own \"needs verifying\" marks listed", "title", async (reader, title, p, cancel) =>
        {
            var page = await reader.Sections(title, cancel);
            if (page is null) return ("no such page", new JsonObject { ["title"] = title, ["sections"] = new JsonArray() }, false);
            return (Cut(WikiPrint.Page(page), p.GetValue(chars)), WikiPrint.Json(page), true);
        }, cut: true));

        var tables = new Command("tables", "the wiki's tables, or with a name the fields of one");
        var which = new Argument<string?>("table") { Arity = ArgumentArity.ZeroOrOne };
        tables.Arguments.Add(which);
        tables.SetAction((p, cancel) => Run(p, async reader =>
        {
            if (p.GetValue(which) is { } one)
            {
                var fields = await reader.Fields(one, cancel);
                return (string.Join("\n", fields.Select(f => $"{f.Name,-28} {f.Type}{(f.List ? " (list)" : "")}")),
                    new JsonArray(fields.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["type"] = f.Type, ["list"] = f.List }).ToArray()), true);
            }
            var names = await reader.Tables(cancel);
            return (string.Join("\n", names), new JsonArray(names.Select(n => (JsonNode)n).ToArray()), names.Count > 0);
        }));
        root.Subcommands.Add(tables);

        var table = new Command("table", "rows of one table, read whole into the cache the first time and answered locally after that");
        var name = new Argument<string>("table");
        var where = new Option<string[]>("--where") { Description = "field=text: keep rows whose field contains the text (a list field: one of its items); may be repeated", AllowMultipleArgumentsPerToken = true };
        var show = new Option<string?>("--fields") { Description = "the fields to print, comma-separated (default: page and the identifiers)" };
        var limit = new Option<int>("--limit") { Description = "rows to print", DefaultValueFactory = _ => 40 };
        table.Arguments.Add(name);
        table.Options.Add(where);
        table.Options.Add(show);
        table.Options.Add(limit);
        table.SetAction((p, cancel) => Run(p, async reader =>
        {
            var conditions = WikiPrint.Where(p.GetValue(where) ?? []);
            var rows = await reader.Rows(p.GetValue(name)!, conditions, cancel);
            var fields = (p.GetValue(show) ?? "page,name,uid,uuid").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var shown = rows.Take(p.GetValue(limit)).ToList();
            string text = string.Join("\n", shown.Select(row => string.Join("   ", fields.Where(f => row[f] is not null).Select(f => $"{f}={WikiPrint.Value(row[f]!, 80)}"))));
            return ($"{rows.Count} rows" + (rows.Count > shown.Count ? $", the first {shown.Count}" : "") + (text.Length > 0 ? "\n" + text : ""),
                new JsonObject { ["rows"] = rows.Count, ["shown"] = new JsonArray(shown.Select(r => (JsonNode)r.DeepClone()).ToArray()) }, rows.Count > 0);
        }));
        root.Subcommands.Add(table);

        var snapshot = new Command("snapshot", "read every table a card is looked for in into the cache, slowly, stopping at the first refusal");
        snapshot.SetAction((p, cancel) => Run(p, async reader =>
        {
            var lines = new List<string>();
            var counts = new JsonObject();
            foreach (string one in WikiReader.CardTables)
            {
                int rows = (await reader.Table(one, cancel)).Count;
                lines.Add($"{one,-14} {rows} rows");
                counts[one] = rows;
            }
            return (string.Join("\n", lines), counts, true);
        }));
        root.Subcommands.Add(snapshot);
        return root;

        async Task<int> Run(ParseResult p, Func<WikiReader, Task<(string Text, JsonNode Json, bool Found)>> body)
        {
            try
            {
                var (text, node, found) = await body(new WikiReader { Refresh = p.GetValue(refresh) });
                p.InvocationConfiguration.Output.WriteLine(p.GetValue(json) ? node.ToJsonString(Indented).ReplaceLineEndings("\n") : text);
                return found ? 0 : 1;
            }
            catch (WikiException e)
            {
                p.InvocationConfiguration.Error.WriteLine(e.Message);
                return 1;
            }
        }
    }
}
