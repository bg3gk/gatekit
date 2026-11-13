using System.Text.RegularExpressions;
using GateKit.Symbols.Pe;

namespace GateKit.Symbols.Queries;

internal static partial class SymbolText
{
    public static void Callers(SymbolContext c, string text, int limit)
    {
        var found = c.Targets(text);
        foreach (var (dllId, target) in found)
        {
            long rva = c.Whole(dllId, target);
            var folded = new Dictionary<(long, long), long>();
            foreach (var r in c.Db.Query("SELECT dll_id, caller_rva, sites FROM calls WHERE callee_dll = ? AND callee_rva = ?"
                                         + " ORDER BY dll_id, caller_rva", dllId, rva).ToList())
            {
                long callerDll = PyText.L(r[0]);
                var key = (callerDll, c.Whole(callerDll, PyText.L(r[1])));
                folded[key] = folded.GetValueOrDefault(key) + PyText.L(r[2]);
            }
            var rows = folded.OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2).Take(limit).ToList();
            c.WriteLine($"{c.Label(dllId, rva)}   <- {rows.Count} callers");
            foreach (var p in rows)
                c.WriteLine($"    {c.Label(p.Key.Item1, p.Key.Item2)}" + (p.Value > 1 ? $"   x{p.Value}" : ""));
        }
        var imports = SymbolContext.Address().IsMatch(text) ? [] :
            c.Db.Query("SELECT c.import, c.dll_id, c.caller_rva FROM calls c WHERE c.import LIKE ? ESCAPE '\\'"
                       + " ORDER BY 1, 2, 3 LIMIT ?", Sql.Like(text), limit).ToList();
        foreach (var r in imports)
            c.WriteLine($"import {PyText.Str(r[0])}   <- {c.Label(PyText.L(r[1]), PyText.L(r[2]))}");
        if (found.Count == 0 && imports.Count == 0)
            c.Note("nothing found");
    }

    public static void Callees(SymbolContext c, string text, int limit)
    {
        var found = c.Targets(text);
        foreach (var (dllId, target) in found)
        {
            long rva = c.Whole(dllId, target);
            var own = c.Parts(dllId, rva);
            var rows = c.Db.Query("SELECT callee_dll, callee_rva, import, sum(sites) FROM calls WHERE dll_id = ? AND caller_rva IN ("
                                  + string.Join(",", own.Select(_ => "?")) + ") GROUP BY callee_dll, callee_rva, import ORDER BY callee_dll, callee_rva LIMIT ?",
                                  [dllId, .. own.Cast<object?>(), limit]).ToList();
            c.WriteLine($"{c.Label(dllId, rva)}   -> {rows.Count} callees");
            foreach (var r in rows)
            {
                long sites = PyText.L(r[3]);
                c.WriteLine("    " + (PyText.Truthy(r[2]) ? $"import {PyText.Str(r[2])}" : c.Label(PyText.L(r[0]), PyText.L(r[1])))
                            + (sites > 1 ? $"   x{sites}" : ""));
            }
        }
        if (found.Count == 0)
            c.Note("nothing found");
    }

    public static void Vtable(SymbolContext c, string name, bool full)
    {
        if (SymbolContext.Address().IsMatch(name))
        {
            VtableAt(c, name);
            return;
        }
        int cut = name.LastIndexOf("::", StringComparison.Ordinal);
        string ns = cut >= 0 ? name[..cut] : "", cls = cut >= 0 ? name[(cut + 2)..] : name;
        var tables = c.Db.Query("SELECT s.id, s.dll_id, d.name, s.rva, s.signature FROM symbols s JOIN dlls d ON d.id = s.dll_id"
                                + " WHERE s.kind = 'vftable' AND s.class = ? AND s.namespace = ? ORDER BY d.name, s.rva", cls, ns).ToList();
        if (tables.Count == 0)
            tables = c.Db.Query("SELECT s.id, s.dll_id, d.name, s.rva, s.signature FROM symbols s JOIN dlls d ON d.id = s.dll_id"
                                + " WHERE s.kind = 'vftable' AND (s.namespace || '::' || s.class) LIKE ? ESCAPE '\\' ORDER BY d.name, s.rva LIMIT 20",
                                "%" + Sql.Like(name)[1..^1]).ToList();
        foreach (var t in tables)
        {
            long dllId = PyText.L(t[1]);
            var slots = c.Db.Query("SELECT idx, rva FROM vtable_slots WHERE symbol_id = ? ORDER BY idx", t[0]).ToList();
            c.WriteLine($"{PyText.Str(t[2])}+0x{X(PyText.L(t[3]))}  {PyText.Str(t[4])}  ({slots.Count} slots)");
            foreach (var s in slots)
            {
                long idx = PyText.L(s[0]), slot = PyText.L(s[1]);
                var names = c.Db.Query("SELECT signature FROM symbols WHERE dll_id = ? AND rva = ? AND kind <> 'vftable'"
                                       + " ORDER BY length(signature) LIMIT 3", dllId, slot).Select(r => PyText.Str(r[0])).ToList();
                string text = names.Count > 0 ? names[0] : "(not exported)";
                if (names.Count > 1)
                    text += $"   (+{names.Count - 1} more names at this address)";
                if (!full)
                    text = PyText.Cut(text, 200, 197);
                c.WriteLine($"  [{idx,3}] +0x{PyText.Hex(idx * 8, 3)}  0x{X(slot)}  {text}");
            }
        }
        if (tables.Count == 0)
            c.Note("no exported virtual table for that class");
    }

    /// <summary>The file of a binary listed in the database: under the game or the Toolkit folder.</summary>
    static string? LocateBinary(SymbolContext c, string name, string relative, string? origin)
    {
        string root = origin == "game" ? c.Env.Game : c.Env.Toolkit;
        foreach (var p in new[] { Path.Combine(root, relative), Path.Combine(root, "bin", name), Path.Combine(root, name) })
            if (File.Exists(p))
                return p;
        return null;
    }

    /// <summary>Slots of the virtual table at an address (binary+0xRVA): from the tables found when the
    /// database was built, else read from the file itself. An address inside a table is accepted.</summary>
    static void VtableAt(SymbolContext c, string text)
    {
        var m = SymbolContext.Address().Match(text);
        var dll = c.Db.One("SELECT id, name, path, origin FROM dlls WHERE name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT 1", Sql.Like(m.Groups[1].Value))
                  ?? throw new SymbolsException("no such binary in the database");
        long dllId = PyText.L(dll[0]);
        string dllName = PyText.Str(dll[1]);
        long rva = SymbolContext.Rva(m);
        var row = c.Db.One("SELECT id, rva, slots, symbol_id FROM vtabs WHERE dll_id = ? AND rva <= ? AND rva + slots * 8 > ?"
                           + " ORDER BY rva DESC LIMIT 1", dllId, rva, rva);
        long start;
        List<long> slots;
        string source;
        if (row != null)
        {
            start = PyText.L(row[1]);
            slots = c.Db.Query("SELECT rva FROM vtab_slots WHERE vtab_id = ? ORDER BY idx", row[0]).Select(r => PyText.L(r[0])).ToList();
            source = "database";
        }
        else
        {
            var path = LocateBinary(c, dllName, PyText.Str(dll[2]), dll[3] as string)
                       ?? throw new SymbolsException($"no table recorded at that address, and {PyText.Str(dll[2])} was not found to read it (set BG3_GAME / BG3_TOOLKIT)");
            start = rva;
            slots = PeImage.Open(path).Vtable((uint)rva, new HashSet<uint>()).Select(s => (long)s).ToList();
            source = "read from the file: ends at the first value that is not a code address, so it may run into the next table";
        }
        if (slots.Count == 0)
            throw new SymbolsException("no code addresses at that address: not a virtual table");
        var names = c.Db.Query("SELECT s.full_name FROM vtab_matches m JOIN vtabs t ON t.id = m.toolkit_vtab JOIN symbols s ON s.id = t.symbol_id"
                               + " WHERE m.game_vtab = ?", row != null ? row[0] : -1).Select(r => PyText.Str(r[0])).ToList();
        if (row != null && PyText.Truthy(row[3]))
            names.AddRange(c.Db.Query("SELECT full_name FROM symbols WHERE id = ?", row[3]).Select(r => PyText.Str(r[0])));
        c.WriteLine($"{dllName}+0x{X(start)}  {slots.Count} slots  [{source}]" + (names.Count > 0 ? "  = " + string.Join(", ", names.Take(3)) : ""));
        for (int idx = 0; idx < slots.Count; idx++)
        {
            long slot = slots[idx];
            string mark = start + idx * 8 == rva && rva != start ? "  <-- the address asked for" : "";
            var size = c.Db.One("SELECT size FROM functions WHERE dll_id = ? AND rva = ?", dllId, slot);
            c.WriteLine($"  [{idx,3}] +0x{PyText.Hex(idx * 8L, 3)}  {c.Label(dllId, slot)}" + (size != null ? $"   ({PyText.Str(size[0])} bytes)" : "") + mark);
        }
    }

    [GeneratedRegex(@"\b(class|struct|enum|union) ")]
    private static partial Regex TypeKeyword();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static void Users(SymbolContext c, IReadOnlyList<string> words, string? dll, int limit, int per, int callers, int perCallers)
    {
        var game = c.Game(dll) ?? throw new SymbolsException($"no game executable matches {PyText.Repr(dll ?? "bg3.exe")}");
        string text = string.Join(" ", words);
        var found = new List<(long Rva, string What)>();
        var m = SymbolContext.Address().Match(text);
        if (m.Success)
            found.Add((SymbolContext.Rva(m), text));
        else
        {
            var rows = c.Db.Query("SELECT index_rva, type, context FROM ecs_ids WHERE dll_id = ? AND type = ? GROUP BY index_rva", game.Id, text).ToList();
            if (rows.Count == 0)
                rows = c.Db.Query("SELECT index_rva, type, context FROM ecs_ids WHERE dll_id = ? AND type LIKE ? ESCAPE '\\'"
                                  + " GROUP BY index_rva ORDER BY length(type) LIMIT ?", game.Id, Sql.Like(text), limit).ToList();
            if (rows.Count == 0)
            {
                // a type name as `types` prints it: with class/struct keywords, other spacing, cut with "..."
                string loose = TypeKeyword().Replace(text.TrimEnd('.', ' '), "");
                loose = Spaces().Replace(loose, "");
                rows = c.Db.Query("SELECT index_rva, type, context FROM ecs_ids WHERE dll_id = ? AND instr(replace(type, ' ', ''), ?) > 0"
                                  + " GROUP BY index_rva ORDER BY length(type) LIMIT ?", game.Id, loose, limit).ToList();
            }
            foreach (var r in rows)
            {
                string context = r[2] is string s && s.Length > 0 ? s : "query or static registrant";
                int cut = context.LastIndexOf("::", StringComparison.Ordinal);
                found.Add((PyText.L(r[0]), $"{PyText.Str(r[1])}  [{(cut >= 0 ? context[(cut + 2)..] : context)}]"));
            }
            found.AddRange(c.Db.Query("SELECT rva, name FROM se_symbols WHERE dll_id = ? AND name LIKE ? ESCAPE '\\'", game.Id, Sql.Like(text))
                .Select(r => (PyText.L(r[0]), $"{PyText.Str(r[1])}  [Script Extender symbol]")).ToList());
            found.AddRange(c.Db.Query(
                    "SELECT gv.rva, s.full_name FROM vtab_matches m JOIN vtabs gv ON gv.id = m.game_vtab JOIN vtabs t ON t.id = m.toolkit_vtab"
                    + " JOIN symbols s ON s.id = t.symbol_id WHERE gv.dll_id = ? AND s.full_name LIKE ? ESCAPE '\\' GROUP BY gv.rva",
                    game.Id, Sql.Like(text))
                .Select(r => (PyText.L(r[0]), $"{PyText.Str(r[1])}  [virtual table]")).ToList());
        }
        if (c.Db.Scalar("SELECT 1 FROM xrefs LIMIT 1") == null)
            throw new SymbolsException("the xrefs table is empty: build the database again (gatekit symbols build)");
        foreach (var (rva, what) in found.Take(limit))
        {
            var folded = new Dictionary<long, SortedDictionary<string, long>>();
            foreach (var r in c.Db.Query("SELECT func_rva, kind, uses FROM xrefs WHERE dll_id = ? AND target_rva = ?", game.Id, rva).ToList())
            {
                long func = c.Whole(game.Id, PyText.L(r[0]));
                if (!folded.TryGetValue(func, out var kinds))
                    folded[func] = kinds = new SortedDictionary<string, long>(StringComparer.Ordinal);
                string kind = PyText.Str(r[1]);
                kinds[kind] = kinds.GetValueOrDefault(kind) + PyText.L(r[2]);
            }
            var rows = folded.Select(p => (Func: p.Key, Kinds: string.Join(" ", p.Value.Select(k => k.Key + (k.Value > 1 ? $"x{k.Value}" : ""))),
                                           Size: c.WholeSize(game.Id, p.Key)))
                .OrderBy(r => r.Size ?? 0).ToList();
            c.WriteLine($"{game.Name}+0x{X(rva)}  {Decomp.Py.Left(what, 160)}   <- {rows.Count} functions");
            foreach (var r in rows.Take(per))
            {
                c.WriteLine($"    {c.Label(game.Id, r.Func),-60} {r.Size ?? 0,6} bytes  {r.Kinds}");
                if (callers > 0)
                    CallersTree(c, game.Id, r.Func, callers, perCallers, "        ", [r.Func]);
            }
            if (rows.Count > per)
                c.WriteLine($"    ... {rows.Count - per} more (--per N)");
        }
        if (found.Count == 0)
            c.Note("nothing found");
    }

    /// <summary>Prints who calls a function, <paramref name="depth"/> steps up. A function nobody calls
    /// directly is looked up in the virtual tables instead: it is reached through a table slot.</summary>
    static void CallersTree(SymbolContext c, long dllId, long rva, int depth, int per, string indent, HashSet<long> seen)
    {
        var folded = new Dictionary<long, long>();
        foreach (var r in c.Db.Query("SELECT caller_rva, sites FROM calls WHERE callee_dll = ? AND callee_rva = ? AND dll_id = ?", dllId, rva, dllId).ToList())
        {
            long caller = c.Whole(dllId, PyText.L(r[0]));
            folded[caller] = folded.GetValueOrDefault(caller) + PyText.L(r[1]);
        }
        var rows = folded.Select(p => (Caller: p.Key, Sites: p.Value, Size: c.WholeSize(dllId, p.Key))).OrderBy(r => -(r.Size ?? 0)).ToList();
        if (rows.Count == 0)
        {
            var slots = c.Db.Query("SELECT v.rva, s.idx, v.slots FROM vtab_slots s JOIN vtabs v ON v.id = s.vtab_id WHERE v.dll_id = ? AND s.rva = ?"
                                   + " LIMIT 4", dllId, rva).ToList();
            string dll = c.DllName(dllId);
            foreach (var s in slots)
            {
                string table = $"{dll}+0x{X(PyText.L(s[0]))}";
                c.WriteLine($"{indent}<- slot {PyText.Str(s[1])} of the virtual table {table} ({PyText.Str(s[2])} slots)   (gatekit symbols vtable {table})");
            }
            if (slots.Count == 0)
                c.WriteLine($"{indent}<- no direct caller and no table slot found (called through a pointer, or a part of a larger function)");
            return;
        }
        foreach (var (caller, sites, size) in rows.Take(per))
        {
            bool again = seen.Contains(caller);
            c.WriteLine($"{indent}<- {c.Label(dllId, caller),-56} {size ?? 0,6} bytes" + (sites > 1 ? $"  x{sites}" : "") + (again ? "  (already listed)" : ""));
            if (depth > 1 && !again)
            {
                seen.Add(caller);
                CallersTree(c, dllId, caller, depth - 1, per, indent + "    ", seen);
            }
        }
        if (rows.Count > per)
            c.WriteLine($"{indent}<- ... {rows.Count - per} more callers (--per-callers N)");
    }
}
