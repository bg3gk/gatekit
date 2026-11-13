using System.Text;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Queries;

/// <summary>A name known for an address of a game executable.</summary>
/// <param name="Rva">Address relative to the image base.</param>
/// <param name="Kind">function, data or label.</param>
/// <param name="Name">The best name.</param>
/// <param name="Mangled">The decorated name, when the name comes from a Toolkit export.</param>
/// <param name="Source">Where the name comes from.</param>
/// <param name="Aliases">Other names of the same address (identical functions are folded into one).</param>
public sealed record GameLabel(long Rva, string Kind, string Name, string? Mangled, string Source, List<string> Aliases);

/// <summary>Everything known about the addresses of a game executable, and the files debuggers and
/// disassemblers load (export --format json|ghidra|x64dbg|ida).</summary>
public static class GameLabels
{
    static readonly Dictionary<string, int> MethodRank = new() { ["strings"] = 0, ["vtable"] = 1, ["vtable user"] = 2, ["calls"] = 3 };

    /// <summary>(binary name, labels sorted by address). Throws when no game executable matches.</summary>
    public static (string Module, List<GameLabel> Labels) Collect(SqliteConnection db, string dll)
    {
        var row = db.One("SELECT id, name FROM dlls WHERE origin = 'game' AND name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT 1", Sql.Like(dll))
                  ?? throw new SymbolsException($"no game executable matches '{dll}'");
        long gameId = Convert.ToInt64(row[0]);
        var labels = new Dictionary<long, (GameLabel Label, int Rank)>();

        void Add(object? rvaValue, string kind, string? name, string source, string? mangled, int rank)
        {
            if (rvaValue == null || string.IsNullOrEmpty(name))
                return;
            long rva = Convert.ToInt64(rvaValue);
            if (!labels.TryGetValue(rva, out var cur))
                labels[rva] = (new GameLabel(rva, kind, name, mangled, source, []), rank);
            else if (rank < cur.Rank)
            {
                var aliases = new List<string> { cur.Label.Name };
                aliases.AddRange(cur.Label.Aliases);
                labels[rva] = (new GameLabel(rva, kind, name, mangled, source, aliases), rank);
            }
            else if (name != cur.Label.Name && !cur.Label.Aliases.Contains(name))
                cur.Label.Aliases.Add(name);
        }

        foreach (var r in db.Query(
                     "SELECT m.game_rva, m.method, m.relation, s.full_name, s.mangled, m.evidence FROM matches m"
                     + " JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva AND s.kind IN ('method','function')"
                     + " WHERE m.game_dll = ? ORDER BY length(s.full_name)", gameId))
        {
            var method = (string?)r[1] ?? "";
            var relation = (string?)r[2];
            if (relation == "inlined" && method != "vtable")
                continue;                       // the game function only contains that code: not its name
            Add(r[0], "function", (string?)r[3], $"toolkit match by {method}", (string?)r[4],
                MethodRank.GetValueOrDefault(method, 5) + (relation == "partial" ? 10 : 0));
        }
        foreach (var r in db.Query("SELECT rva, name, mapping FROM se_symbols WHERE dll_id = ?", gameId))
            Add(r[0], "label", ((string?)r[1])?.Replace("__", "::"), $"Script Extender pattern {r[2]}", null, 4);
        foreach (var r in db.Query("SELECT func_rva, type FROM type_names WHERE dll_id = ? AND func_rva IS NOT NULL", gameId))
            Add(r[0], "function", $"ls::GetTypeName<{r[1]}>", "type name string", null, 6);
        foreach (var r in db.Query("SELECT index_rva, type, context FROM ecs_ids WHERE dll_id = ? AND type NOT LIKE 'ecs::query::spec::Spec<%'", gameId))
        {
            var context = (string?)r[2] ?? "unknown";
            int cut = context.LastIndexOf("::", StringComparison.Ordinal);
            Add(r[0], "data", $"TypeIndex<{r[1]}>@{(cut >= 0 ? context[(cut + 2)..] : context)}", "ECS registration record", null, 6);
        }
        foreach (var r in db.Query(
                     "SELECT g.rva, s.full_name, s.mangled FROM vtab_matches m JOIN vtabs g ON g.id = m.game_vtab"
                     + " JOIN vtabs t ON t.id = m.toolkit_vtab JOIN symbols s ON s.id = t.symbol_id WHERE g.dll_id = ?"
                     + " ORDER BY length(s.full_name)", gameId))
            Add(r[0], "data", (string?)r[1], "virtual table match", (string?)r[2], 1);
        return ((string)row[1]!, labels.Values.Select(v => v.Label).OrderBy(l => l.Rva).ToList());
    }

