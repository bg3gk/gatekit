using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Wiki;

/// <summary>One row of a wiki table that answers for a game identifier or a title.</summary>
/// <param name="Table">The Cargo table the row is in.</param>
/// <param name="Page">The wiki page the row was stored from.</param>
/// <param name="Ids">The game's identifiers on the row: stats names, template names and GUIDs, a status's stack id.</param>
/// <param name="Row">The row's fields that hold something, by the table's own field names.</param>
public sealed record WikiCard(string Table, string Page, IReadOnlyList<string> Ids, JsonObject Row);

/// <summary>
/// The wiki's structured side. bg3.wiki fills Cargo tables from its page templates - one row per
/// spell, action, passive, reaction, condition, weapon, piece of equipment, item, camp event - and
/// most rows carry the game's own identifier. That is what makes a page a field-by-field answer
/// rather than a wall of text, and what ties it to the data index in both directions.
///
/// A table is read whole, once, into the cache (one request for most tables), and every question
/// after that is answered locally. The rows are the wiki's: they stay in the cache, outside the
/// repository.
/// </summary>
public sealed partial class WikiReader
{
    const int PageOfRows = 5000;

    /// <summary>The tables a card is looked for in, in this order.</summary>
    public static readonly IReadOnlyList<string> CardTables = ["actions", "passives", "reactions", "conditions", "weapons", "equipment", "misc_items", "camp_events"];

    static readonly string[] IdFields = ["uid", "uuid", "stats", "stack_id"];

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex TableName();

    /// <summary>The names of the wiki's tables.</summary>
    public async Task<List<string>> Tables(CancellationToken cancel = default)
    {
        var answer = await Query(cancel, ("action", "cargotables"));
        return (answer["cargotables"] as JsonArray ?? []).Select(t => (string?)t ?? "").Where(t => t.Length > 0).ToList();
    }

    /// <summary>The fields of a table: name, Cargo type, and whether the field holds a list.</summary>
    public async Task<List<(string Name, string Type, bool List)>> Fields(string table, CancellationToken cancel = default)
    {
        if (!TableName().IsMatch(table)) throw new WikiException($"'{table}' is not a table name");
        var answer = await Query(cancel, ("action", "cargofields"), ("table", table));
        if (answer["cargofields"] is not JsonObject fields || fields.Count == 0)
        {
            List<string> known;
            try { known = await Tables(cancel); }
            catch (WikiException) { known = []; }
            throw new WikiException($"the wiki has no table '{table}'" + (known.Count > 0 ? ": " + string.Join(", ", known) : ""));
        }
        return fields.Select(f => (f.Key, (string?)f.Value?["type"] ?? "", f.Value?["isList"] is not null)).ToList();
    }

    /// <summary>
    /// Every row of a table, each with <c>page</c> (the wiki page it came from) and the table's
    /// fields under their own names. File fields (icons, images) are left out. Text is the wiki's
    /// markup as stored. Asked once, it answers from the cache afterwards.
    /// </summary>
    public async Task<List<JsonObject>> Table(string table, CancellationToken cancel = default)
    {
        var fields = (await Fields(table, cancel)).Where(f => f.Type != "File").Select(f => f.Name).ToList();
        string wanted = "_pageName=page," + string.Join(",", fields);
        var rows = new List<JsonObject>();
        for (int offset = 0; ; offset += PageOfRows)
        {
            var answer = await Query(Export, cancel, ("tables", table), ("fields", wanted),
                ("limit", PageOfRows.ToString(CultureInfo.InvariantCulture)), ("offset", offset.ToString(CultureInfo.InvariantCulture)));
            if (answer is not JsonArray page) throw new WikiException($"bg3.wiki did not answer with the rows of '{table}'");
            foreach (var row in page.OfType<JsonObject>()) rows.Add(Tidy(row));
            if (page.Count < PageOfRows) return rows;
        }
    }

