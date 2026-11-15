using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Home;

namespace GateKit.Copies;

/// <summary>
/// What `gatekit doctor` says about the home: every location with its source, whether it is there,
/// its size and a live count where it is a database; and what is out of place - a home that is not
/// configured, a path in a state file that points outside the home, a checkout that still has a
/// `.state` of its own. It only looks; nothing is changed or moved.
/// </summary>
public sealed class HomeReport
{
    /// <summary>Shape of <see cref="ToJson"/>; a change of a field's meaning changes the number.</summary>
    public const string Schema = "gatekit.doctor/1";

    /// <summary>One location as found: <see cref="Bytes"/> is null where it is not measured (the home
    /// itself, folders outside it); <see cref="Count"/> is a live row count of a database, or of instances.</summary>
    public sealed record Row(Location Location, bool Exists, long? Bytes, long? Count, string? Counted);

    /// <summary>Something out of place. Kind: default, outside-home, leftover-state, game-copies;
    /// severity: warn (to act on) or info (the documented exception).</summary>
    public sealed record Finding(string Kind, string Severity, string Path, string Detail);

    /// <summary>A game copy the state knows: who started it last (owner.json) and the mod list its
    /// own profile holds (modsettings.lsx - the record of a copy's mods), or null when it has none;
    /// <see cref="Extra"/> are the mods in it beyond the base game and GateKit's own (a bare copy has none).</summary>
    public sealed record Copy(string Name, string? Build, string? Checkout, IReadOnlyList<string>? Mods, string ModsFrom)
    {
        public IReadOnlyList<string> Extra { get; init; } = [];
        /// <summary>The copy's folder among the game copies, or null when only the state knows the name.</summary>
        public string? Root { get; init; }
        /// <summary>The owner.json read; null when none was found (then <see cref="OwnerLookedIn"/> says where it was looked for).</summary>
        public string? OwnerFrom { get; init; }
        public IReadOnlyList<string> OwnerLookedIn { get; init; } = [];
    }

    HomeReport(GateKitHome home, IReadOnlyList<Row> rows, IReadOnlyList<Finding> findings, IReadOnlyList<Copy> copies)
    {
        Home = home;
        Rows = rows;
        Findings = findings;
        Copies = copies;
    }

    public GateKitHome Home { get; }
    public IReadOnlyList<Row> Rows { get; }
    public IReadOnlyList<Finding> Findings { get; }
    public IReadOnlyList<Copy> Copies { get; }

    /// <summary>Looks at every location of <paramref name="home"/>; <paramref name="settings"/>, when
    /// they could be resolved, add the game copies and their folder.</summary>
    public static HomeReport Read(GateKitHome home, CopySettings? settings = null, GateKit.Platform.IGamePlatform? platform = null)
    {
        var rows = home.All.Select(location => Measure(Discover(location, platform))).ToList();
        var findings = new List<Finding>();

        if (!home.Configured)
            findings.Add(new("default", "warn", home.Folder,
                $"home: default, not configured; {rows.Count(r => r.Location.Source == LocationSource.Default)} locations are resolved from the per-user default. "
                + $"Set {GateKitHome.HomeVariable} or \"home\" in a {GateKitHome.ConfigName} above the working folder or the program"));

        // The state of the copies: one folder for every checkout. A checkout that still has its own is a
        // leftover to move into the home, and it is not read.
        var state = home.State.Path!;
        var leftovers = new List<string>();
        if (home.Checkout() is { } checkout)
        {
            foreach (var tree in GateKitHome.Checkouts(checkout))
            {
                var leftover = System.IO.Path.Combine(tree, ".state");
                if (Directory.Exists(leftover) && !SamePath(leftover, state))
                {
                    leftovers.Add(leftover);
                    findings.Add(new("leftover-state", "warn", leftover,
                        $"a checkout's own state folder, not read any more: move what is in it into {state} (instances/, deck/, script-extender.json, sweep/)"));
                }
            }
        }

        // Paths written into state files: everything they point at should be in the home, except
        // the game and its copies.
        var allowed = new List<string>();
        if (settings is not null) allowed.AddRange([settings.InstancesRoot, settings.GameFolder]);
        foreach (var (file, key, value) in StatePaths(state))
        {
            if (home.Contains(value) || allowed.Any(a => Inside(value, a))) continue;
            findings.Add(new("outside-home", "warn", value, $"{file} (\"{key}\") points outside the home; move it in and rewrite the path"));
        }

        if (settings is not null)
            findings.Add(new("game-copies", "info", settings.InstancesRoot,
                "the game copies stay beside the game, on its volume: a copy is made of hard links, which cannot cross volumes. "
                + "They are the one thing GateKit keeps outside the home (GATEKIT_INSTANCES or \"instances\" moves them)"));

        var copies = ReadCopies(state, leftovers, settings, platform);
        foreach (var copy in copies.Where(c => c.Extra.Count > 0))
            findings.Add(new("copy-mods", "warn", copy.ModsFrom,
                $"copy {copy.Name} is not bare: its mod list loads {string.Join(", ", copy.Extra)} besides the base game and GateKit's own; "
                + "its next `up` without --mods starts it bare again"));
        return new HomeReport(home, rows, findings, copies);
    }

