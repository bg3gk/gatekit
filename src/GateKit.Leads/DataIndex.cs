using System.Text.RegularExpressions;
using GateKit.Symbols;
using GateKit.Wiki;
using Microsoft.Data.Sqlite;

namespace GateKit.Leads;

/// <summary><see cref="IDataIndex"/> over the game's data index (gameindex.sqlite), read-only.</summary>
public sealed class DataIndex : IDataIndex, IDisposable
{
    readonly SqliteConnection db;
    readonly Dictionary<string, List<(StatUse Use, string Value)>> carrying = new(StringComparer.Ordinal);
    List<(string, string)>? osiris;

    public DataIndex(string path)
    {
        if (!File.Exists(path))
            throw new LeadsException($"{path} does not exist: the leads read the game's data index (gatekit data build, or gatekit fetch index)");
        db = Sql.OpenReadOnly(path);
    }

    public void Dispose() => db.Dispose();

    /// <summary>Fields that hold conditions or text, not boosts and functors: a condition may share a boost's name.</summary>
    /// <summary>A functor is run from a functors or properties field, never from a list of boosts.</summary>
    static bool CarriesFunctor(string field) => Carries(field) && !field.Contains("Boost", StringComparison.Ordinal) && !field.Contains("Passive", StringComparison.Ordinal);

    static bool Carries(string field) =>
        !field.Contains("Condition", StringComparison.Ordinal) && !field.StartsWith("Tooltip", StringComparison.Ordinal)
        && !field.Contains("DescriptionParams", StringComparison.Ordinal) && field != "StackId";

    List<(StatUse Use, string Value)> All(string call, bool functor)
    {
        var key = (functor ? "f:" : "b:") + call;
        if (carrying.TryGetValue(key, out var known)) return known;
        var word = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(call) + @"\(", RegexOptions.CultureInvariant);
        var list = db.Query("SELECT s.name, s.type, f.field, s.mod, f.value FROM stat_fields f JOIN stats s ON s.id = f.stat_id WHERE f.value LIKE ? ESCAPE '\\'",
                            Sql.Like(call + "("))
            .Select(r => (Use: new StatUse((string)r[0]!, (string?)r[1] ?? "", (string)r[2]!, (string?)r[3]), Value: (string)r[4]!))
            .Where(x => (functor ? CarriesFunctor(x.Use.Field) : Carries(x.Use.Field)) && word.IsMatch(x.Value))
            .DistinctBy(x => x.Use.Name)
            .OrderBy(x => Rules.Shipping(x.Use.Mod) ? 0 : 1).ThenBy(x => x.Use.Name, StringComparer.Ordinal).ToList();
        carrying[key] = list;
        return list;
    }

    public IReadOnlyList<StatUse> Carrying(string call, int limit, bool functor = false) => [.. All(call, functor).Take(limit).Select(x => x.Use)];

    public int CountCarrying(string call, bool functor = false) => All(call, functor).Count;

    public IReadOnlyList<string> CallsHolding(string word, int limit, bool functor = false)
    {
        var call = new Regex(@"(?<![A-Za-z0-9_])(\w*" + Regex.Escape(word) + @"\w*)\(", RegexOptions.CultureInvariant);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in db.Query("SELECT field, value FROM stat_fields WHERE value LIKE ? ESCAPE '\\'", Sql.Like(word)))
        {
            if (!(functor ? CarriesFunctor((string)r[0]!) : Carries((string)r[0]!))) continue;
            foreach (Match m in call.Matches((string)r[1]!))
                counts[m.Groups[1].Value] = counts.GetValueOrDefault(m.Groups[1].Value) + 1;
        }
        return [.. counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(limit).Select(p => p.Key)];
    }

    public IReadOnlyList<(string Name, string Kind)> OsirisFunctions() =>
        osiris ??= [.. db.Query("SELECT DISTINCT name, kind FROM osiris_functions ORDER BY name").Select(r => ((string)r[0]!, (string?)r[1] ?? ""))];

    public int OsirisUses(string function) =>
        Convert.ToInt32(db.Query("SELECT COALESCE(SUM(uses), 0) FROM osiris_uses WHERE name = ?", function).First()[0], System.Globalization.CultureInfo.InvariantCulture);

    public string? DisplayName(string stat)
    {
        var name = stat;
        for (int i = 0; i < 6 && name is not null; i++)
        {
            var row = db.Query("SELECT s.id, s.parent FROM stats s WHERE s.name = ? ORDER BY s.id DESC LIMIT 1", name).FirstOrDefault();
            if (row is null) return null;
            var handle = db.Query("SELECT value FROM stat_fields WHERE stat_id = ? AND field = 'DisplayName'", row[0]).Select(r => (string?)r[0]).FirstOrDefault();
            if (handle is { Length: > 0 })
            {
                var key = handle.Split(';')[0];
                return db.Query("SELECT text FROM loca WHERE lang = 'English' AND variant = '' AND handle = ?", key).Select(r => (string?)r[0]).FirstOrDefault();
            }
            name = (string?)row[1];
        }
        return null;
    }
}

/// <summary><see cref="IWikiTitles"/> through the cached reader, with a ceiling on requests that are
/// not in the cache: the site is a community's, and a rebuild must not hammer it.</summary>
public sealed class WikiTitles(WikiReader reader, int budget = 300) : IWikiTitles
{
    int asked;

    public int Asked => asked;

    public async Task<string?> PageFor(string name, CancellationToken cancel = default)
    {
        if (asked >= budget || name.Length < 3) return null;
        asked++;
        try
        {
            var found = await reader.Find(name, 5, cancel);
            return found.Select(f => f.Title).FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, name + " (spell)", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, name + " (condition)", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is WikiException or HttpRequestException or TaskCanceledException or IOException)
        {
            return null;   // no network or the site refuses: no page, said by its absence
        }
    }
}
