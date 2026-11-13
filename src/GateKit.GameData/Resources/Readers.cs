using System.Globalization;

namespace GateKit.GameData.Resources;

/// <summary>Small helpers shared by the readers.</summary>
internal static class Values
{
    /// <summary>A number written as text, or <paramref name="fallback"/> when it is not one.</summary>
    public static double? Num(string? text, double? fallback = null)
    {
        if (text != null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        return fallback;
    }

    /// <summary>A whole number written as text (a fraction is cut), or <paramref name="fallback"/>.</summary>
    public static int Int(string? text, int fallback) => (int)(Num(text) ?? fallback);

    public static string? Get(this Dictionary<string, string?> d, string key) => d.TryGetValue(key, out var v) ? v : null;
}

// ---------------------------------------------------------------------------------- timelines

/// <summary>A track segment (EffectComponent) as a timeline file holds it.</summary>
public sealed record TimelineTrack(
    string Type, string? Actor, int Phase, double Start, double? End, int Keys, int? Hides, int? Shows,
    string? Attrs, List<TimelineKey>? KeyList, bool Nested);

/// <summary>One key of a track whose keys are kept.</summary>
public sealed record TimelineKey(int? Channel, double Time, string? Value, string Attrs);

/// <summary>What a timeline file holds.</summary>
public sealed class TimelineData
{
    public double? Duration { get; init; }
    public List<(double? Duration, string? DialogNode)> Phases { get; init; } = [];
    /// <summary>Actors by uuid, in file order, with the attributes of their data.</summary>
    public List<(string Uuid, Dictionary<string, string?> Data)> Actors { get; init; } = [];
    /// <summary>Speaker index of an actor uuid, from the TimelineSpeaker map.</summary>
    public Dictionary<string, int> Speakers { get; init; } = [];
    public List<TimelineTrack> Tracks { get; init; } = [];
}

/// <summary>Reads a timeline: phases, actors, the speaker map and every track segment.</summary>
public sealed class TimelineReader(bool allKeys = false) : ResourceReader
{
    /// <summary>Tracks whose keys are kept one by one; the others are stored with a key count only.</summary>
    public static readonly IReadOnlySet<string> KeyTracks = new HashSet<string>(StringComparer.Ordinal)
    {
        "TLShowWeapon", "TLShowVisual", "TLShowArmor", "TLShapeShift", "TLAttachToEvent", "TLShowPeanuts",
    };

    /// <summary>On/off tracks: the attribute that carries the value. It is written only when it is False.</summary>
    public static readonly IReadOnlyDictionary<string, string> OnOff = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TLShowWeapon"] = "ShowWeapon", ["TLShowVisual"] = "ShowVisual", ["TLShowArmor"] = "Value", ["TLShowPeanuts"] = "ShowPeanuts",
    };

    private sealed class Component
    {
        public int Depth;
        public int Channel = -1;
        public int Keys;
        public readonly List<(int? Channel, Dictionary<string, string?> Key)> KeyList = [];
        public readonly Dictionary<string, string?> Attrs = [];
        public readonly Dictionary<string, string?> Fields = [];
        public bool Nested;
        public string? Actor;
        public string? Type => Fields.Get("Type");
    }

    private readonly List<Component> _comps = [];
    private readonly List<Component> _open = [];
    private Dictionary<string, string?>? _key;
    private readonly List<Dictionary<string, string?>> _phases = [];
    private readonly Dictionary<string, Dictionary<string, string?>> _actors = [];
    private readonly List<string> _actorOrder = [];
    private readonly List<Dictionary<string, string?>> _speakers = [];
    private string? _duration;
    private Dictionary<string, string?>? _actor;
    private Dictionary<string, string?>? _pair;

    /// <summary>Whether the keys of a track type are kept (every type when built with all keys).</summary>
    private bool KeepsKeys(string? type) => type != null && (allKeys || KeyTracks.Contains(type));

