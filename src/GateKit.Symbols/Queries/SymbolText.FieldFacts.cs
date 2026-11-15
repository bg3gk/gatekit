using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Queries;

/// <summary>One thing the symbol database already knows about a member of an engine class.</summary>
/// <param name="Rung">Where the name comes from: <c>game</c> (the game's own member name: a fact),
/// <c>toolkit</c> (a Toolkit accessor: a fact), or <c>extender</c> (what Script Extender's author's
/// headers call it: somebody's earlier reading, the rung of that name - nobody has checked it
/// against the game's code, so it never settles a name by itself).</param>
/// <param name="Name">The name, or the candidates joined with " | " when the game's names of one
/// type could not be told apart.</param>
/// <param name="Offset">Offset inside the class, when the source says it.</param>
/// <param name="Type">The member's type as the source spells it.</param>
/// <param name="Evidence">Where exactly it was read, in words that can go into a layout's record.</param>
/// <param name="Certain">True when this alone settles the name of the field at <paramref name="Offset"/>:
/// a fact, one name, the offset read in the game's own build, and the whole field rather than one
/// bit of it. Only such a fact may be written down without a reader.</param>
public sealed record FieldFact(string Rung, string Name, long? Offset, string? Type, string Evidence, bool Certain = false);

internal static partial class SymbolText
{
    /// <summary>What is already known about the members of an engine class, as data: the game's own
    /// names (ls::FieldMeta strings of the executable carry type and offset, the macOS build's
    /// fieldmeta.json carries the names), the Toolkit's accessors, and Script Extender's layout.
    /// The same reading <see cref="Replication"/> prints, so the two cannot drift apart.</summary>
    public static List<FieldFact> FieldFacts(SqliteConnection db, SymbolsEnvironment env, string name, string? dll)
    {
        var facts = new List<FieldFact>();
        var game = dll != null ? GameRow(db, dll) : GameRow(db, "bg3_dx11.exe") ?? GameRow(db, "bg3.exe");

        // the game's own: type and offset from the executable, names from the macOS build
        var windows = new SortedDictionary<long, string>();
        if (game != null)
            foreach (var r in db.Query("SELECT DISTINCT text FROM strings WHERE dll_id = ? AND text LIKE '%FieldMeta<%' AND text LIKE ?",
                                       game.Value.Id, "%" + name + "::*%").ToList())
                foreach (Match m in FieldMeta().Matches(PyText.Str(r[0])))
                    if (m.Groups[2].Value == name)
                        windows[long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)] = Decomp.Py.Strip(Keyword3().Replace(m.Groups[1].Value, ""));
        string? macFile = env.MacosSymbols == null ? null : Path.Combine(env.MacosSymbols, "fieldmeta.json");
        var mac = MacFields(macFile, name);
        var byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (field, kind) in mac)
        {
            string key = PlainType(kind);
            if (!byType.TryGetValue(key, out var list))
                byType[key] = list = [];
            list.Add(field);
        }
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (offset, type) in windows)
        {
            var candidates = byType.GetValueOrDefault(PlainType(type)) ?? [];
            string where = $"ls::FieldMeta<{type} {name}::*, {offset}> in {game!.Value.Name}";
            if (candidates.Count == 1)
            {
                placed.Add(candidates[0]);
                facts.Add(new FieldFact("game", candidates[0], offset, type,
                    $"{where}; the macOS build names the one member of that type {candidates[0]} (fieldmeta.json)", Certain: true));
            }
            else if (candidates.Count > 1)
                facts.Add(new FieldFact("game", string.Join(" | ", candidates), offset, type,
                    $"{where}; the macOS build has {candidates.Count} members of that type, so the name is one of them and the offset alone does not say which"));
            else
                facts.Add(new FieldFact("game", "?", offset, type, $"{where}; no name for it in the macOS references"));
        }
        foreach (var (field, kind) in mac)
            if (!placed.Contains(field) && !(byType[PlainType(kind)].Count > 1 && windows.Values.Any(t => PlainType(t) == PlainType(kind))))
                facts.Add(new FieldFact("game", field, null, kind, "member name in the macOS build (fieldmeta.json); no offset known from there"));

        // the Toolkit's accessors
        foreach (var r in db.Query("SELECT field, kind, offset, game_offset, width, type, bit FROM accessors WHERE class = ? ORDER BY coalesce(game_offset, offset), field", name))
        {
            long? toolkitOffset = PyText.Long(r[2]), gameOffset = PyText.Long(r[3]);
            string bit = r[6] == null ? "" : $", bit {PyText.L(r[6])}";
            string evidence = gameOffset != null
                ? $"Toolkit accessor {name}::{PyText.Str(r[0])} ({PyText.Str(r[1])}) reads +0x{PyText.Hex(gameOffset.Value)} in the matched game function{bit}"
                : $"Toolkit accessor {name}::{PyText.Str(r[0])} ({PyText.Str(r[1])}) reads +0x{PyText.Hex(toolkitOffset ?? 0)} in the Toolkit build{bit}; no matched game function, so the game's offset may differ";
            facts.Add(new FieldFact("toolkit", PyText.Str(r[0]), gameOffset ?? toolkitOffset, r[5] as string, evidence,
                Certain: gameOffset != null && r[6] == null));
        }

        // what Script Extender's headers call it - its author's headers, not the one generated from
        // this very record, which would only hand our own names back as if somebody else had read them
        foreach (var r in db.Query("SELECT f.name, f.type, f.legacy, f.offset, f.checked, t.file FROM se_fields f JOIN se_types t ON t.id = f.type_id"
                                   + " WHERE t.engine_class = ? ORDER BY f.idx", name))
        {
            if (PyText.Str(r[5]).EndsWith("Coverage.h", StringComparison.Ordinal)) continue;
            string was = r[2] is string legacy && legacy.Length > 0 ? $" (was {legacy})" : "";
            string sure = PyText.Long(r[4]) switch { 1 => "offset computed and equal to the one in the field's name", 2 => "offset taken from the field's name", _ => "offset computed from the header" };
            facts.Add(new FieldFact("extender", PyText.Str(r[0]), PyText.Long(r[3]), r[1] as string,
                $"Script Extender's header {PyText.Str(r[5])} calls it {PyText.Str(r[0])}{was}; {sure}"));
        }
        return facts;
    }

    static (long Id, string Name)? GameRow(SqliteConnection db, string dll)
    {
        var r = db.One("SELECT id, name FROM dlls WHERE origin = 'game' AND name = ?", dll);
        return r == null ? null : (PyText.L(r[0]), PyText.Str(r[1]));
    }
}
