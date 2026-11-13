using System.Text.RegularExpressions;
using GateKit.GameData.Resources;

namespace GateKit.GameData.Query;

/// <summary>
/// The questions the index answers in text, one method per <c>gatekit data</c> verb. Results go to
/// <see cref="Out"/>, notes and counts to <see cref="Err"/>; a question that cannot be answered
/// throws <see cref="DataQueryException"/>.
/// </summary>
public sealed partial class IndexQueries(ReadOnlyDb db, TextWriter output, TextWriter error) : IDisposable
{
    public TextWriter Out { get; } = output;
    public TextWriter Err { get; } = error;
    public ReadOnlyDb Db { get; } = db;

    /// <summary>Opens the index; a missing file says how to build it.</summary>
    public static IndexQueries Open(string path, TextWriter output, TextWriter error) =>
        new(ReadOnlyDb.Open(path, $"{path} does not exist; run:  gatekit data build"), output, error);

    public void Dispose() => Db.Dispose();

    [GeneratedRegex("^h[0-9a-f]{8}g")]
    private static partial Regex HandleStart();

    private static readonly string[] InfoTables =
    [
        "files", "timelines", "timeline_actors", "tracks", "track_keys", "dialogs", "dialog_nodes",
        "dialog_lines", "dialog_links", "dialog_flags", "voice_lines", "templates", "entities", "speaker_groups", "sources", "objects", "attrs", "texts",
        "stats", "stat_fields", "osiris_functions", "osiris_uses", "loca", "problems",
    ];

    // ------------------------------------------------------------------------------ info, survey

    /// <summary>What the database holds, how it was built, the first problems.</summary>
    public void Info(string? package)
    {
        foreach (var r in Db.Rows("SELECT key, value FROM meta"))
        {
            Out.WriteLine($"{r.At(0)}: {r.At(1)}");
        }
        foreach (var table in InfoTables)
        {
            Out.WriteLine($"{table}: {Db.Scalar($"SELECT count(*) FROM {table}")}");
        }
        foreach (var r in Db.Rows("SELECT path, problem FROM problems LIMIT 20"))
        {
            Out.WriteLine($"  problem: {r.At(0)} - {r.At(1)}");
        }
    }