    protected override void OnNode(string id)
    {
        var d = Stack.Count;
        if (id == "EffectComponent")
        {
            var c = new Component { Depth = d, Nested = _open.Count > 0 };
            _open.Add(c);
            _comps.Add(c);
        }
        else if (_open.Count > 0)
        {
            var c = _open[^1];
            if (id == "Key")
            {
                c.Keys++;
                if (KeepsKeys(c.Type))
                {
                    _key = [];
                    c.KeyList.Add((c.Channel >= 0 ? c.Channel : null, _key));
                }
            }
            else if (d == c.Depth + 2 && Up(2) == "Channels")
            {
                c.Channel++;
            }
        }
        else if (id == "Phase" && d > 1 && Up(2) == "Phases")
        {
            _phases.Add([]);
        }
        else if (id == "Object" && d > 1)
        {
            if (Up(2) == "TimelineSpeaker")
            {
                _pair = [];
                _speakers.Add(_pair);
            }
            else if (Up(2) == "TimelineActorData")
            {
                _actor = null;
            }
        }
    }

    protected override void OnNodeEnd(string id)
    {
        if (id == "EffectComponent")
        {
            _open.RemoveAt(_open.Count - 1);
        }
        else if (id == "Key")
        {
            _key = null;
        }
    }

    protected override void OnAttribute(string id, string? value)
    {
        var d = Stack.Count;
        var top = Top;
        if (_open.Count > 0)
        {
            var c = _open[^1];
            if (d == c.Depth)
            {
                if (id is "Type" or "ID" or "StartTime" or "EndTime" or "PhaseIndex")
                {
                    c.Fields[id] = value;
                }
                else if (KeepsKeys(c.Type))
                {
                    c.Attrs[id] = value;
                }
            }
            else if (top == "Key")
            {
                if (_key != null)
                {
                    _key[id] = value;
                }
            }
            else if (top == "Actor" && d == c.Depth + 1 && id == "UUID")
            {
                c.Actor = value;
            }
        }
        else if (top == "Phase" && d > 1 && Up(2) == "Phases")
        {
            _phases[^1][id] = value;
        }
        else if (top == "Effect" && id == "Duration" && d == 2)
        {
            _duration = value;
        }
        else if (top == "Object" && d > 1)
        {
            if (Up(2) == "TimelineSpeaker" && _pair != null)
            {
                _pair[id] = value;
            }
            else if (Up(2) == "TimelineActorData" && id == "MapKey" && value != null)
            {
                if (!_actors.TryGetValue(value, out _actor))
                {
                    _actor = [];
                    _actors[value] = _actor;
                    _actorOrder.Add(value);
                }
            }
        }
        else if (top == "Value" && d > 2 && Up(2) == "Object" && Up(3) == "TimelineActorData" && _actor != null)
        {
            _actor[id] = value;
        }
    }

    public TimelineData Result()
    {
        var tracks = new List<TimelineTrack>();
        foreach (var c in _comps)
        {
            var type = c.Type ?? "?";
            int? hides = null, shows = null;
            List<TimelineKey>? keys = null;
            if (KeepsKeys(c.Type ?? "?"))
            {
                var attr = OnOff.GetValueOrDefault(type);
                keys = [];
                if (attr != null)
                {
                    hides = shows = 0;
                }
                foreach (var (channel, k) in c.KeyList)
                {
                    string? value = null;
                    if (attr != null)
                    {
                        value = k.TryGetValue(attr, out var v) ? v : "True";
                        if (value == "True")
                        {
                            shows++;
                        }
                        else
                        {
                            hides++;
                        }
                    }
                    if (type == "TLShapeShift")
                    {
                        value = k.Get("TemplateId");      // the template to turn into; none = back to normal
                    }
                    keys.Add(new TimelineKey(channel, Values.Num(k.Get("Time"), 0.0)!.Value, value, PyJson.Dumps(k)));
                }
            }
            tracks.Add(new TimelineTrack(type, c.Actor, Values.Int(c.Fields.Get("PhaseIndex"), 0),
                Values.Num(c.Fields.Get("StartTime"), 0.0)!.Value, Values.Num(c.Fields.Get("EndTime")), c.Keys, hides, shows,
                c.Attrs.Count > 0 ? PyJson.Dumps(c.Attrs) : null, keys, c.Nested));
        }
        var speakers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in _speakers)
        {
            if (pair.TryGetValue("MapKey", out var key) && pair.TryGetValue("MapValue", out var mapValue) && mapValue != null)
            {
                speakers[mapValue] = Values.Int(key, -1);
            }
        }
        return new TimelineData
        {
            Duration = Values.Num(_duration),
            Phases = _phases.Select(p => (Values.Num(p.Get("Duration")), p.Get("DialogNodeId"))).ToList(),
            Actors = _actorOrder.Select(u => (u, _actors[u])).ToList(),
            Speakers = speakers,
            Tracks = tracks,
        };
    }
}

