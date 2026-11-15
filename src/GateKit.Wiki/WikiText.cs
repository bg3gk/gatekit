using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Wiki;

/// <summary>One section of a page, in the order the page has them.</summary>
/// <param name="Level">1 for the text before the first heading, 2 for "== Heading ==", and so on.</param>
/// <param name="Heading">The heading; empty for the lead.</param>
/// <param name="Text">The section as plain text.</param>
/// <param name="Links">The pages the section links to, in order of appearance.</param>
/// <param name="Unsure">What the wiki's own editors marked as needing verification in this section.</param>
public sealed record WikiSection(int Level, string Heading, string Text, IReadOnlyList<string> Links, IReadOnlyList<string> Unsure);

/// <summary>A page read from its markup: where it came from, and its sections.</summary>
public sealed record WikiPage(string Title, long Revision, IReadOnlyList<WikiSection> Sections);

/// <summary>The game's identifiers a page carries, by what each one is.</summary>
/// <param name="Uid">Stats names and template names (the wiki's "UID").</param>
/// <param name="Uuid">Template and object GUIDs.</param>
/// <param name="Stats">The stats entry an item or a creature uses.</param>
/// <param name="StackId">A status's stack id - what a condition page carries where it has no UID.</param>
/// <param name="Treasure">Treasure tables: what a trader sells, what a container holds.</param>
public sealed record WikiIds(IReadOnlyList<string> Uid, IReadOnlyList<string> Uuid, IReadOnlyList<string> Stats, IReadOnlyList<string> StackId, IReadOnlyList<string> Treasure)
{
    public int Count => Uid.Count + Uuid.Count + Stats.Count + StackId.Count + Treasure.Count;
}

/// <summary>
/// A page read from its markup rather than from its rendering. The markup keeps what the rendering
/// flattens: a rules page is sections in order, a link is a title, an identifier is a template
/// parameter with a name, and an editor's "this needs verifying" is a marker and not a superscript.
/// </summary>
public sealed partial class WikiReader
{
    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex Guid();

