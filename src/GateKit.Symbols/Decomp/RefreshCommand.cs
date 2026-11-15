using System.Diagnostics;
using System.Globalization;
using System.Text;
using GateKit.Symbols.Queries;

namespace GateKit.Symbols.Decomp;

/// <summary>What one pass of the decompiler is asked for: apply the names and the structures that
/// changed, then decompile again the functions that show them.</summary>
/// <param name="Binary">File name of the binary; its project is in <paramref name="Work"/>.</param>
/// <param name="Work">The binary's work folder (the Ghidra project, console.log).</param>
/// <param name="Names">The names that changed (the symbol database's Ghidra export and the registry's applied functions, only those lines), or null.</param>
/// <param name="Types">The structures that changed (<see cref="RecordTypes.Render"/>), or null.</param>
/// <param name="Helpers">The component helpers that changed (<see cref="RefreshCommand.HelperLines"/>), or null.</param>
/// <param name="Touched">Where the pass lists the functions it decompiles again, one RVA in hex per line.</param>
/// <param name="Output">Where the pass writes them, in the form of the decompiler's export (<see cref="DecompStore.ExportName"/>).</param>
internal sealed record RefreshPass(string Binary, string Work, string? Names, string? Types, string? Helpers, string Touched, string Output, string Memory, int Cpu)
{
    /// <summary>The functions whose first parameter is typed (<see cref="SystemStructures.FunctionLines"/>, and the registry's) that changed, or null.</summary>
    public string? Systems { get; init; }
}

/// <summary>
/// `refresh`: the decompiler's project as a cache of the registry and the databases. Compares what
/// the symbol database, the database's systems and helpers, and the registry's applied set
/// (<see cref="Registry.Applied"/>: names, structures with their applied fields, the first
/// parameter's type) say now with what the binary's Ghidra project was last given, hands the
/// difference to Ghidra, decompiles again only the functions that show a changed thing, and
/// replaces those functions in decomp.sqlite. Nothing on a shown rung of the registry reaches the
/// project.
///
/// The database is never written in place: the work is done on a copy beside it, which is moved
/// over it at the end, so a session reading pseudo-code meanwhile reads the old text to the end.
/// What the project was given is remembered only after that, so a pass that fails is simply made again.
/// </summary>
internal static class RefreshCommand
{
    /// <summary>The lock of the work folder every change of the database takes.</summary>
    public const string Lock = "republish.lock";

    /// <summary>The names the project was last given (written by `run` and by this command).</summary>
    public const string NamesState = "names.tsv";

    /// <summary>The structures the project was last given.</summary>
    public const string TypesState = "types.tsv";

    /// <summary>The component helpers the project was last given (names and return types).</summary>
    public const string HelpersState = "helpers.tsv";

    /// <summary>The functions that take a system first, as the project was last told.</summary>
    public const string SystemsState = "systems.tsv";

    /// <summary>What Ghidra could not apply of a pass's file: the file's name and this, one line per
    /// refusal (rva, name, reason). Written by ApplyNames.java and ApplyHelpers.java.</summary>
    public const string Refused = ".refused";

    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>The decompiler itself: Ghidra without its window over the binary's existing project.</summary>
    public static int Ghidra(DecompSession s, RefreshPass pass)
    {
        var cmd = new List<string> { "-process", pass.Binary, "-noanalysis", "-scriptPath", GhidraCommand.FindScripts() };
        if (pass.Names != null)
            cmd.AddRange(["-preScript", "ApplyNames.java", pass.Names, "rename", pass.Touched]);
        if (pass.Types != null)
            cmd.AddRange(["-preScript", "ApplyTypes.java", pass.Types, pass.Touched]);
        if (pass.Helpers != null)
            cmd.AddRange(["-preScript", "ApplyHelpers.java", pass.Helpers, pass.Touched]);
        if (pass.Systems != null)
            cmd.AddRange(["-preScript", "ApplyThis.java", pass.Systems, pass.Touched]);
        cmd.AddRange(["-postScript", "ExportDecomp.java", pass.Output, "@" + pass.Touched]);
        return GhidraCommand.Launch(s, pass.Work, cmd, pass.Memory, pass.Cpu, "last_refresh.json");
    }

