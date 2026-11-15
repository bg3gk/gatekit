using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Wiki;

/// <summary>A failure the user can act on: printed as it is.</summary>
public sealed class WikiException(string message) : Exception(message);

/// <summary>The wiki answered "too many requests". Thrown by a fetch; the reader turns it into a
/// <see cref="WikiException"/> and stops asking for a while.</summary>
public sealed class WikiRefusedException() : Exception("too many requests");

/// <summary>
/// What players and modders know about a thing of the game, from bg3.wiki - as context for
/// deciding what to try and for understanding what a system does.
///
/// bg3.wiki is a community wiki (MediaWiki; its text is under CC BY-NC-SA 4.0 or CC BY-SA 4.0).
/// Its pages on spells, conditions and passives carry the game's own identifier ("UID"), which
/// ties a page to the data index; its rules pages describe the order in which the game resolves
/// things - the logic the systems implement.
///
/// What it is NOT: a source of layouts or of the game's own member names. A name taken from a
/// wiki description is inferred, never the game's.
///
/// Answers are kept in a local cache outside the repository - the text is the wiki's, not ours -
/// so a question asked once costs nothing the second time and still answers without a network.
/// Requests are plain reads of the public API under a generic client name.
/// </summary>
public sealed partial class WikiReader
{
    public const string Api = "https://bg3.wiki/w/api.php";

    /// <summary>The wiki's structured tables (the Cargo extension), as rows. The API's own
    /// <c>cargoquery</c> is closed to an anonymous reader; this page is not.</summary>
    public const string Export = "https://bg3.wiki/wiki/Special:CargoExport";

    readonly string cache;
    readonly Func<string, CancellationToken, Task<string>> fetch;
    readonly bool paced;

    /// <summary>The cache folder: GATEKIT_WIKI_CACHE, else &lt;home&gt;/cache/wiki.</summary>
    public static string DefaultCache => GateKit.Home.GateKitHome.Current.WikiCache.Path!;

    /// <param name="fetch">Reads a URL; the default asks the wiki. Tests give their own.</param>
    public WikiReader(string? cache = null, Func<string, CancellationToken, Task<string>>? fetch = null)
    {
        this.cache = cache ?? DefaultCache;
        this.fetch = fetch ?? Ask;
        paced = fetch is null;
    }

    /// <summary>Answer afresh even when the cache has it.</summary>
    public bool Refresh { get; set; }

    static readonly HttpClient Http = Client();