    [GeneratedRegex(@"^(UID|UUID)\s*\n\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex Labelled();

    [GeneratedRegex(@"^\s*\|\s*(uid|uuid|stats|stack id|treasure\d*)\s*=[ \t]*([^\n|}]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Parameter();

    [GeneratedRegex(@"^(={2,6})\s*(.+?)\s*\1\s*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<!--.*?-->|<ref[^>/]*>.*?</ref>|<ref[^>]*/>|<gallery.*?</gallery>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Asides();

    [GeneratedRegex(@"\{\{([^{}]*)\}\}", RegexOptions.Singleline)]
    private static partial Regex Template();

    [GeneratedRegex(@"\[\[(?:File|Image|Category):[^\[\]]*(?:\[\[[^\]]*\]\][^\[\]]*)*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex Media();

    [GeneratedRegex(@"\[\[([^\]|]+)(?:\|([^\]]*))?\]\]")]
    private static partial Regex Link();

    [GeneratedRegex(@"\[https?://\S+\s+([^\]]+)\]")]
    private static partial Regex Outside();

    [GeneratedRegex(@"(sai|cond|pass|item|charlink|quest|loc|area|skill|class|race|background|deity|main|see also)$", RegexOptions.IgnoreCase)]
    private static partial Regex Linking();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex Gaps();

    /// <summary>The markup of a page and the revision it was read at; null when there is no such page.</summary>
    async Task<(string Title, long Revision, string Markup)?> Markup(string title, CancellationToken cancel)
    {
        var answer = await Query(cancel, ("action", "parse"), ("page", title), ("prop", "wikitext|revid"), ("redirects", "1"));
        if (answer["error"] is not null || answer["parse"] is not JsonObject parse || (string?)parse["wikitext"]?["*"] is not { } markup) return null;
        return ((string?)parse["title"] ?? title, (long?)parse["revid"] ?? 0, markup);
    }

    /// <summary>
    /// Markup as plain text. A template becomes its first unnamed parameter (so a link template
    /// reads as the thing it links to), or its own name when it has no parameters ("Advantage"),
    /// or nothing when it only has named ones (an infobox). Links are collected, and so is every
    /// "verify" marker.
    /// </summary>
    public static string Flatten(string markup, List<string> links, List<string> unsure)
    {
        string text = Media().Replace(Asides().Replace(markup, ""), "");
        for (int pass = 0; pass < 12 && Template().IsMatch(text); pass++)
            text = Template().Replace(text, m =>
            {
                var parts = m.Groups[1].Value.Split('|');
                string name = parts[0].Trim();
                var plain = parts.Skip(1).Where(p => !p.Contains('=')).Select(p => p.Trim()).ToList();
                if (name.Equals("verify", StringComparison.OrdinalIgnoreCase))
                {
                    unsure.Add(plain.FirstOrDefault() is { Length: > 0 } what ? what : "marked as needing verification, with no note");
                    return "";
                }
                if (name.Equals("ref", StringComparison.OrdinalIgnoreCase) || name.StartsWith('#') || name.StartsWith("nav", StringComparison.OrdinalIgnoreCase) || name.Contains(':')) return "";
                if (plain.Count > 0)
                {
                    // a link template is a link: left as one, so the pass below collects it in its place
                    return Linking().IsMatch(name) && plain[0].Length > 0 && !plain[0].Contains('[') ? $"[[{plain[0]}]]" : plain[0];
                }
                return parts.Length == 1 && name.Length is > 1 and < 30 ? name : "";
            });
        text = Link().Replace(text, m =>
        {
            string target = m.Groups[1].Value.Trim();
            if (!links.Contains(target)) links.Add(target);
            return m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : target;
        });
        text = Outside().Replace(text, "$1").Replace("'''", "").Replace("''", "");
        text = WebUtility.HtmlDecode(Tags().Replace(text, ""));
        var lines = text.Split('\n').Select(line => Spaces().Replace(line, " ").TrimEnd());
        return Gaps().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    /// <summary>
    /// A page as its sections in order - for a rules page, the steps the game resolves a thing in,
    /// as far as the people who play it have worked them out. Null when there is no such page.
    /// </summary>
    public async Task<WikiPage?> Sections(string title, CancellationToken cancel = default)
    {
        if (await Markup(title, cancel) is not { } page) return null;
        var sections = new List<WikiSection>();
        var headings = Heading().Matches(page.Markup);
        void Add(int level, string heading, string body)
        {
            var links = new List<string>();
            var unsure = new List<string>();
            string text = Flatten(body, links, unsure);
            if (text.Length > 0 || unsure.Count > 0) sections.Add(new WikiSection(level, Flatten(heading, [], []), text, links, unsure));
        }
        Add(1, "", headings.Count > 0 ? page.Markup[..headings[0].Index] : page.Markup);
        for (int i = 0; i < headings.Count; i++)
        {
            int from = headings[i].Index + headings[i].Length;
            int to = i + 1 < headings.Count ? headings[i + 1].Index : page.Markup.Length;
            Add(headings[i].Groups[1].Length, headings[i].Groups[2].Value, page.Markup[from..to]);
        }
        return new WikiPage(page.Title, page.Revision, sections);
    }

    /// <summary>
    /// The game's identifiers a page carries - what ties it to the data index and to staging the
    /// thing in a game. They are read from the page's template parameters, where each has a name;
    /// a page that shows identifiers it does not hold itself is read from its rendering instead.
    /// </summary>
    public async Task<WikiIds> Ids(string title, CancellationToken cancel = default)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["uid"] = [], ["uuid"] = [], ["stats"] = [], ["stack id"] = [], ["treasure"] = [] };
        void Put(string kind, string value)
        {
            if (value.Length > 0 && !found[kind].Contains(value)) found[kind].Add(value);
        }
        if (await Markup(title, cancel) is { } page)
        {
            foreach (Match m in Parameter().Matches(page.Markup))
            {
                string kind = m.Groups[1].Value.ToLowerInvariant();
                if (kind.StartsWith("treasure", StringComparison.Ordinal)) kind = "treasure";
                foreach (string id in IdsOf(m.Groups[2].Value)) Put(kind, id);
            }
            foreach (Match m in Guid().Matches(page.Markup)) Put("uuid", m.Value);
        }
        if (found["uid"].Count + found["uuid"].Count == 0)
        {
            string text = await Page(title, cancel);
            foreach (Match m in Labelled().Matches(text)) Put(m.Groups[1].Value == "UID" ? "uid" : "uuid", m.Groups[2].Value);
            foreach (Match m in Guid().Matches(text)) Put("uuid", m.Value);
        }
        return new WikiIds(found["uid"], found["uuid"], found["stats"], found["stack id"], found["treasure"]);
    }

    /// <summary>
    /// The page that carries a stats identifier (the wiki shows it as "UID"): its title and text, or
    /// null. The wiki's tables are asked first - they hold the identifier as a field - and a
    /// full-text search only when they do not have it or cannot be read.
    /// </summary>
    public async Task<(string Title, string Text)?> Uid(string name, CancellationToken cancel = default)
    {
        try
        {
            foreach (var card in await Card(name, cancel))
                if (card.Ids.Contains(name, StringComparer.OrdinalIgnoreCase) && await Page(card.Page, cancel) is { Length: > 0 } text)
                    return (card.Page, text);
        }
        catch (WikiException) { }   // the tables are a shortcut; the search below still stands
        foreach (var (title, _) in await Find($"\"{name}\"", 5, cancel))
        {
            string text = await Page(title, cancel);
            if (text.Contains(name, StringComparison.Ordinal)) return (title, text);
        }
        return null;
    }
}
