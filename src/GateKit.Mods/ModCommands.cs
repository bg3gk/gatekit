using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Names;
using GateKit.Names.Demand;
using GateKit.Symbols;
using Microsoft.Data.Sqlite;

namespace GateKit.Mods;

/// <summary>The <c>mod</c> command: what mods use of the game, by a static scan of their text.</summary>
public static class ModCommands
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>What a scan needs from outside the mod: the extender's names, the Osiris functions, the game's file list.</summary>
    sealed class Context : IDisposable
    {
        public required SeCatalog Catalog;
        public Dictionary<string, List<(string Kind, int Arity, int Out)>>? Osiris;
        public SqliteConnection? Index;
        readonly Dictionary<string, bool> shipped = new(StringComparer.Ordinal);

        public bool Shipped(string path)
        {
            if (Index is null) return false;
            if (!shipped.TryGetValue(path, out var found))
                shipped[path] = found = Index.One("SELECT 1 FROM files WHERE path = ? LIMIT 1", path) is not null;
            return found;
        }

        public UsageScanner Scanner() => new(Catalog, Osiris, Index is null ? null : Shipped);

        public void Dispose() => Index?.Dispose();
    }

    static Context Prepare(string? tree, string? index, TextWriter error)
    {
        var catalog = SeCatalog.FromTree(NamesPaths.ForkTree(tree));
        // the record's own pairs fill in names the tree lacks, when this runs inside a checkout
        try { catalog.AddRecord(LayoutStore.All(NamesPaths.CoverageRoot())); }
        catch (NamesException) { }
        var path = index ?? GateKit.Release.DatabaseKind.Index.DefaultPath;
        var context = new Context { Catalog = catalog, Osiris = DemandFile.OsirisIndex(path) };
        if (context.Osiris is null) error.WriteLine(GateKit.Home.HomeDatabase.LeftOut(path, "the Osiris check, story goals and overrides")
            ?? $"note: {path} has no Osiris functions: Osiris names are not checked, story goals and overrides are not read");
        else context.Index = Sql.OpenReadOnly(path);
        return context;
    }

    static ModUsage ScanOne(Context context, string path, string? label = null)
    {
        using var source = ModSource.Open(path);
        return context.Scanner().Scan(source, label ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))));
    }

    public static Command CreateCommand()
    {
        var root = new Command("mod", "what a mod uses of the game, by a static scan of its text (Lua, stats, story goals, XAML, its file list): "
            + "nothing of a mod is ever executed or loaded. `usage` scans mods; `demand` counts the same over any set of them; "
            + "`unpack` writes a .pak out as files to read");
        var json = new Option<bool>("--json") { Description = "the answer as JSON", Recursive = true };
        var tree = new Option<string?>("--tree") { Description = "the Script Extender source tree the extender's names are mapped with (default: the home's, GATEKIT_SE_FORK or \"seFork\" in gatekit.json)", Recursive = true };
        var index = new Option<string?>("--index") { Description = "the game's data index: Osiris functions and the game's own files (default BG3_INDEX_DB, else <home>/db/index/gameindex.sqlite)", Recursive = true };
        root.Options.Add(json);
        root.Options.Add(tree);
        root.Options.Add(index);

        int Run(ParseResult p, Func<TextWriter, TextWriter, int> body)
        {
            var output = p.InvocationConfiguration.Output;
            var error = p.InvocationConfiguration.Error;
            try { return body(output, error); }
            catch (Exception e) when (e is ModsException or NamesException or SymbolsException)
            {
                error.WriteLine(e.Message);
                return 1;
            }
        }

        // usage
        {
            var paths = new Argument<string[]>("path") { Description = "an unpacked mod folder or a .pak; several may be given", Arity = ArgumentArity.OneOrMore };
            var outDir = new Option<string?>("--out") { Description = "write each mod's JSON document into this folder as <name>.json instead of printing it" };
            var c = new Command("usage", "what one mod uses, as a document (schema gatekit.mod-usage/1 with --json): the engine components its Lua "
                + "reads, writes, creates or subscribes to (by the extender's names, mapped to engine types from the Script Extender source) and the "
                + "fields it names on them, Ext.* calls, events, Osiris calls and listeners, net channels, Ext.Stats fields; stats entry types, "
                + "boosts (and the boost components they create), functors and flags; Osiris calls of story goals; XAML bindings and commands; "
                + "files at a path the game ships. A file that cannot be read is counted and listed") { paths, outDir };
            c.SetAction(p => Run(p, (output, error) =>
            {
                using var context = Prepare(p.GetValue(tree), p.GetValue(index), error);
                foreach (var path in p.GetValue(paths)!)
                {
                    var usage = ScanOne(context, path);
                    if (p.GetValue(outDir) is { } folder)
                    {
                        Directory.CreateDirectory(folder);
                        var file = Path.Combine(folder, Safe(usage.Source) + ".json");
                        File.WriteAllText(file, usage.ToJson().ToJsonString(Indented).ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
                        output.WriteLine($"{path} -> {file}");
                    }
                    else if (p.GetValue(json)) output.WriteLine(usage.ToJson().ToJsonString(Indented).ReplaceLineEndings("\n"));
                    else Print(output, usage);
                }
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // unpack
        {
            var pak = new Argument<string>("pak") { Description = "the .pak to unpack" };
            var into = new Option<string?>("--out") { Description = "the folder to write its files into (default <home>/cache/unpack/<pak name>); never a game folder or a game profile" };
            var replace = new Option<bool>("--replace") { Description = "empty the folder first when it holds something" };
            var c = new Command("unpack", "write a .pak's files out as plain files, to read them: nothing of the mod is run, loaded or installed, "
                + "and nothing is written where a game would load it. Says where the files are") { pak, into, replace };
            c.SetAction(p => Run(p, (output, _) =>
            {
                var file = p.GetValue(pak)!;
                var (folder, files) = ModUnpack.Unpack(file, p.GetValue(into) ?? ModUnpack.DefaultFolder(file), p.GetValue(replace));
                if (p.GetValue(json)) output.Write(GateKit.Json.JsonText.Format(new JsonObject { ["pak"] = Path.GetFullPath(file), ["folder"] = folder, ["files"] = files }));
                else output.WriteLine($"{files} files of {Path.GetFileName(file)} unpacked into {folder}");
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // demand
        {
            var inputs = new Argument<string[]>("input") { Description = "usage documents (.json), mod folders or .pak files", Arity = ArgumentArity.OneOrMore };
            var each = new Option<bool>("--each") { Description = "each folder given holds mods: every subfolder and .pak in it is one mod (names starting with _ or . are left out)" };
            var outFile = new Option<string?>("--out") { Description = "the demand file to write (default <coverage>/demand.jsonl of this checkout)" };
            var usageOut = new Option<string?>("--usage-out") { Description = "also write each scanned mod's usage document into this folder" };
            var c = new Command("demand", "the demand over a set of mods: every engine type, Osiris function, Ext.* call, event, functor and boost "
                + "the mods use, counted by mods, with the fields named on each type; written as a demand file (one JSON row per line, "
                + "a header naming the mods and the scanner) for `gatekit names demand report` and `gatekit names worst --by demand`") { inputs, each, outFile, usageOut };
            c.SetAction(p => Run(p, (output, error) =>
            {
                var mods = new List<ModUsage>();
                Context? context = null;
                try
                {
                    Context Scan() => context ??= Prepare(p.GetValue(tree), p.GetValue(index), error);
                    foreach (var input in p.GetValue(inputs)!)
                    {
                        if (File.Exists(input) && input.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            mods.Add(ModUsage.FromJson(JsonNode.Parse(File.ReadAllText(input)) as JsonObject
                                ?? throw new ModsException($"{input} is not a JSON object"), input));
                            continue;
                        }
                        var targets = p.GetValue(each) && Directory.Exists(input)
                            ? Directory.EnumerateFileSystemEntries(input).Where(e => !Path.GetFileName(e).StartsWith('_') && !Path.GetFileName(e).StartsWith('.')
                                && (Directory.Exists(e) || e.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal).ToList()
                            : [input];
                        foreach (var target in targets)
                        {
                            var usage = ScanOne(Scan(), target);
                            mods.Add(usage);
                            if (p.GetValue(usageOut) is { } folder)
                            {
                                Directory.CreateDirectory(folder);
                                File.WriteAllText(Path.Combine(folder, Safe(usage.Source) + ".json"),
                                    usage.ToJson().ToJsonString(Indented).ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
                            }
                            error.WriteLine($"  {usage.Name}: {usage.Components.Count} components, {usage.OsirisCalls.Count + usage.OsirisListeners.Count + usage.GoalCalls.Count} Osiris names, "
                                + $"{usage.Api.Count} Ext calls ({usage.FilesTotal} files, {usage.Unreadable.Count} unreadable)");
                        }
                    }
                    var osiris = context?.Osiris ?? DemandFile.OsirisIndex(p.GetValue(index) ?? GateKit.Release.DatabaseKind.Index.DefaultPath);
                    var rows = Demand.Rows(mods, context?.Catalog, osiris);
                    var path = p.GetValue(outFile) ?? DemandFile.PathIn(NamesPaths.CoverageRoot());
                    DemandFile.Write(path, rows);
                    var counts = rows.GroupBy(r => (string)r["kind"]!).Select(g => $"{g.Key} {g.Count()}");
                    if (p.GetValue(json)) output.WriteLine(new JsonObject { ["written"] = path, ["mods"] = mods.Count }.ToJsonString(Indented));
                    else output.WriteLine($"{mods.Count} mods -> {path}: {string.Join(", ", counts)}");
                    return 0;
                }
                finally { context?.Dispose(); }
            }));
            root.Subcommands.Add(c);
        }
        return root;
    }

    static string Safe(string name) => string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));

    static void Print(TextWriter output, ModUsage usage)
    {
        output.WriteLine($"{usage.Name} ({usage.Source}): {usage.FilesTotal} files, read {string.Join(", ", usage.FilesRead.Select(f => $"{f.Value} {f.Key}"))}"
                         + (usage.Unreadable.Count > 0 ? $", {usage.Unreadable.Count} unreadable" : ""));
        foreach (var u in usage.Unreadable) output.WriteLine($"  unreadable: {u.Path}: {u.Reason}");
        output.WriteLine($"  components ({usage.Components.Count}):");
        foreach (var c in usage.Components.Values)
            output.WriteLine($"    {c.Type,-56} {(c.Read ? "r" : "-")}{(c.Write ? "w" : "-")}{(c.Create ? "c" : "-")}{(c.Subscribe ? "s" : "-")} "
                             + $"{string.Join("+", c.Via)}  {string.Join(", ", c.Fields)}");
        output.WriteLine($"  Osiris calls: {string.Join(", ", usage.OsirisCalls.Keys)}");
        output.WriteLine($"  Osiris listeners: {string.Join(", ", usage.OsirisListeners.Select(l => $"{l.Key}/{string.Join("/", l.Value)}"))}");
        if (usage.GoalCalls.Count > 0) output.WriteLine($"  story goal calls: {usage.GoalCalls.Count}");
        output.WriteLine($"  Ext: {string.Join(", ", usage.Api)}");
        output.WriteLine($"  events: {string.Join(", ", usage.Events)}");
        if (usage.NetChannels.Count > 0) output.WriteLine($"  net channels: {usage.NetChannels.Count}");
        if (usage.StatsTypes.Count > 0) output.WriteLine($"  stats: {string.Join(", ", usage.StatsTypes.Select(s => $"{s.Value} {s.Key}"))}");
        if (usage.Boosts.Count > 0) output.WriteLine($"  boosts: {string.Join(", ", usage.Boosts.Select(s => $"{s.Key} {s.Value}"))}");
        if (usage.Functors.Count > 0) output.WriteLine($"  functors: {string.Join(", ", usage.Functors.Select(s => $"{s.Key} {s.Value}"))}");
        if (usage.XamlBindings.Count > 0) output.WriteLine($"  XAML: {usage.XamlBindings.Count} bindings, {usage.XamlCommands.Count} commands");
        if (usage.Overrides.Count > 0) output.WriteLine($"  files at a path the game ships: {usage.Overrides.Count}");
    }
}