    /// <summary>A row as it is kept: field names with underscores, entities decoded, empty lists and values dropped.</summary>
    static JsonObject Tidy(JsonObject row)
    {
        var tidy = new JsonObject();
        foreach (var (key, value) in row)
        {
            string name = key.Replace(' ', '_');
            switch (value)
            {
                case JsonArray list:
                    var items = list.Select(v => WebUtility.HtmlDecode(v?.ToString() ?? "").Trim()).Where(v => v.Length > 0).ToList();
                    if (items.Count > 0) tidy[name] = new JsonArray(items.Select(v => (JsonNode)v).ToArray());
                    break;
                case JsonValue scalar when scalar.TryGetValue(out string? text):
                    // a text field arrives as markup, sometimes already expanded into HTML: kept as what it says
                    string said = WebUtility.HtmlDecode(text).Replace(' ', ' ');
                    said = said.Contains('[') || said.Contains('{') || said.Contains('<') ? Flatten(said, [], []) : said.Trim();
                    if (said.Length > 0) tidy[name] = said;
                    break;
                case not null:
                    tidy[name] = value.DeepClone();
                    break;
            }
        }
        return tidy;
    }

    /// <summary>The identifiers in a stored value. The wiki writes several to a field, each with a
    /// note after a colon ("Shout_X:Version used in Act 1, Shout_Y"): the notes are dropped.</summary>
    public static List<string> IdsOf(string? value) =>
        (value ?? "").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(':')[0].Trim()).Where(id => id.Length > 0 && !id.Contains(' ')).Distinct(StringComparer.Ordinal).ToList();

    static List<string> IdsOf(JsonObject row) =>
        IdFields.SelectMany(field => IdsOf(row[field] is JsonValue value ? value.ToString() : null)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The rows that answer for <paramref name="key"/>: a stats name ("Target_MistyStep"), a
    /// template name or GUID, a status's stack id, or a page title. Tables are read in the order of
    /// <see cref="CardTables"/> (items first for a GUID) and the search stops at the first table
    /// that has it, so a spell costs one table and an unknown key all of them - once.
    /// </summary>
    public async Task<List<WikiCard>> Card(string key, CancellationToken cancel = default)
    {
        key = key.Trim();
        bool guid = Guid().IsMatch(key);
        IEnumerable<string> order = guid ? CardTables.OrderBy(t => t is "weapons" or "equipment" or "misc_items" or "camp_events" ? 0 : 1) : CardTables;
        foreach (string table in order)
        {
            var cards = new List<WikiCard>();
            foreach (var row in await Table(table, cancel))
            {
                var ids = IdsOf(row);
                bool hit = ids.Contains(key, StringComparer.OrdinalIgnoreCase)
                           || !guid && (string.Equals(row["page"]?.ToString(), key, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(row["name"] is JsonValue named ? named.ToString() : null, key, StringComparison.OrdinalIgnoreCase));
                if (hit) cards.Add(new WikiCard(table, row["page"]?.ToString() ?? "", ids, row));
            }
            if (cards.Count > 0) return cards;
        }
        return [];
    }

    /// <summary>Rows of a table that pass every condition, a condition being a field and the text
    /// its value must contain (a list field passes when one of its items does). Case is ignored.</summary>
    public async Task<List<JsonObject>> Rows(string table, IReadOnlyList<(string Field, string Text)> where, CancellationToken cancel = default)
    {
        var names = (await Fields(table, cancel)).Select(f => f.Name).Append("page").ToHashSet(StringComparer.Ordinal);
        foreach (var (field, _) in where)
            if (!names.Contains(field)) throw new WikiException($"'{table}' has no field '{field}': {string.Join(", ", names.Order())}");
        return (await Table(table, cancel)).Where(row => where.All(w => row[w.Field] switch
        {
            JsonArray list => list.Any(v => (v?.ToString() ?? "").Contains(w.Text, StringComparison.OrdinalIgnoreCase)),
            null => w.Text.Length == 0,
            var value => value.ToString().Contains(w.Text, StringComparison.OrdinalIgnoreCase),
        })).ToList();
    }
}