    /// <summary>Scenes per track type; for on/off tracks, scenes that hide and show.</summary>
    public void Survey(string? package, bool allFiles)
    {
        var where = allFiles ? "1" : "t.shadowed = 0";
        var args = new List<object?>();
        if (package != null)
        {
            where += " AND t.package = ?";
            args.Add(package);
        }
        var total = Db.Scalar($"SELECT count(*) FROM timelines t WHERE {where}", [.. args]);
        Out.WriteLine($"timelines: {total}" + (package != null ? $" (package {package})" : ""));
        Out.WriteLine($"{"track",-36}{"scenes",8}{"hides",8}{"hides+shows",13}{"only shows",12}");
        var rows = Db.Rows($"""
            SELECT type, count(*), sum(h > 0), sum(h > 0 AND s > 0), sum(h = 0 AND s > 0), max(onoff) FROM (
                SELECT y.name AS type, k.timeline_id, coalesce(sum(k.hides), 0) AS h, coalesce(sum(k.shows), 0) AS s,
                       max(k.hides IS NOT NULL) AS onoff
                FROM tracks k JOIN timelines t ON t.id = k.timeline_id JOIN track_types y ON y.id = k.type_id
                WHERE {where} GROUP BY y.name, k.timeline_id)
            GROUP BY type ORDER BY 2 DESC
            """, [.. args]);
        foreach (var r in rows)
        {
            if (Convert.ToInt64(r.At(5) ?? 0L, System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                Out.WriteLine($"{r.At(0),-36}{r.At(1),8}{r.At(2),8}{r.At(3),13}{r.At(4),12}");
            }
            else
            {
                Out.WriteLine($"{r.At(0),-36}{r.At(1),8}");
            }
        }
    }

    // ------------------------------------------------------------------------------ actors and spans

    /// <summary>Names of the objects a timeline actor stands for, through the dialog's speaker list.</summary>
    public string ActorLabel(long? actorId, int limit = 3)
    {
        var rows = Db.Rows("SELECT entity, template_name, speaker_group, object, speaker FROM v_actor_objects WHERE actor_id = ?", actorId);
        if (rows.Count == 0)
        {
            var a = Db.One("SELECT uuid, speaker, kind, is_player, scene_template FROM timeline_actors WHERE id = ?", actorId);
            if (a == null)
            {
                return "(no actor)";
            }
            var extra = "";
            if (a.S("scene_template") is { } st)
            {
                var t = Db.Scalar("SELECT name FROM templates WHERE uuid = ?", st);
                extra = $", stand-in {t ?? st}";
            }
            var who = (a.L("is_player") ?? 0) != 0 ? "player" : a.S("kind") ?? "actor";
            return $"{who} {a.S("uuid")}" + (a.L("speaker") is { } sp ? $" speaker {sp}" : "") + extra;
        }
        var names = rows.Select(r => r.S("entity") ?? r.S("speaker_group") ?? r.S("template_name") ?? r.S("object") ?? "").Distinct().ToList();
        var more = names.Count > limit ? $" +{names.Count - limit}" : "";
        return $"speaker {Q.Py(rows[0]["speaker"])}: " + string.Join(", ", names.Take(limit)) + more;
    }

    /// <summary>Time spans in which an on/off track hides, per channel.</summary>
    public List<(double Start, double? End)> HideSpans(long trackId, double? end)
    {
        var keys = Db.Rows("SELECT time, value, channel FROM track_keys WHERE track_id = ? ORDER BY channel, time, idx", trackId);
        var spans = new List<(double, double?)>();
        double? since = null;
        object? channel = null;
        var first = true;
        foreach (var k in keys)
        {
            var ch = k["channel"];
            if (first || !Equals(ch, channel))
            {
                if (since != null)
                {
                    spans.Add((since.Value, end));
                }
                since = null;
                channel = ch;
                first = false;
            }
            var value = k.S("value");
            if (value == "False" && since == null)
            {
                since = k.D("time");
            }
            else if (value == "True" && since != null)
            {
                spans.Add((since.Value, k.D("time")));
                since = null;
            }
        }
        if (since != null)
        {
            spans.Add((since.Value, end));
        }
        return spans;
    }

    public static string FormatSpans(IEnumerable<(double Start, double? End)> spans) =>
        string.Join(", ", spans.Distinct().OrderBy(s => s.Start).ThenBy(s => s.End ?? double.MaxValue)
            .Select(s => s.End is { } b ? $"{Q.F(s.Start, 2)}-{Q.F(b, 2)}" : $"{Q.F(s.Start, 2)}-end"));

    /// <summary>Uuids of the templates, placed objects and speaker groups a name or uuid stands for,
    /// including placed objects made from a matching template.</summary>
    public List<string> ObjectIds(string text)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (Q.IsUuid(text))
        {
            ids.Add(text.ToLowerInvariant());
        }
        else
        {
            foreach (var table in new[] { "templates", "entities", "speaker_groups" })
            {
                var exact = Db.Rows($"SELECT uuid FROM {table} WHERE name = ?", text).Select(r => r.S("uuid")!).ToList();
                if (exact.Count == 0)
                {
                    exact = Db.Rows($"SELECT uuid FROM {table} WHERE name LIKE ? ESCAPE '\\'", Q.Like(text)).Select(r => r.S("uuid")!).ToList();
                }
                ids.UnionWith(exact.Where(x => x != null));
            }
        }
        var found = ids.ToList();
        for (var i = 0; i < found.Count; i += 500)
        {
            var part = found.Skip(i).Take(500).Cast<object?>().ToArray();
            foreach (var r in Db.Rows($"SELECT uuid FROM entities WHERE template IN ({Q.Marks(part.Length)})", part))
            {
                if (r.S("uuid") is { } u)
                {
                    ids.Add(u);
                }
            }
        }
        return ids.Order(StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------------------------ scenes

    /// <summary>Scenes where an on/off track hides something, with the actors and the time spans.</summary>
    public void Hidden(string track, string? name, string? package, string? actor, bool countOnly, int limit)
    {
        var where = new List<string> { "type = ?", "hides > 0" };
        var args = new List<object?> { track };
        if (name != null)
        {
            where.Add("timeline LIKE ? ESCAPE '\\'");
            args.Add(Q.Like(name));
        }
        if (package != null)
        {
            where.Add("package = ?");
            args.Add(package);
        }
        if (actor != null)
        {
            var ids = ObjectIds(actor);
            var marks = Q.Marks(ids.Count);
            where.Add($"(actor_id IN (SELECT actor_id FROM v_actor_objects WHERE object IN ({marks}) OR template IN ({marks}))"
                      + $" OR actor_id IN (SELECT id FROM timeline_actors WHERE scene_template IN ({marks}) OR uuid IN ({marks})))");
            for (var i = 0; i < 4; i++)
            {
                args.AddRange(ids);
            }
        }
        var rows = Db.Rows($"SELECT * FROM v_tracks WHERE {string.Join(" AND ", where)} ORDER BY timeline, actor_id, start", [.. args]);
        var scenes = new List<(long Id, string Name, List<(long? Actor, List<Row> Tracks)> Actors)>();
        var sceneAt = new Dictionary<long, int>();
        foreach (var r in rows)
        {
            var tid = r.L("timeline_id")!.Value;
            if (!sceneAt.TryGetValue(tid, out var at))
            {
                sceneAt[tid] = at = scenes.Count;
                scenes.Add((tid, r.S("timeline") ?? "", []));
            }
            var actors = scenes[at].Actors;
            var aid = r.L("actor_id");
            var slot = actors.FindIndex(a => a.Actor == aid);
            if (slot < 0)
            {
                actors.Add((aid, []));
                slot = actors.Count - 1;
            }
            actors[slot].Tracks.Add(r);
        }
        Err.WriteLine($"{scenes.Count} scenes, {scenes.Sum(s => s.Actors.Count)} actors");
        if (countOnly)
        {
            return;
        }
        for (var n = 0; n < scenes.Count; n++)
        {
            if (n >= limit)
            {
                Err.WriteLine($"... first {limit} scenes shown; raise --limit or narrow the search");
                break;
            }
            var (tid, title, actors) = scenes[n];
            var dialog = Db.Scalar("SELECT d.name FROM timelines t JOIN dialogs d ON d.id = t.dialog_id WHERE t.id = ?", tid);
            Out.WriteLine(title + (dialog != null ? $"   [dialog {dialog}]" : ""));
            foreach (var (aid, tracks) in actors)
            {
                var spans = tracks.SelectMany(t => HideSpans(t.L("track_id")!.Value, t.D("end"))).ToList();
                Out.WriteLine($"    {ActorLabel(aid)}: hidden at {FormatSpans(spans)} s");
            }
        }
    }

    /// <summary>Scenes in which a template (or a character/item made from it) takes part.</summary>
    public void Template(string name, bool gear, int limit)
    {
        var ids = ObjectIds(name);
        if (ids.Count == 0)
        {
            throw new DataQueryException($"no template, placed object or speaker group matches '{name}'");
        }
        if (ids.Count > 900)
        {
            throw new DataQueryException($"'{name}' matches {ids.Count} objects; be more specific");
        }
        var marks = Q.Marks(ids.Count);
        var three = ids.Concat(ids).Concat(ids).Cast<object?>().ToArray();
        var named = Db.Rows($"SELECT 'template' AS k, uuid, name FROM templates WHERE uuid IN ({marks})"
                            + $" UNION ALL SELECT 'placed ' || type, uuid, name FROM entities WHERE uuid IN ({marks})"
                            + $" UNION ALL SELECT 'speaker group', uuid, name FROM speaker_groups WHERE uuid IN ({marks})", three);
        Err.WriteLine($"{name}: {named.Count} matching objects");
        foreach (var r in named.Take(12))
        {
            Err.WriteLine($"    {r.At(0),-16} {r.At(1)}  {r.At(2)}");
        }
        if (named.Count > 12)
        {
            Err.WriteLine($"    ... and {named.Count - 12} more");
        }
        var rows = Db.Rows($"""
            SELECT t.name AS timeline, d.name AS dialog, 'speaker ' || s.idx AS role, t.id
            FROM dialog_speakers s JOIN dialogs d ON d.id = s.dialog_id
            JOIN timelines t ON t.dialog_id = d.id AND t.shadowed = 0
            WHERE s.object IN ({marks})
            UNION
            SELECT t.name, NULL, 'stage dummy', t.id FROM scene_actors a JOIN timelines t ON t.id = a.timeline_id AND t.shadowed = 0
            WHERE a.template IN ({marks})
            UNION
            SELECT t.name, NULL, 'actor resource', t.id FROM timeline_actors a JOIN timelines t ON t.id = a.timeline_id AND t.shadowed = 0
            WHERE a.resource IN ({marks})
            ORDER BY 1, 3
            """, three);
        var scenes = rows.GroupBy(r => (r.L("id"), r.S("timeline"))).ToList();
        Err.WriteLine($"{scenes.Count} scenes");
        for (var n = 0; n < scenes.Count; n++)
        {
            if (n >= limit)
            {
                Err.WriteLine($"... first {limit} scenes shown; raise --limit");
                break;
            }
            var parts = scenes[n].ToList();
            var roles = string.Join(", ", parts.Select(p => p.S("role")).Distinct());
            var dialog = parts.Select(p => p.S("dialog")).FirstOrDefault(d => d != null);
            var flags = "";
            if (gear)
            {
                var hit = Db.Scalar("SELECT group_concat(DISTINCT type) FROM v_tracks WHERE timeline_id = ? AND (hides > 0 OR type = 'TLShapeShift')",
                    scenes[n].Key.Item1);
                flags = hit != null ? $"   {{{hit}}}" : "";
            }
            Out.WriteLine($"{scenes[n].Key.Item2}   ({roles})" + (dialog != null ? $"   [dialog {dialog}]" : "") + flags);
        }
    }

    /// <summary>One timeline in detail.</summary>
    public void Timeline(string name)
    {
        var rows = Db.Rows("SELECT * FROM timelines WHERE name = ? ORDER BY shadowed, id", name);
        if (rows.Count == 0)
        {
            rows = Db.Rows("SELECT * FROM timelines WHERE name LIKE ? ESCAPE '\\' ORDER BY shadowed, name LIMIT 40", Q.Like(name));
            if (rows.Count != 1)
            {
                foreach (var r in rows)
                {
                    Out.WriteLine(r.S("name"));
                }
                throw new DataQueryException(rows.Count > 0 ? $"{rows.Count} timelines match; give the exact name" : "no such timeline");
            }
        }
        var t = rows[0];
        var tid = t.L("id");
        Out.WriteLine($"{t.S("name")}   {t.S("package")}:{t.S("path")}");
        Out.WriteLine($"  uuid {Q.Py(t["uuid"])}  duration {Q.Py(t["duration"])} s  phases {Q.Py(t["phases"])}  components {Q.Py(t["components"])}"
                      + ((t.L("shadowed") ?? 0) != 0 ? "  SHADOWED by a later file" : ""));
        var d = Db.One("SELECT * FROM dialogs WHERE id = ?", t["dialog_id"]);
        Out.WriteLine($"  dialog {(d != null ? d.S("name") + "  (" + Q.Py(d["uuid"]) + ", " + Q.Py(d["category"]) + ")" : "-")}");
        Out.WriteLine("  actors:");
        foreach (var a in Db.Rows("SELECT * FROM timeline_actors WHERE timeline_id = ? AND (kind IS NULL OR kind NOT IN ('camera','light'))"
                                  + " ORDER BY speaker IS NULL, speaker", tid))
        {
            Out.WriteLine($"    {a.S("uuid")}  {a.S("kind") ?? "?",-10} {ActorLabel(a.L("id"), 5)}");
        }
        Out.WriteLine("  tracks:");
        foreach (var r in Db.Rows("SELECT type, count(*) AS n, sum(keys) AS k, sum(hides) AS h, count(DISTINCT actor_id) AS a FROM v_tracks"
                                  + " WHERE timeline_id = ? GROUP BY type ORDER BY type", tid))
        {
            var h = r.L("h") ?? 0;
            Out.WriteLine($"    {r.S("type"),-34} {r["n"],5} segments {Q.Py(r["k"]),6} keys {r["a"],3} actors" + (h != 0 ? $"   hides in {h} keys" : ""));
        }
        var gear = Db.Rows("SELECT * FROM v_tracks WHERE timeline_id = ? AND type IN ('TLShowWeapon','TLShowVisual','TLShowArmor','TLShapeShift')"
                           + " AND (hides > 0 OR type = 'TLShapeShift') ORDER BY type, actor_id, start", tid);
        if (gear.Count > 0)
        {
            Out.WriteLine("  gear changes:");
        }
        foreach (var r in gear)
        {
            string what;
            if (r.S("type") == "TLShapeShift")
            {
                var keys = Db.Rows("SELECT time, attrs FROM track_keys WHERE track_id = ? ORDER BY time", r["track_id"]);
                what = string.Join("; ", keys.Select(k => $"{Q.F(k.D("time"), 2)}: {k.S("attrs")}"));
            }
            else
            {
                what = "hidden at " + FormatSpans(HideSpans(r.L("track_id")!.Value, r.D("end"))) + " s";
            }
            Out.WriteLine($"    {r.S("type"),-14} {ActorLabel(r.L("actor_id"))}: {what}");
        }
    }

    /// <summary>Scenes that have a track of the given type.</summary>
    public void Tracks(string type, bool hides, string? package, int limit)
    {
        var where = new List<string> { "type = ?" };
        var args = new List<object?> { type };
        if (hides)
        {
            where.Add("hides > 0");
        }
        if (package != null)
        {
            where.Add("package = ?");
            args.Add(package);
        }
        var rows = Db.Rows($"SELECT timeline, count(*) AS n, count(DISTINCT actor_id) AS a, sum(keys) AS k FROM v_tracks"
                           + $" WHERE {string.Join(" AND ", where)} GROUP BY timeline_id ORDER BY timeline", [.. args]);
        Err.WriteLine($"{rows.Count} scenes");
        if (rows.Count == 0)
        {
            var known = string.Join(", ", Db.Rows("SELECT name FROM track_types ORDER BY name").Select(r => r.S("name")));
            Err.WriteLine($"known track types: {known}");
        }
        foreach (var r in rows.Take(limit))
        {
            Out.WriteLine($"{r.S("timeline")}   {r["n"]} segments, {r["a"]} actors, {Q.Py(r["k"])} keys");
        }
    }

    // ------------------------------------------------------------------------------ text and dialogs

    /// <summary>Localization by text or handle; with <paramref name="where"/>, who uses each string.</summary>
    public void Text(string text, string? lang, bool where, int limit)
    {
        List<Row> rows;
        if (HandleStart().IsMatch(text))
        {
            rows = Db.Rows("SELECT lang, handle, text FROM loca WHERE handle = ? AND variant = ''", text);
        }
        else
        {
            rows = Db.Rows("SELECT lang, handle, text FROM loca WHERE variant = '' AND text LIKE ? ESCAPE '\\' AND (? IS NULL OR lang = ?) LIMIT ?",
                Q.Like(text), lang, lang, limit);
        }
        foreach (var r in rows)
        {
            Out.WriteLine($"{r.S("lang"),-8} {r.S("handle")}  {r.S("text")}");
            if (where)
            {
                foreach (var u in Db.Rows("SELECT d.name, n.constructor FROM dialog_lines l JOIN dialogs d ON d.id = l.dialog_id"
                                          + " LEFT JOIN dialog_nodes n ON n.dialog_id = l.dialog_id AND n.uuid = l.node WHERE l.handle = ? LIMIT 5", r["handle"]))
                {
                    Out.WriteLine($"         dialog {u.At(0)} ({Q.Py(u.At(1))})");
                }
                foreach (var u in Db.Rows("SELECT 'template', name FROM templates WHERE display_handle = ? UNION ALL"
                                          + " SELECT 'placed', name FROM entities WHERE display_handle = ? LIMIT 5", r["handle"], r["handle"]))
                {
                    Out.WriteLine($"         {u.At(0)} {Q.Py(u.At(1))}");
                }
            }
        }
        if (rows.Count == 0)
        {
            Err.WriteLine("nothing found");
        }
    }

    private string FlagName(Dictionary<string, string> cache, string? uuid)
    {
        uuid ??= "";
        if (!cache.TryGetValue(uuid, out var name))
        {
            name = Db.Scalar("SELECT name FROM objects WHERE key = ? AND name IS NOT NULL LIMIT 1", uuid) as string ?? uuid;
            cache[uuid] = name;
        }
        return name;
    }

    /// <summary>A dialog: speakers, and with <paramref name="tree"/> its flow with text, flag checks and flag changes.</summary>
    public void Dialog(string name, bool tree, string lang, int limit)
    {
        var inner = Q.Like(name)[1..^1];
        var rows = Db.Rows("SELECT * FROM dialogs WHERE shadowed = 0 AND (name = ? OR name LIKE ? ESCAPE '\\' OR uuid = ?)"
                           + " ORDER BY name = ? DESC, length(name) LIMIT 40", name, "%/" + inner, name, name);
        if (rows.Count == 0)
        {
            rows = Db.Rows("SELECT * FROM dialogs WHERE shadowed = 0 AND name LIKE ? ESCAPE '\\' ORDER BY length(name) LIMIT 40", Q.Like(name));
        }
        if (rows.Count != 1)
        {
            foreach (var r in rows)
            {
                Out.WriteLine(r.S("name"));
            }
            throw new DataQueryException(rows.Count > 0 ? $"{rows.Count} dialogs match; give the exact name" : "no such dialog");
        }
        var d = rows[0];
        var did = d.L("id");
        var tl = Db.Scalar("SELECT name FROM timelines WHERE dialog_id = ? AND shadowed = 0", did);
        Out.WriteLine($"{d.S("name")}   ({Q.Py(d["uuid"])}, {Q.Py(d["category"])})   {d.S("package")}:{d.S("path")}");
        Out.WriteLine($"  timeline: {tl ?? "-"}   nodes: {Q.Py(d["nodes"])}");
        Out.WriteLine("  speakers:");
        foreach (var idx in Db.Rows("SELECT DISTINCT idx FROM dialog_speakers WHERE dialog_id = ? ORDER BY idx", did).Select(r => r.At(0)))
        {
            var names = Db.Rows("SELECT coalesce(e.name, g.name, t.name, s.object) FROM dialog_speakers s LEFT JOIN entities e ON e.uuid = s.object"
                                + " LEFT JOIN speaker_groups g ON g.uuid = s.object LEFT JOIN templates t ON t.uuid = s.object"
                                + " WHERE s.dialog_id = ? AND s.idx = ?", did, idx).Select(r => Q.Py(r.At(0))).ToList();
            Out.WriteLine($"    {idx}: " + string.Join(", ", names.Take(6)) + (names.Count > 6 ? $" +{names.Count - 6}" : ""));
        }
        if (!tree)
        {
            var counts = Db.Rows("SELECT constructor, count(*) FROM dialog_nodes WHERE dialog_id = ? GROUP BY 1 ORDER BY 2 DESC", did);
            Out.WriteLine("  nodes by kind: " + string.Join(", ", counts.Select(c => $"{Q.Py(c.At(0))} {c.At(1)}")) + "   (--tree for the flow)");
            return;
        }
        var nodes = new Dictionary<string, Row>(StringComparer.Ordinal);
        var nodeOrder = new List<string>();
        foreach (var r in Db.Rows("SELECT * FROM dialog_nodes WHERE dialog_id = ?", did))
        {
            var u = r.S("uuid") ?? "";
            if (nodes.TryAdd(u, r))
            {
                nodeOrder.Add(u);
            }
            else
            {
                nodes[u] = r;
            }
        }
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in Db.Rows("SELECT node, idx, child FROM dialog_links WHERE dialog_id = ? ORDER BY node, idx", did))
        {
            var key = r.S("node") ?? "";
            if (!children.TryGetValue(key, out var list))
            {
                children[key] = list = [];
            }
            list.Add(r.S("child") ?? "");
        }
        var attrs = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var r in Db.Rows("SELECT node, name, value FROM dialog_node_attrs WHERE dialog_id = ?", did))
        {
            var key = r.S("node") ?? "";
            if (!attrs.TryGetValue(key, out var a))
            {
                attrs[key] = a = [];
            }
            a[r.S("name") ?? ""] = r.S("value");
        }
        Dictionary<string, string?> AttrsOf(string u) => attrs.TryGetValue(u, out var a) ? a : [];
        var flags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in Db.Rows("SELECT node, kind, flag_type, flag, value, target FROM dialog_flags WHERE dialog_id = ?", did))
        {
            var key = r.S("node") ?? "";
            if (!flags.TryGetValue(key, out var list))
            {
                flags[key] = list = [];
            }
            var ftype = r.S("flag_type");
            list.Add($"{(r.S("kind") == "check" ? "if" : "set")} {((r.L("value") ?? 0) != 0 ? "" : "not ")}{FlagName(cache, r.S("flag"))}"
                     + (!string.IsNullOrEmpty(ftype) && ftype != "Global" ? $" ({ftype})" : ""));
        }
        var texts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in Db.Rows("SELECT l.node, coalesce(t.text, l.handle), (SELECT max(v.length) FROM voice_lines v WHERE v.handle = l.handle)"
                                  + " FROM dialog_lines l LEFT JOIN loca t ON t.handle = l.handle AND t.lang = ? AND t.variant = ''"
                                  + " WHERE l.dialog_id = ?", lang, did))
        {
            var key = Q.Py(r.At(0));
            if (!texts.TryGetValue(key, out var list))
            {
                texts[key] = list = [];
            }
            var text = Q.Py(r.At(1));
            list.Add(r.At(2) is null ? text : $"{text}  [voice {Q.F(Convert.ToDouble(r.At(2), System.Globalization.CultureInfo.InvariantCulture), 1)} s]");
        }
        var phases = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var r in Db.Rows("SELECT p.dialog_node, p.idx FROM timeline_phases p JOIN timelines t ON t.id = p.timeline_id"
                                  + " WHERE t.dialog_id = ? AND t.shadowed = 0", did))
        {
            phases[r.S("dialog_node") ?? ""] = r.At(1);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var printed = 0;
        string[] showFlags = ["endnode", "Root"];
        string[] showValues = ["jumptarget", "SourceNode", "Ability", "Skill", "DifficultyClassID", "RollType", "NestedDialogNodeUUID"];

        void Walk(string uuid, int depth)
        {
            if (printed >= limit)
            {
                return;
            }
            var indent = "  " + new string(' ', 2 * depth);
            if (!nodes.TryGetValue(uuid, out var n))
            {
                Out.WriteLine($"{indent}-> {uuid} (not in this dialog)");
                return;
            }
            var a = AttrsOf(uuid);
            var head = n.S("constructor") ?? "?";
            if (n.L("speaker") is { } sp && sp >= 0)
            {
                head += $" [{sp}]";
            }
            var extra = showFlags.Where(k => a.Get(k) == "True").ToList();
            extra.AddRange(showValues.Where(k => !string.IsNullOrEmpty(a.Get(k))).Select(k => $"{k}={a[k]}"));
            if (phases.TryGetValue(uuid, out var phase))
            {
                extra.Add($"phase {phase}");
            }
            var line = $"{indent}{head}" + (extra.Count > 0 ? $"  ({string.Join(", ", extra)})" : "");
            if (!seen.Add(uuid))
            {
                Out.WriteLine(line + "  ... (shown above)");
                return;
            }
            printed++;
            Out.WriteLine(line);
            foreach (var text in (texts.TryGetValue(uuid, out var tl) ? tl : []).Take(3))
            {
                Out.WriteLine($"{indent}    \"{Q.Cut(text, 170)}\"");
            }
            foreach (var f in flags.TryGetValue(uuid, out var fl) ? fl : [])
            {
                Out.WriteLine($"{indent}    {f}");
            }
            foreach (var child in children.TryGetValue(uuid, out var ch) ? ch : [])
            {
                Walk(child, depth + 1);
            }
        }

        var allChildren = new HashSet<string>(children.Values.SelectMany(c => c), StringComparer.Ordinal);
        var roots = nodeOrder.Where(u => AttrsOf(u).Get("Root") == "True").ToList();
        if (roots.Count == 0)
        {
            roots = nodeOrder.Where(u => !allChildren.Contains(u)).ToList();
        }
        var thread = new Thread(() =>
        {
            foreach (var root in roots)
            {
                Walk(root, 0);
            }
        }, 64 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (printed >= limit)
        {
            Err.WriteLine($"  ... {limit} nodes shown; raise --limit");
        }
    }

    // ------------------------------------------------------------------------------ chain

    [GeneratedRegex(@"^[\w \-./\\\[\]()]+\.(gr2|dds|lsf|lsx|lsfx|bnk|wem|png|ttf|xaml|loca|bk2|gts|gtp|anc|khn|txt)$", RegexOptions.IgnoreCase)]
    internal static partial Regex FileValue();

    private static readonly HashSet<string> SkipAttrs = new(StringComparer.Ordinal) { "MapKey", "UUID", "ID", "Name", "LevelName", "_OriginalFileVersion_", "Type" };
    private const string ZeroUuid = "00000000-0000-0000-0000-000000000000";

    /// <summary>Follows references from a stats entry, a template or any object down to resources and files.</summary>
    public void Chain(string start, int depthLimit, int fan, int limit)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var budget = limit * 6;

        static string Describe(Row o) => $"{o.S("node")} {o.S("name") ?? ""} {o.S("key") ?? ""}".Replace("  ", " ", StringComparison.Ordinal).Trim();

        bool Walk(string key, int depth, string via)
        {
            var indent = new string(' ', 2 * depth);
            var found = Db.Rows("SELECT id, node, key, name, package, path FROM v_objects WHERE key = ?", key);
            found.Reverse();             // the same key defined by several mods: the last one read wins, show that one first
            if (found.Count == 0)
            {
                return false;
            }
            var o = found[0];
            var path = o.S("path") ?? "";
            var parts = path.Split('/');
            var folder = path.Contains('/', StringComparison.Ordinal) ? parts[^2] : "";
            Out.WriteLine($"{indent}{(via.Length > 0 ? via + " -> " : "")}{Describe(o)}   [{o.S("package")}:{folder}/{parts[^1]}]"
                          + (found.Count > 1 ? $"   (+{found.Count - 1} earlier definitions)" : ""));
            if (!seen.Add(key))
            {
                Out.WriteLine($"{indent}  ... (shown above)");
                return true;
            }
            if (depth >= depthLimit || budget <= 0)
            {
                return true;
            }
            var attrs = Db.Rows("SELECT attr, value FROM v_attrs WHERE object_id = ?", o["id"]);
            var shown = 0;
            var done = new HashSet<(string, string)>();
            foreach (var r in attrs)
            {
                var attr = r.S("attr") ?? "";
                var value = r.S("value");
                var leaf = attr[(attr.LastIndexOf('.') + 1)..];
                if (string.IsNullOrEmpty(value) || (SkipAttrs.Contains(leaf) && !attr.Contains('.', StringComparison.Ordinal)))
                {
                    continue;
                }
                var pieces = Q.IsUuid(value.Split(';')[0]) ? value.Split(';') : [value];
                foreach (var part in pieces)
                {
                    if (budget <= 0 || shown >= fan)
                    {
                        break;
                    }
                    if (!done.Add((attr, part)))
                    {
                        continue;
                    }
                    if (Q.IsUuid(part) && part != key && part != ZeroUuid)
                    {
                        budget--;
                        if (Walk(part.ToLowerInvariant(), depth + 1, attr))
                        {
                            shown++;
                        }
                    }
                    else if (FileValue().IsMatch(part))
                    {
                        var p = part.Replace('\\', '/');
                        var hit = Db.One("SELECT package, size FROM files WHERE path = ? OR path = ? LIMIT 1", p, "Generated/" + p);
                        Out.WriteLine($"{indent}  {attr} -> file {part}" + (hit != null ? $"   [{hit.S("package")}, {Q.Thousands(hit.L("size"))} bytes]" : "   [not in a package under this path]"));
                        shown++;
                    }
                    else if (leaf is "Stats" or "ParentTemplateId" or "TemplateName" or "Icon")
                    {
                        var st = Db.One("SELECT name, type FROM stats WHERE name = ? LIMIT 1", part);
                        if (st != null)
                        {
                            Out.WriteLine($"{indent}  {attr} -> stat {st.S("name")} ({Q.Py(st["type"])})");
                            shown++;
                        }
                    }
                }
            }
            return true;
        }

        var stat = Db.One("SELECT * FROM stats WHERE name = ? ORDER BY id DESC LIMIT 1", start);
        if (stat != null)
        {
            var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var names = new List<string>();
            var entry = stat;
            for (var depth = 0; entry != null && depth < 20; depth++)
            {
                names.Add(entry.S("name") ?? "");
                foreach (var f in Db.Rows("SELECT field, value FROM stat_fields WHERE stat_id = ?", entry["id"]))
                {
                    fields.TryAdd(f.S("field") ?? "", f.S("value") ?? "");
                }
                entry = entry.S("parent") is { Length: > 0 } parent ? Db.One("SELECT * FROM stats WHERE name = ? ORDER BY id DESC LIMIT 1", parent) : null;
            }
            Out.WriteLine($"stat {string.Join(" : ", names)}   ({Q.Py(stat["type"])})");
            foreach (var (field, value) in fields)
            {
                foreach (var part in value.Split(';'))
                {
                    if (Q.IsUuid(part))
                    {
                        Walk(part.ToLowerInvariant(), 1, field);
                    }
                }
            }
            return;
        }
        if (Q.IsUuid(start))
        {
            if (!Walk(start.ToLowerInvariant(), 0, ""))
            {
                throw new DataQueryException("no object has that uuid");
            }
            return;
        }
        var rows = Db.Rows("SELECT key FROM v_objects WHERE name = ? AND key IS NOT NULL ORDER BY node = 'GameObjects' DESC LIMIT 5", start);
        if (rows.Count == 0)
        {
            throw new DataQueryException($"no stats entry or object named '{start}' (try: gatekit data find {start})");
        }
        foreach (var r in rows)
        {
            Walk(r.S("key")!, 0, "");
        }
    }

    // ------------------------------------------------------------------------------ voice

    private string SpeakerName(string? uuid)
    {
        var row = Db.Scalar("SELECT name FROM entities WHERE uuid = ? UNION ALL SELECT name FROM speaker_groups WHERE uuid = ?"
                            + " UNION ALL SELECT name FROM templates WHERE uuid = ? LIMIT 1", uuid, uuid, uuid);
        return row as string ?? Q.Py(uuid);
    }

    /// <summary>Recorded voice lines: by text, by handle, by speaker or by dialog.</summary>
    public void Voice(string text, bool dialog, bool speaker, string lang, int limit)
    {
        string where;
        var args = new List<object?>();
        if (HandleStart().IsMatch(text))
        {
            where = "v.handle = ?";
            args.Add(text);
        }
        else if (Q.IsUuid(text))
        {
            where = "v.speaker = ?";
            args.Add(text.ToLowerInvariant());
        }
        else if (dialog)
        {
            var d = Db.Scalar("SELECT id FROM dialogs WHERE shadowed = 0 AND (name = ? OR name LIKE ? ESCAPE '\\') ORDER BY length(name) LIMIT 1",
                text, "%/" + Q.Like(text)[1..^1]) ?? throw new DataQueryException("no such dialog");
            where = "v.handle IN (SELECT handle FROM dialog_lines WHERE dialog_id = ?)";
            args.Add(d);
        }
        else if (speaker)
        {
            var ids = ObjectIds(text);
            where = $"v.speaker IN ({Q.Marks(ids.Count)})";
            args.AddRange(ids);
        }
        else
        {
            where = "v.handle IN (SELECT handle FROM loca WHERE lang = ? AND variant = '' AND text LIKE ? ESCAPE '\\')";
            args.Add(lang);
            args.Add(Q.Like(text));
        }
        var all = new List<object?> { lang };
        all.AddRange(args);
        all.Add(limit);
        var rows = Db.Rows("SELECT v.speaker, v.handle, v.file, v.length, v.priority, t.text, f.package, f.size FROM voice_lines v"
                           + " LEFT JOIN loca t ON t.handle = v.handle AND t.lang = ? AND t.variant = ''"
                           + $" LEFT JOIN files f ON f.path = v.file WHERE {where} ORDER BY v.speaker, v.handle LIMIT ?", [.. all]);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var key = r.S("speaker") ?? "";
            if (!names.TryGetValue(key, out var who))
            {
                names[key] = who = SpeakerName(r.S("speaker"));
            }
            var place = r.S("package") != null ? $"{r.S("package")}:{r.S("file")} ({Q.Thousands(r.L("size"))} bytes)" : $"{Q.Py(r["file"])} (not in a package)";
            Out.WriteLine($"{who}  {Q.F(r.D("length") ?? 0, 2)} s  {r.S("priority") ?? ""}  {r.S("handle")}");
            Out.WriteLine($"    \"{Q.Cut(r.S("text") ?? "", 160)}\"");
            Out.WriteLine($"    {place}");
        }
        if (rows.Count == 0)
        {
            Err.WriteLine("nothing found");
        }
        else if (rows.Count == limit)
        {
            Err.WriteLine($"... first {limit} shown; raise --limit or narrow the search");
        }
    }

    // ------------------------------------------------------------------------------ files, objects, refs

    /// <summary>Search the catalog of every file in every package.</summary>
    public void Files(IReadOnlyList<string> text, string? ext, string? package, bool summary, int limit)
    {
        var where = new List<string>();
        var args = new List<object?>();
        foreach (var part in text)
        {
            where.Add("path LIKE ? ESCAPE '\\'");
            args.Add(Q.Like(part));
        }
        if (ext != null)
        {
            where.Add("ext = ?");
            args.Add(ext.ToLowerInvariant().TrimStart('.'));
        }
        if (package != null)
        {
            where.Add("package = ?");
            args.Add(package);
        }
        var cond = where.Count > 0 ? string.Join(" AND ", where) : "1";
        if (summary)
        {
            foreach (var r in Db.Rows($"SELECT ext, count(*), sum(size) FROM files WHERE {cond} GROUP BY ext ORDER BY 3 DESC", [.. args]))
            {
                var size = Convert.ToDouble(r.At(2) ?? 0L, System.Globalization.CultureInfo.InvariantCulture) / 1e6;
                Out.WriteLine($"{r.At(1),9} {Q.F(size, 1),12} MB  .{r.At(0)}");
            }
            return;
        }
        var total = Convert.ToInt64(Db.Scalar($"SELECT count(*) FROM files WHERE {cond}", [.. args]), System.Globalization.CultureInfo.InvariantCulture);
        foreach (var r in Db.Rows($"SELECT package, path, size FROM files WHERE {cond} ORDER BY path LIMIT ?", [.. args, limit]))
        {
            Out.WriteLine($"{r["size"],12}  {r.S("package")}:{r.S("path")}");
        }
        Err.WriteLine($"{total} files" + (total > limit ? $", first {limit} shown" : ""));
    }

    private void ShowObject(object? objectId, bool full)
    {
        var o = Db.One("SELECT * FROM v_objects WHERE id = ?", objectId)!;
        Out.WriteLine($"{o.S("node")}  {o.S("key") ?? ""}  {o.S("name") ?? ""}   [{o.S("package")}:{o.S("path")}]");
        var rows = Db.Rows("SELECT attr, value FROM v_attrs WHERE object_id = ?", objectId);
        for (var n = 0; n < rows.Count; n++)
        {
            if (!full && n >= 60)
            {
                Out.WriteLine($"    ... {rows.Count - n} more attributes (--full)");
                break;
            }
            var value = rows[n].S("value");
            var note = "";
            if (Q.IsUuid(value ?? ""))
            {
                var hit = Db.One("SELECT node, name FROM v_objects WHERE key = ? AND id <> ? LIMIT 1", value, objectId);
                if (hit != null && (!string.IsNullOrEmpty(hit.S("name")) || !string.IsNullOrEmpty(hit.S("node"))))
                {
                    note = $"   -> {hit.S("node")} {hit.S("name") ?? ""}";
                }
            }
            else if (value != null && value.StartsWith('h') && value.Length > 30 && value[..12].Contains('g', StringComparison.Ordinal))
            {
                var text = Db.Scalar("SELECT text FROM loca WHERE handle = ? AND variant = '' ORDER BY lang = 'English' DESC LIMIT 1", value.Split(';')[0]);
                if (text is string s)
                {
                    note = $"   \"{Q.Cut(s, 80)}\"";
                }
            }
            Out.WriteLine($"    {rows[n].S("attr")} = {Q.Py(value)}{note}");
        }
    }

    /// <summary>Objects of any kind by name or key.</summary>
    public void Find(IReadOnlyList<string> text, string? node, string? package, bool show, bool full, int limit)
    {
        var where = new List<string>();
        var args = new List<object?>();
        if (text.Count == 1 && Q.IsUuid(text[0]))
        {
            where.Add("key = ?");
            args.Add(text[0].ToLowerInvariant());
        }
        else
        {
            foreach (var part in text)
            {
                where.Add("(name LIKE ? ESCAPE '\\' OR key LIKE ? ESCAPE '\\')");
                args.Add(Q.Like(part));
                args.Add(Q.Like(part));
            }
        }
        if (node != null)
        {
            where.Add("node = ?");
            args.Add(node);
        }
        if (package != null)
        {
            where.Add("package = ?");
            args.Add(package);
        }
        var rows = Db.Rows($"SELECT * FROM v_objects WHERE {string.Join(" AND ", where)} ORDER BY node, name LIMIT ?", [.. args, limit]);
        if (show || rows.Count == 1)
        {
            foreach (var r in rows)
            {
                ShowObject(r["id"], full);
            }
        }
        else
        {
            foreach (var r in rows)
            {
                Out.WriteLine($"{r.S("node"),-24} {r.S("key") ?? "",-38} {r.S("name") ?? ""}   [{r.S("package")}:{r.S("path")}]");
            }
        }
        if (rows.Count == 0)
        {
            Err.WriteLine("nothing found");
        }
        else if (rows.Count == limit)
        {
            Err.WriteLine($"... first {limit} shown; raise --limit or narrow the search");
        }
    }

    /// <summary>Everything that mentions a uuid, a name or any other exact value.</summary>
    public void Refs(string value, int limit)
    {
        var rows = Db.Rows("SELECT o.id, o.node, o.key, o.name, o.package, o.path, n.name AS attr FROM attrs a"
                           + " JOIN names n ON n.id = a.attr_id JOIN v_objects o ON o.id = a.object_id"
                           + " WHERE a.value = ? AND length(a.value) BETWEEN 4 AND 80 ORDER BY o.node, o.name LIMIT ?", value, limit);
        foreach (var r in rows)
        {
            Out.WriteLine($"{r.S("node"),-22} {r.S("key") ?? "",-38} {r.S("name") ?? "",-40} .{r.S("attr")}   [{r.S("package")}:{r.S("path")}]");
        }
        if (Q.IsUuid(value))
        {
            foreach (var r in Db.Rows("SELECT d.name, s.idx FROM dialog_speakers s JOIN dialogs d ON d.id = s.dialog_id WHERE s.object = ? LIMIT ?", value, limit))
            {
                Out.WriteLine($"{"dialog speaker",-22} {r.At(0)}  (speaker {r.At(1)})");
            }
            foreach (var r in Db.Rows("SELECT t.name FROM track_keys k JOIN tracks x ON x.id = k.track_id JOIN timelines t ON t.id = x.timeline_id"
                                      + " WHERE k.value = ? GROUP BY t.id LIMIT ?", value, limit))
            {
                Out.WriteLine($"{"timeline key",-22} {r.At(0)}");
            }
            foreach (var r in Db.Rows("SELECT t.name FROM scene_actors a JOIN timelines t ON t.id = a.timeline_id WHERE a.template = ? LIMIT ?", value, limit))
            {
                Out.WriteLine($"{"scene stand-in",-22} {r.At(0)}");
            }
        }
        foreach (var r in Db.Rows("SELECT s.name, f.field FROM stat_fields f JOIN stats s ON s.id = f.stat_id WHERE f.value = ? LIMIT ?", value, limit))
        {
            Out.WriteLine($"{"stat",-22} {r.At(0)}.{r.At(1)}");
        }
        List<Row> hits;
        try
        {
            hits = Db.Rows("SELECT t.kind, t.path FROM texts_fts f JOIN texts t ON t.id = f.rowid WHERE texts_fts MATCH ? LIMIT ?",
                "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"", limit);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            hits = [];
        }
        foreach (var r in hits)
        {
            Out.WriteLine($"{r.S("kind"),-22} {r.At(1)}");
        }
        if (rows.Count == limit)
        {
            Err.WriteLine($"... first {limit} objects shown; raise --limit");
        }
    }

    /// <summary>A stats entry with the fields it inherits.</summary>
    public void Stat(string name, int limit)
    {
        var rows = Db.Rows("SELECT * FROM stats WHERE name = ? ORDER BY id DESC", name);
        if (rows.Count == 0)
        {
            rows = Db.Rows("SELECT * FROM stats WHERE name LIKE ? ESCAPE '\\' GROUP BY name ORDER BY name LIMIT ?", Q.Like(name), limit);
            if (rows.Count != 1)
            {
                foreach (var r in rows)
                {
                    Out.WriteLine($"{r.S("type") ?? "",-18} {r.S("name")}" + (!string.IsNullOrEmpty(r.S("parent")) ? $"   using {r.S("parent")}" : ""));
                }
                if (rows.Count == 0)
                {
                    Err.WriteLine("nothing found");
                }
                return;
            }
        }
        var seen = new Dictionary<string, (string? Value, string? Origin, int Depth)>(StringComparer.Ordinal);
        var entry = rows[0];
        for (var depth = 0; entry != null && depth < 20; depth++)
        {
            Out.WriteLine($"{new string(' ', 2 * depth)}{entry.S("name")}  ({Q.Py(entry["type"])})   [{entry.S("package")}:{entry.S("file")}]");
            foreach (var f in Db.Rows("SELECT field, value FROM stat_fields WHERE stat_id = ?", entry["id"]))
            {
                seen.TryAdd(f.S("field") ?? "", (f.S("value"), entry.S("name"), depth));
            }
            entry = entry.S("parent") is { Length: > 0 } parent ? Db.One("SELECT * FROM stats WHERE name = ? ORDER BY id DESC", parent) : null;
        }
        foreach (var (field, (value, origin, d)) in seen.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Out.WriteLine($"    {field} = {value}" + (d != 0 ? $"      (from {origin})" : ""));
        }
    }

    /// <summary>Osiris functions the engine offers, and the story goals that use a name.</summary>
    public void Osiris(string name, string? kind, bool uses, int limit)
    {
        var where = new List<string> { "name LIKE ? ESCAPE '\\'" };
        var args = new List<object?> { Q.Like(name) };
        if (kind != null)
        {
            where.Add("kind = ?");
            args.Add(kind);
        }
        var rows = Db.Rows($"SELECT * FROM osiris_functions WHERE {string.Join(" AND ", where)} ORDER BY kind, name LIMIT ?", [.. args, limit]);
        foreach (var r in rows)
        {
            Out.WriteLine($"{r.S("kind"),-8} {r.S("name")}({r.S("params")})");
        }
        if (uses || rows.Count == 0)
        {
            var exact = Db.One("SELECT 1 FROM osiris_uses WHERE name = ? LIMIT 1", name) != null;
            var (cond, value) = exact ? ("u.name = ?", name) : ("u.name LIKE ? ESCAPE '\\'", Q.Like(name));
            var found = Db.Rows($"SELECT u.name, u.role, sum(u.uses) AS n, t.path FROM osiris_uses u JOIN texts t ON t.id = u.text_id"
                                + $" WHERE {cond} GROUP BY u.name, u.role, t.id ORDER BY u.name, u.role, t.path LIMIT ?", value, limit);
            foreach (var r in found)
            {
                var path = r.S("path") ?? "";
                Out.WriteLine($"    {r.S("name")}  {r.S("role"),-9} x{r["n"],-4} {path[(path.LastIndexOf('/') + 1)..]}");
            }
            if (found.Count == 0 && rows.Count == 0)
            {
                Err.WriteLine("nothing found");
            }
        }
    }

    /// <summary>Full-text search over story goals, stats, scripts and interface files.</summary>
    public void Grep(string text, string? kind, bool raw, int limit)
    {
        var query = raw ? text : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => "\"" + w.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
        var where = new List<string> { "texts_fts MATCH ?" };
        var args = new List<object?> { query };
        if (kind != null)
        {
            where.Add("t.kind = ?");
            args.Add(kind);
        }
        var rows = Db.Rows("SELECT t.kind, t.package, t.path, snippet(texts_fts, 0, '>>', '<<', ' ... ', 16) AS hit"
                           + $" FROM texts_fts JOIN texts t ON t.id = texts_fts.rowid WHERE {string.Join(" AND ", where)} LIMIT ?", [.. args, limit]);
        foreach (var r in rows)
        {
            Out.WriteLine($"{r.S("package")}:{r.S("path")}  ({r.S("kind")})");
            Out.WriteLine("    " + string.Join(" ", (r.S("hit") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }
        if (rows.Count == 0)
        {
            Err.WriteLine("nothing found");
        }
    }

    /// <summary>Any read-only SQL; tab-separated with a header.</summary>
    public void Sql(string query)
    {
        var (columns, rows, _) = Db.Run(query, int.MaxValue);
        if (columns.Length > 0)
        {
            Out.WriteLine(string.Join("\t", columns));
        }
        foreach (var r in rows)
        {
            Out.WriteLine(string.Join("\t", r.Select(v => v == null ? "" : Q.Py(v))));
        }
    }
}
