using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.GameData.Query;

/// <summary>Where the three offline databases are. Only the index is required; the others add answers when present.</summary>
public sealed record OfflineDatabases(string Index, string Symbols, string Decomp)
{
    /// <summary>The locations from the environment (BG3_INDEX_DB, BG3_SYMBOLS_DB, BG3_DECOMP_WORK) or the defaults.</summary>
    public static OfflineDatabases FromEnvironment() => new(DataPaths.IndexDatabase, DataPaths.SymbolsDatabase, DataPaths.DecompDatabase);

    public string? PathOf(string name) => name switch { "index" => Index, "symbols" => Symbols, "decomp" => Decomp, _ => null };

    public static readonly string[] Names = ["decomp", "index", "symbols"];
}

/// <summary>
/// Offline lookups for people and for language models: search, about, scene, track, hook, sql and
/// schema over the game index, the code symbol database and the pseudo-code database. Each method
/// returns a JSON-ready value (dictionaries, lists, strings, numbers); the bg3-offline MCP tools and
/// <c>gatekit data query</c> call them. Not thread-safe; open one per caller.
/// </summary>
public sealed partial class OfflineQueries(OfflineDatabases paths) : IDisposable
{
    private readonly Dictionary<string, ReadOnlyDb?> _open = [];

    public OfflineDatabases Paths { get; } = paths;

    [GeneratedRegex("^h[0-9a-f]{8}g[0-9a-f]{4}g[0-9a-f]{4}g[0-9a-f]{4}g[0-9a-f]{12}$")]
    private static partial Regex Handle();

    [GeneratedRegex(@"^([\w.]+?)\+(?:0x)?([0-9a-fA-F]+)$")]
    private static partial Regex Address();

    [GeneratedRegex(@"^(class|struct) ")]
    private static partial Regex ClassPrefix();

    /// <summary>A database by name; a missing required one raises an error that says how to build it.</summary>
    private ReadOnlyDb? Db(string name, bool required = true)
    {
        if (!_open.TryGetValue(name, out var db))
        {
            var path = Paths.PathOf(name) ?? throw new KeyNotFoundException($"'{name}'");
            db = ReadOnlyDb.TryOpen(path);
            if (db == null && required)
            {
                throw new DataQueryException($"{path} does not exist; build it first (see the README of the {name} tool)");
            }
            _open[name] = db;
        }
        if (db == null && required)
        {
            throw new DataQueryException($"{Paths.PathOf(name)} does not exist; build it first (see the README of the {name} tool)");
        }
        return db;
    }

    private bool Has(string name) => Db(name, false) != null;

    private List<Row> Rows(string name, string sql, params object?[] args)
    {
        var db = Db(name);
        return db!.Rows(sql, args);
    }

    private List<Row> RowsIf(string name, string sql, params object?[] args) => Db(name, false)?.Rows(sql, args) ?? [];

    private long? GameId(string name = "bg3.exe") =>
        Rows("symbols", "SELECT id FROM dlls WHERE origin = 'game' AND name = ?", name).FirstOrDefault()?.L("id");

    // ------------------------------------------------------------------------------ search

