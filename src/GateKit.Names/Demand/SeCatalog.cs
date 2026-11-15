using System.Text.RegularExpressions;

namespace GateKit.Names.Demand;

/// <summary>
/// What Script Extender's source tree says about the names a mod writes: which engine type an
/// extender component name stands for, which <c>Ext.*</c> functions and <c>Ext.Events</c> exist,
/// and the stats functors. Read from the tree's text, nothing compiled: the component macros of the
/// headers (<c>DEFINE_COMPONENT</c>, <c>DEFINE_ONEFRAME_COMPONENT</c>, <c>DEFINE_PROXY_COMPONENT</c>,
/// <c>DEFINE_TAG_COMPONENT</c>, <c>DEFINE_ONEFRAME_TAG_COMPONENT</c>, <c>DEFN_BOOST</c>), the Lua
/// modules (<c>DECLARE_MODULE</c> ... <c>MODULE_FUNCTION</c>), the Lua built-ins, the events of
/// <c>GameDefinitions/Events.inl</c> and the enumeration <c>stats::FunctorId</c>.
/// A layout's <c>ext_name</c> fills in a name the tree does not declare (<see cref="AddRecord"/>).
/// </summary>
public sealed partial class SeCatalog
{
    readonly Dictionary<string, string> extToEngine = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> engineToExt = new(StringComparer.Ordinal);
    readonly Dictionary<string, HashSet<string>> modules = new(StringComparer.Ordinal);
    readonly HashSet<string> topLevel = new(StringComparer.Ordinal);