    /// <summary>The scripts a pass asks Ghidra to run, in the order of <see cref="Ghidra"/>.</summary>
    internal static List<string> Scripts(RefreshPass pass)
    {
        var scripts = new List<string>();
        if (pass.Names != null)
            scripts.Add("ApplyNames");
        if (pass.Types != null)
            scripts.Add("ApplyTypes");
        if (pass.Helpers != null)
            scripts.Add("ApplyHelpers");
        if (pass.Systems != null)
            scripts.Add("ApplyThis");
        scripts.Add("ExportDecomp");
        return scripts;
    }

    /// <summary>Lines of a names file by address: (kind, name, decorated name) is what makes a name the same.</summary>
    static Dictionary<string, (string Line, string Key)> NameLines(string text)
    {
        var lines = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var p = line.Split('\t');
            if (p.Length >= 5 && p[0].Length > 0)
                lines[p[0]] = (line, p[1] + "\t" + p[2] + "\t" + p[3]);
        }
        return lines;
    }

    static string ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";

    /// <summary>The helpers of `ecs_helpers` for the pass, one line each: rva (hex), family, type,
    /// name, the structure of the return type (only for a helper that returns the component and a
    /// type the project has a structure of; else empty), basis. A helper at an address the symbol
    /// database names is left out: that name wins.</summary>
    internal static string HelperLines(Microsoft.Data.Sqlite.SqliteConnection db, string binary, ISet<long> named, ISet<string> structures)
    {
        var b = new StringBuilder();
        foreach (var h in HelpersCommand.Load(db, binary).Values.OrderBy(h => h.Rva))
        {
            if (named.Contains(h.Rva))
                continue;
            string returns = h.Returns == "component" && structures.Contains(h.Type) ? h.Type : "";
            b.Append(CultureInfo.InvariantCulture, $"{h.Rva:X}\t{h.Family}\t{h.Type}\t{HelpersCommand.Name(h.Family, h.Type)}\t{returns}\t{h.Basis}\n");
        }
        return b.ToString();
    }

    /// <summary>True for the source column of a name a pair gave (<see cref="PairCommand.Names"/>).</summary>
    internal static bool IsPairBasis(string basis) => basis.StartsWith("macos-anchor: ", StringComparison.Ordinal) || basis.StartsWith("pair ", StringComparison.Ordinal);

    /// <summary>The names the binary's functions take from their pairs in other builds, as lines of
    /// the names file (rva, `function`, name, no decorated name, where from); none at an address
    /// the symbol database or a helper names.</summary>
    internal static string PairLines(Microsoft.Data.Sqlite.SqliteConnection db, string binary, ISet<long> named, ISet<long> helpers, out int count, ISet<long>? given = null)
    {
        var b = new StringBuilder();
        count = 0;
        foreach (var (rva, (name, basis)) in PairCommand.Names(db, binary, given).OrderBy(p => p.Key))
        {
            if (named.Contains(rva) || helpers.Contains(rva))
                continue;
            b.Append(CultureInfo.InvariantCulture, $"{rva:X}\tfunction\t{name.Replace('\t', ' ').Replace('\n', ' ')}\t\t{basis.Replace('\t', ' ').Replace('\n', ' ')}\n");
            count++;
        }
        return b.ToString();
    }

    /// <summary>The names file with the registry's applied functions in it: each named by its id at
    /// its place in the build's table, in the place of any other name the symbol database gives the
    /// address; the source column says the registry and the rung.</summary>
    internal static string WithRegistryNames(string names, IReadOnlyList<System.Text.Json.Nodes.JsonObject> applied)
    {
        var registry = new SortedDictionary<long, string>();
        foreach (var line in applied.Where(l => GateKit.Json.JsonText.Text(l["kind"]) == "function"))
        {
            var rva = Registry.RegistryStore.ParseHex(GateKit.Json.JsonText.Text(line["rva"])!);
            registry[rva] = string.Create(CultureInfo.InvariantCulture, $"{rva:X}\tfunction\t{GateKit.Json.JsonText.Text(line["id"])}\t\tregistry {GateKit.Json.JsonText.Text(line["rung"])}");
        }
        if (registry.Count == 0) return names;
        return string.Concat(names.Split('\n').Where(l => l.Length > 0 && !(Py.Hex(l.Split('\t')[0]) is long r && registry.ContainsKey(r))).Select(l => l + "\n"))
               + string.Concat(registry.Values.Select(l => l + "\n"));
    }

    /// <summary>The functions file of ApplyThis.java with the registry's applied functions whose first
    /// parameter points at a structure the pass gives the project (an applied tie through it, or its
    /// applied type): rva, `registry`, the id, the structure, the rung. A system's own line stands.</summary>
    internal static string WithRegistryThis(string systems, IReadOnlyList<System.Text.Json.Nodes.JsonObject> applied, ISet<string> structures)
    {
        var taken = systems.Split('\n').Where(l => l.Length > 0).Select(l => Py.Hex(l.Split('\t')[0])).OfType<long>().ToHashSet();
        var b = new StringBuilder(systems);
        foreach (var line in applied.Where(l => GateKit.Json.JsonText.Text(l["kind"]) == "function"))
        {
            var rva = Registry.RegistryStore.ParseHex(GateKit.Json.JsonText.Text(line["rva"])!);
            var first = (line["variables"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>()
                .FirstOrDefault(v => GateKit.Json.JsonText.Text(v["storage"]) == "reg:rcx");
            var type = GateKit.Json.JsonText.Text(first?["type_id"])
                       ?? (line["ties"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>()
                           .Where(t => first is not null && GateKit.Json.JsonText.Text(t["via"]) == GateKit.Json.JsonText.Text(first["name"]))
                           .Select(t => GateKit.Json.JsonText.Text(t["type"])).FirstOrDefault();
            if (type is null || !structures.Contains(type) || !taken.Add(rva)) continue;
            b.Append(CultureInfo.InvariantCulture, $"{rva:X}\tregistry\t{GateKit.Json.JsonText.Text(line["id"])}\t{type}\tregistry {GateKit.Json.JsonText.Text(line["rung"])}\n");
        }
        return b.ToString();
    }

    /// <summary>The lines that clear each structure the project was last given and this pass does not
    /// give: its size alone (a field of size 0 is no field).</summary>
    internal static string Cleared(IReadOnlyDictionary<string, string> before, ISet<string> now)
    {
        var b = new StringBuilder();
        foreach (var (name, lines) in before.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (now.Contains(name)) continue;
            var size = lines.Split('\t') is { Length: > 1 } p ? p[1] : "0";
            b.Append(name).Append('\t').Append(size).Append("\t0\t0\tbytes\t").Append(RecordTypes.SizeOnly).Append("\tnot given\n");
        }
        return b.ToString();
    }

    /// <summary>The game build the database records for a binary's bytes, or null.</summary>
    static string? StampedBuild(string dbPath, string binary)
    {
        try
        {
            using var read = Sql.OpenReadOnly(dbPath);
            return (read.Scalar("SELECT game_build FROM binary_meta WHERE binary = ? COLLATE NOCASE", binary) as string)?.Split(',')[0] is { Length: > 0 } b ? b : null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    /// <summary>The lines of a state file without those at the given addresses.</summary>
    static string Without(string text, ISet<long> rvas) => rvas.Count == 0 ? text
        : string.Concat(text.Split('\n').Where(l => l.Length > 0 && !(Py.Hex(l.Split('\t')[0]) is long r && rvas.Contains(r))).Select(l => l + "\n"));

    /// <summary>The refusals Ghidra wrote beside a pass's file: (rva, line).</summary>
    static List<(long Rva, string Line)> RefusedOf(string? file)
    {
        var list = new List<(long, string)>();
        if (file != null && File.Exists(file + Refused))
            foreach (var line in File.ReadLines(file + Refused, Encoding.UTF8))
                if (line.Length > 0 && Py.Hex(line.Split('\t')[0]) is long rva)
                    list.Add((rva, line));
        return list;
    }

    public static int Run(DecompSession s, string binaryName, string? coverage, string memory, int cpu, bool dryRun, int wait,
                          Func<DecompSession, RefreshPass, int>? decompiler = null)
    {
        decompiler ??= Ghidra;
        var started = Stopwatch.StartNew();
        string binary = Path.GetFileName(binaryName);
        string work = Path.Combine(s.Settings.Work, binary);
        string dbPath = s.Settings.DbPath;
        string first = $"the first pass is:  gatekit decomp run {binary}  then  gatekit decomp load {binary}";

        long binaryId;
        if (!File.Exists(dbPath))
            throw new SymbolsException($"{dbPath} does not exist, so there is no pseudo-code to refresh; {first}");
        using (var db = Sql.OpenReadOnly(dbPath))
        {
            binaryId = Sql.Long(db.Scalar("SELECT id FROM binaries WHERE name = ?", binary))
                       ?? throw new SymbolsException($"{dbPath} has no pseudo-code of {binary}; {first}");
        }

        // 1. the names of functions and globals, from the symbol database
        string namesNow = "";
        if (File.Exists(s.Settings.SymbolsDb))
        {
            try
            {
                using var symbols = Sql.OpenReadOnly(s.Settings.SymbolsDb);
                var (module, labels) = GameLabels.Collect(symbols, binary);
                if (string.Equals(module, binary, StringComparison.OrdinalIgnoreCase))
                    namesNow = labels.Count == 0 ? "" : GameLabels.Render(module, labels, "ghidra");
            }
            catch (Exception e) when (e is SymbolsException or Microsoft.Data.Sqlite.SqliteException)
            {
                s.Err.WriteLine($"no names for {binary} in {s.Settings.SymbolsDb}: function and global names are left as they are");
            }
        }
        else
            s.Err.WriteLine($"{s.Settings.SymbolsDb} does not exist: function and global names are left as they are");
        // 1b. the registry's applied set: names, structures with their applied fields, first parameters
        var folder = RecordTypes.FindCoverage(coverage);
        var applied = new List<System.Text.Json.Nodes.JsonObject>();
        if (folder != null && Registry.RegistryStore.Of(folder) is { Exists: true } store)
        {
            var build = StampedBuild(dbPath, binary);
            if (store.BuildFor(binary, build) is { } table && (build is null || table == build))
                applied = [.. Registry.Applied.Of(store, binary, table)];
            else
                s.Err.WriteLine($"the registry has no table of {binary} for build {build ?? "(not stamped)"}: its functions do not reach the project, its structures do");
            if (applied.Count == 0)
                applied = [.. Registry.Applied.Of(store, binary, build ?? "").Where(l => GateKit.Json.JsonText.Text(l["kind"]) == "type")];
        }
        namesNow = WithRegistryNames(namesNow, applied);
        // 2. the structures: the registry's applied set, and the components' sizes of the ledger
        string typesNow = "";
        if (folder != null)
            typesNow = RecordTypes.Render(RecordTypes.FromApplied(applied, folder).Values);
        else
            s.Err.WriteLine("no coverage record found (--coverage, GATEKIT_COVERAGE, or research/coverage of the repository): structures are left as they are");
        // 2b. a structure per system, the functions that take the system first (decomp.sqlite), and the
        // registry's applied functions whose first parameter points at an applied structure
        string systemsNow = "";
        if (folder != null)
        {
            using var db = Sql.OpenReadOnly(dbPath);
            var (sizes, functions, _) = SystemStructures.Build(db, binary);
            typesNow += SystemStructures.TypeLines(sizes, RecordTypes.Split(typesNow).Keys.ToHashSet(StringComparer.Ordinal));
            systemsNow = WithRegistryThis(SystemStructures.FunctionLines(functions), applied, RecordTypes.Split(typesNow).Keys.ToHashSet(StringComparer.Ordinal));
        }
        var systemsBefore = NameLines(ReadIfExists(Path.Combine(work, SystemsState)));
        var resystemed = NameLines(systemsNow).Where(p => !systemsBefore.TryGetValue(p.Key, out var old) || old.Key != p.Value.Key)
            .Select(p => (Rva: Py.Hex(p.Key), p.Value.Line)).Where(p => p.Rva != null).OrderBy(p => p.Rva).ToList();
        var typesBefore = RecordTypes.Split(ReadIfExists(Path.Combine(work, TypesState)));
        // a structure the project was given and is no longer is cleared to its size: what it held was
        // not applied (a field of a shown rung), and the cache keeps nothing the registry does not give
        if (folder != null)
            typesNow += Cleared(typesBefore, RecordTypes.Split(typesNow).Keys.ToHashSet(StringComparer.Ordinal));
        // without a record nothing is known to have changed, and what the project holds is kept
        var retyped = RecordTypes.Split(folder == null ? "" : typesNow).Where(p => !typesBefore.TryGetValue(p.Key, out var old) || old != p.Value)
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

        // 3. the component helpers, from decomp.sqlite (gatekit decomp helpers)
        var namedRvas = NameLines(namesNow).Keys.Select(Py.Hex).OfType<long>().ToHashSet();
        var structures = new HashSet<string>((folder == null ? typesBefore : RecordTypes.Split(typesNow)).Keys, StringComparer.Ordinal);
        string helpersNow;
        using (var db = Sql.OpenReadOnly(dbPath))
            helpersNow = HelperLines(db, binary, namedRvas, structures);
        var helpersBefore = NameLines(ReadIfExists(Path.Combine(work, HelpersState)));
        var current = NameLines(helpersNow);
        var rehelped = current.Where(p => !helpersBefore.TryGetValue(p.Key, out var old) || old.Line != p.Value.Line)
            .Select(p => (Rva: Py.Hex(p.Key), p.Value.Line))
            // a helper the classifier no longer finds is given back its default name (family -),
            // unless the symbol database names the address now: that name is applied above
            .Concat(helpersBefore.Where(p => !current.ContainsKey(p.Key) && !(Py.Hex(p.Key) is long r && namedRvas.Contains(r)))
                .Select(p => (Rva: Py.Hex(p.Key), Line: $"{p.Key}\t-\t{p.Value.Line.Split('\t')[2]}\t\t\tno longer found a helper")))
            .Where(p => p.Rva != null).OrderBy(p => p.Rva).ToList();

        // 4. the names of the functions' pairs in other builds (gatekit decomp pair): only where
        // neither the symbol database nor a helper names the address, so a stronger name stands
        int paired = 0;
        var before = NameLines(ReadIfExists(Path.Combine(work, NamesState)));
        var givenByPairs = before.Where(p => IsPairBasis(p.Value.Line.Split('\t')[^1])).Select(p => Py.Hex(p.Key)).OfType<long>().ToHashSet();
        using (var db = Sql.OpenReadOnly(dbPath))
            namesNow += PairLines(db, binary, namedRvas, current.Keys.Select(Py.Hex).OfType<long>().ToHashSet(), out paired, givenByPairs);
        var renamed = NameLines(namesNow).Where(p => !before.TryGetValue(p.Key, out var old) || old.Key != p.Value.Key)
            .Select(p => (Rva: Py.Hex(p.Key), p.Value.Line)).Where(p => p.Rva != null).OrderBy(p => p.Rva).ToList();
        if (paired > 0)
            s.Err.WriteLine($"{binary}: {paired} functions take their name from the function paired with them in another build");

        if (renamed.Count == 0 && retyped.Count == 0 && rehelped.Count == 0 && resystemed.Count == 0)
        {
            s.Out.WriteLine($"{binary}: nothing was renamed since the last pass; the pseudo-code is up to date");
            return 0;
        }
        string what = $"{renamed.Count} names and {retyped.Count} structures" + (rehelped.Count > 0 ? $" and {rehelped.Count} component helpers" : "")
                      + (resystemed.Count > 0 ? $" and {resystemed.Count} functions of systems" : "") + " changed since the last pass";
        if (dryRun)
        {
            // what the database itself can tell without the decompiler: the renamed functions and their callers
            var functions = new HashSet<long>();
            using (var db = Sql.OpenReadOnly(dbPath))
                foreach (var (rva, _) in renamed.Concat(rehelped).Concat(resystemed))
                {
                    if (db.Scalar("SELECT 1 FROM functions WHERE binary_id = ? AND rva = ?", binaryId, rva) != null)
                        functions.Add(rva!.Value);
                    foreach (var r in db.Query("SELECT caller FROM calls WHERE binary_id = ? AND callee = ?", binaryId, rva))
                        functions.Add(Convert.ToInt64(r[0], CultureInfo.InvariantCulture));
                }
            s.Out.WriteLine($"{binary}: {what}");
            s.Out.WriteLine($"  at least {functions.Count} functions would be decompiled again (the renamed functions and their callers; "
                            + "users of a renamed global or structure are found by the decompiler)");
            return 0;
        }
        if (!Directory.Exists(Path.Combine(work, "project.rep")))
            throw new SymbolsException($"{work} has no Ghidra project of {binary} to decompile from; {first}");

        // One refresh at a time: two would copy the same database and the second would undo the first.
        FileStream gate;
        try
        {
            gate = new FileStream(Path.Combine(s.Settings.Work, Lock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            throw new SymbolsException($"another refresh or change of the database is running in {s.Settings.Work}");
        }
        using (gate)
        {
            string scratch = Path.Combine(work, "refresh");
            Directory.CreateDirectory(scratch);
            var pass = new RefreshPass(binary, work,
                renamed.Count > 0 ? Path.Combine(scratch, "names.tsv") : null,
                retyped.Count > 0 ? Path.Combine(scratch, "types.tsv") : null,
                rehelped.Count > 0 ? Path.Combine(scratch, "helpers.tsv") : null,
                Path.Combine(scratch, "touched.txt"), Path.Combine(scratch, DecompStore.ExportName), memory, cpu)
                { Systems = resystemed.Count > 0 ? Path.Combine(scratch, "systems.tsv") : null };
            File.Delete(pass.Touched);
            File.Delete(pass.Output);
            if (pass.Names != null)
                File.WriteAllText(pass.Names, string.Concat(renamed.Select(r => r.Line + "\n")), Utf8);
            if (pass.Types != null)
                File.WriteAllText(pass.Types, string.Concat(retyped.Select(r => r.Value)), Utf8);
            if (pass.Helpers != null)
                File.WriteAllText(pass.Helpers, string.Concat(rehelped.Select(r => r.Line + "\n")), Utf8);
            if (pass.Systems != null)
                File.WriteAllText(pass.Systems, string.Concat(resystemed.Select(r => r.Line + "\n")), Utf8);
            foreach (var file in new[] { pass.Names, pass.Helpers, pass.Systems })
                if (file != null)
                    File.Delete(file + Refused);
            s.Err.WriteLine($"{binary}: {what}");

            string consoleLog = Path.Combine(work, "console.log");
            long logStart = ScriptCheck.Length(consoleLog);
            int exit = decompiler(s, pass);
            if (exit != 0)
                throw new SymbolsException($"the decompiler ended with {exit} (see {consoleLog}); nothing was changed in {dbPath}");
            // Ghidra ends with 0 also when a script did not compile and was skipped, with the ones after it
            var problems = ScriptCheck.Problems(ScriptCheck.Since(consoleLog, logStart), Scripts(pass));
            if (problems.Count > 0)
                throw new SymbolsException($"the decompiler ended with 0, but its scripts did not all run (see {consoleLog}):\n  "
                                           + string.Join("\n  ", problems) + $"\nnothing was changed in {dbPath}, and the pass is made again next time");

            var lines = new Dictionary<long, string>();
            if (File.Exists(pass.Output))
                foreach (var line in File.ReadLines(pass.Output, Encoding.UTF8))
                    if (FunctionWriter.RvaOf(line) is long rva)
                        lines[rva] = line;

            int added = 0, failed = 0, named = 0;
            if (lines.Count > 0)
            {
                // beside the database, then over it: a reader never sees half a pass
                string building = Path.ChangeExtension(dbPath, ".refreshing");
                File.Copy(dbPath, building, overwrite: true);
                using (var db = DecompStore.OpenWrite(building))
                {
                    db.Execute("BEGIN");
                    using (var writer = new FunctionWriter(db, binaryId))
                    {
                        foreach (var line in lines.Values)
                            writer.Write(line, replace: true);
                        (added, failed) = (writer.Added, writer.Failed);
                    }
                    db.Execute("UPDATE binaries SET functions = (SELECT count(*) FROM functions WHERE binary_id = ?),"
                               + " failed = (SELECT count(*) FROM functions f WHERE f.binary_id = ? AND NOT EXISTS (SELECT 1 FROM code c WHERE c.function_id = f.id))"
                               + " WHERE id = ?", binaryId, binaryId, binaryId);
                    db.Execute("INSERT INTO republishes (binary_id, finished, names, structures, functions) VALUES (?, ?, ?, ?, ?)",
                               binaryId, Py.Now(), renamed.Count, retyped.Count, lines.Count);
                    db.Execute("COMMIT");
                    named = LoadCommand.NameExports(s, db, binary);
                    Provenance.Record(s, db, binary);
                }
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Swap(building, dbPath, wait);
                MergeOutput(DecompStore.ExportPath(work, s.Err), lines);
            }
            else
                s.Err.WriteLine($"{binary}: no function shows what was renamed");

            // what Ghidra refused is said, and not remembered: the next pass offers it again
            var refusedNames = RefusedOf(pass.Names);
            var refusedHelpers = RefusedOf(pass.Helpers);
            var refusedSystems = RefusedOf(pass.Systems);
            foreach (var (kind, list) in new[] { ("names", refusedNames), ("component helpers", refusedHelpers), ("functions of systems", refusedSystems) })
            {
                if (list.Count == 0)
                    continue;
                s.Err.WriteLine($"{binary}: {list.Count} {kind} refused by Ghidra (rva, name, reason); the next pass offers them again:");
                foreach (var (_, line) in list)
                    s.Err.WriteLine("  " + line);
            }

            // only now: a pass that failed above is made again from the same difference
            if (namesNow.Length > 0)
                File.WriteAllText(Path.Combine(work, NamesState), Without(namesNow, refusedNames.Select(r => r.Rva).ToHashSet()), Utf8);
            if (folder != null)
                File.WriteAllText(Path.Combine(work, TypesState), typesNow, Utf8);
            if (pass.Helpers != null || helpersNow.Length > 0)
                File.WriteAllText(Path.Combine(work, HelpersState), Without(helpersNow, refusedHelpers.Select(r => r.Rva).ToHashSet()), Utf8);

            if (pass.Systems != null || systemsNow.Length > 0)
                File.WriteAllText(Path.Combine(work, SystemsState), Without(systemsNow, refusedSystems.Select(r => r.Rva).ToHashSet()), Utf8);

            s.Out.WriteLine($"{binary}: {renamed.Count} names and {retyped.Count} structures"
                            + (rehelped.Count > 0 ? $" and {rehelped.Count} component helpers" : "") + (resystemed.Count > 0 ? $" and {resystemed.Count} functions of systems" : "") + $" applied, {lines.Count} functions decompiled again"
                            + (added > 0 ? $" ({added} of them new)" : "") + (failed > 0 ? $", {failed} without pseudo-code" : "")
                            + (named > 0 ? $", {named} named from the export table" : "")
                            + string.Create(CultureInfo.InvariantCulture, $", in {started.Elapsed.TotalSeconds:F0} s"));
        }
        return 0;
    }

    /// <summary>Puts the lines of a pass into the binary's export, so that a later `load`
    /// builds the same database: a function already there is replaced where it stands, a new one
    /// is added at the end. Nothing happens when the file is not there (a database that was installed).</summary>
    internal static void MergeOutput(string main, Dictionary<long, string> lines)
    {
        if (!File.Exists(main))
            return;
        string merging = main + ".merging";
        var left = new HashSet<long>(lines.Keys);
        using (var reader = new StreamReader(main, Utf8, false, 1 << 20))
        using (var writer = new StreamWriter(merging, false, Utf8, 1 << 20) { NewLine = "\n" })
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (FunctionWriter.RvaOf(line) is long rva && lines.TryGetValue(rva, out var replacement))
                {
                    if (!left.Remove(rva))
                        continue;                         // the same function twice (a run that was resumed): once is enough
                    line = replacement;
                }
                writer.WriteLine(line);
            }
            foreach (long rva in left.Order())
                writer.WriteLine(lines[rva]);
        }
        File.Move(merging, main, overwrite: true);
    }

    /// <summary>Changes the database the way a refresh does: under the work folder's lock, on a
    /// copy beside it (<paramref name="extension"/>) that is moved over it when <paramref name="change"/>
    /// is done. A reader sees the old database whole or the new one whole; a refresh or another
    /// such change running at the same time is an expected failure and nothing is written.</summary>
    internal static void ThroughACopy(DecompSettings settings, string extension, int wait, Action<Microsoft.Data.Sqlite.SqliteConnection> change)
    {
        FileStream gate;
        try
        {
            gate = new FileStream(Path.Combine(settings.Work, Lock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            throw new SymbolsException($"a refresh or another change of the database is running in {settings.Work}; nothing was written");
        }
        using (gate)
        {
            string building = Path.ChangeExtension(settings.DbPath, extension);
            File.Copy(settings.DbPath, building, overwrite: true);
            using (var db = DecompStore.OpenWrite(building))
                change(db);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Swap(building, settings.DbPath, wait);
        }
    }

    /// <summary>Moves the finished copy over the database. On Windows that fails while a reader
    /// holds the file, so it is tried for <paramref name="wait"/> seconds; readers open it per question.</summary>
    internal static void Swap(string building, string dbPath, int wait)
    {
        var until = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(building, dbPath, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (until.Elapsed.TotalSeconds >= wait)
                    throw new SymbolsException($"{dbPath} was held open by a reader for {wait} s and was left as it was; the refreshed database is {building}. "
                                               + "Run the command again when the reader is done (the pass is made again), or move that file over the database.");
                Thread.Sleep(500);
            }
        }
    }
}