    /// <summary>The names known for a game executable in a form a debugger or disassembler can load:
    /// json, ghidra (tab-separated: rva, kind, name, decorated name or empty, source), x64dbg, ida (an IDAPython script).</summary>
    public static string Render(string module, List<GameLabel> labels, string format)
    {
        switch (format)
        {
            case "json":
                // json.dumps(..., indent=1) of the Python tool, byte for byte
                return PyJson.Dumps(new Dictionary<string, object?>
                {
                    ["module"] = module,
                    ["labels"] = labels.Select(e => new Dictionary<string, object?>
                    {
                        ["rva"] = e.Rva, ["kind"] = e.Kind, ["name"] = e.Name, ["mangled"] = e.Mangled, ["source"] = e.Source, ["aliases"] = e.Aliases,
                    }).ToList(),
                }, 1);
            case "ghidra":
            {
                var b = new StringBuilder();
                foreach (var e in labels)
                    b.Append(e.Rva.ToString("X")).Append('\t').Append(e.Kind).Append('\t').Append(e.Name).Append('\t')
                        .Append(e.Mangled ?? "").Append('\t').Append(e.Source).Append('\n');
                return labels.Count == 0 ? "\n" : b.ToString();
            }
            case "x64dbg":
                return PyJson.Dumps(new Dictionary<string, object?>
                {
                    ["labels"] = labels.Select(e => new Dictionary<string, object?>
                    {
                        ["module"] = module.ToLowerInvariant(), ["address"] = "0x" + e.Rva.ToString("X"), ["manual"] = true,
                        ["text"] = Decomp.Py.Left(e.Name, 255),
                    }).ToList(),
                }, 1);
            case "ida":
            {
                var b = new StringBuilder();
                b.Append("import idaapi, idc\nbase = idaapi.get_imagebase()\nnames = [\n");
                foreach (var e in labels)
                    b.Append("    (0x").Append(e.Rva.ToString("X")).Append(", ").Append(PythonString(e.Name)).Append("),\n");
                b.Append("]\nfor rva, name in names:\n");
                b.Append("    idc.set_name(base + rva, ''.join(c if c.isalnum() or c in '_:<>,~' else '_' for c in name)[:500], idc.SN_NOWARN | idc.SN_FORCE)\n");
                b.Append("    idc.set_cmt(base + rva, name, 1)\n");
                return b.ToString();
            }
            default:
                throw new SymbolsException($"unknown format '{format}' (json, ghidra, x64dbg, ida)");
        }
    }

    static string PythonString(string s) => PyText.Repr(s);

    /// <summary>Writes the tab-separated names file the Ghidra script ApplyNames.java reads. Returns
    /// the number of names, or -1 when the symbol database or the executable is not there.</summary>
    public static int WriteGhidraNames(string symbolsDb, string dll, string outPath)
    {
        if (!File.Exists(symbolsDb))
            return -1;
        using var db = Sql.OpenReadOnly(symbolsDb);
        try
        {
            var (module, labels) = Collect(db, dll);
            File.WriteAllText(outPath, Render(module, labels, "ghidra"), new UTF8Encoding(false));
            return labels.Count;
        }
        catch (SymbolsException)
        {
            return -1;
        }
    }
}
