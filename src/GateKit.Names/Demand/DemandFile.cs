using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Symbols;
using Microsoft.Data.Sqlite;

namespace GateKit.Names.Demand;

/// <summary>
/// A demand file (<c>research/coverage/demand.jsonl</c> by default): what a set of mods uses of the
/// engine, counted by mods, as <c>gatekit mod demand</c> writes it from the documents of
/// <c>gatekit mod usage</c>. Facts only - type, field and function names, counts, and the mods'
/// names as the source. One JSON object per line, a <c>kind</c> on each:
/// <list type="bullet">
/// <item><c>source</c> - the first line: the schema, the scanner's version, the mods it was built from;</item>
/// <item><c>component</c> - an engine type: <c>mods</c> using it, how (<c>uses</c>: read, write,
///   create, subscribe, each a count of mods), the fields they name (<c>{name, mods}</c>);</item>
/// <item><c>osiris</c> - an Osiris function or event: mods calling it (Lua or story goals), mods
///   listening to it, the arities seen, and what the game's data index holds for the name;</item>
/// <item><c>se_api</c>, <c>se_event</c>, <c>functor</c>, <c>boost</c> - an <c>Ext.*</c> function,
///   an <c>Ext.Events</c> event, a stats functor, a stats boost, counted by mods.</item>
/// </list>
/// This class reads and writes the file; building one is <c>gatekit mod demand</c>'s.
/// </summary>
public static class DemandFile
{
    public const string FileName = "demand.jsonl";

    static readonly JsonSerializerOptions Line = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string PathIn(string coverageRoot) => Path.Combine(coverageRoot, FileName);

    /// <summary>The game data index's Osiris functions: every overload of a name (kind, arity, out
    /// parameters), fewest parameters first; null without an index.</summary>
    public static Dictionary<string, List<(string Kind, int Arity, int Out)>>? OsirisIndex(string? path)
    {
        if (path is null || !GateKit.Home.HomeDatabase.Usable(path)) return null;
        using var db = Sql.OpenReadOnly(path);
        var functions = new Dictionary<string, List<(string, int, int)>>(StringComparer.Ordinal);
        try
        {
            foreach (var row in db.Query("SELECT name, kind, arity, params FROM osiris_functions ORDER BY name, arity"))
                if (row[0] is string name)
                {
                    int outs = ((string?)row[3] ?? "").Split("[out]").Length - 1;
                    if (!functions.TryGetValue(name, out var overloads)) functions[name] = overloads = [];
                    overloads.Add(((string?)row[1] ?? "", (int)(Sql.Long(row[2]) ?? -1), outs));
                }
        }
        catch (SqliteException) { return null; }
        return functions;
    }

    public static string Serialize(IEnumerable<JsonObject> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows) text.Append(row.ToJsonString(Line)).Append('\n');
        return text.ToString();
    }

    public static void Write(string path, IEnumerable<JsonObject> rows) =>
        File.WriteAllText(path, Serialize(rows), new UTF8Encoding(false));

    public static List<JsonObject> Read(string path)
    {
        if (!File.Exists(path)) throw new NamesException($"{path} does not exist: build it with `gatekit mod demand <mods or usage files> --out {path}`");
        var rows = new List<JsonObject>();
        int number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (line.Trim().Length == 0) continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject row) rows.Add(row);
            }
            catch (JsonException e) { throw new NamesException($"{path}:{number} is not JSON: {e.Message}"); }
        }
        return rows;
    }
}
