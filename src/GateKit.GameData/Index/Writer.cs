using GateKit.GameData.Resources;
using GateKit.GameData.Text;
using LSLib.LS;
using Microsoft.Data.Sqlite;

namespace GateKit.GameData.Index;

/// <summary>Writes the results of the readers into the tables, in load order.</summary>
internal sealed class Writer(SqliteConnection db) : IDisposable
{
    private readonly Dictionary<string, Insert> _inserts = [];
    private readonly Dictionary<string, long> _typeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _nameIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Id, string? DialogResource, string? Name)> _bank = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(int ActorType, string? Template)>> _scenes = new(StringComparer.Ordinal);
    private readonly List<(long Id, string Rel)> _timelines = [];
    private long _objectId, _sourceId, _textId, _statId, _trackId, _actorId, _timelineId, _dialogId;

    public long NestedComponents { get; private set; }

    private static readonly (string Name, string Sql)[] Statements =
    [
        ("problem", "INSERT INTO problems VALUES (?,?)"),
        ("track_type", "INSERT INTO track_types VALUES (?,?)"),
        ("dialog", "INSERT INTO dialogs (id, uuid, name, package, mod, path, category, timeline_uuid, nodes) VALUES (?,?,?,?,?,?,?,?,?)"),
        ("dialog_speaker", "INSERT INTO dialog_speakers VALUES (?,?,?,?)"),
        ("dialog_node", "INSERT INTO dialog_nodes VALUES (?,?,?,?)"),
        ("dialog_line", "INSERT INTO dialog_lines VALUES (?,?,?,?)"),
        ("dialog_link", "INSERT INTO dialog_links VALUES (?,?,?,?)"),
        ("dialog_flag", "INSERT INTO dialog_flags VALUES (?,?,?,?,?,?,?)"),
        ("dialog_node_attr", "INSERT INTO dialog_node_attrs VALUES (?,?,?,?)"),
        ("source", "INSERT INTO sources VALUES (?,?,?,?,?,?)"),
        ("object", "INSERT INTO objects VALUES (?,?,?,?,?,?)"),
        ("attr", "INSERT INTO attrs VALUES (?,?,?)"),
        ("text", "INSERT INTO texts VALUES (?,?,?,?,?,?)"),
        ("fts", "INSERT INTO texts_fts (rowid, content) VALUES (?,?)"),
        ("stat", "INSERT INTO stats VALUES (?,?,?,?,?,?,?)"),
        ("stat_field", "INSERT INTO stat_fields VALUES (?,?,?)"),
        ("osiris_use", "INSERT INTO osiris_uses VALUES (?,?,?,?)"),
        ("osiris_function", "INSERT INTO osiris_functions VALUES (?,?,?,?,?)"),
        ("osiris_type", "INSERT INTO osiris_types VALUES (?,?,?)"),
        ("voice", "INSERT INTO voice_lines VALUES (?,?,?,?,?,?,?)"),
        ("timeline", "INSERT INTO timelines (id, name, package, mod, path, duration, phases, components) VALUES (?,?,?,?,?,?,?,?)"),
        ("phase", "INSERT INTO timeline_phases VALUES (?,?,?,?)"),
        ("actor", "INSERT INTO timeline_actors (id, timeline_id, uuid, speaker, kind, is_player, resource, scene_actor_type, scene_actor_index) VALUES (?,?,?,?,?,?,?,?,?)"),
        ("track", "INSERT INTO tracks VALUES (?,?,?,?,?,?,?,?,?,?,?)"),
        ("key", "INSERT INTO track_keys VALUES (?,?,?,?,?,?)"),
        ("template", "INSERT OR REPLACE INTO templates VALUES (?,?,?,?,?,?,?,?,?,?)"),
        ("entity", "INSERT OR REPLACE INTO entities VALUES (?,?,?,?,?,?,?,?,?,?)"),
        ("speaker_group", "INSERT OR REPLACE INTO speaker_groups VALUES (?,?,?)"),
        ("timeline_bank", "UPDATE timelines SET uuid=?, dialog_resource=? WHERE id=?"),
        ("scene_actor", "INSERT INTO scene_actors VALUES (?,?,?,?,?)"),
        ("has_scene", "UPDATE timelines SET has_scene=1 WHERE id=?"),
        ("name", "INSERT INTO names VALUES (?,?)"),
        ("file", "INSERT INTO files (package, path, ext, size) VALUES (?,?,?,?)"),
        ("loca", "INSERT OR REPLACE INTO loca VALUES (?,?,?,?,?)"),
    ];

    public void Prepare(SqliteTransaction tx)
    {
        foreach (var (name, sql) in Statements)
        {
            _inserts[name] = new Insert(db, tx, Sql.Positional(sql), sql.Count(c => c == '?'));
        }
    }

    private Insert this[string name] => _inserts[name];

    public void AddProblem(string path, string problem) => this["problem"].Run(path, problem);

    private long TypeId(string name)
    {
        if (!_typeIds.TryGetValue(name, out var id))
        {
            id = _typeIds.Count + 1;
            _typeIds[name] = id;
            this["track_type"].Run(id, name);
        }
        return id;
    }

    private long NameId(string name)
    {
        if (!_nameIds.TryGetValue(name, out var id))
        {
            id = _nameIds.Count + 1;
            _nameIds[name] = id;
        }
        return id;
    }

    public void Add(string kind, string package, string rel, object? data, (string? Region, List<GenericObject> Objects)? generic,
        string? problem, Action<string, long> count)
    {
        if (problem != null)
        {
            AddProblem($"{package}/{rel}", problem);
        }
        if (generic is { } g)
        {
            AddGeneric(package, rel, g.Region, g.Objects, count);
        }
        if (data == null)
        {
            if (kind == "generic" && generic != null)
            {
                count(kind, 1);
            }
            return;
        }
        count(kind, 1);
        var mod = FileKinds.ModOf(rel);
        var slash = rel.LastIndexOf('/');
        var stem = rel[(slash + 1)..^4];
        switch (kind)
        {
            case "text":
                var (size, content) = ((long, string))data;
                AddText(package, rel, size, content);
                break;
            case "voice":
                var folder = slash >= 0 ? rel[..slash] : rel;
                var language = ("/" + rel).Contains("/Localization/", StringComparison.Ordinal)
                    ? Split1(rel, "/Localization/").Split('/', 2)[0] : "";
                foreach (var v in (List<Dictionary<string, string?>>)data)
                {
                    var source = v.Get("Source");
                    this["voice"].Run(v.Get("speaker"), v.Get("handle"), string.IsNullOrEmpty(source) ? null : folder + "/" + source,
                        Values.Num(v.Get("Length")), v.Get("Priority"), v.Get("Codec"), language);
                }
                break;
            case "timeline":
                AddTimeline(package, mod, rel, stem, (TimelineData)data);
                break;
            case "scene":
                _scenes[rel[..^"_Scene.lsx".Length].ToLowerInvariant()] = (List<(int, string?)>)data;
                break;
            case "bank":
                foreach (var b in (List<Dictionary<string, string?>>)data)
                {
                    var file = b.Get("SourceFile");
                    if (!string.IsNullOrEmpty(file))
                    {
                        _bank[file.ToLowerInvariant()] = (b.Get("ID"), b.Get("DialogResourceId"), b.Get("Name"));
                    }
                }
                break;
            case "dialog":
                AddDialog(package, rel, (DialogData)data);
                break;
            case "templates":
                foreach (var t in (List<Dictionary<string, string?>>)data)
                {
                    if (!string.IsNullOrEmpty(t.Get("MapKey")))
                    {
                        this["template"].Run(t.Get("MapKey"), t.Get("Name"), t.Get("Type"), Or(t.Get("ParentTemplateId")), t.Get("DisplayName"),
                            t.Get("Stats"), Or(t.Get("VisualTemplate")) ?? t.Get("CharacterVisualResourceID"), t.Get("Icon"), package, mod);
                    }
                }
                break;
            case "entities":
                var parts = rel.Split('/');
                foreach (var e in (List<Dictionary<string, string?>>)data)
                {
                    if (!string.IsNullOrEmpty(e.Get("MapKey")))
                    {
                        this["entity"].Run(e.Get("MapKey"), e.Get("Name"), e.Get("Type"), Or(e.Get("TemplateName")), e.Get("DisplayName"),
                            e.Get("Stats"), Or(e.Get("LevelName")) ?? parts[3], parts[2], package, mod);
                    }
                }
                break;
            case "speakergroups":
                foreach (var s in (List<Dictionary<string, string?>>)data)
                {
                    if (!string.IsNullOrEmpty(s.Get("UUID")))
                    {
                        this["speaker_group"].Run(s.Get("UUID"), s.Get("Name"), s.Get("Description"));
                    }
                }
                break;
        }
    }

    /// <summary>Python's <c>text.split(sep, 1)[-1]</c>.</summary>
    private static string Split1(string text, string separator)
    {
        var i = text.IndexOf(separator, StringComparison.Ordinal);
        return i < 0 ? text : text[(i + separator.Length)..];
    }

    private static string? Or(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private void AddGeneric(string package, string rel, string? region, List<GenericObject> objects, Action<string, long> count)
    {
        if (objects.Count == 0)
        {
            return;
        }
        _sourceId++;
        this["source"].Run(_sourceId, package, FileKinds.ModOf(rel), rel, region, objects.Count);
        var objectInsert = this["object"];
        var attrInsert = this["attr"];
        foreach (var o in objects)
        {
            _objectId++;
            objectInsert.Run(_objectId, _sourceId, NameId(o.Node), o.Key, o.Name, o.Truncated ? 1 : null);
            foreach (var (attr, value) in o.Attrs)
            {
                attrInsert.Run(_objectId, NameId(attr), value);
            }
        }
        count("objects", objects.Count);
    }

    private void AddText(string package, string rel, long size, string content)
    {
        var kind = FileKinds.TextKind(rel);
        _textId++;
        this["text"].Run(_textId, package, FileKinds.ModOf(rel), rel, kind, size);
        this["fts"].Run(_textId, content);
        switch (kind)
        {
            case "stats":
                foreach (var s in TextParsers.ReadStats(content))
                {
                    _statId++;
                    this["stat"].Run(_statId, s.Name, s.Type, s.Parent, package, FileKinds.ModOf(rel), rel);
                    foreach (var (f, v) in s.Fields)
                    {
                        this["stat_field"].Run(_statId, f, v);
                    }
                }
                break;
            case "goal":
                foreach (var ((name, role), n) in TextParsers.ReadGoal(content))
                {
                    this["osiris_use"].Run(_textId, name, role, n);
                }
                break;
            case "osiris header":
                var (functions, types) = TextParsers.ReadOsirisHeader(content);
                foreach (var f in functions)
                {
                    this["osiris_function"].Run(f.Name, f.Kind, f.Params, f.Ids, f.Arity);
                }
                foreach (var ty in types)
                {
                    this["osiris_type"].Run(ty.Name, ty.Kind, ty.Definition);
                }
                break;
        }
    }

    private void AddTimeline(string package, string mod, string rel, string stem, TimelineData data)
    {
        var tid = ++_timelineId;
        this["timeline"].Run(tid, stem, package, mod, rel, data.Duration, data.Phases.Count, data.Tracks.Count);
        _timelines.Add((tid, rel));
        for (var i = 0; i < data.Phases.Count; i++)
        {
            this["phase"].Run(tid, i, data.Phases[i].Duration, data.Phases[i].DialogNode);
        }
        var actors = data.Actors.ToDictionary(a => a.Uuid, a => a.Data, StringComparer.Ordinal);
        var uuids = data.Actors.Select(a => a.Uuid).ToList();
        foreach (var u in data.Tracks.Select(tr => tr.Actor).Distinct())
        {
            if (!string.IsNullOrEmpty(u) && !actors.ContainsKey(u))
            {
                uuids.Add(u);
            }
        }
        var actorIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var uuid in uuids)
        {
            var a = actors.GetValueOrDefault(uuid) ?? [];
            var id = ++_actorId;
            actorIds[uuid] = id;
            int? speaker = data.Speakers.TryGetValue(uuid, out var s) ? s : null;
            if (speaker == null && a.ContainsKey("Speaker"))
            {
                speaker = Values.Int(a.Get("Speaker"), -1);
            }
            this["actor"].Run(id, tid, uuid, speaker, a.Get("ActorTypeId"),
                a.Count > 0 ? (a.Get("IsPlayer") == "True" ? 1 : 0) : null, a.Get("ResourceId"),
                a.ContainsKey("SceneActorType") ? (int?)Values.Num(a.Get("SceneActorType")) : null,
                a.ContainsKey("SceneActorIndex") ? (int?)Values.Num(a.Get("SceneActorIndex")) : null);
        }
        var track = this["track"];
        var key = this["key"];
        foreach (var tr in data.Tracks)
        {
            var trackId = ++_trackId;
            if (tr.Nested)
            {
                NestedComponents++;
            }
            track.Run(trackId, tid, TypeId(tr.Type), tr.Actor != null && actorIds.TryGetValue(tr.Actor, out var aid) ? aid : null,
                tr.Phase, tr.Start, tr.End, tr.Keys, tr.Hides, tr.Shows, tr.Attrs);
            if (tr.KeyList != null)
            {
                for (var i = 0; i < tr.KeyList.Count; i++)
                {
                    var k = tr.KeyList[i];
                    key.Run(trackId, i, k.Channel, k.Time, k.Value, k.Attrs);
                }
            }
        }
    }

    private void AddDialog(string package, string rel, DialogData d)
    {
        var name = Split1(rel, "/DialogsBinary/")[..^4];
        var did = ++_dialogId;
        this["dialog"].Run(did, d.Uuid, name, package, rel.Split('/')[1], rel, d.Category, d.Timeline, d.Nodes.Count);
        foreach (var s in d.Speakers)
        {
            this["dialog_speaker"].Run(did, s.Index, s.Object, s.Mapping);
        }
        foreach (var n in d.Nodes)
        {
            this["dialog_node"].Run(did, n.Uuid, n.Constructor, n.Speaker);
        }
        foreach (var l in d.Lines)
        {
            this["dialog_line"].Run(did, l.Node, l.Handle, l.LineId);
        }
        foreach (var l in d.Links)
        {
            this["dialog_link"].Run(did, l.Node, l.Index, l.Child);
        }
        foreach (var f in d.Flags)
        {
            this["dialog_flag"].Run(did, f.Node, f.Kind, f.Type, f.Flag, f.Value, f.Target);
        }
        foreach (var a in d.Attrs)
        {
            this["dialog_node_attr"].Run(did, a.Node, a.Name, a.Value);
        }
    }

    /// <summary>Bank, scene files and dialogs: links between timelines, once every file is read.</summary>
    public void Link(SqliteTransaction tx)
    {
        foreach (var (tid, rel) in _timelines)
        {
            var stem = rel[..^4].ToLowerInvariant();
            if (_bank.TryGetValue(stem + ".lsf", out var entry))
            {
                this["timeline_bank"].Run(entry.Id, entry.DialogResource, tid);
            }
            if (_scenes.TryGetValue(stem, out var scene))
            {
                var seen = new Dictionary<int, int>();
                for (var i = 0; i < scene.Count; i++)
                {
                    var (type, template) = scene[i];
                    var n = seen.GetValueOrDefault(type);
                    this["scene_actor"].Run(tid, i, type, n, template);
                    seen[type] = n + 1;
                }
                this["has_scene"].Run(tid);
            }
        }
        Sql.Exec(db, Schema.Link, tx);
    }

    /// <summary>Names of nodes and attributes, and the catalog of every file.</summary>
    public void Finish(List<(string Package, string Path, long Size)> catalog, Action<string, long> count)
    {
        foreach (var (name, id) in _nameIds)
        {
            this["name"].Run(id, name);
        }
        var file = this["file"];
        foreach (var (package, path, size) in catalog)
        {
            file.Run(package, path, FileKinds.Extension(path), size);
        }
    }

    public void AddLoca(string lang, string variant, LocaResource loca)
    {
        var insert = this["loca"];
        foreach (var e in loca.Entries)
        {
            insert.Run(lang, variant, e.Key, (int)e.Version, e.Text);
        }
    }

    public void Dispose()
    {
        foreach (var i in _inserts.Values)
        {
            i.Dispose();
        }
        _inserts.Clear();
    }
}
