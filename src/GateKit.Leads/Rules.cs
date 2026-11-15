using System.Text.RegularExpressions;

namespace GateKit.Leads;

/// <summary>
/// The rules a lead is built by, in tables a reader can check line by line. Nothing here is a
/// model's judgement: a class comes from the type's name, a kind of entry from the name of the
/// function the walk stopped at, an instrument from the entry and the class. A rule that fails
/// is fixed here, once, for every row.
/// </summary>
public static class Rules
{
    /// <summary>The closed vocabularies of a lead's fields.</summary>
    public static readonly string[] Classes =
        ["gameplay_event", "gameplay_state", "boost", "lifecycle", "cinematic", "multiplayer", "split_screen_controller", "online_ugc", "editor_debug"];

    public static readonly string[] Reachables = ["solo", "needs_pad", "needs_two_copies", "needs_network", "not_in_shipping_game", "unknown"];

    public static readonly string[] Instruments = ["play", "gui", "stage", "osiris", "sweep", "two_copies", "split_screen", "network", "none"];

    public static readonly string[] States = ["open", "in_work", "lit", "parked", "unreachable"];

    public static readonly string[] Outcomes = ["lit", "empty", "blocked"];

    /// <summary>The kinds of entry, most telling first: when the walk meets two at the same depth,
    /// the earlier one wins.</summary>
    public static readonly string[] EntryKinds =
        ["osiris", "message_handler", "functor", "status", "boost", "system_update", "load_save", "replication", "unknown"];

    /// <summary>A type's class by its name: the first line that matches wins. (class, reachable, what
    /// the class means, the pattern on the full type name.)</summary>
    public static readonly (string Class, string Reachable, string Means, Regex Name)[] ClassByName =
    [
        ("online_ugc", "needs_network", "mods from the online browser, Larian's account and its services",
            new(@"^ls::ugc::|^gui::(lariannet|registration)::|::analytics::|Lariannet|CrossPlay|Telemetry", RegexOptions.CultureInvariant)),
        ("editor_debug", "unknown", "the editor, debugging and the developers' tools",
            new(@"Editor|Debug|::dev::|^navcloud::generate::|^mon::|UnitTest|Cheat", RegexOptions.CultureInvariant)),
        ("split_screen_controller", "needs_pad", "a game pad, or the second player of split screen",
            new(@"::controller::|SplitScreen|Gamepad|Controller", RegexOptions.CultureInvariant)),
        ("multiplayer", "needs_two_copies", "a second player over the network: two game copies",
            new(@"::multiplayer::|Multiplayer|Lobby|PeerConnect|Reservation|UserConnect|ConnectedUser|PlayerJoin", RegexOptions.CultureInvariant)),
        ("boost", "solo", "a boost: carried by a spell, status, passive or item of the game's data",
            new(@"BoostComponent$|^esv::boost::", RegexOptions.CultureInvariant)),
        ("cinematic", "solo", "dialogues, timelines, cameras, scenes and their dummies",
            new(@"[Tt]imeline|::camera_spline::|Cinematic|Movie|::dummy::|TLScene", RegexOptions.CultureInvariant)),
        ("lifecycle", "solo", "the session, saving and loading, levels, modules and the menus",
            new(@"GameState|Session|Savegame|SaveGame|LoadLevel|LevelLoad|LevelSwap|Unload|Reload|::mod::|Module|MainMenu|Menu|::character_creation::|CharacterCreation|::gamestate::", RegexOptions.CultureInvariant)),
        ("gameplay_event", "solo", "an event or a request of play: lives a frame or until handled",
            new(@"OneFrame|Event|Request", RegexOptions.CultureInvariant)),
        ("gameplay_state", "solo", "a state of play that lasts",
            new(@".", RegexOptions.CultureInvariant)),
    ];