/// <summary>Reads the companion *_Scene file of a timeline: its stage actors in file order.</summary>
public sealed class SceneReader : ResourceReader
{
    private readonly List<Dictionary<string, string?>> _actors = [];

    protected override void OnNode(string id)
    {
        if (id == "TLActor" && Stack.Count > 1 && Up(2) == "TLActors")
        {
            _actors.Add([]);
        }
    }

    protected override void OnAttribute(string id, string? value)
    {
        if (Top == "TLActor" && _actors.Count > 0 && id is "ActorType" or "TemplateId")
        {
            _actors[^1][id] = value;
        }
    }

    /// <summary>(actor type, template) per actor.</summary>
    public List<(int ActorType, string? Template)> Result() =>
        _actors.Select(a => (Values.Int(a.Get("ActorType"), -1), a.Get("TemplateId"))).ToList();
}

/// <summary>Collects the direct attributes of every node with the given id at the given depth.</summary>
public class FlatReader(string nodeId, int depth, params string[] keep) : ResourceReader
{
    private readonly HashSet<string> _keep = new(keep, StringComparer.Ordinal);
    private readonly List<Dictionary<string, string?>> _rows = [];

    protected override void OnNode(string id)
    {
        if (id == nodeId && Stack.Count == depth)
        {
            _rows.Add([]);
        }
    }

    protected override void OnAttribute(string id, string? value)
    {
        if (Stack.Count == depth && Top == nodeId && _keep.Contains(id))
        {
            _rows[^1][id] = value;
        }
    }

    public List<Dictionary<string, string?>> Result() => _rows;

    /// <summary>Timeline bank resources: the resource id of each timeline file.</summary>
    public static FlatReader Bank() => new("Resource", 2, "ID", "Name", "SourceFile", "DialogResourceId");

    /// <summary>Root templates and placed characters and items.</summary>
    public static FlatReader Templates() => new("GameObjects", 2,
        "MapKey", "Name", "Type", "ParentTemplateId", "TemplateName", "DisplayName", "Stats", "VisualTemplate",
        "CharacterVisualResourceID", "Icon", "LevelName");

    public static FlatReader SpeakerGroups() => new("SpeakerGroup", 2, "UUID", "Name", "Description");
}

// ---------------------------------------------------------------------------------- dialogs

/// <summary>What a dialog file holds, flattened to the rows of the dialog tables.</summary>
public sealed class DialogData
{
    public string? Uuid { get; init; }
    public string? Category { get; init; }
    public string? Timeline { get; init; }
    public List<(int Index, string Object, string? Mapping)> Speakers { get; init; } = [];
    public List<(string? Uuid, string? Constructor, int? Speaker)> Nodes { get; init; } = [];
    public List<(string? Node, string? Handle, string? LineId)> Lines { get; init; } = [];
    public List<(string? Node, int Index, string? Child)> Links { get; init; } = [];
    public List<(string? Node, string Kind, string? Type, string? Flag, int Value, int? Target)> Flags { get; init; } = [];
    public List<(string? Node, string Name, string? Value)> Attrs { get; init; } = [];
}

/// <summary>Reads a dialog: speakers, nodes with their lines, links and flags.</summary>
public sealed class DialogReader : ResourceReader
{
    private readonly Dictionary<string, string?> _head = [];
    private readonly List<Dictionary<string, string?>> _speakers = [];
    private readonly List<Dictionary<string, string?>> _nodes = [];
    private readonly List<(Dictionary<string, string?> Node, Dictionary<string, string?> Line)> _lines = [];
    private readonly List<(Dictionary<string, string?> Node, string? Child)> _links = [];
    private readonly List<(Dictionary<string, string?> Node, string Kind, string? Type, Dictionary<string, string?> Flag)> _flags = [];
    private string? _group;

    private string At(int i) => Stack[i];

    private bool FlagsBranch => At(3) is "checkflags" or "setflags";

