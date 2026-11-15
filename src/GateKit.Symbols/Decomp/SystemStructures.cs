using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Decomp;

/// <summary>A function whose first parameter is a system: its update entry, its update, its constructor.</summary>
internal sealed record SystemFunction(long Rva, string System, string Role, string Basis);

/// <summary>
/// A structure per ECS system for its own fields, and the functions that take the system as
/// their first parameter. A system keeps its queues, views and caches in itself; the pseudo-code
/// reads them as `param_1 + 0x38` because nothing tells the decompiler what `param_1` is. Three
/// functions are known to receive the system first, from `system_updates` and `system_factories`:
/// the update entry the factory stores (the engine calls it with the system, the world and the
/// time), the update that entry calls when the entry hands its own first parameter on, and the
/// constructor when it stores something at the start of its first parameter (the virtual table).
/// The structure is as large as the furthest offset those functions reach and has no field of its
/// own: the decompiler then writes `this->field_0x38`, and a guard reads `System.field_0x38`.
/// A function two systems share (a base class's) is left alone.
/// </summary>
internal static partial class SystemStructures
{
    [GeneratedRegex(@"\(\s*(?<type>[A-Za-z_][\w ]*?)\s*(?<stars>\**)\s*param_1\s*[,)]")]
    private static partial Regex FirstParameter();

    [GeneratedRegex(@"\(\s*(?:\(\w+\)\s*)?param_1 \+ (0x[0-9a-fA-F]+|\d+)\)|\bparam_1\[(0x[0-9a-fA-F]+|\d+)\]")]
    private static partial Regex Reach();

    [GeneratedRegex(@"(?m)^\s*\*\s*(?:\([^()]*\)\s*)?param_1 = ")]
    private static partial Regex StoresAtStart();

