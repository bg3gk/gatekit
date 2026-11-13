using LSLib.LS;
using LSLib.LS.Enums;

namespace GateKit.GameData.Tests;

/// <summary>Small resources, packages and localization files made up for the tests (nothing from the game).</summary>
internal static class Synthetic
{
    /// <summary>A node: id, attributes as (name, value) with the type guessed from the value, and children.</summary>
    public static Node N(string name, params object[] content)
    {
        var node = new Node { Name = name };
        foreach (var item in content)
        {
            switch (item)
            {
                case Node child:
                    child.Parent = node;
                    node.AppendChild(child);
                    break;
                case (string key, string value):
                    node.Attributes[key] = new NodeAttribute(AttributeType.LSString) { Value = value };
                    break;
                case (string key, float value):
                    node.Attributes[key] = new NodeAttribute(AttributeType.Float) { Value = value };
                    break;
                case (string key, int value):
                    node.Attributes[key] = new NodeAttribute(AttributeType.Int) { Value = value };
                    break;
                case (string key, bool value):
                    node.Attributes[key] = new NodeAttribute(AttributeType.Bool) { Value = value };
                    break;
                case (string key, Guid value):
                    node.Attributes[key] = new NodeAttribute(AttributeType.UUID) { Value = NodeAttribute.ByteSwapGuid(value) };
                    break;
                case (string key, TranslatedFSString value):
                    node.Attributes[key] = new NodeAttribute(AttributeType.TranslatedFSString) { Value = value };
                    break;
                case (string key, TranslatedString value):
                    node.Attributes[key] = new NodeAttribute(AttributeType.TranslatedString) { Value = value };
                    break;
                default:
                    throw new ArgumentException($"unknown content {item}");
            }
        }
        return node;
    }

    /// <summary>A resource with one region whose root node is <paramref name="root"/>.</summary>
    public static Resource Res(Node root)
    {
        var region = new Region { Name = root.Name, RegionName = root.Name };
        foreach (var (k, v) in root.Attributes)
        {
            region.Attributes[k] = v;
        }
        foreach (var list in root.Children.Values)
        {
            foreach (var c in list)
            {
                c.Parent = region;
                region.AppendChild(c);
            }
        }
        var resource = new Resource();
        resource.Metadata.MajorVersion = 4;
        resource.Regions[root.Name] = region;
        return resource;
    }

    public static byte[] Lsf(Resource resource)
    {
        using var ms = new MemoryStream();
        new LSFWriter(ms).Write(resource);
        return ms.ToArray();
    }

    public static byte[] Lsx(Resource resource)
    {
        using var ms = new MemoryStream();
        new LSXWriter(ms) { Version = LSXVersion.V4 }.Write(resource);
        return ms.ToArray();
    }

    public static byte[] Loca(params (string Handle, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        new LocaWriter(ms).Write(new LocaResource
        {
            Entries = entries.Select(e => new LocalizedText { Key = e.Handle, Version = 1, Text = e.Text }).ToList(),
        });
        return ms.ToArray();
    }

    /// <summary>Writes a BG3 package with the given files.</summary>
    public static void Pak(string path, params (string Path, byte[] Body)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var build = new PackageBuildData { Version = PackageVersion.V18, Compression = CompressionMethod.LZ4 };
        foreach (var (p, body) in files)
        {
            build.Files.Add(PackageBuildInputFile.CreateFromBlob(body, p));
        }
        using var writer = PackageWriterFactory.Create(build, path);
        writer.Write();
    }

    public static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    /// <summary>A timeline with one actor (speaker 0) and a TLShowWeapon track that hides at 1 s and shows at 2.5 s.</summary>
    public static Resource Timeline(string actor) => Res(
        N("Timeline",
            N("Effect", ("Duration", 10f),
                N("Phases", N("Phase", ("Duration", 10f), ("DialogNodeId", "node-1"))),
                N("EffectComponents",
                    N("EffectComponent", ("Type", "TLShowWeapon"), ("ID", "c1"), ("StartTime", 0f), ("EndTime", 10f), ("PhaseIndex", 0),
                        N("Actor", ("UUID", actor)),
                        N("Keys",
                            N("Key", ("Time", 1f), ("ShowWeapon", false), ("InterpolationType", 3)),
                            N("Key", ("Time", 2.5f), ("InterpolationType", 3)))),
                    N("EffectComponent", ("Type", "TLAnimation"), ("StartTime", 0f), ("EndTime", 5f),
                        N("Actor", ("UUID", actor)),
                        N("Keys", N("Key", ("Time", 0f)))))),
            N("TimelineSpeakers", N("TimelineSpeaker", N("Object", ("MapKey", 0), ("MapValue", actor)))),
            N("TimelineActorData", N("TimelineActorData",
                N("Object", ("MapKey", actor), N("Value", ("ActorTypeId", "character"), ("IsPlayer", false), ("SceneActorType", 1), ("SceneActorIndex", 0)))))));

    /// <summary>A dialog with two nodes, one line each, a link, a flag check and a speaker list.</summary>
    public static Resource Dialog(string uuid, string timeline, string speaker) => Res(
        N("dialog", ("UUID", uuid), ("category", "Test"), ("TimelineId", timeline),
            N("speakerlist", N("speaker", ("index", "0"), ("list", speaker), ("SpeakerMappingId", "map"))),
            N("nodes",
                N("node", ("UUID", "node-1"), ("constructor", "TagGreeting"), ("speaker", 0), ("Root", true),
                    N("children", N("child", ("UUID", "node-2"))),
                    N("checkflags", N("flaggroup", ("type", "Global"), N("flag", ("UUID", "flag-1"), ("value", true)))),
                    N("TaggedTexts", N("TaggedText", N("TagTexts", N("TagText", ("TagText", "h00000001g0000g0000g0000g000000000001"), ("LineId", "l1")))))),
                N("node", ("UUID", "node-2"), ("constructor", "TagAnswer"), ("endnode", true),
                    N("TaggedTexts", N("TaggedText", N("TagTexts", N("TagText", ("TagText", "h00000002g0000g0000g0000g000000000002"), ("LineId", "l2")))))))));
}