    protected override void OnNode(string id)
    {
        var d = Stack.Count;
        if (d == 3 && At(1) == "speakerlist" && id == "speaker")
        {
            _speakers.Add([]);
        }
        else if (d == 3 && At(1) == "nodes" && id == "node")
        {
            _nodes.Add([]);
        }
        else if (id == "TagText" && d > 3 && At(1) == "nodes" && _nodes.Count > 0)
        {
            _lines.Add((_nodes[^1], []));
        }
        else if (d == 5 && At(1) == "nodes" && At(2) == "node" && FlagsBranch && id == "flaggroup")
        {
            _group = null;
        }
        else if (d == 6 && At(1) == "nodes" && FlagsBranch && id == "flag" && _nodes.Count > 0)
        {
            _flags.Add((_nodes[^1], At(3) == "checkflags" ? "check" : "set", _group, []));
        }
    }

    protected override void OnAttribute(string id, string? value)
    {
        var d = Stack.Count;
        if (d == 1)
        {
            _head[id] = value;
        }
        else if (d == 3 && At(1) == "speakerlist")
        {
            _speakers[^1][id] = value;
        }
        else if (d == 3 && At(1) == "nodes" && At(2) == "node")
        {
            _nodes[^1][id] = value;
        }
        else if (d > 3 && Top == "TagText" && At(1) == "nodes" && _lines.Count > 0 && id is "TagText" or "LineId")
        {
            _lines[^1].Line[id] = value;
        }
        else if (d == 5 && At(1) == "nodes" && At(2) == "node" && At(3) == "children" && At(4) == "child" && id == "UUID" && _nodes.Count > 0)
        {
            _links.Add((_nodes[^1], value));
        }
        else if (d == 5 && At(1) == "nodes" && FlagsBranch && At(4) == "flaggroup" && id == "type")
        {
            _group = value;
        }
        else if (d == 6 && At(1) == "nodes" && FlagsBranch && At(5) == "flag" && _flags.Count > 0)
        {
            _flags[^1].Flag[id] = value;
        }
    }

    public DialogData Result()
    {
        var speakers = new List<(int, string, string?)>();
        foreach (var s in _speakers)
        {
            foreach (var obj in (s.Get("list") ?? "").Split(';'))
            {
                if (obj.Length > 0)
                {
                    speakers.Add((Values.Int(s.Get("index"), -1), obj, s.Get("SpeakerMappingId")));
                }
            }
        }
        var nodes = _nodes.Select(n => (n.Get("UUID"), n.Get("constructor"),
            n.ContainsKey("speaker") ? Values.Int(n.Get("speaker"), -1) : (int?)null)).ToList();
        var lines = _lines.Where(l => !string.IsNullOrEmpty(l.Line.Get("TagText")))
            .Select(l => (l.Node.Get("UUID"), l.Line.Get("TagText"), l.Line.Get("LineId"))).ToList();
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        var links = new List<(string?, int, string?)>();
        foreach (var (node, child) in _links)
        {
            var key = node.Get("UUID") ?? "";
            var n = order.GetValueOrDefault(key);
            links.Add((node.Get("UUID"), n, child));
            order[key] = n + 1;
        }
        var flags = _flags.Select(f => (f.Node.Get("UUID"), f.Kind, f.Type, f.Flag.Get("UUID"), f.Flag.Get("value") == "True" ? 1 : 0,
            f.Flag.Get("paramval") != null ? Values.Int(f.Flag.Get("paramval"), 0) : (int?)null)).ToList();
        var attrs = new List<(string?, string, string?)>();
        foreach (var n in _nodes)
        {
            foreach (var (k, v) in n)
            {
                if (k is not ("UUID" or "constructor" or "speaker"))
                {
                    attrs.Add((n.Get("UUID"), k, v));
                }
            }
        }
        return new DialogData
        {
            Uuid = _head.Get("UUID"), Category = _head.Get("category"), Timeline = _head.Get("TimelineId"),
            Speakers = speakers, Nodes = nodes, Lines = lines, Links = links, Flags = flags, Attrs = attrs,
        };
    }
}