    /// <summary>Everything whose name contains the text, grouped by kind, with the command that tells more.</summary>
    public List<Row> Search(string text, int limit = 10)
    {
        var pat = Q.Like(text);
        var output = new List<Row>();

        void Add(string kind, List<Row> items, Func<Row, string> tell)
        {
            foreach (var item in items)
            {
                item["kind"] = kind;
                item["more"] = tell(item);
                output.Add(item);
            }
        }

        if (Has("index"))
        {
            Add("timeline", Rows("index", "SELECT name FROM timelines WHERE shadowed = 0 AND name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT ?", pat, limit),
                r => $"gatekit data query scene {r.S("name")}");
            Add("dialog", Rows("index", "SELECT name, uuid FROM dialogs WHERE shadowed = 0 AND name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT ?", pat, limit),
                r => $"gatekit data dialog {r.S("name")} --tree");
            Add("root template", Rows("index", "SELECT name, uuid, type FROM templates WHERE name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT ?", pat, limit),
                r => $"gatekit data query about {r.S("uuid")}");
            Add("placed object", Rows("index", "SELECT name, uuid, type, level FROM entities WHERE name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT ?", pat, limit),
                r => $"gatekit data query about {r.S("uuid")}");
            Add("stat", Rows("index", "SELECT name, type FROM stats WHERE name LIKE ? ESCAPE '\\' GROUP BY name ORDER BY length(name) LIMIT ?", pat, limit),
                r => $"gatekit data stat {r.S("name")}");
            Add("osiris", Rows("index", "SELECT name, kind AS type, params FROM osiris_functions WHERE name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT ?", pat, limit),
                r => $"gatekit data osiris {r.S("name")} --uses");
            Add("object", Rows("index", "SELECT name, key AS uuid, node AS type FROM v_objects WHERE name LIKE ? ESCAPE '\\'"
                                        + " AND node NOT IN ('GameObjects') ORDER BY length(name) LIMIT ?", pat, limit),
                r => $"gatekit data query about {r.S("uuid") ?? r.S("name")}");
            Add("text", Rows("index", "SELECT handle, text AS name FROM loca WHERE lang = 'English' AND variant = '' AND text LIKE ? ESCAPE '\\' ORDER BY length(text) LIMIT ?", pat, limit),
                r => $"gatekit data text {r.S("handle")} --where");
        }
        if (Has("symbols"))
        {
            Add("class", Rows("symbols", "SELECT CASE WHEN namespace = '' THEN class ELSE namespace || '::' || class END AS name, count(*) AS members"
                                         + " FROM symbols WHERE class <> '' AND (namespace || '::' || class) LIKE ? ESCAPE '\\'"
                                         + " GROUP BY namespace, class ORDER BY length(name) LIMIT ?", pat, limit),
                r => $"gatekit data query about {r.S("name")}");
            Add("function", Rows("symbols", "SELECT full_name AS name, signature FROM symbols WHERE kind IN ('method','function') AND method LIKE ? ESCAPE '\\'"
                                            + " GROUP BY full_name ORDER BY length(full_name) LIMIT ?", pat, limit),
                r => $"gatekit data query hook {r.S("name")}");
            Add("ecs type", Rows("symbols", "SELECT type AS name, kind AS type FROM type_names WHERE dll_id = ? AND kind <> 'query' AND type LIKE ? ESCAPE '\\'"
                                            + " ORDER BY length(type) LIMIT ?", GameId(), pat, limit),
                r => $"gatekit data query about {ClassPrefix().Replace(r.S("name") ?? "", "")}");
            Add("layout", Rows("symbols", "SELECT coalesce(engine_class, name) AS name, kind AS type FROM se_types WHERE name LIKE ? ESCAPE '\\'"
                                          + " OR engine_class LIKE ? ESCAPE '\\' ORDER BY engine_class IS NULL, length(name) LIMIT ?", pat, pat, limit),
                r => $"gatekit symbols layout {r.S("name")}");
            Add("enum", Rows("symbols", "SELECT name, underlying AS type FROM enums WHERE name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT ?", pat, limit),
                r => $"gatekit symbols enum {r.S("name")}");
            Add("enum value", Rows("symbols", "SELECT e.name || '::' || v.name AS name, v.value FROM enum_values v JOIN enums e ON e.id = v.enum_id"
                                              + " WHERE v.name LIKE ? ESCAPE '\\' ORDER BY length(v.name) LIMIT ?", pat, limit),
                r => $"gatekit symbols enum {(r.S("name") ?? "").Split("::")[0]}");
        }
        return output;
    }

    // ------------------------------------------------------------------------------ about

    /// <summary>A C++ class of the engine: what every source knows about it.</summary>
    public Dictionary<string, object?> AboutClass(string name)
    {
        var gid = GameId();
        var cut = name.LastIndexOf("::", StringComparison.Ordinal);
        var ns = cut < 0 ? "" : name[..cut];
        var cls = cut < 0 ? name : name[(cut + 2)..];
        var output = new Dictionary<string, object?> { ["kind"] = "class", ["name"] = name };
        output["exports"] = Rows("symbols", "SELECT d.name AS dll, s.rva, s.signature, s.kind FROM symbols s JOIN dlls d ON d.id = s.dll_id"
                                            + " WHERE s.class = ? AND s.namespace = ? ORDER BY s.kind, s.method LIMIT 400", cls, ns);
        var ecs = Rows("symbols", "SELECT context, index_rva FROM ecs_ids WHERE dll_id = ? AND type = ? GROUP BY index_rva", gid, name);
        output["ecs"] = ecs;
        output["type_name"] = Rows("symbols", "SELECT kind, func_rva FROM type_names WHERE dll_id = ? AND type IN (?, ?)", gid, "class " + name, "struct " + name);
        output["layout"] = Rows("symbols", "SELECT t.kind, t.ext_name, t.size, t.file, t.line, f.idx, f.name, f.type, f.offset, f.checked, f.legacy"
                                           + " FROM se_types t LEFT JOIN se_fields f ON f.type_id = t.id WHERE t.engine_class = ? ORDER BY f.idx", name);
        output["accessors"] = Rows("symbols", "SELECT field, kind, offset, width, type, game_offset FROM accessors WHERE class = ? ORDER BY offset, field", name);
        output["game_functions"] = Rows("symbols",
            "SELECT m.game_rva, s.full_name, s.signature, m.method, m.relation, m.evidence FROM matches m"
            + " JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva WHERE m.game_dll = ? AND s.class = ? AND s.namespace = ?"
            + " ORDER BY s.method", gid, cls, ns);
        output["game_vtables"] = Rows("symbols",
            "SELECT gv.rva, gv.slots, m.support FROM vtab_matches m JOIN vtabs gv ON gv.id = m.game_vtab JOIN vtabs t ON t.id = m.toolkit_vtab"
            + " JOIN symbols s ON s.id = t.symbol_id WHERE gv.dll_id = ? AND s.class = ? AND s.namespace = ?", gid, cls, ns);
        var index = ecs.Select(e => e["index_rva"]).ToList();
        if (index.Count > 0)
        {
            output["game_users"] = Rows("symbols", "SELECT x.func_rva, f.size, group_concat(DISTINCT x.kind) AS access FROM xrefs x"
                                                   + " LEFT JOIN functions f ON f.dll_id = x.dll_id AND f.rva = x.func_rva"
                                                   + $" WHERE x.dll_id = ? AND x.target_rva IN ({Q.Marks(index.Count)}) GROUP BY x.func_rva ORDER BY f.size LIMIT 200",
                [gid, .. index]);
        }
        return output;
    }

