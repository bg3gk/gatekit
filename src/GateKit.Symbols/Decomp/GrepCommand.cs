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

    public static int Run(DecompSession s, string text, string? binary, int lines, bool raw, bool word, int limit)
    {
        var db = s.Db;
        string? query = raw ? text : word ? WordQuery(text) : SubstringQuery(db, text);
        if (query == null)
        {
            s.Err.WriteLine("nothing found");
            return 0;
        }
        var rows = db.Query("SELECT f.id, b.name, f.rva, f.name FROM code_fts JOIN functions f ON f.id = code_fts.rowid"
                            + " JOIN binaries b ON b.id = f.binary_id WHERE code_fts MATCH ? AND (? IS NULL OR b.name LIKE ?) LIMIT ?",
                            query, binary, (binary ?? "") + "%", limit).ToList();
        var words = LineWords(text);
        foreach (var r in rows)
        {
            s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Py.Str(r[1])}+0x{Convert.ToInt64(r[2], CultureInfo.InvariantCulture):X}  {Py.Str(r[3])}"));
            if (lines != 0)
                PrintLines(s.Out, DecompStore.Decompress(db.Scalar("SELECT text FROM code WHERE function_id = ?", r[0])), words, lines, null);
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
