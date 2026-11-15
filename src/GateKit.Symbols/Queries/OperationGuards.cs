using GateKit.Symbols.Decomp;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Queries;

/// <summary>
/// The guards of calls in the pseudo-code database (decomp.sqlite): under which condition a
/// function calls a helper or another function, read with <see cref="PseudoGuard"/>. The names
/// it knows are the binary's classified helpers (`ecs_helpers`, by the name their functions carry
/// in the pseudo-code and by their canonical name) and the field names of the coverage record.
/// Read-only; it holds no pseudo-code beyond the functions asked about.
/// </summary>
public sealed class OperationGuards
{
    readonly SqliteConnection db;
    readonly string? coverage;
    readonly Dictionary<string, long?> ids = new(StringComparer.Ordinal);
    readonly Dictionary<(string, long), string?> code = [];
    readonly Dictionary<(string, long), string> masks = [];
    readonly Dictionary<(string, long), string> names = [];
    readonly Dictionary<string, GuardNames> known = new(StringComparer.Ordinal);
    Dictionary<string, Dictionary<long, string>>? fields;

    /// <param name="decomp">An open connection to decomp.sqlite.</param>
    /// <param name="coverage">The coverage record (its layouts give field names), or null for none.</param>
    public OperationGuards(SqliteConnection decomp, string? coverage)
    {
        db = decomp;
        this.coverage = coverage;
    }

    long? Id(string binary)
    {
        if (!ids.TryGetValue(binary, out var id))
            ids[binary] = id = PyText.Long(db.Scalar("SELECT id FROM binaries WHERE name = ?", binary));
        return id;
    }

    /// <summary>The pseudo-code of a function, or null.</summary>
    public string? Code(string binary, long rva)
    {
        if (code.TryGetValue((binary, rva), out var text)) return text;
        text = Id(binary) is long id
            ? DecompStore.Decompress(db.Scalar("SELECT c.text FROM functions f JOIN code c ON c.function_id = f.id WHERE f.binary_id = ? AND f.rva = ?", id, rva))
            : null;
        if (code.Count > 400) { code.Clear(); masks.Clear(); }
        return code[(binary, rva)] = text;
    }

    /// <summary>The function's name in the pseudo-code ("" when it has none).</summary>
    public string NameOf(string binary, long rva)
    {
        if (!names.TryGetValue((binary, rva), out var name))
            names[(binary, rva)] = name = Id(binary) is long id ? Py.Str(db.Scalar("SELECT name FROM functions WHERE binary_id = ? AND rva = ?", id, rva)) : "";
        return name;
    }

    /// <summary>What the guard reader knows of the binary.</summary>
    public GuardNames Names(string binary)
    {
        if (known.TryGetValue(binary, out var n)) return n;
        n = new GuardNames();
        try
        {
            if (Id(binary) is long id)
                foreach (var r in db.Query("SELECT h.family, h.type, f.name FROM ecs_helpers h LEFT JOIN functions f ON f.binary_id = ? AND f.rva = h.rva WHERE h.dll = ?", id, binary))
                {
                    var family = Py.Str(r[0]);
                    var type = Py.Str(r[1]);
                    n.Helpers.TryAdd(HelpersCommand.Name(family, type), (family, type));
                    if (r[2] is string shown && shown.Length > 0) n.Helpers.TryAdd(shown, (family, type));
                }
        }
        catch (SqliteException)
        {
            // no helpers table: nothing named
        }
        fields ??= LoadFields();
        foreach (var (type, byOffset) in fields) n.AddType(type, byOffset);
        return known[binary] = n;
    }

    Dictionary<string, Dictionary<long, string>> LoadFields()
    {
        var result = new Dictionary<string, Dictionary<long, string>>(StringComparer.Ordinal);
        if (coverage == null) return result;
        foreach (var t in RecordTypes.Read(coverage).Values)
            result[t.Name] = t.Fields.GroupBy(f => f.Offset).ToDictionary(g => g.Key, g => g.First().Name);
        return result;
    }

