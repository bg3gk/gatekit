namespace GateKit.GameData;

/// <summary>The tables of the index. Comments after a column (<c>-- ...</c>) are its meaning; the
/// schema query reads them back.</summary>
public static class Schema
{
    /// <summary>Tables, created before anything is read.</summary>
    public const string Tables = """
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE problems (path TEXT, problem TEXT);          -- files that could not be read

-- One row per timeline file. shadowed = 1 when a later mod or package provides the same
-- resource (same uuid or same name); queries leave those out.
CREATE TABLE timelines (
    id INTEGER PRIMARY KEY, name TEXT, package TEXT, mod TEXT, path TEXT,
    uuid TEXT,              -- resource id from the timeline bank
    dialog_resource TEXT,   -- bank: DialogResourceId (a resource id, not the dialog's uuid)
    dialog_id INTEGER,      -- dialogs.id of the dialog this timeline stages (dialog.TimelineId = uuid)
    duration REAL, phases INTEGER, components INTEGER, has_scene INTEGER, shadowed INTEGER DEFAULT 0
);
CREATE TABLE timeline_phases (timeline_id INTEGER, idx INTEGER, duration REAL, dialog_node TEXT);
-- Actors as the timeline declares them. speaker is the index into the dialog's speaker list.
CREATE TABLE timeline_actors (
    id INTEGER PRIMARY KEY, timeline_id INTEGER, uuid TEXT,
    speaker INTEGER, kind TEXT,            -- ActorTypeId: character, item, camera, light, ...
    is_player INTEGER, resource TEXT,      -- ResourceId, when the actor is a fixed resource
    scene_actor_type INTEGER, scene_actor_index INTEGER,
    scene_template TEXT                    -- stand-in template from the *_Scene file, when it resolves
);
-- Actors of the companion *_Scene file (stage dummies), in file order.
CREATE TABLE scene_actors (timeline_id INTEGER, idx INTEGER, actor_type INTEGER, type_index INTEGER, template TEXT);
CREATE TABLE track_types (id INTEGER PRIMARY KEY, name TEXT UNIQUE);
-- One row per EffectComponent (track segment).
CREATE TABLE tracks (
    id INTEGER PRIMARY KEY, timeline_id INTEGER, type_id INTEGER,
    actor_id INTEGER,                      -- timeline_actors.id, NULL for tracks without an actor
    phase INTEGER, start REAL, end REAL, keys INTEGER,
    hides INTEGER, shows INTEGER,          -- on/off tracks: number of keys that hide / show
    attrs TEXT                             -- remaining attributes (JSON), kept for key tracks only
);
-- Keys of the tracks listed in KEY_TRACKS.
CREATE TABLE track_keys (
    track_id INTEGER, idx INTEGER,
    channel INTEGER,                       -- TLShowArmor: which piece (index into Channels), else NULL
    time REAL, value TEXT,                 -- on/off tracks: 'True' / 'False'; TLShapeShift: template uuid
    attrs TEXT                             -- every attribute of the key (JSON)
);

CREATE TABLE dialogs (
    id INTEGER PRIMARY KEY, uuid TEXT, name TEXT, package TEXT, mod TEXT, path TEXT,
    category TEXT, timeline_uuid TEXT, nodes INTEGER, shadowed INTEGER DEFAULT 0
);
-- A speaker slot lists one or more objects: characters, templates or speaker groups.
CREATE TABLE dialog_speakers (dialog_id INTEGER, idx INTEGER, object TEXT, mapping TEXT);
CREATE TABLE dialog_nodes (dialog_id INTEGER, uuid TEXT, constructor TEXT, speaker INTEGER);
CREATE TABLE dialog_lines (dialog_id INTEGER, node TEXT, handle TEXT, line_id TEXT);
-- Recorded voice: one row per (speaker, text handle). file is the path of the sound inside its
-- package (Voice.pak); length is in seconds. speaker is the uuid the recording was made for:
-- a placed character, a speaker group, or the narrator.
CREATE TABLE voice_lines (speaker TEXT, handle TEXT, file TEXT, length REAL, priority TEXT, codec TEXT, language TEXT);
-- The flow of a dialog: which node leads to which, in order.
CREATE TABLE dialog_links (dialog_id INTEGER, node TEXT, idx INTEGER, child TEXT);
-- Flags a node checks before it can be chosen (kind 'check') and flags it sets when played ('set').
-- flag_type: Tag, Global, Local, Object, Dialog, Script, User, Party, ... ; target is the speaker
-- index the flag applies to (paramval).
CREATE TABLE dialog_flags (dialog_id INTEGER, node TEXT, kind TEXT, flag_type TEXT, flag TEXT, value INTEGER, target INTEGER);
-- Every other attribute of a node: endnode, Root, jumptarget, roll parameters, ...
CREATE TABLE dialog_node_attrs (dialog_id INTEGER, node TEXT, name TEXT, value TEXT);

CREATE TABLE templates (               -- root templates, every type
    uuid TEXT PRIMARY KEY, name TEXT, type TEXT, parent TEXT, display_handle TEXT,
    stats TEXT, visual TEXT, icon TEXT, package TEXT, mod TEXT
);
CREATE TABLE entities (                -- characters and items placed in levels and globals
    uuid TEXT PRIMARY KEY, name TEXT, type TEXT, template TEXT, display_handle TEXT,
    stats TEXT, level TEXT, scope TEXT, package TEXT, mod TEXT
);
CREATE TABLE speaker_groups (uuid TEXT PRIMARY KEY, name TEXT, description TEXT);

-- Every file of every package (the whole game), whether indexed further or not.
CREATE TABLE files (id INTEGER PRIMARY KEY, package TEXT, path TEXT, ext TEXT, size INTEGER);

-- Generic index of every resource file (.lsf/.lsx) except timelines and dialogs, which have
-- their own tables above. An object is a node that carries attributes: a template, a flag, a
-- tag, a resource of a bank, a race, a progression, an object placed in a level, ...
CREATE TABLE sources (id INTEGER PRIMARY KEY, package TEXT, mod TEXT, path TEXT, region TEXT, objects INTEGER);
CREATE TABLE names (id INTEGER PRIMARY KEY, name TEXT UNIQUE);     -- node ids and attribute paths
CREATE TABLE objects (
    id INTEGER PRIMARY KEY, source_id INTEGER, node_id INTEGER,   -- names.id of the node id
    key TEXT,                -- MapKey / UUID / ID / Name, whichever the node has
    name TEXT,
    truncated INTEGER        -- 1 when the node had more nested attributes than are kept
);
-- attr_id is names.id of the attribute; nested ones are written Child/Grandchild.Attribute.
CREATE TABLE attrs (object_id INTEGER, attr_id INTEGER, value TEXT);

-- Text files: story goals, stats, scripts, interface markup. Searchable through texts_fts.
CREATE TABLE texts (id INTEGER PRIMARY KEY, package TEXT, mod TEXT, path TEXT, kind TEXT, bytes INTEGER);
CREATE VIRTUAL TABLE texts_fts USING fts5(content, tokenize = "unicode61 tokenchars '_'");

CREATE TABLE stats (id INTEGER PRIMARY KEY, name TEXT, type TEXT, parent TEXT, package TEXT, mod TEXT, file TEXT);
CREATE TABLE stat_fields (stat_id INTEGER, field TEXT, value TEXT);

-- Osiris (story scripting): the functions the engine offers and where the story uses them.
CREATE TABLE osiris_functions (name TEXT, kind TEXT, params TEXT, ids TEXT, arity INTEGER);  -- kind: event, call, query, ...
CREATE TABLE osiris_types (name TEXT, kind TEXT, definition TEXT);
-- role: trigger (first line of an IF), condition, action, define (PROC/QRY header), init, exit.
CREATE TABLE osiris_uses (text_id INTEGER, name TEXT, role TEXT, uses INTEGER);
-- variant is '' for the main file of a language; gendered variants keep their file name
-- (e.g. 'Gender/Female/english_to_F') and hold only the strings that differ.
CREATE TABLE loca (lang TEXT, variant TEXT, handle TEXT, version INTEGER, text TEXT,
                   PRIMARY KEY (lang, variant, handle)) WITHOUT ROWID;
""";

