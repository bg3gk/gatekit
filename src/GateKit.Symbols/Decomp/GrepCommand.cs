using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>`grep`: full-text search over names and pseudo-code.</summary>
internal static class GrepCommand
{
    static readonly Regex Word = new("[0-9A-Za-z_]+", RegexOptions.Compiled);
    static readonly Regex AnyWord = new(@"\w+", RegexOptions.Compiled);

    /// <summary>An FTS5 query that finds the words as parts of longer names. The index holds whole
    /// tokens (GroundMaterialComponent is one token), so every token that contains a word is
    /// looked up in the index's vocabulary first. Words of fewer than three characters match
    /// whole tokens only. Null when a word is in no token.</summary>
    public static string? SubstringQuery(SqliteConnection db, string text, int perWord = 400)
    {
        db.Execute("CREATE VIRTUAL TABLE IF NOT EXISTS temp.code_vocab USING fts5vocab(main, code_fts, row)");
        var groups = new List<string>();
        foreach (Match m in Word.Matches(text))
        {
            string low = m.Value.ToLowerInvariant();
            List<string> terms = low.Length < 3 ? [low] : db.Query(
                "SELECT term FROM temp.code_vocab WHERE term LIKE ? ESCAPE '!' ORDER BY doc DESC LIMIT ?",
                "%" + low.Replace("!", "!!").Replace("_", "!_").Replace("%", "!%") + "%", perWord).Select(r => Py.Str(r[0])).ToList();
            if (terms.Count == 0)
                return null;
            groups.Add("(" + string.Join(" OR ", terms.Select(Quote)) + ")");
        }
        return groups.Count > 0 ? string.Join(" AND ", groups) : null;
    }

    /// <summary>An FTS5 string: "text" with inner quotes doubled.</summary>
    public static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    /// <summary>Words as whole tokens: each word quoted, all of them required.</summary>
    public static string WordQuery(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Quote));

    /// <summary>Lowercased \w+ words of the text, for picking matching lines.</summary>
    public static List<string> LineWords(string text) => AnyWord.Matches(text).Select(m => m.Value.ToLowerInvariant()).ToList();

    static readonly Regex Qualified = new(@"^\s*[A-Za-z_]\w*(?:::[A-Za-z_]\w*)+\s*$", RegexOptions.Compiled);

    /// <summary>A namespace-qualified name (eoc::lock::LockComponent) as FTS5 phrases of whole tokens:
    /// the tokens in that order and next to each other, which the index answers at once instead of
    /// scanning its vocabulary for every token that contains a word (10-40 s a query on the game's
    /// database, N1 of the third coverage run). The last token also matches with `_const`: the macOS
    /// names write a read-only view of a type as `Type_const`.</summary>
    public static string? QualifiedQuery(string text)
    {
        if (!Qualified.IsMatch(text))
            return null;
        var parts = text.Trim().Split("::").Select(p => p.ToLowerInvariant()).ToList();
        var head = string.Join(" ", parts.Take(parts.Count - 1));
        string Phrase(string last) => Quote((head.Length > 0 ? head + " " : "") + last);
        return Phrase(parts[^1]) + " OR " + Phrase(parts[^1] + "_const");
    }

    /// <summary>Whether a function reads or writes a type, from how it names the type: a view or query
    /// of the ECS takes `Type const` (`Type_const` in the macOS names) for what it only reads and
    /// `Type` for what it writes. Only mentions inside a view, a query or GetComponent count.
    /// "writes" when any of them is not const, "reads" when all are, null when there is none.
    /// Read off names, not proven.</summary>
    static readonly string[] ViewMarkers = ["EntityRefView<", "TypeList<", "GetComponent<", "View<", "Spec<", "Query<", "WorldView"];

    public static string? Access(string? name, string? code, string type)
    {
        var last = type.Split("::")[^1];
        var pattern = new Regex(@"(?<![\w])" + Regex.Escape(type.Contains("::", StringComparison.Ordinal) ? type : last) + @"(_const|\s+const)?(?=\s*[,>&*])");
        bool reads = false, writes = false;
        foreach (var source in new[] { name, code })
        {
            if (source == null)
                continue;
            foreach (Match m in pattern.Matches(source))
            {
                // Only where the ECS is asked for the type: a view, a query, GetComponent. Its
                // registration (RegisterType<Type>, ComponentOps<Type>) is neither a read nor a write.
                var before = source.Substring(Math.Max(0, m.Index - 400), Math.Min(400, m.Index));
                if (!ViewMarkers.Any(marker => before.Contains(marker, StringComparison.Ordinal)))
                    continue;
                if (m.Groups[1].Success) reads = true; else writes = true;
            }
        }
        return writes ? "writes" : reads ? "reads" : null;
    }

    public static int Run(DecompSession s, string text, string? binary, int lines, bool raw, bool word, int limit, string? within = null, bool access = false)
    {
        var db = s.Db;
        var qualified = raw ? null : QualifiedQuery(text);
        string? query = raw ? text : qualified ?? (word ? WordQuery(text) : SubstringQuery(db, text));
        if (query == null)
        {
            s.Err.WriteLine("nothing found");
            return 0;
        }
        // N14: on the macOS binary the names carry whole template lists, so a type matched the names
        // of hundreds of functions before any code; `--in code` (or `name`) keeps to one column.
        if (within is { Length: > 0 })
        {
            if (within is not ("code" or "name"))
                throw new SymbolsException($"--in is code or name, not {within}");
            query = "{" + within + "} : (" + query + ")";
        }
        var rows = db.Query("SELECT f.id, b.name, f.rva, f.name FROM code_fts JOIN functions f ON f.id = code_fts.rowid"
                            + " JOIN binaries b ON b.id = f.binary_id WHERE code_fts MATCH ? AND (? IS NULL OR b.name LIKE ?) LIMIT ?",
                            query, binary, (binary ?? "") + "%", limit).ToList();
        var words = LineWords(text);
        foreach (var r in rows)
        {
            var code = lines != 0 || access ? DecompStore.Decompress(db.Scalar("SELECT text FROM code WHERE function_id = ?", r[0])) : null;
            var how = access ? Access(r[3] as string, code, text.Trim()) : null;
            s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Py.Str(r[1])}+0x{Convert.ToInt64(r[2], CultureInfo.InvariantCulture):X}  ")
                            + (access ? $"[{how ?? "?"}] " : "") + Py.Str(r[3]));
            if (lines != 0)
                PrintLines(s.Out, code, words, lines, null);
        }
        if (rows.Count == 0)
            s.Err.WriteLine("nothing found");
        return 0;
    }

    /// <summary>Up to `max` lines of a text that contain one of the words, stripped and cut to 200
    /// characters, indented by four spaces (with the line number when `numbered`).</summary>
    public static void PrintLines(TextWriter output, string? text, List<string> words, int max, bool? numbered)
    {
        if (text == null)
            return;
        int shown = 0, n = 0;
        foreach (var line in Py.SplitLines(text))
        {
            n++;
            var low = line.ToLowerInvariant();
            if (!words.Any(w => low.Contains(w, StringComparison.Ordinal)))
                continue;
            output.WriteLine(numbered == true ? string.Create(CultureInfo.InvariantCulture, $"    {n}: {Py.Left(Py.Strip(line), 200)}") : "    " + Py.Left(Py.Strip(line), 200));
            shown++;
            if (shown >= max)
                break;
        }
    }
}
