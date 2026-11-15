using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Symbols.Queries;

/// <summary>
/// The "symbols" layer of a system's description (research/coverage/systems.md): what <c>gatekit symbols system</c> knows, written into
/// <c>&lt;out&gt;/&lt;namespace with :: as __&gt;/&lt;Name&gt;.json</c> - the components its methods take
/// as read-only and as writable views, and its methods with the address each was found at in the
/// game executable asked for (bg3_dx11.exe by default: the binary a copy starts).
///
/// Only the <c>symbols</c> key is written, and in it only <c>reads</c>, <c>writes</c> and
/// <c>methods</c>. A file that exists keeps every other key as it is -
/// <c>does</c>, <c>unknown</c>, <c>sure</c> are written by people and agents by hand - and a new file
/// gets <c>system</c>, <c>namespace</c> and <c>symbols</c> and nothing else.
/// </summary>
internal static class SystemFiles
{
    static readonly JsonSerializerOptions Stored = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The folder of a namespace: <c>esv::spell_cast::random</c> -> <c>esv__spell_cast__random</c>.</summary>
    public static string Folder(string ns) => Safe(ns.Replace("::", "__", StringComparison.Ordinal));

    /// <summary>A file name Windows accepts, whatever a template argument puts in a class name.</summary>
    public static string Safe(string text)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*', ',', ' ']).ToHashSet();
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(bad.Contains(ch) ? '_' : ch);
        return sb.ToString().TrimEnd('.', ' ');
    }

    /// <summary>The systems to describe: the `system` column of <paramref name="survey"/> when it
    /// exists, otherwise every system type the database knows.</summary>
    public static List<string> List(SymbolContext c, string? survey)
    {
        if (survey is not null && File.Exists(survey))
        {
            var lines = File.ReadAllLines(survey);
            if (lines.Length == 0) return [];
            var column = Array.IndexOf(lines[0].Split(','), "system");
            if (column < 0) throw new SymbolsException($"{survey} has no `system` column");
            return [.. lines.Skip(1).Select(l => l.Split(','))
                .Where(cells => cells.Length > column && cells[column].Length > 0)
                .Select(cells => cells[column].Trim()).Distinct(StringComparer.Ordinal)];
        }
        return [.. c.Db.Query("SELECT DISTINCT type FROM type_names WHERE kind = 'system' ORDER BY type")
            .Select(r => Decomp.Py.Strip(System.Text.RegularExpressions.Regex.Replace(PyText.Str(r[0]), "^(class|struct) ", "")))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The symbols layer of one system.</summary>
    public static JsonObject Layer(SymbolContext c, string sys, (long Id, string Name) game)
    {
        int cut = sys.LastIndexOf("::", StringComparison.Ordinal);
        string ns = cut >= 0 ? sys[..cut] : "", cls = cut >= 0 ? sys[(cut + 2)..] : sys;
        var methods = c.Db.Query("SELECT s.dll_id, s.rva, s.signature, s.method, s.kind FROM symbols s"
                                 + " WHERE s.class = ? AND s.namespace = ? ORDER BY s.method, s.rva", cls, ns).ToList();
        var access = new Dictionary<string, string>(StringComparer.Ordinal);
        var indexed = new SortedDictionary<string, string>(StringComparer.Ordinal);   // type -> the matched function that reads its index
        var list = new JsonArray();
        foreach (var m in methods)
        {
            foreach (var (component, how) in SymbolText.ComponentsOf(PyText.Str(m[2])))
                if (access.GetValueOrDefault(component) != "write") access[component] = how;
            if (PyText.Str(m[4]) == "vftable") continue;
            var hit = m[1] != null
                ? c.Db.One("SELECT game_rva FROM matches WHERE toolkit_dll = ? AND toolkit_rva = ? AND game_dll = ?", m[0], m[1], game.Id)
                : null;
            if (hit != null)
            {
                // the components whose type index the matched game function reads: what it adds,
                // removes or gets without a view in its parameters (D21 of the day run)
                foreach (var r in c.Db.Query("SELECT DISTINCT e.type FROM xrefs x JOIN ecs_ids e ON e.dll_id = x.dll_id AND e.index_rva = x.target_rva"
                    + " WHERE x.dll_id = ? AND x.func_rva = ? AND e.context IN ('ecs::ComponentTypeIdContext', 'ecs::UncheckedComponentTypeIdContext')",
                    game.Id, hit[0]).ToList())
                    indexed.TryAdd(PyText.Str(r[0]), $"{game.Name}+0x{PyText.L(hit[0]):X}");
            }
            list.Add(new JsonObject
            {
                ["name"] = PyText.Str(m[3]),
                ["address"] = hit != null ? $"{game.Name}+0x{PyText.L(hit[0]):X}" : null,
                ["binary"] = hit != null ? game.Name : null,
            });
        }
        return new JsonObject
        {
            ["reads"] = new JsonArray([.. access.Where(p => p.Value == "read").Select(p => p.Key).Order(StringComparer.Ordinal).Select(k => (JsonNode)k!)]),
            ["writes"] = new JsonArray([.. access.Where(p => p.Value == "write").Select(p => p.Key).Order(StringComparer.Ordinal).Select(k => (JsonNode)k!)]),
            ["uses"] = Uses(c, sys, game.Name, indexed.Where(p => !access.ContainsKey(p.Key))),
            ["methods"] = list,
            ["limits"] = Limits,
        };
    }

    /// <summary>The component operations of the system (<see cref="ComponentFacts"/>: its update entries
    /// in the binary asked for, else in bg3.exe), then the components whose index a matched method
    /// reads without an operation of the type being known (kind `index`).</summary>
    static JsonArray Uses(SymbolContext c, string sys, string binary, IEnumerable<KeyValuePair<string, string>> indexed)
    {
        var facts = ComponentFacts.Of(c, binary).GetValueOrDefault(sys);
        if ((facts == null || facts.Count == 0) && binary != "bg3.exe")
            facts = ComponentFacts.Of(c, "bg3.exe").GetValueOrDefault(sys);
        var list = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in facts ?? [])
        {
            seen.Add(f.Type);
            list.Add(new JsonObject
            {
                ["type"] = f.Type,
                ["kind"] = f.Kind,
                ["function"] = $"{f.Binary}+0x{f.Function:X}",
                ["site"] = f.Site is long site ? $"{f.Binary}+0x{site:X}" : null,
                ["depth"] = f.Depth,
                ["confidence"] = f.Confidence,
            });
        }
        foreach (var (type, function) in indexed)
            if (!seen.Contains(type))
                list.Add(new JsonObject { ["type"] = type, ["kind"] = "index", ["function"] = function, ["site"] = null, ["depth"] = null, ["confidence"] = null });
        return list;
    }

    /// <summary>What the layer cannot see, said in every file (D21 of the day run: CastSystem's
    /// SystemLoad adds InterruptRequestsComponent and its writes list was empty).</summary>
    public const string Limits = "reads/writes: the component views in the parameters of the system's exported methods. uses: "
        + "the component operations (add, remove, get, has) its per-frame update performs through the engine's out-of-line "
        + "helpers, itself or in a function it calls (depth 2), with the call site; kind index: a matched method reads the "
        + "type's index and the operation is not known. Not listed: operations the compiler inlined, those deeper than depth 2, "
        + "and what a method that is not the update does (e.g. a SystemLoad) - read its pseudo-code; an empty writes list is "
        + "not \"writes nothing\".";

    /// <summary>Writes the layer of each system; returns (written, created, unchanged).</summary>
    public static (int Written, int Created, int Unchanged) Write(SymbolContext c, IEnumerable<string> systems, string? prefix, string output, string? dll)
    {
        var game = c.Game(dll ?? "bg3_dx11.exe")
            ?? throw new SymbolsException($"no game executable {dll ?? "bg3_dx11.exe"} in the symbol database (gatekit symbols build --game ...)");
        int written = 0, created = 0, unchanged = 0;
        foreach (var sys in systems)
        {
            if (prefix is not null && !sys.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int cut = sys.LastIndexOf("::", StringComparison.Ordinal);
            string ns = cut >= 0 ? sys[..cut] : "", cls = cut >= 0 ? sys[(cut + 2)..] : sys;
            var folder = Path.Combine(output, Folder(ns.Length > 0 ? ns : "_"));
            var path = Path.Combine(folder, Safe(cls) + ".json");
            JsonObject file;
            var isNew = !File.Exists(path);
            if (isNew) file = new JsonObject { ["system"] = sys, ["namespace"] = ns };
            else
            {
                try { file = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new JsonException("not an object"); }
                catch (JsonException e) { throw new SymbolsException($"{path} is not a JSON object ({e.Message}); nothing was written to it"); }
            }
            var before = file.ToJsonString(Stored);
            var layer = Layer(c, sys, game);
            if (file["symbols"] is JsonObject had)
            {
                // reads, writes and methods are the tool's; anything else a person put there (a
                // note) stays, in its place
                foreach (var key in new[] { "reads", "writes", "uses", "methods", "limits" })
                {
                    var value = layer[key]!.DeepClone();
                    if (had.ContainsKey(key)) had[key] = value; else had.Add(key, value);
                }
            }
            else if (file.ContainsKey("symbols")) file["symbols"] = layer;   // the key keeps its place
            else file.Add("symbols", layer);
            var text = file.ToJsonString(Stored).ReplaceLineEndings("\n") + "\n";
            if (!isNew && before.ReplaceLineEndings("\n") + "\n" == text) { unchanged++; continue; }
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            written++;
            if (isNew) created++;
        }
        return (written, created, unchanged);
    }
}
