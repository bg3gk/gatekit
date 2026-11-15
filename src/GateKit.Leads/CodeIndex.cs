using System.Text.RegularExpressions;
using GateKit.Symbols;
using Microsoft.Data.Sqlite;

namespace GateKit.Leads;

/// <summary>
/// <see cref="ICodeIndex"/> over the two databases built on the user's machine: the pseudo-code
/// (decomp.sqlite: functions, direct calls, classified helpers, systems' updates, matches to the
/// Toolkit's named functions) and the symbols (symbols.sqlite: ECS type indexes and who reads them,
/// virtual tables recognised in the game). Read-only; a missing table answers "nothing known".
/// </summary>
public sealed partial class CodeIndex : ICodeIndex, IDisposable
{
    /// <summary>The build the copies run; its addresses are the ones a runner can act on.</summary>
    public const string Windows = "bg3_dx11.exe";

    /// <summary>The build that carries its functions' names.</summary>
    public const string Macos = "bg3_macos_x64";

    readonly SqliteConnection decomp;
    readonly SqliteConnection? symbols;
    readonly Dictionary<string, long> binaryIds = [];
    readonly long? gameDll;
    readonly Dictionary<(string, long), string?> names = [];
    readonly Dictionary<(string, long), List<long>> callers = [];
    Dictionary<string, List<(long Rva, string Name)>>? macAdds;
    Dictionary<(long, long), string>? updates;

    readonly GateKit.Symbols.Queries.OperationGuards guards;

    /// <param name="coverage">The coverage record, whose layouts name the fields a condition reads; null for none.</param>
    public CodeIndex(string decompDb, string? symbolsDb, string? coverage = null)
    {
        try { decomp = Sql.OpenReadOnly(decompDb); }
        catch (GateKit.Home.MissingDatabaseException e) { throw new LeadsException("the leads read the pseudo-code database: " + e.Message); }
        guards = new GateKit.Symbols.Queries.OperationGuards(decomp, coverage);
        foreach (var r in decomp.Query("SELECT id, name FROM binaries"))
            binaryIds[(string)r[1]!] = (long)r[0]!;
        if (symbolsDb is not null && GateKit.Home.HomeDatabase.Usable(symbolsDb))
        {
            symbols = Sql.OpenReadOnly(symbolsDb);
            gameDll = symbols.Query("SELECT id FROM dlls WHERE name = ?", Windows).Select(r => (long?)(long)r[0]!).FirstOrDefault();
        }
        else if (symbolsDb is not null)
            SymbolsNote = GateKit.Home.HomeDatabase.LeftOut(symbolsDb, "names from the symbol database");
    }

    /// <summary>Why the symbol database was not read, when it was not: the leads are then without its names.</summary>
    public string? SymbolsNote { get; }

    public void Dispose()
    {
        decomp.Dispose();
        symbols?.Dispose();
    }

    long? Id(string binary) => binaryIds.TryGetValue(binary, out var id) ? id : null;

    static string Address(string binary, long rva) => $"{binary}+0x{rva:X}";

    [GeneratedRegex(@"(AddComponent\w*|AccessAddStorage|AddImmediate\w*)<(?<t>[\w:]+?)(_const)?[,>]", RegexOptions.CultureInvariant)]
    private static partial Regex MacAdd();

    public IReadOnlyList<Creator> Adders(string type)
    {
        var list = new List<Creator>();
        try
        {
            if (Id(Windows) is { } win)
                foreach (var r in decomp.Query("SELECT DISTINCT c.caller, h.confidence FROM ecs_helpers h JOIN calls c INDEXED BY calls_callee ON c.binary_id = ? AND c.callee = h.rva"
                                               + " WHERE h.dll = ? AND h.family = 'add' AND h.type = ? ORDER BY c.caller", win, Windows, type).ToList())
                    list.Add(new Creator(null, Address(Windows, (long)r[0]!), Windows, null, null, (string?)r[1] ?? "unknown", NameOf(Windows, (long)r[0]!)));
        }
        catch (SqliteException) { }   // no helpers table: nothing known from Windows
        if (Id(Macos) is { } mac)
        {
            macAdds ??= LoadMacAdds(mac);
            foreach (var (rva, _) in macAdds.GetValueOrDefault(type) ?? [])
                foreach (var caller in Callers(Macos, rva))
                {
                    var name = NameOf(Macos, caller);
                    if (name is not null && (MacAdd().IsMatch(name) || name.StartsWith("ecs::ComponentOps<", StringComparison.Ordinal)))
                        continue;   // the engine's templates passing it on, not a reason
                    list.Add(new Creator(null, Address(Macos, caller), Macos, null, null, "name", name));
                }
        }
        return [.. list.DistinctBy(c => c.Function)];
    }

