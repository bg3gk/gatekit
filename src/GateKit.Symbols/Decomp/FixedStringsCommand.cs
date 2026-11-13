using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>`fixedstrings`: the game's global FixedStrings, read from the functions that register them.</summary>
internal static class FixedStringsCommand
{
    static readonly Regex EntryLink = new(@"(_?DAT_1[0-9a-f]{8}) = &(DAT_1[0-9a-f]{8}); \2 = ""((?:[^""\\]|\\.)*)"";", RegexOptions.Compiled);
    const long ValueOffset = 0x18;

    static long L(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    static List<long> Pointers(string flat, long valueAddress, long imageBase) =>
        Regex.Matches(flat, string.Create(CultureInfo.InvariantCulture, $"_?DAT_(1[0-9a-f]{{8}}) = &DAT_{valueAddress:x};"))
             .Select(x => Py.Hex(x.Groups[1].Value)!.Value - imageBase).ToList();

    /// <summary>Reads the registrations of global FixedStrings out of the pseudo-code. Every string
    /// literal used as a FixedString has a small function that fills a registry entry on first use:
    ///
    ///     entry.length = N;  entry.next = head;  head = &amp;entry;  entry.text = "Literal";
    ///
    /// and usually stores the address of the entry's value (entry + 0x18, a FixedString the
    /// registry fills when the game starts) into one more global. The list head is the global most
    /// often written in that pattern. Returns the number of strings found.</summary>
    public static int Find(DecompSession s, SqliteConnection db, string binary)
    {
        var idValue = db.Scalar("SELECT id FROM binaries WHERE name = ?", binary) ?? throw new SymbolsException($"{binary} is not loaded");
        long binaryId = L(idValue);
        long imageBase = DecompStore.ImageBase(binary);
        var heads = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in db.Query("SELECT f.id, f.rva FROM code_fts JOIN functions f ON f.id = code_fts.rowid"
                                   + " WHERE code_fts MATCH ? AND f.binary_id = ?", "\"_Init_thread_header\"", binaryId).ToList())
        {
            if (DecompStore.Decompress(db.Scalar("SELECT text FROM code WHERE function_id = ?", r[0])) is not string body)
                continue;
            var flat = Py.Collapse(body);
            foreach (Match m in EntryLink.Matches(flat))
            {
                var head = m.Groups[1].Value.TrimStart('_');
                heads[head] = heads.GetValueOrDefault(head) + 1;
            }
        }
        if (heads.Count == 0)
            return 0;
        string best = heads.First().Key;
        foreach (var (k, v) in heads)
            if (v > heads[best])
                best = k;
        // Second pass, now that the list head is known: every function that writes to it, with the
        // text assignment anywhere in the function (the compiler does not always keep the order).
        var found = new List<(long Rva, long Entry, string Text, List<long> Pointers)>();
        var link = new Regex($"_?{Regex.Escape(best)} = &DAT_(1[0-9a-f]{{8}});");
        foreach (var r in db.Query("SELECT f.id, f.rva FROM code_fts JOIN functions f ON f.id = code_fts.rowid"
                                   + " WHERE code_fts MATCH ? AND f.binary_id = ?", $"\"{best}\"", binaryId).ToList())
        {
            if (DecompStore.Decompress(db.Scalar("SELECT text FROM code WHERE function_id = ?", r[0])) is not string body)
                continue;
            long rva = L(r[1]);
            var flat = Py.Collapse(body);
            foreach (Match m in link.Matches(flat))
            {
                var hex = m.Groups[1].Value;
                string text;
                var literal = Regex.Match(flat, $@"_?DAT_{hex} = ""((?:[^""\\]|\\.)*)"";");
                if (literal.Success)
                    text = literal.Groups[1].Value;
                else
                {
                    // a short text Ghidra did not recognise as a string: read it from the file
                    var raw = Regex.Match(flat, $@"_?DAT_{hex} = (?:\([^()]*\))?&DAT_(1[0-9a-f]{{8}});");
                    var read = raw.Success ? s.ReadText(binary, Py.Hex(raw.Groups[1].Value)!.Value - imageBase) : null;
                    if (read == null)
                        continue;                      // the text comes in as an argument: not resolved here
                    text = read;
                }
                long entry = Py.Hex(hex)!.Value - imageBase;
                found.Add((rva, entry, text, Pointers(flat, entry + ValueOffset + imageBase, imageBase)));
            }
        }
        var rows = new List<object?[]>();
        foreach (var (rva, entry, text, pointers) in found)
        {
            rows.Add([binaryId, entry, "entry", text, rva]);
            rows.Add([binaryId, entry + ValueOffset, "value", text, rva]);
            rows.AddRange(pointers.Select(p => new object?[] { binaryId, p, "pointer", text, rva }));
        }
        db.Execute("BEGIN");
        db.Execute("DELETE FROM fixed_strings WHERE binary_id = ?", binaryId);
        foreach (var row in rows)
            db.Execute("INSERT INTO fixed_strings VALUES (?,?,?,?,?)", row);
        db.Execute("COMMIT");
        return found.Select(f => f.Entry).Distinct().Count();
    }

    public static int Run(DecompSession s, string? text, bool rebuild, string binary, int limit)
    {
        // read-only unless the table has to be (re)built
        var db = File.Exists(s.Settings.DbPath) && !rebuild ? s.Db : s.OpenWrite();
        if (rebuild || !SystemsCommand.HasRows(db, "fixed_strings"))
        {
            db = s.OpenWrite();
            int count = Find(s, db, binary);
            s.Err.WriteLine($"{count} global FixedStrings found in {binary}");
        }
        var m = CodeCommand.Address.Match(text ?? "");
        List<object?[]> rows;
        if (m.Success)
        {
            long rva = Py.Hex(m.Groups[2].Value) ?? -1;
            foreach (long imageBase in new long[] { 0x180000000, 0x140000000 })
                if (rva >= imageBase)
                {
                    rva -= imageBase;
                    break;
                }
            rows = db.Query("SELECT b.name, s.rva, s.kind, s.text, s.registered_in FROM fixed_strings s JOIN binaries b ON b.id = s.binary_id"
                            + " WHERE b.name LIKE ? AND s.rva = ?", m.Groups[1].Value + "%", rva).ToList();
        }
        else
            rows = db.Query("SELECT b.name, s.rva, s.kind, s.text, s.registered_in FROM fixed_strings s JOIN binaries b ON b.id = s.binary_id"
                            + " WHERE s.text LIKE ? ORDER BY s.text, s.kind DESC LIMIT ?", "%" + (text ?? "") + "%", limit).ToList();
        foreach (var r in rows)
        {
            string b = Py.Str(r[0]);
            s.Out.WriteLine($"{b}+0x{L(r[1]):X}  {Py.Str(r[2]),-7}  \"{Py.Str(r[3])}\"   (registered in {b}+0x{L(r[4]):X})");
        }
        if (rows.Count == 0)
            s.Err.WriteLine("nothing found");
        return 0;
    }
}