    /// <summary>Indexes and views, created after everything is written.</summary>
    public const string Finish = """
CREATE INDEX timelines_name ON timelines(name);
CREATE INDEX timelines_uuid ON timelines(uuid);
CREATE INDEX timelines_dialog ON timelines(dialog_id);
CREATE INDEX phases_timeline ON timeline_phases(timeline_id);
CREATE INDEX actors_timeline ON timeline_actors(timeline_id);
CREATE INDEX scene_actors_timeline ON scene_actors(timeline_id);
CREATE INDEX scene_actors_template ON scene_actors(template);
CREATE INDEX tracks_timeline ON tracks(timeline_id);
CREATE INDEX tracks_type ON tracks(type_id, hides);
CREATE INDEX tracks_actor ON tracks(actor_id);
CREATE INDEX keys_track ON track_keys(track_id);
CREATE INDEX dialogs_uuid ON dialogs(uuid);
CREATE INDEX dialogs_name ON dialogs(name);
CREATE INDEX speakers_dialog ON dialog_speakers(dialog_id, idx);
CREATE INDEX speakers_object ON dialog_speakers(object);
CREATE INDEX nodes_dialog ON dialog_nodes(dialog_id);
CREATE INDEX nodes_uuid ON dialog_nodes(uuid);
CREATE INDEX lines_node ON dialog_lines(node);
CREATE INDEX lines_handle ON dialog_lines(handle);
CREATE INDEX voice_handle ON voice_lines(handle);
CREATE INDEX voice_speaker ON voice_lines(speaker);
CREATE INDEX links_node ON dialog_links(dialog_id, node);
CREATE INDEX links_child ON dialog_links(child);
CREATE INDEX dflags_node ON dialog_flags(dialog_id, node);
CREATE INDEX dflags_flag ON dialog_flags(flag);
CREATE INDEX dnattrs_node ON dialog_node_attrs(dialog_id, node);
CREATE INDEX templates_name ON templates(name);
CREATE INDEX templates_parent ON templates(parent);
CREATE INDEX entities_name ON entities(name);
CREATE INDEX entities_template ON entities(template);
CREATE INDEX files_path ON files(path);
CREATE INDEX files_ext ON files(ext);
CREATE INDEX objects_key ON objects(key);
CREATE INDEX objects_name ON objects(name);
CREATE INDEX objects_node ON objects(node_id);
CREATE INDEX objects_source ON objects(source_id);
CREATE INDEX attrs_object ON attrs(object_id);
CREATE INDEX attrs_value ON attrs(value) WHERE length(value) BETWEEN 4 AND 80;
CREATE INDEX texts_path ON texts(path);
CREATE INDEX stats_name ON stats(name);
CREATE INDEX stats_parent ON stats(parent);
CREATE INDEX stat_fields_stat ON stat_fields(stat_id);
CREATE INDEX stat_fields_value ON stat_fields(field, value);
CREATE INDEX osiris_functions_name ON osiris_functions(name);
CREATE INDEX osiris_uses_name ON osiris_uses(name);
CREATE INDEX osiris_uses_text ON osiris_uses(text_id);

CREATE VIEW v_objects AS
    SELECT o.id, s.package, s.mod, s.path, n.name AS node, o.key, o.name
    FROM objects o JOIN sources s ON s.id = o.source_id JOIN names n ON n.id = o.node_id;
CREATE VIEW v_attrs AS
    SELECT a.object_id, n.name AS attr, a.value FROM attrs a JOIN names n ON n.id = a.attr_id;

-- Tracks with readable names.
CREATE VIEW v_tracks AS
    SELECT k.id AS track_id, t.id AS timeline_id, t.name AS timeline, t.package, y.name AS type,
           k.actor_id, a.uuid AS actor_uuid, a.speaker, a.kind AS actor_kind, a.is_player,
           k.phase, k.start, k.end, k.keys, k.hides, k.shows
    FROM tracks k JOIN timelines t ON t.id = k.timeline_id JOIN track_types y ON y.id = k.type_id
    LEFT JOIN timeline_actors a ON a.id = k.actor_id
    WHERE t.shadowed = 0;

-- Who a timeline actor can be: the objects in the matching speaker slot of the timeline's dialog,
-- resolved to a placed character/item, a root template or a speaker group.
CREATE VIEW v_actor_objects AS
    SELECT a.id AS actor_id, a.timeline_id, a.speaker, s.object,
           e.name AS entity, coalesce(e.template, rt.uuid) AS template,
           coalesce(et.name, rt.name) AS template_name, g.name AS speaker_group,
           coalesce(e.display_handle, et.display_handle, rt.display_handle) AS display_handle
    FROM timeline_actors a
    JOIN timelines t ON t.id = a.timeline_id
    JOIN dialog_speakers s ON s.dialog_id = t.dialog_id AND s.idx = a.speaker
    LEFT JOIN entities e ON e.uuid = s.object
    LEFT JOIN templates et ON et.uuid = e.template
    LEFT JOIN templates rt ON rt.uuid = s.object
    LEFT JOIN speaker_groups g ON g.uuid = s.object
    WHERE a.speaker IS NOT NULL;
""";