    /// <summary>The mods a bare copy runs: the base game and the bridge mods of this checkout.</summary>
    static HashSet<string> OwnMods(CopySettings? settings)
    {
        var own = new HashSet<string>(ModSettings.BaseMods, StringComparer.Ordinal) { ModSettings.BridgeFolder };
        if (settings?.BridgeSource is { } source)
        {
            try { own.UnionWith(BridgeMods.Find(source).Select(m => m.Folder)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CopyException) { }
        }
        return own;
    }

    /// <summary>Every copy the state knows or the copies' folder holds. Its owner record is read from
    /// the home's state, else from a checkout's leftover state (said as such); a copy with none says
    /// where it was looked for.</summary>
    static List<Copy> ReadCopies(string state, IReadOnlyList<string> leftovers, CopySettings? settings, GateKit.Platform.IGamePlatform? platform)
    {
        var own = OwnMods(settings);
        var copies = new List<Copy>();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var instances = System.IO.Path.Combine(state, "instances");
        if (Directory.Exists(instances)) names.UnionWith(Directory.EnumerateDirectories(instances).Select(d => System.IO.Path.GetFileName(d)));
        var onDisk = new HashSet<string>(StringComparer.Ordinal);
        if (settings is not null)
        {
            try { onDisk.UnionWith(Instance.Names(settings.InstancesRoot)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            names.UnionWith(onDisk);
        }
        foreach (var name in names)
        {
            var looked = new List<string> { System.IO.Path.Combine(instances, name, "owner.json") };
            looked.AddRange(leftovers.Select(l => System.IO.Path.Combine(l, "instances", name, "owner.json")));
            JsonObject owner = [];
            string? ownerFrom = null;
            foreach (var path in looked.Where(File.Exists))
            {
                try { owner = SharedFile.Read(path, GameCopy.OwnerVersion); }
                catch (HomeException) { continue; }
                if (owner.Count == 0) continue;
                ownerFrom = path;
                break;
            }
            IReadOnlyList<string>? mods = null;
            var from = "no profile of the copy found";
            if (settings is not null && platform is not null)
            {
                try
                {
                    var profile = platform.InstanceProfileFolder(System.IO.Path.Combine(settings.InstancesRoot, name));
                    var list = System.IO.Path.Combine(profile, "PlayerProfiles", "Public", "modsettings.lsx");
                    from = list;
                    if (File.Exists(list)) mods = ModSettings.Folders(File.ReadAllText(list));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            copies.Add(new Copy(name, owner["build"]?.ToString(), owner["checkout"]?.ToString(), mods, from)
            {
                Extra = mods?.Where(m => !own.Contains(m)).ToList() ?? [],
                Root = settings is not null && onDisk.Contains(name) ? System.IO.Path.Combine(settings.InstancesRoot, name) : null,
                OwnerFrom = ownerFrom,
                OwnerLookedIn = looked,
            });
        }
        return copies;
    }

    /// <summary>The game and the Toolkit as the tools find them when no setting names them: in a
    /// Steam library, the one it was found in being where it came from. A location still not found
    /// says where it was looked for.</summary>
    static Location Discover(Location location, GateKit.Platform.IGamePlatform? platform)
    {
        if (location.Path is not null || platform is null || location.Id is not ("game" or "toolkit")) return location;
        IReadOnlyList<string> libraries;
        try { libraries = platform.SteamLibraries(); }
        catch (Exception e) when (e is PlatformNotSupportedException or IOException or UnauthorizedAccessException) { return location; }
        foreach (var library in libraries)
        {
            var found = location.Id == "game"
                ? GateKit.Platform.Steam.GameFolderIn(library)
                : GateKit.Platform.Steam.AppFolderIn(library, GateKit.Platform.GamePlatform.ToolkitAppId, GateKit.Platform.GamePlatform.ToolkitInstallDir);
            if (found is not null)
                return location with { Path = found, Source = LocationSource.Discovered, Origin = $"the Steam library {library}" };
        }
        return location with { Origin = libraries.Count == 0 ? "no Steam library found" : "in none of the Steam libraries: " + string.Join(", ", libraries) };
    }

    static Row Measure(Location location)
    {
        if (location.Path is not { } path) return new(location, false, null, null, null);
        if (location.Kind == LocationKind.Database)
        {
            if (!File.Exists(path)) return new(location, false, null, null, null);
            var bytes = new FileInfo(path).Length;
            var (count, counted) = location.Id switch
            {
                "index" => Count(path, "SELECT count(*) FROM objects", "objects"),
                "symbols" => Count(path, "SELECT (SELECT count(*) FROM symbols), (SELECT count(*) FROM se_types)", "symbols", "layouts"),
                "decomp-db" => Count(path, "SELECT count(*) FROM functions", "functions"),
                _ => Count(path, "SELECT count(*) FROM sqlite_master WHERE type = 'table'", "tables"),
            };
            return new(location, true, bytes, count, counted);
        }
        if (!Directory.Exists(path)) return new(location, false, null, null, null);
        var outside = location.Id is "home" or "game" or "toolkit" or "se-fork";
        long? size = outside ? null : FolderSize(path);
        if (location.Id == "state")
        {
            var instances = System.IO.Path.Combine(path, "instances");
            long known = Directory.Exists(instances) ? Directory.EnumerateDirectories(instances).Count() : 0;
            return new(location, true, size, known, $"{known} instances known");
        }
        return new(location, true, size, null, null);
    }

    /// <summary>Live counts of a database: the first column is the count, the others are said beside it.</summary>
    static (long?, string?) Count(string path, string sql, params string[] names)
    {
        try
        {
            using var db = HomeDatabase.OpenReadOnly(path);
            using var command = db.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return (null, null);
            var counts = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetInt64(i)).ToList();
            var text = string.Join(", ", counts.Select((c, i) => c.ToString("N0", CultureInfo.InvariantCulture) + " " + names[i]));
            return (counts[0], text);
        }
        catch (Exception e) when (e is MissingDatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            return (null, "unreadable: " + e.Message.Split('\n')[0]);
        }
    }

    static long FolderSize(string folder)
    {
        long total = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options)) total += file.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>Every absolute path held in the state files `gatekit` writes: the remembered Script
    /// Extender build, each copy's switch record, the deck's files.</summary>
    static IEnumerable<(string File, string Key, string Value)> StatePaths(string state)
    {
        var files = new List<string> { System.IO.Path.Combine(state, "script-extender.json") };
        var instances = System.IO.Path.Combine(state, "instances");
        if (Directory.Exists(instances))
            files.AddRange(Directory.EnumerateDirectories(instances).Select(d => System.IO.Path.Combine(d, "sefork.json")));
        var deck = System.IO.Path.Combine(state, "deck");
        if (Directory.Exists(deck)) files.AddRange(Directory.EnumerateFiles(deck, "*.json"));
        foreach (var file in files.Where(File.Exists))
        {
            JsonNode? root;
            try { root = JsonNode.Parse(File.ReadAllText(file)); }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { continue; }
            foreach (var (key, value) in Strings(root, ""))
            {
                if (System.IO.Path.IsPathFullyQualified(value)) yield return (file, key, value);
            }
        }
    }

    static IEnumerable<(string Key, string Value)> Strings(JsonNode? node, string key) => node switch
    {
        JsonObject o => o.SelectMany(p => Strings(p.Value, key.Length == 0 ? p.Key : key + "." + p.Key)),
        JsonArray a => a.SelectMany((n, i) => Strings(n, $"{key}[{i}]")),
        JsonValue v when v.TryGetValue<string>(out var s) => [(key, s)],
        _ => [],
    };

    static string Trim(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    static bool SamePath(string a, string b) => string.Equals(Trim(a), Trim(b), GateKitHome.PathComparison);
    static bool Inside(string path, string folder) =>
        SamePath(path, folder) || Trim(path).StartsWith(Trim(folder) + System.IO.Path.DirectorySeparatorChar, GateKitHome.PathComparison);

    static string Size(long? bytes) => bytes switch
    {
        null => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024:0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"),
    };

    /// <summary>A location's path as the table shows it: what found it when it was looked for, the
    /// setting that names it and where it was looked for when nothing has.</summary>
    static string Where(Location location) => location switch
    {
        { Path: null } => string.IsNullOrEmpty(location.Origin) ? $"({location.Change})" : $"({location.Change}; {location.Origin})",
        { Source: LocationSource.Discovered, Id: "game" or "toolkit" } => $"{location.Path}  (found in {location.Origin})",
        _ => location.Path,
    };

    /// <summary>The locations as a table, then the findings.</summary>
    public void Write(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"home: {Home.Folder} ({(Home.Configured ? $"{Rows[0].Location.SourceName}: {Home.Origin}" : "default, not configured")})");
        if (Home.ConfigFiles.Count > 0) output.WriteLine("gatekit.json read: " + string.Join(", ", Home.ConfigFiles));
        var idWidth = Rows.Max(r => r.Location.Id.Length);
        output.WriteLine($"  {"location".PadRight(idWidth)}  {"source",-12}  {"there",-5}  {"size",9}  {"count",-30}  path");
        foreach (var row in Rows)
        {
            output.WriteLine($"  {row.Location.Id.PadRight(idWidth)}  {row.Location.SourceName,-12}  {(row.Exists ? "yes" : "no"),-5}  {Size(row.Bytes),9}  "
                + $"{row.Counted ?? "",-30}  {Where(row.Location)}");
        }
        foreach (var copy in Copies)
        {
            output.WriteLine($"  copy {copy.Name}{(copy.Root is null ? " (no folder among the game copies)" : $" ({copy.Root})")}: "
                + (copy.OwnerFrom is null
                    ? $"no owner record (looked in {string.Join(", ", copy.OwnerLookedIn)})"
                    : $"last started by GateKit {copy.Build} from {copy.Checkout ?? "an installed GateKit"} (owner: {copy.OwnerFrom}"
                      + (copy.OwnerFrom == copy.OwnerLookedIn[0] ? ")" : ", a checkout's leftover state)"))
                + "; mods: " + (copy.Mods is null ? $"none recorded ({copy.ModsFrom})" : copy.Mods.Count == 0 ? "none" : string.Join(", ", copy.Mods))
                + (copy.Extra.Count > 0 ? " - not bare" : ""));
        }
        foreach (var finding in Findings)
            output.WriteLine($"  {(finding.Severity == "warn" ? "warn" : "note")}  {finding.Kind}: {finding.Path}\n        -> {finding.Detail}");
    }

    /// <summary>The report as JSON, in the stable shape <see cref="Schema"/> names.</summary>
    public JsonObject ToJson(IReadOnlyList<Doctor.Check>? checks = null) => new()
    {
        ["schema"] = Schema,
        ["home"] = new JsonObject
        {
            ["path"] = Home.Folder,
            ["source"] = Rows[0].Location.SourceName,
            ["origin"] = Home.Origin,
            ["configured"] = Home.Configured,
            ["config_files"] = new JsonArray([.. Home.ConfigFiles.Select(f => (JsonNode)f)]),
        },
        ["locations"] = new JsonArray([.. Rows.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Location.Id,
            ["path"] = r.Location.Path,
            ["kind"] = r.Location.Kind == LocationKind.Database ? "database" : "folder",
            ["source"] = r.Location.SourceName,
            ["origin"] = r.Location.Origin,
            ["exists"] = r.Exists,
            ["bytes"] = r.Bytes,
            ["count"] = r.Count,
            ["counted"] = r.Counted,
            ["change"] = r.Location.Change,
            ["purpose"] = r.Location.Purpose,
        })]),
        ["instances"] = new JsonArray([.. Copies.Select(c => (JsonNode)new JsonObject
        {
            ["name"] = c.Name,
            ["build"] = c.Build,
            ["checkout"] = c.Checkout,
            ["mods"] = c.Mods is null ? null : new JsonArray([.. c.Mods.Select(m => (JsonNode)m)]),
            ["mods_from"] = c.ModsFrom,
            ["root"] = c.Root,
            ["owner_from"] = c.OwnerFrom,
            ["owner_looked_in"] = new JsonArray([.. c.OwnerLookedIn.Select(p => (JsonNode)p)]),
            ["extra_mods"] = new JsonArray([.. c.Extra.Select(m => (JsonNode)m)]),
        })]),
        ["findings"] = new JsonArray([.. Findings.Select(f => (JsonNode)new JsonObject
        {
            ["kind"] = f.Kind, ["severity"] = f.Severity, ["path"] = f.Path, ["detail"] = f.Detail,
        })]),
        ["checks"] = new JsonArray([.. (checks ?? []).Select(c => (JsonNode)new JsonObject
        {
            ["name"] = c.Name,
            ["verdict"] = c.Verdict switch { Doctor.Verdict.Good => "ok", Doctor.Verdict.Warn => "warn", _ => "fail" },
            ["detail"] = c.Detail,
            ["fix"] = c.Fix,
        })]),
        ["ready"] = checks is null ? null : checks.All(c => c.Verdict != Doctor.Verdict.Bad),
    };
}