    static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("wiki-reader/1.0");
        return client;
    }

    static async Task<string> Ask(string url, CancellationToken cancel)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancel);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new WikiRefusedException();
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancel);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            throw new WikiException($"bg3.wiki did not answer ({e.Message}); what was asked before is still in the cache, this was not");
        }
    }

    /// <summary>The clock; tests give their own.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>How long nothing is asked of an endpoint after it refused a request.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(15);

    static string Kind(string endpoint) => endpoint == Export ? "export" : "api";

    /// <summary>
    /// A community site with a rate limit of its own is read slowly and by one reader at a time:
    /// every request reserves the next free moment in a file beside the cache, so several sessions
    /// calling this at once queue up instead of arriving together.
    /// </summary>
    async Task Pace(string endpoint, CancellationToken cancel)
    {
        if (!paced) return;
        var gap = endpoint == Export ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(1.2);
        Directory.CreateDirectory(cache);
        string file = Path.Combine(cache, $"next-{Kind(endpoint)}.txt");
        DateTime mine = Now();
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var bytes = new byte[32];
                int read = await stream.ReadAsync(bytes, cancel);
                if (long.TryParse(Encoding.ASCII.GetString(bytes, 0, read), out long ticks) && new DateTime(ticks, DateTimeKind.Utc) is var next
                    && next > mine && next - mine < TimeSpan.FromMinutes(10))
                    mine = next;
                stream.SetLength(0);
                await stream.WriteAsync(Encoding.ASCII.GetBytes((mine + gap).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancel);
                break;
            }
            catch (IOException) { await Task.Delay(40, cancel); }
        }
        var wait = mine - Now();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancel);
    }

    /// <summary>One read, cached by its parameters. A refusal (HTTP 429) is remembered beside the
    /// cache, and nothing more is asked of that endpoint for <see cref="Quiet"/>: no caller can
    /// turn a refusal into a loop of requests.</summary>
    async Task<JsonNode> Query(string endpoint, CancellationToken cancel, params (string Key, string Value)[] parameters)
    {
        string query = string.Join("&", parameters.Append(("format", "json")).OrderBy(p => p.Item1, StringComparer.Ordinal)
            .Select(p => $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Item2)}"));
        string key = endpoint == Api ? query : Kind(endpoint) + "?" + query;   // API answers keep the name they always had
        string file = Path.Combine(cache, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40] + ".json");
        if (!Refresh && File.Exists(file))
        {
            try { if (JsonNode.Parse(await File.ReadAllTextAsync(file, cancel)) is { } kept) return kept; }
            catch (JsonException) { }   // a damaged entry is asked again
        }
        string refused = Path.Combine(cache, $"refused-{Kind(endpoint)}.txt");
        if (File.Exists(refused) && long.TryParse(File.ReadAllText(refused), out long at) && new DateTime(at, DateTimeKind.Utc) is var when && when + Quiet > Now())
            throw new WikiException($"bg3.wiki refused a request (too many) at {when:HH:mm} UTC; nothing is asked of it again before {when + Quiet:HH:mm} UTC. What is in the cache still answers");
        await Pace(endpoint, cancel);
        string text;
        try { text = await fetch($"{endpoint}?{query}", cancel); }
        catch (WikiRefusedException)
        {
            Directory.CreateDirectory(cache);
            File.WriteAllText(refused, Now().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            throw new WikiException($"bg3.wiki refused the request (too many); nothing is asked of it again for {Quiet.TotalMinutes:0} minutes. What is in the cache still answers");
        }
        JsonNode answer;
        try { answer = JsonNode.Parse(text) ?? throw new JsonException("empty"); }
        catch (JsonException) { throw new WikiException("bg3.wiki answered with something that is not JSON"); }
        Directory.CreateDirectory(cache);
        await File.WriteAllTextAsync(file, answer.ToJsonString(), cancel);
        return answer;
    }

    Task<JsonNode> Query(CancellationToken cancel, params (string Key, string Value)[] parameters) => Query(Api, cancel, parameters);

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline)]
    private static partial Regex Code();

    [GeneratedRegex(@"<(br|/p|/li|/tr|/h\d|/div)[^>]*>")]
    private static partial Regex Breaks();

    [GeneratedRegex(@"</t[dh]>")]
    private static partial Regex Cells();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\xa0]+")]
    private static partial Regex Spaces();

    /// <summary>The HTML of a rendered page as readable text.</summary>
    public static string Plain(string html)
    {
        html = Code().Replace(html, " ");
        html = Breaks().Replace(html, "\n");
        html = Cells().Replace(html, " | ");
        html = WebUtility.HtmlDecode(Tags().Replace(html, ""));
        var lines = html.Split('\n').Select(line => Spaces().Replace(line, " ").Trim());
        return string.Join("\n", lines.Where(line => line.Length > 0 && line != "|"));
    }

    /// <summary>Titles that match a full-text search, each with the words around the match.</summary>
    public async Task<List<(string Title, string Snippet)>> Find(string words, int limit = 12, CancellationToken cancel = default)
    {
        var answer = await Query(cancel, ("action", "query"), ("list", "search"), ("srsearch", words), ("srlimit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return (answer["query"]?["search"] as JsonArray ?? []).Where(row => row is not null)
            .Select(row => ((string?)row!["title"] ?? "", Plain((string?)row["snippet"] ?? ""))).ToList();
    }

    /// <summary>The rendered page as text (templates are expanded, so stat blocks are readable); empty when there is no such page.</summary>
    public async Task<string> Page(string title, CancellationToken cancel = default)
    {
        var answer = await Query(cancel, ("action", "parse"), ("page", title), ("prop", "text"), ("redirects", "1"), ("disabletoc", "1"));
        return answer["error"] is not null ? "" : Plain((string?)answer["parse"]?["text"]?["*"] ?? "");
    }
}