    /// <summary>Links timelines to the bank, scenes and dialogs once every file is read.</summary>
    public const string Link = """
CREATE INDEX tmp_scene ON scene_actors(timeline_id, actor_type, type_index);
UPDATE timeline_actors SET scene_template = (SELECT s.template FROM scene_actors s
    WHERE s.timeline_id = timeline_actors.timeline_id AND s.actor_type = timeline_actors.scene_actor_type
      AND s.type_index = timeline_actors.scene_actor_index)
  WHERE scene_actor_type IS NOT NULL;
DROP INDEX tmp_scene;
-- The last file in load order wins for a resource id, and for a name among files without one.
UPDATE timelines SET shadowed = 1 WHERE uuid IS NOT NULL
    AND id < (SELECT max(o.id) FROM timelines o WHERE o.uuid = timelines.uuid);
UPDATE timelines SET shadowed = 1 WHERE uuid IS NULL
    AND id < (SELECT max(o.id) FROM timelines o WHERE o.name = timelines.name);
UPDATE dialogs SET shadowed = 1 WHERE uuid IS NOT NULL
    AND id < (SELECT max(o.id) FROM dialogs o WHERE o.uuid = dialogs.uuid);
CREATE INDEX tmp_dialog ON dialogs(timeline_uuid, shadowed);
UPDATE timelines SET dialog_id = (SELECT max(d.id) FROM dialogs d
    WHERE d.timeline_uuid = timelines.uuid AND d.shadowed = 0) WHERE uuid IS NOT NULL;
DROP INDEX tmp_dialog;
""";
}