    private string? NameAt(object? gid, object? rva) =>
        Rows("symbols", "SELECT s.full_name FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
                        + " WHERE m.game_dll = ? AND m.game_rva = ? AND (m.relation IS NULL OR m.relation <> 'inlined' OR m.method = 'vtable')"
                        + " ORDER BY length(s.full_name) LIMIT 1", gid, rva).FirstOrDefault()?.S("full_name");

    /// <summary>An address in a binary: the function there, its names, callers, callees and pseudo-code.</summary>
    public Dictionary<string, object?> AboutAddress(string binary, long rva)
    {
        var d = Rows("symbols", "SELECT id, name, origin FROM dlls WHERE name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT 1", Q.Like(binary));
        if (d.Count == 0)
        {
            return new() { ["kind"] = "address", ["error"] = $"no binary matches {binary}" };
        }
        var dllId = d[0]["id"];
        var dll = d[0].S("name");
        var output = new Dictionary<string, object?> { ["kind"] = "address", ["binary"] = dll, ["rva"] = rva };
        var f = Rows("symbols", "SELECT rva, size, leaf FROM functions WHERE dll_id = ? AND rva <= ? ORDER BY rva DESC LIMIT 1", dllId, rva);
        long start;
        if (f.Count > 0 && rva < (f[0].L("rva") ?? 0) + (f[0].L("size") ?? 0))
        {
            output["function"] = f[0];
            start = f[0].L("rva")!.Value;
        }
        else
        {
            start = rva;
        }
        output["exported_as"] = Rows("symbols", "SELECT signature, mangled FROM symbols WHERE dll_id = ? AND rva = ?", dllId, start);
        output["matches"] = Rows("symbols",
            "SELECT t.name AS toolkit_dll, m.toolkit_rva, s.signature, m.method, m.relation, m.evidence, m.sample FROM matches m"
            + " JOIN dlls t ON t.id = m.toolkit_dll LEFT JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
            + " WHERE m.game_dll = ? AND m.game_rva = ?", dllId, start);
        output["labels"] = Rows("symbols", "SELECT name, mapping FROM se_symbols WHERE dll_id = ? AND rva = ?", dllId, rva)
            .Concat(Rows("symbols", "SELECT 'TypeIndex<' || type || '>' AS name, context FROM ecs_ids WHERE dll_id = ? AND index_rva = ? GROUP BY type", dllId, rva))
            .Concat(Rows("symbols", "SELECT 'GetTypeName<' || type || '>' AS name FROM type_names WHERE dll_id = ? AND func_rva = ?", dllId, start)).ToList();
        output["strings"] = Rows("symbols", "SELECT t.text FROM string_refs r JOIN strings t ON t.id = r.string_id WHERE t.dll_id = ? AND r.func_rva = ? LIMIT 40", dllId, start);
        var callers = Rows("symbols", "SELECT dll_id, caller_rva, sites FROM calls WHERE callee_dll = ? AND callee_rva = ? LIMIT 60", dllId, start);
        var callees = Rows("symbols", "SELECT callee_dll, callee_rva, import, sites FROM calls WHERE dll_id = ? AND caller_rva = ? LIMIT 80", dllId, start);
        output["callers"] = callers;
        output["callees"] = callees;
        output["touches"] = Rows("symbols", "SELECT x.target_rva, x.kind, e.type FROM xrefs x LEFT JOIN ecs_ids e ON e.dll_id = x.dll_id AND e.index_rva = x.target_rva"
                                            + " WHERE x.dll_id = ? AND x.func_rva = ? GROUP BY x.target_rva, x.kind LIMIT 80", dllId, start);
        if (d[0].S("origin") == "game")
        {
            foreach (var c in callers)
            {
                c["name"] = NameAt(c["dll_id"], c["caller_rva"]);
            }
            foreach (var c in callees)
            {
                if (c["callee_rva"] != null)
                {
                    c["name"] = NameAt(c["callee_dll"], c["callee_rva"]);
                }
            }
        }
        var code = RowsIf("decomp", "SELECT c.text FROM functions f JOIN binaries b ON b.id = f.binary_id JOIN code c ON c.function_id = f.id"
                                    + " WHERE b.name = ? AND f.rva = ?", dll, start);
        if (code.Count > 0 && code[0]["text"] is byte[] blob)
        {
            using var z = new ZLibStream(new MemoryStream(blob), CompressionMode.Decompress);
            using var text = new StreamReader(z, Encoding.UTF8);
            output["pseudo_code"] = text.ReadToEnd();
        }
        return output;
    }

