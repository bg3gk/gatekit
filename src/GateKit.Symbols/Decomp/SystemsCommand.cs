using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>`systems`: where the game creates each ECS system (factory, constructor, virtual
/// table) and its per-frame update, read from the pseudo-code.</summary>
internal static class SystemsCommand
{
    const string SystemIndex = @"TypeIndex<([^;]+?)>_SystemsContext";
    static readonly Regex VtableSet = new(@"\*\s*(?:\([^()]*\))?\s*(\w+) = &?(PTR_\w+_|PTR_\w+|\w+_vftable_?);", RegexOptions.Compiled);
    static readonly Regex TableAddress = new(@"PTR_\w+?_(1[0-9a-f]{8})$", RegexOptions.Compiled);
    static readonly Regex Use = new(@"(\w+) = " + SystemIndex + ";", RegexOptions.Compiled);
    static readonly Regex HelperCall = new(@"\b(FUN_1[0-9a-f]{8})\(([^;]*?)\);", RegexOptions.Compiled);
    static readonly Regex DirectIndex = new(SystemIndex + "$", RegexOptions.Compiled);
    static readonly Regex StoredPointer = new(@"= (?:\([^()]*\))?(?:thunk_)?FUN_(1[0-9a-f]{8});", RegexOptions.Compiled);
    static readonly Regex VoidEntry = new(@"\bvoid\s+(?:thunk_)?FUN_", RegexOptions.Compiled);
    static readonly Regex CallOnSystem = new(@"\b(?:thunk_)?FUN_(1[0-9a-f]{8})\(param_1,", RegexOptions.Compiled);
    static readonly Regex NamedCallOnSystem = new(@"(?<![\w:])((?:\w+::)+~?\w+)\(param_1,", RegexOptions.Compiled);

    static long L(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    /// <summary>True when the table exists and has a row (a database made before the table was added has neither).</summary>
    internal static bool HasRows(SqliteConnection db, string table)
    {
        try
        {
            return db.One($"SELECT 1 FROM {table} LIMIT 1") != null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    static int Count(string text, string part)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(part, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += part.Length;
        }
        return n;
    }

    /// <summary>Reads from the pseudo-code where each ECS system is created. The game adds a system
    /// to its world with one helper function that takes the system's type index; that helper is
    /// the function most often called with a `TypeIndex&lt;...&gt;_SystemsContext` argument. The
    /// caller is the factory; the constructor is the function it calls before that which stores a
    /// virtual table into its first argument. Every row is an inference to check, not a fact.</summary>
    public static int FindSystemFactories(SqliteConnection db, string binary)
    {
        var idValue = db.Scalar("SELECT id FROM binaries WHERE name = ?", binary) ?? throw new SymbolsException($"{binary} is not loaded");
        long binaryId = L(idValue);

        string TextOf(long rva)
        {
            var blob = db.Scalar("SELECT c.text FROM functions f JOIN code c ON c.function_id = f.id WHERE f.binary_id = ? AND f.rva = ?", binaryId, rva);
            return DecompStore.Decompress(blob) is string t ? Py.Collapse(t) : "";
        }

        // the helper: among functions called from many places, the one whose callers pass a system index
        var votes = new Dictionary<string, int>(StringComparer.Ordinal);
        var sample = db.Query("SELECT f.rva FROM code_fts JOIN functions f ON f.id = code_fts.rowid WHERE code_fts MATCH ? AND f.binary_id = ?",
                              "\"_SystemsContext\"", binaryId).Select(r => L(r[0])).ToList();
        foreach (long rva in sample)
        {
            var flat = TextOf(rva);
            foreach (Match m in Use.Matches(flat))
            {
                int end = m.Index + m.Length;
                var window = flat.Substring(end, Math.Min(6000, flat.Length - end));
                foreach (Match call in HelperCall.Matches(window))
                {
                    var arguments = call.Groups[2].Value.Split(',').Select(Py.Strip).ToArray();
                    if (arguments.Length == 3 && arguments[1] == m.Groups[1].Value)
                        votes[call.Groups[1].Value] = votes.GetValueOrDefault(call.Groups[1].Value) + 1;
                }
            }
        }
        if (votes.Count == 0)
            return 0;
        // max() keeps the first of equal counts, in the order the votes were first cast
        string helper = votes.First().Key;
        foreach (var (k, v) in votes)
            if (v > votes[helper])
                helper = k;
        long helperRva = Py.Hex(helper[4..])!.Value - 0x140000000;
        var call2 = new Regex(Regex.Escape(helper) + @"\(([^,;]+),\s*([^,;]+),");
        var updates = new List<object?[]>();
        var rows = new List<object?[]>();
        var stored = new Dictionary<long, List<string>>();      // callee -> virtual tables it writes into its first argument
        var factories = db.Query("SELECT DISTINCT caller FROM calls INDEXED BY calls_callee WHERE binary_id = ? AND callee = ?", binaryId, helperRva)
                          .Select(r => L(r[0])).ToList();
        foreach (long factory in factories)
        {
            var flat = TextOf(factory);
            var found = call2.Matches(flat).ToList();
            foreach (var m in found)
            {
                var variable = Py.Strip(m.Groups[2].Value);
                var direct = DirectIndex.Match(variable);
                var before = flat[..m.Index];
                var assigned = Regex.Matches(before, Regex.Escape(variable) + " = " + SystemIndex + ";");
                string? system = direct.Success ? direct.Groups[1].Value : assigned.Count > 0 ? assigned[^1].Groups[1].Value : null;
                if (system == null)
                    continue;
                long? constructor = null, vtable = null;
                string note = found.Count == 1 ? "" : $"one of {found.Count} systems created in this function; constructor and table not attributed";
                if (found.Count == 1)
                {
                    var own = VtableSet.Matches(before).Select(x => x.Groups[2].Value).ToList();
                    foreach (var r in db.Query("SELECT DISTINCT callee FROM calls INDEXED BY calls_caller WHERE binary_id = ? AND caller = ?", binaryId, factory).ToList())
                    {
                        long callee = L(r[0]);
                        int where = flat.IndexOf(string.Create(CultureInfo.InvariantCulture, $"FUN_14{callee:x7}("), StringComparison.Ordinal);
                        if (where < 0 || where > m.Index)
                            continue;
                        if (!stored.TryGetValue(callee, out var tables))
                        {
                            var body = TextOf(callee);
                            tables = body.Contains("param_1 = &", StringComparison.Ordinal)
                                ? VtableSet.Matches(body).Where(x => x.Groups[1].Value == "param_1").Select(x => x.Groups[2].Value).ToList()
                                : [];
                            stored[callee] = tables;
                        }
                        if (tables.Count > 0)
                        {
                            constructor = callee;
                            own = tables;
                        }
                    }
                    if (own.Count > 0)
                    {
                        var address = TableAddress.Match(own[^1].TrimEnd('_'));
                        vtable = address.Success ? Py.Hex(address.Groups[1].Value)!.Value - 0x140000000 : null;
                        note = (constructor == null ? "constructor inlined into the factory" : "") + (address.Success ? "" : $" table named {own[^1]}");
                    }
                }
                rows.Add([binaryId, system, factory, constructor, vtable, Py.Strip(note)]);
                if (found.Count == 1)
                {
                    // Function pointers the factory stores: the registration helper's update entry
                    // for this system. It builds the views and calls the system's own update, unless
                    // the compiler folded that update into it.
                    foreach (var pointer in StoredPointer.Matches(flat).Select(x => x.Groups[1].Value).Distinct())
                    {
                        long thunk = Py.Hex(pointer)!.Value - 0x140000000;
                        var body = TextOf(thunk);
                        if (body.Length == 0)
                            continue;
                        int brace = body.IndexOf('{');
                        var head = brace >= 0 ? body[..brace] : body;
                        int parameters = Count(head, "param_");
                        if (parameters is not (2 or 3) || !VoidEntry.IsMatch(head))
                            continue;                 // an update entry is void (system, world[, time])
                        var calls = CallOnSystem.Matches(body).Select(x => Py.Hex(x.Groups[1].Value)!.Value - 0x140000000).Where(c => c != thunk).ToList();
                        // callees that already carry a name (from the Toolkit) are looked up by it
                        foreach (Match named in NamedCallOnSystem.Matches(body))
                        {
                            var hit = db.Query("SELECT rva FROM functions WHERE binary_id = ? AND name = ? LIMIT 2", binaryId, named.Groups[1].Value).ToList();
                            if (hit.Count == 1 && L(hit[0][0]) != thunk)
                                calls.Add(L(hit[0][0]));
                        }
                        calls = calls.Distinct().ToList();
                        var size = db.Scalar("SELECT size FROM functions WHERE binary_id = ? AND rva = ?", binaryId, thunk);
                        bool small = size != null && L(size) != 0 && L(size) <= 160;
                        if (small && calls.Count == 1)
                            updates.Add([binaryId, system, thunk, calls[0], "the entry builds the views and calls the update"]);
                        else if (small && calls.Count > 1)
                            for (int i = 0; i < calls.Count; i++)
                                updates.Add([binaryId, system, thunk, calls[i], $"step {i + 1} of {calls.Count} the entry calls on the system"]);
                        else if (!small)
                            updates.Add([binaryId, system, thunk, thunk, "the update is folded into the entry"]);
                        else
                            updates.Add([binaryId, system, thunk, null, "small entry with no call on the system found: look at it"]);
                    }
                }
            }
        }
        db.Execute("BEGIN");
        db.Execute("DELETE FROM system_updates WHERE binary_id = ?", binaryId);
        foreach (var u in updates)
            db.Execute("INSERT INTO system_updates VALUES (?,?,?,?,?)", u);
        db.Execute("DELETE FROM system_factories WHERE binary_id = ?", binaryId);
        foreach (var r in rows)
            db.Execute("INSERT INTO system_factories VALUES (?,?,?,?,?,?)", r);
        db.Execute("COMMIT");
        return rows.Count;
    }

    public static int Run(DecompSession s, string? name, bool rebuild, string binary)
    {
        // read-only unless the table has to be (re)built
        var db = File.Exists(s.Settings.DbPath) && !rebuild ? s.Db : s.OpenWrite();
        if (rebuild || !HasRows(db, "system_factories"))
        {
            db = s.OpenWrite();
            int count = FindSystemFactories(db, binary);
            s.Err.WriteLine($"{count} systems found in {binary}");
        }
        var rows = db.Query("SELECT b.name, s.system, s.factory, s.constructor, s.vtable, s.note FROM system_factories s JOIN binaries b ON b.id = s.binary_id"
                            + " WHERE s.system LIKE ? ORDER BY s.system", "%" + (name ?? "") + "%").ToList();
        foreach (var r in rows)
        {
            string b = Py.Str(r[0]);
            long? constructor = r[3] == null ? null : L(r[3]), vtable = r[4] == null ? null : L(r[4]);
            string note = r[5] as string ?? "";
            s.Out.WriteLine($"{Py.Str(r[1])}\n    created in {b}+0x{L(r[2]):X}"
                            + (constructor is long c && c != 0 ? $", constructor {b}+0x{c:X}" : "")
                            + (vtable is long v && v != 0 ? $", virtual table {b}+0x{v:X}" : "") + (note.Length > 0 ? $"   ({note})" : ""));
            foreach (var u in db.Query("SELECT u.thunk, u.updater, u.note FROM system_updates u JOIN binaries b ON b.id = u.binary_id"
                                       + " WHERE b.name = ? AND u.system = ?", b, r[1]))
                s.Out.WriteLine("    per-frame update: " + (u[1] != null ? $"{b}+0x{L(u[1]):X}" : "?")
                                + $"   (entry stored by the factory: {b}+0x{L(u[0]):X}; {Py.Str(u[2])})");
        }
        if (rows.Count == 0)
            s.Err.WriteLine("nothing found");
        return 0;
    }
}