    /// <summary>The kind of entry a function's name says it is (demangled, as the macOS build and
    /// the names a refresh gives the project give it): the first line that matches wins. (kind, pattern, the group
    /// of the pattern that names it.)</summary>
    public static readonly (string Kind, Regex Name)[] EntryByName =
    [
        ("osiris", new(@"^esv::Osiris\w*Functions::(?<n>\w+)|^esv::osiris::\w+::(?<n>\w+)|^osi::\w+::(?<n>\w+)", RegexOptions.CultureInvariant)),
        ("message_handler", new(@"^(?<n>[\w:]*(Protocol|MessageHandler|NetworkHandler|GameServer|GameClient)[\w:]*::(ProcessMsg|OnMessage|HandleMsg|Process\w*Message)\w*)", RegexOptions.CultureInvariant)),
        ("functor", new(@"^esv::functor::StatsFunctor(?<n>\w+)::|^(?<n>esv::ExecuteStatsFunctor_\w+)|^esv::functor::(?<n>ExecuteStatsFunctors\w*)|StatsFunctor(?<n>\w+)::Execute", RegexOptions.CultureInvariant)),
        ("status", new(@"^esv::(?<n>Status\w*)::(On\w+|Enter|Exit|Update|Apply)\b", RegexOptions.CultureInvariant)),
        ("boost", new(@"^esv::(?<n>[\w:]*Boost\w*System)\b|^esv::boost::(?<n>\w+)|^eoc::(?<n>BoostFactory\w*)", RegexOptions.CultureInvariant)),
        ("system_update", new(@"SystemUpdate<(?<n>[\w:]+System)>|^(?<n>[\w:]+System)::Update\b", RegexOptions.CultureInvariant)),
        ("load_save", new(@"(?<n>ls::savegame::[\w:<>,]*::(Load|Save)\b)|(?<n>[\w:]+::(SystemLoad|OnLoad|PostLoad|OnLevelLoaded|LoadLevel|LoadSession|LoadGame|OnGameStateChanged))\b|^(?<n>App::InitGame|[\w:]*GameState\w*::\w+)", RegexOptions.CultureInvariant)),
        ("replication", new(@"(?<n>ecs::sync::ComponentDeserializer<[\w:]+>)|(?<n>ecs::sync::\w+)", RegexOptions.CultureInvariant)),
    ];

    /// <summary>Plumbing the walk passes through without stopping: the engine's own templates,
    /// which every component goes through and which say nothing of why.</summary>
    public static readonly Regex Plumbing = new(
        @"^(ecs::(legacy::|EntityCommandBuffer|EntityWorld|ComponentOps|_private::(?!SystemRegistrationHelper)|ComponentSignals|QueryRegistry)|ls::(Function|DynamicArray|HashMap|Variant|TypeContext|StaticTypeRegistry)\b|std::)",
        RegexOptions.CultureInvariant);

    /// <summary>The class and reachability of a type by its name alone.</summary>
    public static (string Class, string Reachable) ClassOf(string type)
    {
        foreach (var (cls, reachable, _, name) in ClassByName)
            if (name.IsMatch(type))
                return (cls, reachable);
        return ("gameplay_state", "solo");
    }

    /// <summary>The kind of entry a function name is and what it names, or null for neither.</summary>
    public static (string Kind, string Detail)? EntryOf(string? function)
    {
        if (string.IsNullOrEmpty(function) || function.StartsWith("FUN_", StringComparison.Ordinal))
            return null;
        var name = Demangle(function);
        foreach (var (kind, pattern) in EntryByName)
        {
            var m = pattern.Match(name);
            if (m.Success)
                return (kind, m.Groups["n"].Success && m.Groups["n"].Length > 0 ? m.Groups["n"].Value : name);
        }
        return null;
    }

    /// <summary>The instrument a lead is best taken with: by the entry first, then by the class.</summary>
    public static string InstrumentOf(string cls, string entryKind, string? firstStatKind)
    {
        switch (cls)
        {
            case "online_ugc": return "network";
            case "multiplayer": return "two_copies";
            case "split_screen_controller": return "split_screen";
            case "editor_debug": return "none";
        }
        return entryKind switch
        {
            "osiris" => "osiris",
            "message_handler" => "gui",
            "functor" or "status" => firstStatKind is "StatusData" or "PassiveData" ? "osiris" : "play",
            "boost" => firstStatKind is "StatusData" or "PassiveData" ? "osiris" : firstStatKind is null ? "sweep" : "play",
            "system_update" or "load_save" or "replication" => cls == "boost" && firstStatKind is not null
                ? (firstStatKind is "StatusData" or "PassiveData" ? "osiris" : "play")
                : "play",
            _ => cls == "boost" && firstStatKind is not null ? (firstStatKind is "StatusData" or "PassiveData" ? "osiris" : "play") : "sweep",
        };
    }