    /// <summary>An object of the game data by uuid or exact name: its attributes and everything that refers to it.</summary>
    public Dictionary<string, object?> AboutObject(string key)
    {
        var output = new Dictionary<string, object?> { ["kind"] = "object", ["key"] = key };
        var objects = Rows("index", "SELECT id, package, mod, path, node, key, name FROM v_objects WHERE key = ? LIMIT 20", key);
        if (objects.Count == 0)
        {
            objects = Rows("index", "SELECT id, package, mod, path, node, key, name FROM v_objects WHERE name = ? LIMIT 20", key);
        }
        foreach (var o in objects)
        {
            o["attributes"] = Rows("index", "SELECT attr, value FROM v_attrs WHERE object_id = ? LIMIT 300", o["id"]);
        }
        output["definitions"] = objects;
        var keys = new List<string> { key };
        keys.AddRange(objects.Select(o => o.S("key")).OfType<string>().Where(k => !keys.Contains(k)).Distinct());
        var refs = new List<Row>();
        foreach (var k in keys)
        {
            refs.AddRange(Rows("index", "SELECT o.node, o.key, o.name, o.path, n.name AS via FROM attrs a JOIN names n ON n.id = a.attr_id"
                                        + " JOIN v_objects o ON o.id = a.object_id WHERE a.value = ? AND length(a.value) BETWEEN 4 AND 80"
                                        + " AND o.key IS NOT ? LIMIT 200", k, k));
        }
        output["referenced_by"] = refs;
        output["template"] = Rows("index", "SELECT * FROM templates WHERE uuid = ?", key);
        output["placed"] = Rows("index", "SELECT * FROM entities WHERE uuid = ?", key);
        output["dialogs_as_speaker"] = Rows("index", "SELECT d.name, s.idx FROM dialog_speakers s JOIN dialogs d ON d.id = s.dialog_id AND d.shadowed = 0 WHERE s.object = ? LIMIT 100", key);
        output["stats_using"] = Rows("index", "SELECT s.name, f.field FROM stat_fields f JOIN stats s ON s.id = f.stat_id WHERE f.value = ? LIMIT 100", key);
        return output;
    }