    /// <summary>The guard of the <paramref name="occurrence"/>-th call (text order) of
    /// <paramref name="callee"/> in <paramref name="caller"/>.</summary>
    public GuardResult OfCall(string binary, long caller, long callee, int occurrence, string? alias = null)
    {
        var text = Code(binary, caller);
        if (text == null) return GuardResult.NotExtracted("no pseudo-code of the caller");
        var g = Names(binary);
        var name = NameOf(binary, callee);
        if (!masks.TryGetValue((binary, caller), out var masked)) masks[(binary, caller)] = masked = PseudoGuard.Mask(text);
        var result = name.Length > 0 ? PseudoGuard.Find(text, masked, name, occurrence, g) : GuardResult.NotExtracted("the callee has no name");
        if (result.Status == "not_extracted" && alias != null && alias != name)
        {
            var again = PseudoGuard.Find(text, masked, alias, occurrence, g);
            if (again.Status != "not_extracted") result = again;
        }
        return result;
    }

    /// <summary>How many times the caller's pseudo-code shows a call of the callee.</summary>
    public int CallsShown(string binary, long caller, long callee)
    {
        var text = Code(binary, caller);
        var name = NameOf(binary, callee);
        return text == null || name.Length == 0 ? 0 : PseudoGuard.Calls(PseudoGuard.Mask(text), name).Count;
    }

    /// <summary>
    /// The condition under which a system's update performs an operation: the guard of the call of
    /// <paramref name="function"/> in the update when the operation is at depth 2, and the guard of
    /// the helper's call in <paramref name="function"/>. <paramref name="occurrence"/> is the
    /// operation's place among the calls of the same helper in that function (by address).
    /// </summary>
    public (GuardResult? Outer, GuardResult Inner) Of(string binary, string system, long function, string kind, string type, int occurrence)
    {
        long? id = Id(binary);
        if (id == null) return (null, GuardResult.NotExtracted($"{binary} is not in the pseudo-code database"));
        var helpers = new List<long>();
        try
        {
            helpers = db.Query("SELECT DISTINCT h.rva FROM ecs_helpers h JOIN calls c INDEXED BY calls_caller ON c.binary_id = ? AND c.caller = ? AND c.callee = h.rva"
                               + " WHERE h.dll = ? AND h.family = ? AND h.type = ? ORDER BY h.rva", id, function, binary, kind, type)
                .Select(r => PyText.L(r[0])).ToList();
        }
        catch (SqliteException)
        {
            // no helpers table
        }
        GuardResult inner = GuardResult.NotExtracted("no helper of this operation is called by the function");
        foreach (var h in helpers)
        {
            inner = OfCall(binary, function, h, occurrence, HelpersCommand.Name(kind, type));
            if (inner.Status != "not_extracted") break;
        }
        GuardResult? outer = null;
        var update = PyText.Long(db.Scalar("SELECT updater FROM system_updates WHERE binary_id = ? AND system = ? AND updater IS NOT NULL ORDER BY updater LIMIT 1", id, system));
        if (update is long u && u != function)
            outer = OfCall(binary, u, function, 0);
        return (outer, inner);
    }

    /// <summary>The conditions of both levels as one sentence for a lead: "A and B", or null when
    /// none was extracted.</summary>
    public static string? Condition(GuardResult? outer, GuardResult inner)
    {
        // only the terms that name something (a component, a field, a named call's answer, a loop over a list):
        // a comparison of the decompiler's locals tells a runner nothing and is a piece of the pseudo-code
        var all = (outer?.Terms ?? []).Concat(inner.Terms).Distinct(StringComparer.Ordinal).ToList();
        var terms = all.Where(t => !PseudoGuard.HasUnnamed(t) && (PseudoGuard.Names(t) || t == "for each item of a list")).ToList();
        if (!terms.Any(PseudoGuard.Names)) return null;
        bool partial = (outer?.Partial ?? false) || inner.Partial;
        int rest = all.Count - terms.Count;
        return string.Join(" and ", terms) + (rest > 0 ? $" (and {rest} more over the decompiler's locals)" : "") + (partial ? " (one path; also reached by a jump)" : "");
    }
}
