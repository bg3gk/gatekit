using System.Text;
using System.Text.RegularExpressions;
using GateKit.GameData.Text;
using GateKit.Names.Demand;

namespace GateKit.Mods;

/// <summary>
/// The static scan behind <c>gatekit mod usage</c>: reads a mod's text files and writes down what
/// they name. Nothing is run; a file that cannot be read is listed and the scan goes on.
/// <list type="bullet">
/// <item>Lua (Script Extender): components by the extender's names - in a call that names one
///   (<c>GetComponent</c>, <c>CreateComponent</c>, <c>RemoveComponent</c>, <c>HasRawComponent</c>,
///   <c>GetAllEntitiesWithComponent</c>, <c>Ext.Entity.Subscribe/OnChange/OnCreate*/OnDestroy*</c>,
///   <c>Replicate</c>), or as <c>x.Name</c> where the name is not also an ordinary struct member
///   of the extender's headers, or the receiver is named like an entity; the field read or written
///   right after (<c>entity.Health.Hp</c>); <c>Ext.*</c> calls; <c>Ext.Events.*</c>; <c>Osi.*</c>
///   calls (argument counts of literal calls) and <c>Ext.Osiris.RegisterListener("X", n, ...)</c>;
///   net channel names; fields read from an <c>Ext.Stats.Get</c> result. Comments are left out.</item>
/// <item>Stats (<c>Stats/Generated/**/*.txt</c>): entry types; the boosts of boost fields (each
///   mapped to its boost component, which the game creates); the functors of functor and property
///   fields; the values of flag fields.</item>
/// <item>Story goals (<c>Story/**/*.txt</c>): the Osiris functions called, by the data index.</item>
/// <item>XAML: the binding paths and the bound commands.</item>
/// <item>Overrides: files at a path the game itself ships (by the data index).</item>
/// <item><c>meta.lsx</c>: each module's name, UUID, version and folder.</item>
/// </list>
/// </summary>
public sealed partial class UsageScanner(SeCatalog catalog, IReadOnlyDictionary<string, List<(string Kind, int Arity, int Out)>>? osiris,
    Func<string, bool>? shippedByGame = null)
{
    [GeneratedRegex(@"\b(GetComponent|CreateComponent|RemoveComponent|HasRawComponent|GetAllEntitiesWithComponent|Subscribe|OnChange|OnCreate\w*|OnDestroy\w*|Replicate)\s*\(\s*(?:[^,()""']*,\s*)?[""'](\w+)[""']")]
    private static partial Regex NamedCall();
    // the field and the assignment are looked at, not taken, so a.B.C still offers C as a name of its own
    [GeneratedRegex(@"(?<recv>[A-Za-z_]\w*|\)|\])\s*\.\s*(?<name>[A-Z]\w*)\b(?![\(\[""'{])(?=\s*\.\s*(?<field>[A-Za-z_]\w*)\b(?!\s*\()(?<fassign>\s*=(?!=))?)?(?<assign>\s*=(?!=))?")]
    private static partial Regex Dotted();
    [GeneratedRegex(@"([A-Za-z_]\w*)(?:\s*(?:\.\s*\w+|\[[^\]]*\]|\([^()]*\)))*\s*\.?\s*$")]
    private static partial Regex ChainRoot();
    // (not `e`: an event handler's parameter is called e as often as an entity is - e.Key of KeyInput)
    [GeneratedRegex(@"(?i)entit|^ent$|^char|character|item|target|caster|owner|host|player|member|companion|summon|source|^obj|object|dummy|npc|creature|proxy")]
    private static partial Regex EntityLike();
    [GeneratedRegex(@"^\s*---\s*@meta\b", RegexOptions.Multiline)]
    private static partial Regex MetaDefinitions();
    [GeneratedRegex(@"([A-Za-z_]\w*|\))\s*\[\s*[""']([A-Z]\w*)[""']\s*\]")]
    private static partial Regex IndexLiteral();
    [GeneratedRegex(@"\b(\w*(?:Component|Proxy)\w*)\s*\([^()]*?[""']([A-Z]\w*)[""']")]
    private static partial Regex HelperLiteral();
    [GeneratedRegex(@"\{(?:\s*(?:\[\s*\d+\s*\]\s*=\s*)?[""'][A-Za-z_]\w*[""']\s*[,;]?)+\s*\}")]
    private static partial Regex ListLiteral();
    [GeneratedRegex(@"([""'])([A-Z]\w*)\1")]
    private static partial Regex StringLiteral();
    [GeneratedRegex(@"\bExt\.(\w+)(?:\s*\.\s*(\w+))?")]
    private static partial Regex ExtCall();
    [GeneratedRegex(@"\bOsi\s*(?:\.\s*(\w+)|\[\s*[""'](\w+)[""']\s*\])")]
    private static partial Regex OsiName();
    [GeneratedRegex(@"\bOsi\.(\w+)\s*\(([^()]*)\)")]
    private static partial Regex OsiLiteralCall();
    [GeneratedRegex(@"RegisterListener\s*\(\s*[""'](\w+)[""']\s*,\s*(\d+)")]
    private static partial Regex Listener();
    [GeneratedRegex(@"\b(?:CreateChannel\s*\([^,()]*,\s*|(?:PostMessageToServer|PostMessageToClient|PostMessageToUser|BroadcastMessage|RegisterNetListener)\s*\(\s*(?:[^,()""']*,\s*)?)[""']([^""']+)[""']")]
    private static partial Regex NetChannel();
    [GeneratedRegex(@"\b(?:local\s+)?([A-Za-z_]\w*)\s*=\s*Ext\.Stats\.Get\s*\(")]
    private static partial Regex StatsVariable();
    [GeneratedRegex(@"Ext\.Stats\.Get\s*\([^()]*\)\s*\.\s*([A-Za-z_]\w*)")]
    private static partial Regex StatsInline();
    [GeneratedRegex(@"(?<![\w.])([A-Za-z_]\w*)\s*\(")]
    private static partial Regex CallName();
    [GeneratedRegex(@"\{Binding\s+(?:Path=)?([\w.\[\]()]+)")]
    private static partial Regex Binding();
    [GeneratedRegex(@"Command(?:Parameter)?=""\{Binding\s+(?:Path=)?([\w.\[\]]+)")]
    private static partial Regex CommandBinding();
    [GeneratedRegex(@"<attribute\s+id=""(Name|UUID|Version64|Version|Folder)""[^>]*?value=""([^""]*)""")]
    private static partial Regex MetaAttribute();

    static readonly HashSet<string> NotEntities = new(StringComparer.Ordinal)
        { "Ext", "Osi", "Mods", "string", "table", "math", "_G", "_D", "_P", "os", "io", "debug", "utf8", "coroutine", "Game" };

    public ModUsage Scan(ModSource source, string label)
    {
        var usage = new ModUsage { Source = label, HadIndex = osiris is not null || shippedByGame is not null };
        usage.Unreadable.AddRange(source.Unreadable);
        foreach (var file in source.Files)
        {
            usage.FilesTotal++;
            var low = file.Path.ToLowerInvariant();
            string kind = low.EndsWith(".lua", StringComparison.Ordinal) ? "lua"
                : low.EndsWith(".txt", StringComparison.Ordinal) && low.Contains("/stats/generated/", StringComparison.Ordinal) ? "stats"
                : low.EndsWith(".txt", StringComparison.Ordinal) && low.Contains("/story/", StringComparison.Ordinal) ? "goals"
                : low.EndsWith(".xaml", StringComparison.Ordinal) ? "xaml"
                : low.EndsWith("meta.lsx", StringComparison.Ordinal) ? "meta"
                : "";
            if (shippedByGame is not null && ModSource.PackagePath(file.Path) is { } shipped && shippedByGame(shipped))
                usage.Overrides.Add(shipped);
            if (kind.Length == 0) continue;
            string text;
            try { text = Decode(file.Read()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                usage.Unreadable.Add((file.Path, e.Message));
                continue;
            }
            // a definitions file for the editor (LuaLS ---@meta, such as a copy of the extender's IDE helpers)
            // declares the whole API and uses none of it
            if (kind == "lua" && MetaDefinitions().IsMatch(text)) kind = "lua definitions (not scanned)";
            // a meta.lsx at a path the game ships replaces a module of the game's: not the mod's own
            if (kind == "meta" && shippedByGame is not null && ModSource.PackagePath(file.Path) is { } metaPath && shippedByGame(metaPath))
                kind = "meta of a game module (not the mod's)";
            usage.FilesRead[kind] = usage.FilesRead.GetValueOrDefault(kind) + 1;
            try
            {
                switch (kind)
                {
                    case "lua": Lua(usage, text); break;
                    case "stats": Stats(usage, text); break;
                    case "goals": Goals(usage, text); break;
                    case "xaml": Xaml(usage, text); break;
                    case "meta": Meta(usage, text); break;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                usage.Unreadable.Add((file.Path, $"{kind} not understood: {e.Message}"));
            }
        }
        return usage;
    }

    /// <summary>Text of a file: UTF-8 (a byte-order mark honoured), else Latin-1.</summary>
    public static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        try { return reader.ReadToEnd(); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>Lua with its comments blanked out (strings kept; line breaks kept so nothing joins).</summary>
    public static string StripLuaComments(string text)
    {
        var output = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                int start = i++;
                while (i < text.Length && text[i] != c && text[i] != '\n') i += text[i] == '\\' ? 2 : 1;
                i = Math.Min(i + 1, text.Length);
                output.Append(text, start, i - start);
            }
            else if (c == '[' && LongBracket(text, i) is { } level)
            {
                int end = text.IndexOf("]" + new string('=', level) + "]", i, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + level + 2;
                output.Append(text, i, end - i);
                i = end;
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                if (i + 2 < text.Length && text[i + 2] == '[' && LongBracket(text, i + 2) is { } depth)
                {
                    int end = text.IndexOf("]" + new string('=', depth) + "]", i, StringComparison.Ordinal);
                    end = end < 0 ? text.Length : end + depth + 2;
                    foreach (char k in text.AsSpan(i, end - i)) output.Append(k == '\n' ? '\n' : ' ');
                    i = end;
                }
                else
                {
                    while (i < text.Length && text[i] != '\n') i++;
                }
            }
            else
            {
                output.Append(c);
                i++;
            }
        }
        return output.ToString();
    }

    static int? LongBracket(string text, int at)
    {
        int i = at + 1, level = 0;
        while (i < text.Length && text[i] == '=') { level++; i++; }
        return i < text.Length && text[i] == '[' ? level : null;
    }

    static bool IsFieldName(string name) => name.Length > 1 && (char.IsUpper(name[0]) || Names.Tally.IsMachine(name));

    void Lua(ModUsage usage, string raw)
    {
        var text = StripLuaComments(raw);

        foreach (Match m in NamedCall().Matches(text))
        {
            if (!catalog.ExtToEngine.TryGetValue(m.Groups[2].Value, out var type)) continue;
            var use = usage.Component(type, m.Groups[2].Value);
            use.Via.Add("lua");
            switch (m.Groups[1].Value)
            {
                case "GetComponent" or "HasRawComponent" or "GetAllEntitiesWithComponent": use.Read = true; break;
                case "CreateComponent": use.Create = true; break;
                case "RemoveComponent" or "Replicate": use.Write = true; break;
                default: use.Subscribe = true; break;
            }
        }

        foreach (Match m in Dotted().Matches(text))
        {
            var name = m.Groups["name"].Value;
            var receiver = m.Groups["recv"].Value;
            if (NotEntities.Contains(receiver) || !catalog.ExtToEngine.TryGetValue(name, out var type)) continue;
            // a chain that starts at Ext, Osi or Mods is a library's table, not an entity
            var before = LineBefore(text, m.Index + receiver.Length);
            // (a call's result, Ext.Entity.Get(x).Health, is judged by what was called)
            if (receiver is not (")" or "]") && ChainRoot().Match(before) is { Success: true } root
                && NotEntities.Contains(root.Groups[1].Value) && root.Groups[1].Value != receiver) continue;
            bool entityish = EntityLike().IsMatch(receiver)
                || receiver is ")" or "]" && before.Contains("Entity", StringComparison.Ordinal);
            // a name that is also an ordinary member name (Level, Data, Icon), or one plain word (Key, Debug, Net),
            // counts only on an entity-like receiver; a compound name of its own (ServerItem, ActionResources) anywhere
            if (!Distinctive(name) && !entityish) continue;
            var use = usage.Component(type, name);
            use.Via.Add("lua");
            bool assigned = m.Groups["assign"].Success;
            if (m.Groups["field"].Success && IsFieldName(m.Groups["field"].Value))
            {
                use.Fields.Add(m.Groups["field"].Value);
                if (m.Groups["fassign"].Success) use.Write = true; else use.Read = true;
            }
            else if (assigned) use.Write = true;
            else use.Read = true;
        }

        // a component named in a string where only a component name fits: entity["IsSummon"], the argument of a
        // helper named for components or proxies (TryGetProxy(e, "Equipable")), or a table that is a list of
        // names most of which are components ({"Armor", "Data", "Equipable", ...})
        var literals = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in IndexLiteral().Matches(text))
            if (catalog.ExtToEngine.ContainsKey(m.Groups[2].Value) && (EntityLike().IsMatch(m.Groups[1].Value) || Distinctive(m.Groups[2].Value)))
                literals.Add(m.Groups[2].Value);
        // (a widget library's AddComponent("Key") is not an entity's: a plain word needs a proxy helper)
        foreach (Match m in HelperLiteral().Matches(text))
            if (catalog.ExtToEngine.ContainsKey(m.Groups[2].Value)
                && (Distinctive(m.Groups[2].Value) || m.Groups[1].Value.Contains("Proxy", StringComparison.Ordinal)))
                literals.Add(m.Groups[2].Value);
        foreach (Match m in ListLiteral().Matches(text))
        {
            var items = Regex.Matches(m.Value, @"[""']([A-Za-z_]\w*)[""']").Select(s => s.Groups[1].Value).ToList();
            var known = items.Where(catalog.ExtToEngine.ContainsKey).ToList();
            if (known.Count >= 3 && known.Count * 2 >= items.Count) literals.UnionWith(known);
        }
        foreach (var name in literals)
        {
            var use = usage.Component(catalog.ExtToEngine[name], name);
            use.Via.Add("lua");
            if (!(use.Write || use.Create || use.Subscribe)) use.Read = true;
        }

        foreach (Match m in ExtCall().Matches(text))
        {
            var module = m.Groups[1].Value;
            if (!m.Groups[2].Success)
            {
                if (catalog.Defines(module, null) is true) usage.Api.Add("Ext." + module);
                continue;
            }
            var member = m.Groups[2].Value;
            if (module == "Events") { usage.Events.Add(member); continue; }
            // only what the extender defines: a mod's own table hung on Ext is not the extender's API
            if (catalog.IsModule(module) ? catalog.Defines(module, member) is true : catalog.Defines(module, null) is true)
                usage.Api.Add($"Ext.{module}.{member}");
            else if (catalog.Defines(module, null) is true) usage.Api.Add("Ext." + module);
        }

        foreach (Match m in OsiName().Matches(text))
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (osiris is not null && !osiris.ContainsKey(name) && !name.StartsWith("DB_", StringComparison.Ordinal)) continue;
            ModUsage.Add(usage.OsirisCalls, name);
        }
        foreach (Match m in OsiLiteralCall().Matches(text))
        {
            var args = m.Groups[2].Value;
            if (args.Contains('"') || args.Contains('\'')) args = Regex.Replace(args, @"""[^""]*""|'[^']*'", "s");
            ModUsage.Add(usage.OsirisLuaArguments, m.Groups[1].Value, args.Trim().Length == 0 ? 0 : args.Split(',').Length);
        }
        foreach (Match m in Listener().Matches(text))
            ModUsage.Add(usage.OsirisListeners, m.Groups[1].Value, int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        foreach (Match m in NetChannel().Matches(text)) usage.NetChannels.Add(m.Groups[1].Value);

        var statVariables = StatsVariable().Matches(text).Select(m => m.Groups[1].Value).Where(v => v is not ("local" or "self")).ToHashSet(StringComparer.Ordinal);
        foreach (var variable in statVariables)
            foreach (Match m in Regex.Matches(text, $@"(?<![\w.]){Regex.Escape(variable)}\s*\.\s*([A-Z]\w*)"))
                usage.StatsFieldsTouched.Add(m.Groups[1].Value);
        foreach (Match m in StatsInline().Matches(text)) usage.StatsFieldsTouched.Add(m.Groups[1].Value);
    }

    /// <summary>A component name that cannot be anything else: not a member name of the extender's
    /// structs, and a compound word (ServerItem, ActionResources) rather than one plain word.</summary>
    bool Distinctive(string name) => !catalog.MemberNames.Contains(name) && name.Length >= 6 && name.Skip(1).Any(char.IsUpper);

    static string LineBefore(string text, int at)
    {
        int start = Math.Max(text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1, at - 200);   // a minified line is not walked whole
        return text[start..at];
    }

    static bool IsBoostField(string field) => field.Contains("Boost", StringComparison.Ordinal) && !field.Contains("Condition", StringComparison.Ordinal);
    static bool IsFunctorField(string field) =>
        (field.Contains("Functors", StringComparison.Ordinal) || field.Contains("Properties", StringComparison.Ordinal) || field == "OnApply")
        && !field.Contains("Condition", StringComparison.Ordinal) && field != "WeaponProperties" && field != "StatusPropertyFlags";
    static bool IsFlagField(string field) => field.EndsWith("Flags", StringComparison.Ordinal) || field is "WeaponProperties" or "StatusGroups" or "SpellType" or "SpellStyleGroup";

    void Stats(ModUsage usage, string text)
    {
        foreach (var entry in TextParsers.ReadStats(text))
        {
            var type = entry.Type ?? "(no type)";
            usage.StatsTypes[type] = usage.StatsTypes.GetValueOrDefault(type) + 1;
            foreach (var (field, value) in entry.Fields)
            {
                if (IsBoostField(field))
                    foreach (Match m in CallName().Matches(value))
                    {
                        var name = m.Groups[1].Value;
                        if (!catalog.Boosts.TryGetValue(name, out var component)) continue;
                        usage.Boosts[name] = usage.Boosts.GetValueOrDefault(name) + 1;
                        var use = usage.Component(component, catalog.EngineToExt.GetValueOrDefault(component));
                        use.Create = true;
                        use.Via.Add("stats");
                    }
                if (IsFunctorField(field))
                    foreach (Match m in CallName().Matches(value))
                        if (catalog.Functors.Contains(m.Groups[1].Value))
                            usage.Functors[m.Groups[1].Value] = usage.Functors.GetValueOrDefault(m.Groups[1].Value) + 1;
                if (IsFlagField(field))
                {
                    if (!usage.Flags.TryGetValue(field, out var values)) usage.Flags[field] = values = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var v in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) values.Add(v);
                }
            }
        }
    }

    void Goals(ModUsage usage, string text)
    {
        if (osiris is null) return;
        foreach (var line in text.Split('\n'))
        {
            var code = line.Contains("//", StringComparison.Ordinal) ? line[..line.IndexOf("//", StringComparison.Ordinal)] : line;
            foreach (Match m in CallName().Matches(code))
                if (osiris.ContainsKey(m.Groups[1].Value)) usage.GoalCalls.Add(m.Groups[1].Value);
        }
    }

    void Xaml(ModUsage usage, string text)
    {
        foreach (Match m in Binding().Matches(text)) usage.XamlBindings.Add(m.Groups[1].Value);
        foreach (Match m in CommandBinding().Matches(text)) usage.XamlCommands.Add(m.Groups[1].Value);
    }

    void Meta(ModUsage usage, string text)
    {
        // the module of the file: the first ModuleInfo node's attributes
        int info = text.IndexOf("\"ModuleInfo\"", StringComparison.Ordinal);
        if (info < 0) return;
        int end = text.IndexOf("<children>", info, StringComparison.Ordinal);
        var block = end < 0 ? text[info..] : text[info..end];
        var attrs = MetaAttribute().Matches(block).GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => System.Net.WebUtility.HtmlDecode(g.First().Groups[2].Value));
        if (attrs.TryGetValue("Name", out var name) && name.Length > 0)
            usage.Modules.Add((name, attrs.GetValueOrDefault("UUID"), attrs.GetValueOrDefault("Version64") ?? attrs.GetValueOrDefault("Version"), attrs.GetValueOrDefault("Folder")));
    }
}