    static long L(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    static long Number(string text) => text.StartsWith("0x", StringComparison.Ordinal)
        ? long.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : long.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>How many bytes one step of pointer arithmetic on the first parameter is: 1 for an
    /// integer, the pointee's size for a pointer.</summary>
    internal static int Step(string code)
    {
        int brace = code.IndexOf('{');
        var m = FirstParameter().Match(brace > 0 ? code[..brace] : code);
        if (!m.Success || m.Groups["stars"].Length == 0)
            return 1;
        if (m.Groups["stars"].Length > 1)
            return 8;
        string type = m.Groups["type"].Value.Split(' ')[^1];
        return type switch
        {
            "longlong" or "ulonglong" or "undefined8" or "double" or "code" => 8,
            "int" or "uint" or "undefined4" or "float" => 4,
            "short" or "ushort" or "undefined2" => 2,
            "char" or "uchar" or "byte" or "undefined1" or "undefined" or "bool" or "void" => 1,
            _ => 0,                                       // a structure already: nothing to learn from its arithmetic
        };
    }

    /// <summary>The furthest byte offset from its first parameter that a function's pseudo-code reaches, or -1.</summary>
    internal static long Furthest(string code)
    {
        int step = Step(code);
        if (step == 0)
            return -1;
        int brace = code.IndexOf('{');
        long furthest = -1;
        foreach (Match m in Reach().Matches(brace > 0 ? code[brace..] : code))
        {
            bool index = m.Groups[2].Success;
            // a cast before the parameter makes the arithmetic bytewise
            bool cast = !index && m.Value.Contains(')', StringComparison.Ordinal) && m.Value.IndexOf(')') < m.Value.IndexOf("param_1", StringComparison.Ordinal);
            long offset = Number(index ? m.Groups[2].Value : m.Groups[1].Value) * (cast ? 1 : step);
            if (offset is >= 0 and < 0x8000)
                furthest = Math.Max(furthest, offset);
        }
        return furthest;
    }

    /// <summary>The systems' structures (name -> size) and the functions that take each as their
    /// first parameter, with how many candidates were left out and why.</summary>
    public static (SortedDictionary<string, long> Sizes, List<SystemFunction> Functions, SortedDictionary<string, int> Skipped) Build(SqliteConnection db, string binary)
    {
        var sizes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var functions = new List<SystemFunction>();
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Skip(string why) => skipped[why] = skipped.GetValueOrDefault(why) + 1;
        if (db.Scalar("SELECT id FROM binaries WHERE name = ?", binary) is not { } id)
            return (sizes, functions, skipped);
        var candidates = new List<(long Rva, string System, string Role, long? Entry)>();
        try
        {
            foreach (var r in db.Query("SELECT system, thunk, updater FROM system_updates WHERE binary_id = ?", id))
            {
                if (r[1] != null)
                    candidates.Add((L(r[1]), Py.Str(r[0]), "update entry", null));
                if (r[2] != null && (r[1] == null || L(r[2]) != L(r[1])))
                    candidates.Add((L(r[2]), Py.Str(r[0]), "update", r[1] == null ? null : L(r[1])));
            }
            foreach (var r in db.Query("SELECT system, constructor FROM system_factories WHERE binary_id = ? AND constructor IS NOT NULL", id))
                candidates.Add((L(r[1]), Py.Str(r[0]), "constructor", null));
        }
        catch (SqliteException)
        {
            return (sizes, functions, skipped);
        }
        var owners = candidates.GroupBy(c => c.Rva).ToDictionary(g => g.Key, g => g.Select(c => c.System).Distinct(StringComparer.Ordinal).Count());
        var text = new Dictionary<long, (string Name, string Code)>();
        (string Name, string Code)? Function(long rva)
        {
            if (!text.TryGetValue(rva, out var f))
            {
                var row = db.One("SELECT f.name, c.text FROM functions f LEFT JOIN code c ON c.function_id = f.id WHERE f.binary_id = ? AND f.rva = ?", id, rva);
                if (row == null || row[1] is not byte[] blob)
                    return null;
                text[rva] = f = (Py.Str(row[0]), DecompStore.Decompress(blob));
            }
            return f;
        }
        var seen = new HashSet<(long, string)>();
        foreach (var c in candidates.OrderBy(c => c.Rva).ThenBy(c => c.Role, StringComparer.Ordinal))
        {
            if (!seen.Add((c.Rva, c.System)))
                continue;
            if (owners[c.Rva] > 1) { Skip("shared by two systems"); continue; }
            if (Function(c.Rva) is not var (name, code)) { Skip("no pseudo-code"); continue; }
            if (!FirstParameter().IsMatch(code[..Math.Max(0, code.IndexOf('{'))])) { Skip("no first parameter"); continue; }
            string basis;
            if (c.Role == "update entry")
                basis = "the update entry the factory stores: the engine calls it with the system first";
            else if (c.Role == "update")
            {
                // only when the entry hands its own first parameter on as the first argument
                string needle = PairCommand.Short(name);
                if (c.Entry is not long entry || Function(entry) is not var (_, entryCode)
                    || !Regex.IsMatch(entryCode, @"(?<![\w:])" + Regex.Escape(needle) + @"\s*\(\s*param_1\s*[,)]", RegexOptions.CultureInvariant))
                { Skip("the entry does not hand its first parameter to the update"); continue; }
                basis = $"the update entry +0x{c.Entry:X} hands it its first parameter";
            }
            else
            {
                if (!StoresAtStart().IsMatch(code)) { Skip("the constructor stores nothing at the start of its first parameter"); continue; }
                basis = "the constructor stores the virtual table at the start of its first parameter";
            }
            functions.Add(new SystemFunction(c.Rva, c.System, c.Role, basis));
            long reach = Furthest(code);
            sizes[c.System] = Math.Max(sizes.GetValueOrDefault(c.System, 8), (reach + 8 + 7) / 8 * 8);
        }
        return (sizes, functions, skipped);
    }

    /// <summary>The lines of the types file for the systems' structures (one line each: a structure of its size with nothing in it).</summary>
    public static string TypeLines(SortedDictionary<string, long> sizes, ISet<string> taken)
    {
        var b = new StringBuilder();
        foreach (var (system, size) in sizes)
            if (!taken.Contains(system) && !system.Contains('\t', StringComparison.Ordinal))
                b.Append(new RecordType(system, size, [new RecordField(0, 0, "bytes", RecordTypes.SizeOnly, "system")]).Lines());
        return b.ToString();
    }

    /// <summary>The functions for the pass, one line each: rva (hex), role, system, the structure, basis.</summary>
    public static string FunctionLines(IEnumerable<SystemFunction> functions) =>
        string.Concat(functions.OrderBy(f => f.Rva).Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Rva:X}\t{f.Role}\t{f.System}\t{f.System}\t{f.Basis}\n")));
}
