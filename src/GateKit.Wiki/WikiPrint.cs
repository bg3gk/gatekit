using System.Text;
using System.Text.Json.Nodes;

namespace GateKit.Wiki;

/// <summary>What the reader found, as text for a person and as JSON for a machine - one shape for
/// the command line and for the tool.</summary>
public static class WikiPrint
{
    static JsonArray List(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());

    /// <summary>A value on one line, cut to a length.</summary>
    public static string Value(JsonNode node, int limit)
    {
        string text = node is JsonArray list ? string.Join(", ", list.Select(v => v?.ToString())) : node.ToString();
        text = text.ReplaceLineEndings(" ");
        return text.Length > limit ? text[..limit] + "..." : text;
    }

    /// <summary>"field=text" conditions as pairs; one without "=" is refused.</summary>
    public static List<(string Field, string Text)> Where(IEnumerable<string> conditions)
    {
        var parsed = new List<(string, string)>();
        foreach (string condition in conditions.SelectMany(c => c.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            int at = condition.IndexOf('=');
            if (at <= 0) throw new WikiException($"a condition is field=text; got '{condition}'");
            parsed.Add((condition[..at].Trim().Replace(' ', '_'), condition[(at + 1)..].Trim()));
        }
        return parsed;
    }

    public static string Ids(WikiIds ids)
    {
        var text = new StringBuilder($"UID  {string.Join(", ", ids.Uid)}\nUUID {string.Join(", ", ids.Uuid)}");
        if (ids.Stats.Count > 0) text.Append($"\nstats {string.Join(", ", ids.Stats)}");
        if (ids.StackId.Count > 0) text.Append($"\nstack id {string.Join(", ", ids.StackId)}");
        if (ids.Treasure.Count > 0) text.Append($"\ntreasure {string.Join(", ", ids.Treasure)}");
        return text.ToString();
    }

    public static JsonObject Json(WikiIds ids)
    {
        var node = new JsonObject { ["UID"] = List(ids.Uid), ["UUID"] = List(ids.Uuid) };
        if (ids.Stats.Count > 0) node["stats"] = List(ids.Stats);
        if (ids.StackId.Count > 0) node["stack_id"] = List(ids.StackId);
        if (ids.Treasure.Count > 0) node["treasure"] = List(ids.Treasure);
        return node;
    }

    /// <summary>A card: the page, the table, the identifiers, then the fields that hold something.</summary>
    public static string Card(WikiCard card)
    {
        var text = new StringBuilder($"== {card.Page}   [{card.Table}]");
        if (card.Ids.Count > 0) text.Append($"\n   ids: {string.Join(", ", card.Ids)}");
        foreach (var (field, value) in card.Row)
            if (value is not null && field != "page" && field != "cargo_uid")
                text.Append($"\n   {field,-22} {Value(value, 300)}");
        return text.ToString();
    }

    public static JsonObject Json(WikiCard card) => new()
    {
        ["table"] = card.Table,
        ["page"] = card.Page,
        ["ids"] = List(card.Ids),
        ["row"] = card.Row.DeepClone(),
    };

    public static string Page(WikiPage page)
    {
        var text = new StringBuilder($"== {page.Title}   (revision {page.Revision})");
        foreach (var section in page.Sections)
        {
            text.Append("\n\n").Append(new string('#', section.Level)).Append(' ').Append(section.Heading.Length > 0 ? section.Heading : "(lead)");
            if (section.Text.Length > 0) text.Append('\n').Append(section.Text);
            foreach (string doubt in section.Unsure) text.Append("\n[the wiki is not sure: ").Append(doubt).Append(']');
        }
        return text.ToString();
    }

    public static JsonObject Json(WikiPage page) => new()
    {
        ["title"] = page.Title,
        ["revision"] = page.Revision,
        ["sections"] = new JsonArray(page.Sections.Select(s => (JsonNode)new JsonObject
        {
            ["level"] = s.Level,
            ["heading"] = s.Heading,
            ["text"] = s.Text,
            ["links"] = List(s.Links),
            ["unsure"] = List(s.Unsure),
        }).ToArray()),
    };
}