/// <summary>Voice metadata: VoiceSpeakerMetaData (speaker) &gt; VoiceTextMetaData (handle) &gt; MapValue (file, length).</summary>
public sealed class VoiceReader : ResourceReader
{
    private string? _speaker;
    private readonly List<Dictionary<string, string?>> _rows = [];

    protected override void OnAttribute(string id, string? value)
    {
        var top = Top;
        if (top == "VoiceSpeakerMetaData" && id == "MapKey")
        {
            _speaker = value;
        }
        else if (top == "VoiceTextMetaData" && id == "MapKey")
        {
            _rows.Add(new() { ["handle"] = value, ["speaker"] = _speaker });
        }
        else if (top == "MapValue" && Stack.Count > 1 && Up(2) == "VoiceTextMetaData" && _rows.Count > 0)
        {
            _rows[^1][id] = value;
        }
    }

    public List<Dictionary<string, string?>> Result() => _rows;
}

// ---------------------------------------------------------------------------------- generic objects

/// <summary>An object of the generic index: a node that carries attributes.</summary>
public sealed record GenericObject(string Node, string? Key, string? Name, bool Truncated, List<(string Attr, string Value)> Attrs);

/// <summary>
/// Any resource file as a list of objects. A node with attributes of its own, found one to three
/// levels below the region's root, is an object; a node without attributes at those levels is
/// taken as a container and its children are looked at instead. Attributes of the nodes inside an
/// object are kept under a path (Child/Grandchild.Attribute), up to <see cref="NestedLimit"/> per object.
/// </summary>
public sealed class GenericReader : ResourceReader
{
    public const int NestedLimit = 200;
    public const int ValueLimit = 1000;
    private static readonly string[] Keys = ["MapKey", "UUID", "ID", "Id", "Guid", "GUID", "Name"];

    private sealed class Obj
    {
        public required string Node;
        public readonly List<(string, string)> Attrs = [];
        public readonly Dictionary<string, string> Direct = [];
        public int Nested;
        public bool Cut;
    }

    private readonly List<Obj> _objects = [];
    private string? _region;
    private Obj? _obj;
    private int _objDepth;
    private (string Id, int Depth)? _pending;

    private void New(string id, int depth)
    {
        _obj = new Obj { Node = id };
        _objDepth = depth;
        _objects.Add(_obj);
    }

    protected override void OnNode(string id)
    {
        var d = Stack.Count;
        if (d == 1)
        {
            _region = id;
        }
        if (_obj != null && _objDepth > 1 && d > _objDepth)
        {
            return;                                   // inside an object
        }
        _obj = null;
        _pending = d <= 4 ? (id, d) : null;
        if (d == 4)
        {
            New(id, d);
        }
    }

    protected override void OnNodeEnd(string id)
    {
        var d = Stack.Count + 1;
        if (_obj != null && d <= _objDepth)
        {
            _obj = null;
        }
        _pending = null;
    }

    protected override void OnAttribute(string id, string? value)
    {
        if (id == "_OriginalFileVersion_" || value == null)
        {
            return;
        }
        var d = Stack.Count;
        if (_obj == null || (_objDepth == 1 && d > 1))
        {
            if (_pending is not { } p || p.Depth != d)
            {
                return;
            }
            New(p.Id, p.Depth);
        }
        var o = _obj!;
        if (value.Length > ValueLimit)
        {
            value = value[..ValueLimit];
        }
        if (d == _objDepth)
        {
            o.Attrs.Add((id, value));
            o.Direct.TryAdd(id, value);
        }
        else if (o.Nested < NestedLimit)
        {
            o.Nested++;
            o.Attrs.Add((string.Join('/', Stack.Skip(_objDepth)) + "." + id, value));
        }
        else
        {
            o.Cut = true;
        }
    }

    /// <summary>The region (root node id) and the objects found.</summary>
    public (string? Region, List<GenericObject> Objects) Result()
    {
        var list = new List<GenericObject>(_objects.Count);
        foreach (var o in _objects)
        {
            string? key = null;
            foreach (var k in Keys)
            {
                if (o.Direct.TryGetValue(k, out var v) && v.Length > 0)
                {
                    key = v;
                    break;
                }
            }
            list.Add(new GenericObject(o.Node, key, o.Direct.GetValueOrDefault("Name"), o.Cut, o.Attrs));
        }
        return (_region, list);
    }
}