    Dictionary<string, List<(long, string)>> LoadMacAdds(long mac)
    {
        var map = new Dictionary<string, List<(long, string)>>(StringComparer.Ordinal);
        foreach (var r in decomp.Query("SELECT rva, name FROM functions WHERE binary_id = ? AND (name LIKE '%AddComponent%<%' OR name LIKE '%AccessAddStorage<%')", mac).ToList())
        {
            var name = (string)r[1]!;
            if (name.StartsWith("ecs::ComponentOps<", StringComparison.Ordinal) || name.StartsWith("ecs::QueryRegistry", StringComparison.Ordinal)) continue;
            var m = MacAdd().Match(name);
            if (!m.Success) continue;
            var type = m.Groups["t"].Value;
            if (!map.TryGetValue(type, out var l)) map[type] = l = [];
            l.Add(((long)r[0]!, name));
        }
        return map;
    }

    public IReadOnlyList<long> Callers(string binary, long rva)
    {
        if (callers.TryGetValue((binary, rva), out var known)) return known;
        var list = Id(binary) is { } id
            ? decomp.Query("SELECT DISTINCT caller FROM calls INDEXED BY calls_callee WHERE binary_id = ? AND callee = ?", id, rva).Select(r => (long)r[0]!).ToList()
            : [];
        callers[(binary, rva)] = list;
        return list;
    }

    public string? NameOf(string binary, long rva)
    {
        if (names.TryGetValue((binary, rva), out var known)) return known;
        string? name = null;
        if (Id(binary) is { } id)
        {
            name = decomp.Query("SELECT name FROM functions WHERE binary_id = ? AND rva = ?", id, rva).Select(r => (string?)r[0]).FirstOrDefault();
            if (name is null || name.StartsWith("FUN_", StringComparison.Ordinal))
            {
                // The Toolkit's own build of the same function carries its name.
                try
                {
                    var matched = decomp.Query("SELECT f.name FROM code_matches m JOIN functions f ON f.binary_id = m.toolkit_binary AND f.rva = m.toolkit_rva"
                                               + " WHERE m.game_binary = ? AND m.game_rva = ? AND f.name NOT LIKE 'FUN_%' ORDER BY m.score DESC LIMIT 1", id, rva)
                        .Select(r => (string?)r[0]).FirstOrDefault();
                    if (matched is not null) name = matched;
                }
                catch (SqliteException) { }
            }
            if ((name is null || name.StartsWith("FUN_", StringComparison.Ordinal)) && binary == Windows && symbols is not null && gameDll is { } dll)
            {
                // A virtual function: the class whose table holds it, where the Toolkit's table is recognised in the game.
                var slot = symbols.Query("SELECT s.namespace, s.class, gs.idx FROM vtab_slots gs JOIN vtabs g ON g.id = gs.vtab_id AND g.dll_id = ?"
                                         + " JOIN vtab_matches m ON m.game_vtab = g.id JOIN vtabs t ON t.id = m.toolkit_vtab JOIN symbols s ON s.id = t.symbol_id"
                                         + " WHERE gs.rva = ? ORDER BY m.support DESC LIMIT 1", dll, rva).FirstOrDefault();
                if (slot is not null)
                    name = $"{(slot[0] is string ns && ns.Length > 0 ? ns + "::" : "")}{slot[1]}::slot{slot[2]}";
            }
        }
        names[(binary, rva)] = name;
        return name;
    }

    public string? UpdateOf(string binary, long rva)
    {
        if (updates is null)
        {
            updates = [];
            try
            {
                foreach (var r in decomp.Query("SELECT binary_id, updater, thunk, system FROM system_updates").ToList())
                {
                    if (r[1] is long u) updates[((long)r[0]!, u)] = (string)r[3]!;
                    if (r[2] is long t) updates.TryAdd(((long)r[0]!, t), (string)r[3]!);
                }
            }
            catch (SqliteException) { }
        }
        return Id(binary) is { } id && updates.TryGetValue((id, rva), out var system) ? system : null;
    }

    public IReadOnlyList<long> SystemIndexUsers(string binary, string system)
    {
        if (binary != Windows || symbols is null || gameDll is not { } dll) return [];
        return [.. symbols.Query("SELECT DISTINCT x.func_rva FROM ecs_ids e JOIN xrefs x ON x.dll_id = e.dll_id AND x.target_rva = e.index_rva"
                                 + " WHERE e.dll_id = ? AND e.type = ? AND e.context LIKE '%SystemsContext' ORDER BY x.func_rva", dll, system)
            .Select(r => (long)r[0]!)];
    }

    public string? CreationCondition(string binary, string system, long function, string type, int occurrence)
    {
        if (function < 0) return null;
        try
        {
            var (outer, inner) = guards.Of(binary, system, function, "add", type, occurrence);
            return GateKit.Symbols.Queries.OperationGuards.Condition(outer, inner);
        }
        catch (SqliteException)
        {
            return null;   // a database without the tables: nothing known
        }
    }

    public int IndexUsers(string type)
    {
        if (symbols is null || gameDll is not { } dll) return -1;
        return Convert.ToInt32(symbols.Query("SELECT COUNT(DISTINCT x.func_rva) FROM ecs_ids e JOIN xrefs x ON x.dll_id = e.dll_id AND x.target_rva = e.index_rva"
                                             + " WHERE e.dll_id = ? AND e.type = ? AND e.context IN ('ecs::ComponentTypeIdContext', 'ecs::OneFrameComponentTypeIdContext')", dll, type)
            .First()[0], System.Globalization.CultureInfo.InvariantCulture);
    }
}
