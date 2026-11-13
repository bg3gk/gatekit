using System.Xml;
using LSLib.LS;

namespace GateKit.GameData.Resources;

/// <summary>
/// A reader of a resource file as a stream of events: a node opens, an attribute of the node
/// currently open, a node closes. Events come in the order an .lsx file written by LSLib lists
/// them: a node's attributes first, then its children. Every reader keeps its own stack of open
/// node ids, so several readers can listen to one pass over a file.
/// </summary>
public abstract class ResourceReader
{
    /// <summary>Ids of the nodes open right now; the last one is the innermost.</summary>
    protected List<string> Stack { get; } = [];

    internal void Open(string id)
    {
        Stack.Add(id);
        OnNode(id);
    }

    internal void Close()
    {
        var id = Stack[^1];
        Stack.RemoveAt(Stack.Count - 1);
        OnNodeEnd(id);
    }

    internal void Attribute(string id, string? value) => OnAttribute(id, value);

    /// <summary>A node opened; it is already on <see cref="Stack"/>.</summary>
    protected virtual void OnNode(string id) { }

    /// <summary>A node closed; it is no longer on <see cref="Stack"/>.</summary>
    protected virtual void OnNodeEnd(string id) { }

    /// <summary>An attribute of the innermost open node. The value is the text an .lsx file holds.</summary>
    protected virtual void OnAttribute(string id, string? value) { }

    /// <summary>The innermost open node, or "" when none is.</summary>
    protected string Top => Stack.Count > 0 ? Stack[^1] : "";

    /// <summary>The node <paramref name="back"/> levels above the innermost one (1 is the innermost).</summary>
    protected string Up(int back) => Stack[^back];
}

/// <summary>Feeds resource files to readers.</summary>
public static class ResourceEvents
{
    private static readonly NodeSerializationSettings Settings = new();

    /// <summary>Reads a resource file (.lsf, or .lsx when <paramref name="lsx"/>) and feeds every reader.</summary>
    public static void Read(Stream stream, bool lsx, params ResourceReader[] readers)
    {
        if (lsx)
        {
            ReadLsx(stream, readers);
        }
        else
        {
            using var reader = new LSFReader(stream, keepOpen: true);
            Walk(reader.Read(), readers);
        }
    }

    /// <summary>Feeds a resource already in memory, node by node, as LSLib would write it to .lsx.</summary>
    public static void Walk(Resource resource, params ResourceReader[] readers)
    {
        foreach (var region in resource.Regions.Values)
        {
            WalkNode(region, readers);
        }
    }

    private static void WalkNode(Node node, ResourceReader[] readers)
    {
        foreach (var r in readers)
        {
            r.Open(node.Name);
        }
        foreach (var (id, attribute) in node.Attributes)
        {
            var value = LsxValue(attribute);
            foreach (var r in readers)
            {
                r.Attribute(id, value);
            }
        }
        foreach (var children in node.Children.Values)
        {
            foreach (var child in children)
            {
                WalkNode(child, readers);
            }
        }
        foreach (var r in readers)
        {
            r.Close();
        }
    }

    /// <summary>
    /// The value of an attribute as text, as an .lsx file shows it: the <c>value</c> LSLib writes,
    /// or the <c>handle</c> of a translated string that has no value (dialog lines are such
    /// strings). Numbers are written by <see cref="NumberText"/>; a matrix is its numbers in file
    /// order (column by column), separated by spaces.
    /// </summary>
    public static string LsxValue(NodeAttribute attribute)
    {
        switch (attribute.Type)
        {
            case AttributeType.TranslatedString:
                var ts = (TranslatedString)attribute.Value;
                return ts.Value != null ? ts.ToString() : ts.Handle ?? "";
            case AttributeType.TranslatedFSString:
                var fs = (TranslatedFSString)attribute.Value;
                return string.IsNullOrEmpty(fs.Value) ? fs.Handle ?? "" : fs.Value;
            case AttributeType.Float:
                return NumberText.Float((float)attribute.Value);
            case AttributeType.Double:
                return NumberText.Double((double)attribute.Value);
            case AttributeType.Vec2 or AttributeType.Vec3 or AttributeType.Vec4:
                return string.Join(' ', ((float[])attribute.Value).Select(NumberText.Float));
            case AttributeType.Mat2 or AttributeType.Mat3 or AttributeType.Mat3x4 or AttributeType.Mat4x3 or AttributeType.Mat4:
                var m = (Matrix)attribute.Value;
                var cells = new List<string>(m.rows * m.cols);
                for (var col = 0; col < m.cols; col++)
                {
                    for (var row = 0; row < m.rows; row++)
                    {
                        cells.Add(NumberText.Float((float)m[row, col]));
                    }
                }
                return string.Join(' ', cells);
            default:
                return attribute.AsString(Settings).Replace("\x1f", "", StringComparison.Ordinal);
        }
    }

    /// <summary>Reads an .lsx document element by element. A broken document still gives the events before the break.</summary>
    private static void ReadLsx(Stream stream, ResourceReader[] readers)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false,
        };
        using var xml = XmlReader.Create(stream, settings);
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element)
            {
                if (xml.LocalName == "attribute")
                {
                    var id = xml.GetAttribute("id") ?? throw new XmlException("attribute without id");
                    var value = xml.GetAttribute("value") ?? xml.GetAttribute("handle");
                    foreach (var r in readers)
                    {
                        r.Attribute(id, value);
                    }
                }
                else if (xml.LocalName == "node")
                {
                    var id = xml.GetAttribute("id") ?? "";
                    var empty = xml.IsEmptyElement;
                    foreach (var r in readers)
                    {
                        r.Open(id);
                    }
                    if (empty)
                    {
                        foreach (var r in readers)
                        {
                            r.Close();
                        }
                    }
                }
            }
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "node")
            {
                foreach (var r in readers)
                {
                    r.Close();
                }
            }
        }
    }
}