    /// <summary>Extender component name -> engine type.</summary>
    public IReadOnlyDictionary<string, string> ExtToEngine => extToEngine;
    /// <summary>Engine type -> its extender component name.</summary>
    public IReadOnlyDictionary<string, string> EngineToExt => engineToExt;
    public HashSet<string> Events { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Functors { get; } = new(StringComparer.Ordinal);
    /// <summary>A boost's stats function name (its BoostType, e.g. <c>AC</c>) -> the boost component.</summary>
    public Dictionary<string, string> Boosts { get; } = new(StringComparer.Ordinal);
    /// <summary>Every member name the headers declare in a struct: an extender component name that is
    /// also one of these (Level, Data, Icon) is not taken from a bare <c>x.Name</c> alone.</summary>
    public HashSet<string> MemberNames { get; } = new(StringComparer.Ordinal);
    /// <summary>Where the component names came from: (source, names).</summary>
    public Dictionary<string, int> Sources { get; } = new(StringComparer.Ordinal);

    [GeneratedRegex(@"\bDEFINE_(?:PROXY_|ONEFRAME_)?COMPONENT\(\s*(\w+)\s*,\s*""([\w:]+)""")]
    private static partial Regex DefineComponent();
    [GeneratedRegex(@"\bDEFINE_(?:ONEFRAME_)?TAG_COMPONENT\(\s*([\w:]+)\s*,\s*(\w+)\s*,\s*(\w+)\s*\)")]
    private static partial Regex DefineTag();
    [GeneratedRegex(@"^\s*DEFN_BOOST\(\s*(\w+)\s*,\s*(\w+)\s*,", RegexOptions.Multiline)]
    private static partial Regex DefineBoost();
    /// <summary>A struct member declaration: <c>    Array&lt;Foo&gt; Bars;</c>, <c>    int32_t Level{ 0 };</c>.</summary>
    [GeneratedRegex(@"^[ \t]{2,}(?!return\b|using\b|typedef\b|static\b|friend\b)[\w:]+(?:<[^;(){}]*>)?[\*&\s]+([A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s*(?:=[^;]*|\{[^;]*\})?;", RegexOptions.Multiline)]
    private static partial Regex Member();
    [GeneratedRegex(@"DECLARE_MODULE\(\s*(\w+)\s*,|MODULE_FUNCTION\(\s*(\w+)\s*\)|MODULE_NAMED_FUNCTION\(\s*""(\w+)""|END_MODULE\(\)")]
    private static partial Regex ModuleParts();
    [GeneratedRegex(@"(?:^|[^\w.])Ext\.(\w+)(?:\.(\w+))?\s*=|function\s+Ext\.(\w+)(?:\.(\w+))?\s*\(|function\s+Ext_(\w+)\.(\w+)\s*\(|^---\s*@class\s+Ext_(\w+)", RegexOptions.Multiline)]
    private static partial Regex LuaDefinition();
    [GeneratedRegex(@"^\s*EVENT(?:_NS)?\(\s*(\w+)\s*,\s*(\w+)", RegexOptions.Multiline)]
    private static partial Regex EventLine();
    [GeneratedRegex(@"BEGIN_ENUM_NS\(\s*stats\s*,\s*FunctorId\b(.*?)END_ENUM", RegexOptions.Singleline)]
    private static partial Regex FunctorEnum();
    [GeneratedRegex(@"\bEV?\(\s*(\w+)")]
    private static partial Regex EnumEntry();

    /// <summary>Lua module names the extender exposes under a client or server prefix in its classes.</summary>
    static string ModuleName(string name) =>
        name.StartsWith("Client", StringComparison.Ordinal) && name.Length > 6 ? name[6..]
        : name.StartsWith("Server", StringComparison.Ordinal) && name.Length > 6 ? name[6..] : name;

    public static SeCatalog FromTree(string tree)
    {
        var catalog = new SeCatalog();
        var root = Path.Combine(tree, "BG3Extender");
        if (!Directory.Exists(root)) throw new NamesException($"{tree} has no BG3Extender folder: not a Script Extender source tree");
        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".inl", StringComparison.OrdinalIgnoreCase)
                                 || p.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => Path.GetFileName(p).Equals("Coverage.h", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                     .ThenBy(p => p, StringComparer.Ordinal))
            catalog.ReadSource(File.ReadAllText(path), Path.GetFileName(path));
        foreach (var path in Directory.EnumerateFiles(root, "*.lua", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            catalog.ReadLua(File.ReadAllText(path));
        return catalog;
    }

    /// <summary>One header or source file; <paramref name="name"/> only labels where names came from.</summary>
    public void ReadSource(string text, string name = "source")
    {
        // GateKit's own generated header names types too; it counts as a separate source, and the
        // author's names come first (files are read in order, the first name met is kept).
        string source = name.Equals("Coverage.h", StringComparison.OrdinalIgnoreCase) ? "fork (Coverage.h)" : "extender headers";
        foreach (Match m in DefineComponent().Matches(text)) Add(m.Groups[1].Value, m.Groups[2].Value, source);
        foreach (Match m in DefineTag().Matches(text)) Add(m.Groups[3].Value, m.Groups[1].Value + "::" + m.Groups[2].Value, source);
        foreach (Match m in DefineBoost().Matches(text))
        {
            Add(m.Groups[1].Value + "Boost", "eoc::" + m.Groups[1].Value + "BoostComponent", source);
            // the stats function a boost is written with (AC(2)) is its BoostType; the component is named after the struct
            Boosts.TryAdd(m.Groups[2].Value, "eoc::" + m.Groups[1].Value + "BoostComponent");
        }
        if (name.EndsWith(".h", StringComparison.OrdinalIgnoreCase))
            foreach (Match m in Member().Matches(text)) MemberNames.Add(m.Groups[1].Value);

        string? module = null;
        foreach (Match m in ModuleParts().Matches(text))
        {
            if (m.Groups[1].Success) module = ModuleName(m.Groups[1].Value);
            else if (m.Value.StartsWith("END_MODULE", StringComparison.Ordinal)) module = null;
            else if (module is not null) Function(module, m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        }
        if (name.Equals("Events.inl", StringComparison.OrdinalIgnoreCase) || text.Contains("EVENT_NS(", StringComparison.Ordinal))
            foreach (Match m in EventLine().Matches(text))
            {
                Events.Add(m.Groups[2].Value);
            }
        if (FunctorEnum().Match(text) is { Success: true } functors)
            foreach (Match e in EnumEntry().Matches(functors.Groups[1].Value))
                if (e.Groups[1].Value != "Extender") Functors.Add(e.Groups[1].Value);
    }

    /// <summary>A Lua file of the extender: built-in functions, and its IDE helpers' module classes.</summary>
    public void ReadLua(string text)
    {
        foreach (Match m in LuaDefinition().Matches(text))
        {
            if (m.Groups[1].Success) { if (m.Groups[2].Success) Function(m.Groups[1].Value, m.Groups[2].Value); else topLevel.Add(m.Groups[1].Value); }
            else if (m.Groups[3].Success) { if (m.Groups[4].Success) Function(m.Groups[3].Value, m.Groups[4].Value); else topLevel.Add(m.Groups[3].Value); }
            else if (m.Groups[5].Success) Function(ModuleName(m.Groups[5].Value), m.Groups[6].Value);
            else if (m.Groups[7].Success) Module(ModuleName(m.Groups[7].Value));
        }
    }

    void Module(string module)
    {
        if (!modules.ContainsKey(module)) modules[module] = new HashSet<string>(StringComparer.Ordinal);
    }

    void Function(string module, string function)
    {
        Module(module);
        modules[module].Add(function);
    }

    public void AddFunction(string module, string function) => Function(module, function);

    public void Add(string ext, string engine, string source)
    {
        if (!extToEngine.TryAdd(ext, engine)) return;
        engineToExt.TryAdd(engine, ext);
        Sources[source] = Sources.GetValueOrDefault(source) + 1;
    }

    /// <summary>The record's own pairs (a layout's component and ext_name), for names the tree lacks.</summary>
    public void AddRecord(IEnumerable<LayoutFile> layouts)
    {
        foreach (var layout in layouts)
            if (!layout.IsSystem && LayoutFile.Text(layout.Json["ext_name"]) is { Length: > 0 } ext && ext != "X" && layout.Component.Contains("::", StringComparison.Ordinal))
                Add(ext, layout.Component, "layouts (ext_name)");
    }

    /// <summary>Whether the extender defines <c>Ext.&lt;module&gt;[.&lt;function&gt;]</c>: true, false,
    /// or null when the module is not one the tree declares (nothing to say either way).</summary>
    public bool? Defines(string module, string? function)
    {
        if (function is null) return modules.ContainsKey(module) || topLevel.Contains(module);
        if (!modules.TryGetValue(module, out var functions)) return topLevel.Contains(module) ? null : false;
        return functions.Contains(function);
    }

    public bool IsModule(string module) => modules.ContainsKey(module);
}