    /// <summary>A package of the game that ships to players: not one of the developers' (…Dev).</summary>
    public static bool Shipping(string? mod) => mod is { Length: > 0 } && !mod.EndsWith("Dev", StringComparison.Ordinal);

    /// <summary>The words of a type's own name that say what it is about: the name after the last
    /// <c>::</c> without the engine's suffixes (Component, OneFrame, Event, Request, ...) and without
    /// a leading Has/Is. "HasTargetOverrideComponent" gives "TargetOverride".</summary>
    public static string Core(string type)
    {
        var tail = type[(type.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
        string last;
        do
        {
            last = tail;
            foreach (var suffix in new[] { "Component", "OneFrame", "Singleton", "Event", "Request", "Changed", "Result", "State", "Boost" })
                if (tail.Length > suffix.Length + 3 && tail.EndsWith(suffix, StringComparison.Ordinal))
                    tail = tail[..^suffix.Length];
        } while (tail != last);
        foreach (var prefix in new[] { "Has", "Is" })
            if (tail.Length > prefix.Length + 3 && tail.StartsWith(prefix, StringComparison.Ordinal) && char.IsUpper(tail[prefix.Length]))
                tail = tail[prefix.Length..];
        return tail;
    }

    /// <summary>A boost component's boost name as the game's data writes it: <c>eoc::WeightBoostComponent</c> → Weight.</summary>
    public static string? BoostName(string type)
    {
        var tail = type[(type.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
        return tail.EndsWith("BoostComponent", StringComparison.Ordinal) && tail.Length > "BoostComponent".Length
            ? tail[..^"BoostComponent".Length]
            : null;
    }

    /// <summary>A stats functor's name as the game's data writes it, from its class: StatsFunctorDoTeleport → Teleport... (the data's own spelling is checked against the index).</summary>
    public static IEnumerable<string> FunctorNames(string detail)
    {
        var name = detail[(detail.LastIndexOf("::", StringComparison.Ordinal) + 2)..].Replace("StatsFunctor", "", StringComparison.Ordinal).TrimStart('_');
        if (name.EndsWith("Context", StringComparison.Ordinal)) yield break;   // a context, not a functor
        yield return name;
        if (name.StartsWith("Do", StringComparison.Ordinal) && name.Length > 4) yield return name[2..];
    }

    /// <summary>Namespaces too wide to name a functor by: "Status" matches ApplyStatus in thousands of entries.</summary>
    public static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
        { "Status", "Spell", "SpellCast", "Character", "Item", "Combat", "Party", "Inventory", "Action", "Stats", "Boost", "Passive", "Projectile" };

    /// <summary>A namespace's last part as the game's data spells a name: improvised_weapon → ImprovisedWeapon.</summary>
    public static string Pascal(string type)
    {
        var parts = type.Split("::");
        var space = parts.Length >= 3 ? parts[^2] : "";
        return string.Concat(space.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>The plain name of a mangled Itanium symbol of the form _ZN...E (nested names only);
    /// any other text comes back as it is.</summary>
    public static string Demangle(string name)
    {
        var at = name.IndexOf("_ZN", StringComparison.Ordinal);
        if (at < 0 || at > 2) return name;
        var parts = new List<string>();
        int i = at + 3;
        while (i < name.Length && name[i] is 'K' or 'V' or 'r') i++;
        while (i < name.Length && char.IsAsciiDigit(name[i]))
        {
            int n = 0;
            while (i < name.Length && char.IsAsciiDigit(name[i])) n = n * 10 + (name[i++] - '0');
            if (i + n > name.Length) break;
            parts.Add(name.Substring(i, n));
            i += n;
            if (i < name.Length && name[i] == 'I') break;   // template arguments: the name so far is enough
        }
        return parts.Count > 0 ? string.Join("::", parts) : name;
    }
}