    /// <summary>Routes a uuid, a localization handle, binary+0xRVA, a class name or any other name.</summary>
    public Dictionary<string, object?> About(string text)
    {
        var m = Address().Match(text);
        if (m.Success)
        {
            return AboutAddress(m.Groups[1].Value, long.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        if (Handle().IsMatch(text))
        {
            return new()
            {
                ["kind"] = "text", ["handle"] = text,
                ["text"] = Rows("index", "SELECT lang, variant, text FROM loca WHERE handle = ?", text),
                ["dialogs"] = Rows("index", "SELECT d.name, l.node FROM dialog_lines l JOIN dialogs d ON d.id = l.dialog_id WHERE l.handle = ? LIMIT 50", text),
                ["objects"] = Rows("index", "SELECT o.node, o.key, o.name, n.name AS via FROM attrs a JOIN names n ON n.id = a.attr_id JOIN v_objects o ON o.id = a.object_id"
                                            + " WHERE a.value LIKE ? LIMIT 50", text + "%"),
            };
        }
        if (Q.IsUuid(text))
        {
            return AboutObject(text.ToLowerInvariant());
        }
        if (text.Contains("::", StringComparison.Ordinal) && Has("symbols"))
        {
            var got = AboutClass(text);
            if (NonEmpty(got["exports"]) || NonEmpty(got["ecs"]) || NonEmpty(got["layout"]) || NonEmpty(got["type_name"]))
            {
                return got;
            }
        }
        if (Has("index"))
        {
            var got = AboutObject(text);
            if (NonEmpty(got["definitions"]) || NonEmpty(got["template"]) || NonEmpty(got["placed"]))
            {
                return got;
            }
        }
        return new() { ["kind"] = "search", ["query"] = text, ["results"] = Search(text) };
    }

    private static bool NonEmpty(object? v) => v is System.Collections.ICollection { Count: > 0 };

    // ------------------------------------------------------------------------------ scene, track, hook

    private List<string> ActorNames(object? actorId) =>
        Rows("index", "SELECT coalesce(entity, speaker_group, template_name, object) AS name FROM v_actor_objects WHERE actor_id = ?", actorId)
            .Select(x => x.S("name") ?? "").Distinct().ToList();

    /// <summary>A timeline: its dialog, actors, the tracks that change gear with their time spans, and the story goals that mention it.</summary>
    public Dictionary<string, object?> Scene(string name)
    {
        var t = Rows("index", "SELECT * FROM timelines WHERE name = ? ORDER BY shadowed, id LIMIT 1", name);
        if (t.Count == 0)
        {
            var candidates = Rows("index", "SELECT name FROM timelines WHERE shadowed = 0 AND name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT 30", Q.Like(name));
            if (candidates.Count != 1)
            {
                return new() { ["kind"] = "scene", ["error"] = "no exact match", ["candidates"] = candidates.Select(c => c.S("name")).ToList() };
            }
            t = Rows("index", "SELECT * FROM timelines WHERE name = ? ORDER BY shadowed, id LIMIT 1", candidates[0]["name"]);
        }
        var tl = t[0];
        var output = new Dictionary<string, object?>
        {
            ["kind"] = "scene", ["name"] = tl["name"], ["uuid"] = tl["uuid"], ["package"] = tl["package"], ["path"] = tl["path"],
            ["duration"] = tl["duration"], ["phases"] = tl["phases"], ["components"] = tl["components"],
        };
        var d = Rows("index", "SELECT id, name, uuid, category, nodes FROM dialogs WHERE id = ?", tl["dialog_id"]);
        output["dialog"] = d.FirstOrDefault();
        var actors = Rows("index", "SELECT id, uuid, speaker, kind, is_player, scene_template FROM timeline_actors WHERE timeline_id = ?"
                                   + " AND (kind IS NULL OR kind NOT IN ('scenecam','scenelight','light','effect','vcam')) ORDER BY speaker IS NULL, speaker", tl["id"]);
        foreach (var a in actors)
        {
            a["who"] = ActorNames(a["id"]);
        }
        output["actors"] = actors;
        output["tracks"] = Rows("index", "SELECT type, count(*) AS segments, sum(keys) AS keys, count(DISTINCT actor_id) AS actors, sum(hides) AS hiding_keys"
                                         + " FROM v_tracks WHERE timeline_id = ? GROUP BY type ORDER BY type", tl["id"]);
        var gear = new List<Dictionary<string, object?>>();
        foreach (var tr in Rows("index", "SELECT track_id, type, actor_id, start, end FROM v_tracks WHERE timeline_id = ? AND (hides > 0 OR type = 'TLShapeShift')"
                                         + " ORDER BY type, actor_id, start", tl["id"]))
        {
            var keys = Rows("index", "SELECT channel, time, value FROM track_keys WHERE track_id = ? ORDER BY channel, time, idx", tr["track_id"]);
            gear.Add(new()
            {
                ["type"] = tr["type"], ["who"] = tr["actor_id"] != null ? ActorNames(tr["actor_id"]) : new List<string>(),
                ["from"] = tr["start"], ["to"] = tr["end"], ["keys"] = keys,
            });
        }
        output["gear_changes"] = gear;
        var words = new List<string> { tl.S("name") ?? "" };
        if (d.Count > 0)
        {
            var dn = d[0].S("name") ?? "";
            words.Add(dn[(dn.LastIndexOf('/') + 1)..]);
        }
        var goals = new List<Row>();
        foreach (var w in words.Distinct())
        {
            try
            {
                goals.AddRange(Rows("index", "SELECT t.path, snippet(texts_fts, 0, '[', ']', ' ... ', 12) AS hit FROM texts_fts JOIN texts t ON t.id = texts_fts.rowid"
                                             + " WHERE texts_fts MATCH ? AND t.kind = 'goal' LIMIT 20", "\"" + w.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"));
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
            }
        }
        output["story_goals"] = goals;
        return output;
    }

    /// <summary>A timeline track type across data and code.</summary>
    public Dictionary<string, object?> Track(string name)
    {
        var output = new Dictionary<string, object?> { ["kind"] = "track", ["name"] = name };
        output["data"] = Rows("index", "SELECT count(DISTINCT timeline_id) AS scenes, count(*) AS segments, sum(keys) AS keys,"
                                       + " count(DISTINCT CASE WHEN hides > 0 THEN timeline_id END) AS scenes_that_hide FROM v_tracks WHERE type = ?", name);
        output["key_attributes"] = Rows("index", "SELECT k.attrs, count(*) AS keys FROM track_keys k JOIN tracks t ON t.id = k.track_id"
                                                 + " JOIN track_types y ON y.id = t.type_id WHERE y.name = ? GROUP BY k.attrs ORDER BY 2 DESC LIMIT 12", name);
        output["examples"] = Rows("index", "SELECT DISTINCT timeline FROM v_tracks WHERE type = ? AND (hides > 0 OR hides IS NULL) LIMIT 8", name)
            .Select(r => r.S("timeline")).ToList();
        if (Has("symbols"))
        {
            var gid = GameId();
            output["engine_classes"] = Rows("symbols", "SELECT CASE WHEN namespace = '' THEN class ELSE namespace || '::' || class END AS name, count(*) AS members,"
                                                       + " group_concat(DISTINCT d.name) AS dlls FROM symbols s JOIN dlls d ON d.id = s.dll_id"
                                                       + " WHERE class LIKE ? ESCAPE '\\' GROUP BY namespace, class ORDER BY length(class) LIMIT 12", Q.Like(name));
            var shortName = name.StartsWith("TL", StringComparison.Ordinal) ? name[2..] : name;
            output["related_functions"] = Rows("symbols", "SELECT d.name AS dll, s.rva, s.signature FROM symbols s JOIN dlls d ON d.id = s.dll_id"
                                                          + " WHERE s.kind IN ('method','function') AND s.method LIKE ? ESCAPE '\\' ORDER BY length(s.signature) LIMIT 40", Q.Like(shortName));
            output["game_functions"] = Rows("symbols", "SELECT m.game_rva, s.full_name, m.method, m.relation FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll"
                                                       + " AND s.rva = m.toolkit_rva WHERE m.game_dll = ? AND (s.full_name LIKE ? ESCAPE '\\' OR s.full_name LIKE ? ESCAPE '\\') LIMIT 40",
                gid, Q.Like(name), Q.Like(shortName));
            output["game_strings"] = Rows("symbols", "SELECT t.rva, t.text, group_concat(r.func_rva) AS functions FROM strings t LEFT JOIN string_refs r ON r.string_id = t.id"
                                                     + " WHERE t.dll_id = ? AND t.text LIKE ? ESCAPE '\\' GROUP BY t.id LIMIT 20", gid, Q.Like(shortName));
            output["layouts"] = Rows("symbols", "SELECT coalesce(engine_class, name) AS name, kind, file, line FROM se_types WHERE name LIKE ? ESCAPE '\\' LIMIT 10", Q.Like(shortName));
        }
        return output;
    }

    /// <summary>Where a function lives in the game and what is needed to hook it.</summary>
    public Dictionary<string, object?> Hook(string name)
    {
        var gid = GameId();
        var functions = new List<Row>();
        var output = new Dictionary<string, object?> { ["kind"] = "hook", ["query"] = name, ["functions"] = functions };
        var m = Address().Match(name);
        List<Row> found;
        if (m.Success)
        {
            found = [new Row(["game_rva", "full_name", "signature", "method", "relation", "evidence"],
                [long.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), null, null, "address", null, null])];
        }
        else
        {
            found = Rows("symbols", "SELECT m.game_rva, s.full_name, s.signature, s.mangled, m.method, m.relation, m.evidence, d.name AS toolkit_dll, m.toolkit_rva"
                                    + " FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva JOIN dlls d ON d.id = m.toolkit_dll"
                                    + " WHERE m.game_dll = ? AND s.full_name LIKE ? ESCAPE '\\' ORDER BY length(s.full_name) LIMIT 20", gid, Q.Like(name));
        }
        foreach (var f in found)
        {
            var rva = f.L("game_rva") ?? 0;
            var hex = rva.ToString("X", CultureInfo.InvariantCulture);
            f["address"] = $"bg3.exe+0x{hex}";
            if (f.S("method") == "vtable")
            {
                f["trust"] = "the function in the matching slot of the class's virtual table"
                             + (f.S("relation") == "inlined" ? "; larger than in the Toolkit because its callees are inlined into it" : "");
            }
            else
            {
                f["trust"] = f.S("relation") switch
                {
                    "same" => "function found, sizes agree",
                    "inlined" => "the game function is much larger: the code was probably inlined into this caller",
                    "partial" => "the game function is much smaller: probably only a part",
                    _ => "not compared",
                };
            }
            f["other_names_at_address"] = Rows("symbols",
                "SELECT DISTINCT s.full_name FROM matches m JOIN symbols s ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva"
                + " WHERE m.game_dll = ? AND m.game_rva = ? AND s.full_name IS NOT ? LIMIT 8", gid, rva, f["full_name"]).Select(r => r.S("full_name")).ToList();
            f["size"] = Rows("symbols", "SELECT size FROM functions WHERE dll_id = ? AND rva = ?", gid, rva).FirstOrDefault()?["size"];
            f["callers"] = Rows("symbols", "SELECT caller_rva FROM calls WHERE callee_dll = ? AND callee_rva = ? LIMIT 25", gid, rva)
                .Select(c => new Dictionary<string, object?>
                {
                    ["address"] = "bg3.exe+0x" + (c.L("caller_rva") ?? 0).ToString("X", CultureInfo.InvariantCulture),
                    ["name"] = NameAt(gid, c["caller_rva"]),
                }).ToList();
            f["in_vtables"] = Rows("symbols", "SELECT v.rva AS table_rva, s.idx AS slot FROM vtab_slots s JOIN vtabs v ON v.id = s.vtab_id WHERE v.dll_id = ? AND s.rva = ? LIMIT 10", gid, rva);
            f["signature_command"] = $"gatekit symbols signature bg3.exe+0x{hex}";
            functions.Add(f);
        }
        if (found.Count == 0)
        {
            output["toolkit_only"] = Rows("symbols", "SELECT d.name AS dll, s.rva, s.signature FROM symbols s JOIN dlls d ON d.id = s.dll_id"
                                                     + " WHERE s.kind IN ('method','function') AND s.full_name LIKE ? ESCAPE '\\' ORDER BY length(s.full_name) LIMIT 20", Q.Like(name));
            output["note"] = "not found in the game executable; the Toolkit addresses listed are editor addresses. Look for the game function among "
                             + "the users of the ECS types it touches: gatekit symbols users <component>";
        }
        return output;
    }

    // ------------------------------------------------------------------------------ sql, schema

    /// <summary>One read-only statement on a database: columns, at most <paramref name="limit"/> rows, whether rows were cut.</summary>
    public Dictionary<string, object?> RunSql(string database, string query, int limit = 500)
    {
        if (Paths.PathOf(database) == null)
        {
            throw new KeyNotFoundException($"'{database}'");
        }
        var (columns, rows, truncated) = Db(database)!.Run(query, limit);
        return new() { ["columns"] = columns, ["rows"] = rows, ["truncated"] = truncated };
    }

    [GeneratedRegex(@"_(data|idx|content|docsize|config)$")]
    private static partial Regex FtsShadow();

    [GeneratedRegex(@"^\s*(\w+)\s+[A-Z].*?--\s*(.*)$")]
    private static partial Regex ColumnComment();

    /// <summary>Tables, columns (with the comments written in the schema) and row counts of the databases.</summary>
    public Dictionary<string, object?> Schema(string? name = null)
    {
        var output = new Dictionary<string, object?>();
        foreach (var key in name != null ? [name] : OfflineDatabases.Names)
        {
            var path = Paths.PathOf(key) ?? throw new KeyNotFoundException($"'{key}'");
            var db = Db(key, false);
            if (db == null)
            {
                output[key] = new Dictionary<string, object?> { ["missing"] = path };
                continue;
            }
            var tables = new Dictionary<string, object?>();
            foreach (var r in db.Rows("SELECT name, type, sql FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name"))
            {
                var tname = r.S("name") ?? "";
                if (FtsShadow().IsMatch(tname))
                {
                    continue;
                }
                var comments = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in (r.S("sql") ?? "").Split('\n'))
                {
                    var m = ColumnComment().Match(line.TrimEnd('\r'));
                    if (m.Success)
                    {
                        comments[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                    }
                }
                object? count;
                List<Dictionary<string, object?>> cols;
                try
                {
                    count = r.S("type") == "table" ? db.Scalar($"SELECT count(*) FROM \"{tname}\"") : null;
                    cols = db.Rows($"PRAGMA table_info(\"{tname}\")").Select(c => new Dictionary<string, object?>
                    {
                        ["name"] = c.At(1), ["type"] = c.At(2), ["comment"] = comments.GetValueOrDefault(c.S("name") ?? "", ""),
                    }).ToList();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    count = null;
                    cols = [];
                }
                tables[tname] = new Dictionary<string, object?> { ["type"] = r.S("type"), ["rows"] = count, ["columns"] = cols };
            }
            output[key] = new Dictionary<string, object?> { ["path"] = path, ["tables"] = tables };
        }
        return output;
    }

    /// <summary>The schema as Markdown (the old <c>schema --write</c> file).</summary>
    public string SchemaMarkdown()
    {
        var lines = new List<string> { "# Database schema", "", "Generated by `gatekit data query schema --write`. Row counts are from the build it was generated on.", "" };
        foreach (var (key, value) in Schema())
        {
            var info = (Dictionary<string, object?>)value!;
            lines.Add($"## {key}");
            lines.Add("");
            if (info.TryGetValue("missing", out var missing))
            {
                lines.Add($"Not built (`{missing}`).");
                lines.Add("");
                continue;
            }
            foreach (var (tname, tv) in (Dictionary<string, object?>)info["tables"]!)
            {
                var t = (Dictionary<string, object?>)tv!;
                lines.Add($"### `{tname}`" + (t["rows"] != null ? $" — {Q.Thousands(Convert.ToInt64(t["rows"], CultureInfo.InvariantCulture))} rows" : " (view)"));
                lines.Add("");
                lines.Add("| Column | Type | Meaning |");
                lines.Add("|---|---|---|");
                foreach (var c in (List<Dictionary<string, object?>>)t["columns"]!)
                {
                    lines.Add($"| `{c["name"]}` | {c["type"] ?? ""} | {c["comment"]} |");
                }
                lines.Add("");
            }
        }
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------------------ output

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 1, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A result as JSON text: indented for people, compact for the MCP server.</summary>
    public static string ToJson(object? value, bool indented = true) => JsonSerializer.Serialize(ToNode(value), indented ? Indented : Compact);

    /// <summary>A result as a JSON tree (byte arrays become base64, everything else as it is).</summary>
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        System.Collections.IDictionary dict => new JsonObject(dict.Keys.Cast<object>().Select(k =>
            new KeyValuePair<string, JsonNode?>(Convert.ToString(k, CultureInfo.InvariantCulture)!, ToNode(dict[k])))),
        System.Collections.IEnumerable list => new JsonArray(list.Cast<object?>().Select(ToNode).ToArray()),
        IFormattable f => JsonValue.Create(f.ToString(null, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>A result as indented text for people: nested values, numbers named like an address in hex, long lists cut.</summary>
    public static void Show(TextWriter w, object? value, int indent = 0, string? key = null)
    {
        var pad = new string(' ', 2 * indent);
        switch (value)
        {
            case System.Collections.IDictionary dict:
            {
                if (key != null)
                {
                    w.WriteLine($"{pad}{key}:");
                    indent++;
                    pad = new string(' ', 2 * indent);
                }
                var entries = dict.Keys.Cast<object>().Select(k => (Key: Convert.ToString(k, CultureInfo.InvariantCulture)!, Value: dict[k])).ToList();
                bool Complex(object? v) => v is System.Collections.IDictionary || (v is System.Collections.IEnumerable && v is not string && v is not byte[]);
                var simple = entries.Where(e => !Complex(e.Value)).ToList();
                if (simple.Count > 0 && simple.Count == entries.Count && PyRepr(simple).Length < 230)
                {
                    w.WriteLine(pad + string.Join("  ", simple.Where(e => e.Value is not (null or "")).Select(e => $"{e.Key}={Fmt(e.Key, e.Value)}")));
                    return;
                }
                foreach (var (k, v) in entries)
                {
                    if (Complex(v))
                    {
                        Show(w, v, indent, k);
                    }
                    else if (v is not (null or ""))
                    {
                        var text = Fmt(k, v);
                        if (text.Contains('\n', StringComparison.Ordinal))
                        {
                            w.WriteLine($"{pad}{k}:");
                            foreach (var line in text.Split('\n'))
                            {
                                w.WriteLine($"{pad}  {line.TrimEnd('\r')}");
                            }
                        }
                        else
                        {
                            w.WriteLine($"{pad}{k}: {text}");
                        }
                    }
                }
                break;
            }
            case System.Collections.IEnumerable list when value is not string && value is not byte[]:
            {
                var items = list.Cast<object?>().ToList();
                if (items.Count == 0)
                {
                    return;
                }
                w.WriteLine(key != null ? $"{pad}{key} ({items.Count}):" : $"{pad}({items.Count})");
                foreach (var item in items.Take(60))
                {
                    if (item is System.Collections.IDictionary || (item is System.Collections.IEnumerable && item is not string && item is not byte[]))
                    {
                        Show(w, item, indent + 1);
                    }
                    else
                    {
                        w.WriteLine($"{pad}  {Q.Py(item)}");
                    }
                }
                if (items.Count > 60)
                {
                    w.WriteLine($"{pad}  ... {items.Count - 60} more (use --json for everything)");
                }
                break;
            }
            default:
                w.WriteLine($"{pad}{(key != null ? key + ": " : "")}{Q.Py(value)}");
                break;
        }
    }

    private static string PyRepr(List<(string Key, object? Value)> simple) =>
        "{" + string.Join(", ", simple.Select(e => $"'{e.Key}': {(e.Value is string s ? "'" + s + "'" : Q.Py(e.Value))}")) + "}";

    private static string Fmt(string key, object? v)
    {
        if (v is long or int && Convert.ToInt64(v, CultureInfo.InvariantCulture) >= 0
            && (key.Contains("rva", StringComparison.Ordinal) || key is "offset" or "game_offset"))
        {
            return "0x" + Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString("X", CultureInfo.InvariantCulture);
        }
        var text = Q.Py(v);
        return text.Length <= 400 || text.Contains('\n', StringComparison.Ordinal) ? text : text[..397] + "...";
    }

    public void Dispose()
    {
        foreach (var db in _open.Values)
        {
            db?.Dispose();
        }
        _open.Clear();
    }
}
