using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Copies;
using GateKit.Symbols;
using GateKit.Symbols.Queries;

namespace GateKit.Names;

/// <summary>The <c>names</c> command: the record of what the engine's fields are called.</summary>
public static class NamesCommands
{
    const string About =
        "the names of the engine's fields: how many are still a machine's, what is already known about one, "
        + "writing a name with its evidence, and the declarations Script Extender is built with\n\n"
        + "A name carries where it came from: game (the game's own member name) and toolkit (a Toolkit accessor) are facts, "
        + "inferred is reasoning from the game's code and needs its evidence, extender is Script Extender's name for the field "
        + "at that offset that nobody has checked against the game's code, unknown says so. The game's own name wins; a real name that gives "
        + "way to a better one stays as an alias. A plausible name is worse than field_8, which tells the truth about what is known.";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Command CreateCommand()
    {
        var root = new Command("names", About);
        var coverage = new Option<string?>("--coverage")
        {
            Description = "the coverage record, the folder holding `layouts` (default GATEKIT_COVERAGE, else research/coverage of this repository)",
            Recursive = true,
        };
        var json = new Option<bool>("--json") { Description = "the answer as JSON", Recursive = true };
        root.Options.Add(coverage);
        root.Options.Add(json);

        int Run(ParseResult p, Func<string, TextWriter, TextWriter, int> body)
        {
            var output = p.InvocationConfiguration.Output;
            var error = p.InvocationConfiguration.Error;
            try
            {
                return body(NamesPaths.CoverageRoot(p.GetValue(coverage)), output, error);
            }
            catch (Exception e) when (e is NamesException or SymbolsException or CopyException or LayoutError)
            {
                error.WriteLine(e.Message);
                return 1;
            }
        }
        List<LayoutFile> Load(string record, TextWriter error, List<string>? unreadable = null) =>
            LayoutStore.All(record, path =>
            {
                unreadable?.Add(path);
                error.WriteLine($"note: {path} cannot be read as a layout and is not counted");
            });
        void Json(TextWriter output, JsonNode node) => output.WriteLine(node.ToJsonString(Indented).ReplaceLineEndings("\n"));

        var space = new Option<string>("--ns") { Description = "only types whose name starts with this, e.g. esv::lock", DefaultValueFactory = _ => "" };
        var demandFile = new Option<string?>("--demand") { Description = "the demand file `gatekit mod demand` wrote (default <coverage>/demand.jsonl)" };
        var db = new Option<string>("--db") { Description = "the symbol database (default BG3_SYMBOLS_DB or <local>/symbols/symbols.sqlite)", DefaultValueFactory = _ => SymbolPaths.SymbolsDb };

        // tally
        {
            var strict = new Option<bool>("--strict") { Description = "fail when a layout file cannot be read, instead of leaving it out with a note" };
            var c = new Command("tally", "the measure: of the fields written down, how many carry a name that means something, and where each name came from") { space, strict };
            c.SetAction(p => Run(p, (record, output, error) =>
            {
                var unreadable = new List<string>();
                var tally = Tally.Measure(Load(record, error, unreadable), p.GetValue(space)!);
                if (tally.Fields == 0) throw new NamesException("no layouts matched");
                if (p.GetValue(json)) Json(output, tally.ToJson(unreadable));
                else tally.Print(output);
                return p.GetValue(strict) && unreadable.Count > 0 ? 1 : 0;
            }));
            root.Subcommands.Add(c);
        }
        // worst
        {
            var min = new Option<int>("--min-fields") { Description = "leave out types with fewer fields", DefaultValueFactory = _ => 6 };
            var top = new Option<int>("--top") { DefaultValueFactory = _ => 30 };
            var by = new Option<string>("--by")
            {
                Description = "share: the largest share of machine names (default); demand: the fields still machine-named or at rung "
                    + "unknown or extender in the types a set of mods uses, by how many mods name the field, then use the type (reads a demand file)",
                DefaultValueFactory = _ => "share",
            };
            by.AcceptOnlyFromAmong("share", "demand");
            var c = new Command("worst", "the components where a reader would pay off most: the largest share of machine names, or the most demanded") { space, min, top, by, demandFile };
            c.SetAction(p => Run(p, (record, output, error) =>
            {
                if (p.GetValue(by) == "demand")
                {
                    var rows = Demand.DemandFile.Read(p.GetValue(demandFile) ?? Demand.DemandFile.PathIn(record));
                    var state = Demand.RecordState.Load(record, error.WriteLine);
                    var prefix = p.GetValue(space)!;
                    var worst = Demand.DemandReport.Worst(rows, state, int.MaxValue)
                        .Where(w => ((string)w["type"]!).StartsWith(prefix, StringComparison.Ordinal)).Take(p.GetValue(top)).ToList();
                    if (p.GetValue(json)) Json(output, new JsonArray([.. worst]));
                    else
                    {
                        output.WriteLine("fields owed a name in the types the mods use (mods naming the field / mods using the type):");
                        foreach (var w in worst)
                            output.WriteLine($"  {w["field_mods"],3}/{w["type_mods"],-3} {w["type"]}+0x{Demand.DemandReport.Num(w["offset"]) ?? 0:X} {w["field"]}  ({w["rung"]}{((bool)w["machine"]! ? ", machine's name" : "")})");
                    }
                    return 0;
                }
                var tally = Tally.Measure(Load(record, error), p.GetValue(space)!);
                if (tally.Fields == 0) throw new NamesException("no layouts matched");
                if (p.GetValue(json))
                    Json(output, new JsonArray(tally.Worst(p.GetValue(min), p.GetValue(top)).Select(w => (JsonNode)new JsonObject
                        { ["component"] = w.Component, ["machine"] = w.Machine, ["fields"] = w.Fields }).ToArray()));
                else tally.PrintWorst(output, p.GetValue(min), p.GetValue(top));
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // suggest
        {
            var component = new Argument<string?>("component") { Description = "one type; without it, every type of --ns", Arity = ArgumentArity.ZeroOrOne };
            var all = new Option<bool>("--all") { Description = "every field, not only those with a machine's name" };
            var known = new Option<bool>("--known") { Description = "only fields the symbol database knows something about" };
            var c = new Command("suggest", "what the symbol database already knows at the offsets that still carry a machine's name: "
                + "the game's own member names, the Toolkit's accessors, Script Extender's layout. Read-only.") { component, space, all, known, db };
            c.SetAction(p => Run(p, (record, output, error) =>
            {
                var files = Load(record, error);
                string prefix = p.GetValue(space)!;
                if (p.GetValue(component) is { } one)
                {
                    files = [LayoutStore.Find(files, one)];
                    prefix = "";
                }
                else if (prefix.Length == 0)
                    throw new NamesException("say one component, or a namespace with --ns");
                using var symbols = new SymbolQueries(p.GetValue(db)!);
                var owed = FieldNames.Owed(files, symbols, prefix, p.GetValue(all));
                if (p.GetValue(known)) owed = owed.Where(o => o.Facts.Count > 0).ToList();
                if (p.GetValue(json))
                {
                    Json(output, FieldNames.ToJson(owed));
                    return 0;
                }
                foreach (var group in owed.GroupBy(o => o.Component))
                {
                    output.WriteLine($"{group.Key}  ({group.First().File})");
                    foreach (var field in group)
                    {
                        output.WriteLine($"  +0x{field.Offset:X3}  {field.Name,-28} {field.Type}");
                        foreach (var fact in field.Facts)
                            output.WriteLine($"      {(fact.Certain ? "=" : "~")} {fact.Rung,-8} {fact.Name}   [{fact.Evidence}]");
                    }
                }
                int settled = owed.Count(o => o.Settled is not null);
                output.WriteLine($"{owed.Count} fields, {owed.Count(o => o.Facts.Count > 0)} with something known, {settled} settled by a fact alone"
                                 + (settled > 0 ? " (= lines; `gatekit names adopt` writes those)" : ""));
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // adopt
        {
            var write = new Option<bool>("--write") { Description = "write the names; without it they are only listed" };
            var c = new Command("adopt", "the names a fact settles by itself - the game's own member name or a Toolkit accessor, read at the field's own "
                + "offset in the game's build - listed, or written with --write, each with the evidence it was read from") { space, write, db };
            c.SetAction(p => Run(p, (record, output, error) =>
            {
                using var symbols = new SymbolQueries(p.GetValue(db)!);
                var changes = FieldNames.Adoptable(FieldNames.Owed(Load(record, error), symbols, p.GetValue(space)!));
                var written = FieldNames.Apply(record, changes, dryRun: !p.GetValue(write));
                Report(p, output, written, p.GetValue(write));
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // set
        {
            var component = new Argument<string?>("component") { Arity = ArgumentArity.ZeroOrOne };
            var offset = new Argument<string?>("offset") { Description = "24 or 0x18", Arity = ArgumentArity.ZeroOrOne };
            var name = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne };
            var source = new Option<string?>("--source") { Description = "game, toolkit, inferred, extender or unknown" };
            var evidence = new Option<string?>("--evidence") { Description = "what backs the name: what was read, forced or repeated, and where" };
            var type = new Option<string?>("--type") { Description = "the field's type, when the naming settles it too" };
            var legacy = new Option<string?>("--legacy") { Description = "the earlier name to keep as an alias (by default the one replaced, if it was a real name)" };
            var nested = new Option<string?>("--struct") { Description = "a nested struct of the layout, when the field is inside one" };
            var from = new Option<string?>("--from") { Description = "a JSON file with one name or an array of them (component, offset, name, source, evidence; `-` reads standard input)" };
            var force = new Option<bool>("--force") { Description = "let a weaker source replace a stronger one" };
            var dry = new Option<bool>("--dry-run") { Description = "check and say what would change; write nothing" };
            var c = new Command("set", "write the name of a field into its layout, with where the name came from and what backs it. "
                + "Several at once with --from: all of a batch or none of it.") { component, offset, name, source, evidence, type, legacy, nested, from, force, dry };
            c.SetAction(p => Run(p, (record, output, error) =>
            {
                List<NameChange> changes;
                if (p.GetValue(from) is { } file)
                {
                    string text = file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file);
                    try { changes = FieldNames.Parse(JsonNode.Parse(text)); }
                    catch (JsonException e) { throw new NamesException($"{file} is not JSON: {e.Message}"); }
                }
                else
                {
                    if (p.GetValue(component) is not { } one || p.GetValue(offset) is not { } at || p.GetValue(name) is not { } called)
                        throw new NamesException("say the component, the offset and the name - or give a file with --from");
                    changes = [new NameChange(one, FieldNames.Offset(at), called,
                        p.GetValue(source) ?? throw new NamesException("say where the name comes from: --source game, toolkit, inferred, extender or unknown"),
                        p.GetValue(evidence) ?? "", p.GetValue(nested), p.GetValue(type), p.GetValue(legacy))];
                }
                var written = FieldNames.Apply(record, changes, p.GetValue(force), p.GetValue(dry));
                Report(p, output, written, !p.GetValue(dry));
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // extender
        {
            var write = new Option<bool>("--write") { Description = "write the rungs; without it they are only listed" };
            var c = new Command("extender", "the names the record carries at rung unknown that Script Extender's author's headers declare at the same "
                + "offset of the same type: listed, or raised to rung extender with --write. Also lists the inferred names whose evidence "
                + "cites Script Extender and no code - for a reader; nothing demotes them") { space, write, db };
            c.SetAction(p => Run(p, (record, output, error) =>
            {
                var files = Load(record, error);
                string prefix = p.GetValue(space)!;
                using var symbols = new SymbolQueries(p.GetValue(db)!);
                var written = FieldNames.Apply(record, FieldNames.ExtenderNames(files, symbols, prefix), dryRun: !p.GetValue(write));
                var uncited = FieldNames.InferredWithoutCode(files, prefix);
                if (p.GetValue(json))
                {
                    Json(output, new JsonObject { ["written"] = p.GetValue(write), ["names"] = FieldNames.ToJson(written),
                        ["inferred_without_code"] = FieldNames.ToJson(uncited) });
                    return 0;
                }
                foreach (var name in written)
                    output.WriteLine($"{name.Component}+0x{name.Offset:X}: {name.Name}  unknown -> extender");
                output.WriteLine(p.GetValue(write) ? $"{written.Count} rungs written" : $"{written.Count} rungs would be written; nothing was");
                output.WriteLine();
                output.WriteLine("inferred names whose evidence cites Script Extender and no code (a list to read, not a verdict):");
                foreach (var name in uncited)
                    output.WriteLine($"  {name.Component}{(name.Struct is null ? "" : "/" + name.Struct)}+0x{name.Offset:X} {name.Name}   [{name.Evidence}]");
                output.WriteLine($"{uncited.Count} such names");
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // take
        {
            var folder = new Argument<string>("folder") { Description = "candidate layout files, as `gatekit symbols se-layouts --out` writes them" };
            var batch = new Option<string>("--batch") { Description = "the batch the taken layouts are recorded under", DefaultValueFactory = _ => "extender" };
            var write = new Option<bool>("--write") { Description = "write the files; without it they are only checked" };
            var c = new Command("take", "take candidate layouts into the record: for every type with no layout yet, a candidate that holds together "
                + "(status declared, every field at rung extender, an ext_name, the registered size, the checks of `names generate`) "
                + "becomes a type entry of the registry (registry/types/), its batch recorded in its survey; every other is named with its reason") { folder, batch, write, db };
            c.SetAction(p => Run(p, (record, output, error) =>
            {
                using var symbols = Declarations.OpenSymbols(p.GetValue(db));
                if (symbols is null) error.WriteLine("note: no symbol database, so a candidate naming one of Script Extender's own structs cannot be sized");
                var result = Intake.Take(record, p.GetValue(folder)!, p.GetValue(batch)!, symbols, p.GetValue(write));
                if (p.GetValue(json))
                {
                    Json(output, new JsonObject { ["written"] = p.GetValue(write), ["taken"] = FieldNames.ToJson(result.Taken),
                        ["refused"] = FieldNames.ToJson(result.Refused), ["already_there"] = result.AlreadyThere.Count });
                    return 0;
                }
                foreach (var refused in result.Refused)
                    output.WriteLine($"REFUSED {refused.File}: {refused.Reason}");
                output.WriteLine($"{result.Taken.Count} candidates {(p.GetValue(write) ? "taken into" : "would go into")} the registry as batch {p.GetValue(batch)}, "
                                 + $"{result.Refused.Count} refused, {result.AlreadyThere.Count} types already have a layout");
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // pending
        {
            var c = new Command("pending", "what the runs have claimed about fields (with their recipes) that no layout carries yet: the names waiting for a reader");
            c.SetAction(p => Run(p, (record, output, _) =>
            {
                var pending = FieldNames.Pending(record);
                if (p.GetValue(json)) Json(output, FieldNames.ToJson(pending));
                else
                {
                    foreach (var claim in pending)
                        output.WriteLine($"{claim.Component} {claim.Offset}: {claim.Belief}   [{claim.Evidence}, reached by {claim.ReachedBy}; {claim.State}; {claim.Recipe}]");
                    output.WriteLine($"{pending.Count} claims waiting");
                }
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // generate
        {
            var tree = new Option<string?>("--tree") { Description = "the Script Extender source tree to write into (default GATEKIT_SE_FORK, else BG3SE_SRC)" };
            var check = new Option<bool>("--check") { Description = "only validate the layout files; write nothing" };
            var outDir = new Option<string?>("--out") { Description = "write Coverage.h and ECS.inl into this folder instead of the tree, to look at them first" };
            var c = new Command("generate", "Script Extender's declarations for the layouts in the build: GameDefinitions/Components/Coverage.h, "
                + "every field commented with where its name comes from, and the component names in Enumerations/ECS.inl") { tree, check, outDir, db };
            c.SetAction(p => Run(p, (record, output, error) =>
            {
                string fork = NamesPaths.ForkTree(p.GetValue(tree));
                if (CopySettings.RepositoryRoot() is { } repository && p.GetValue(outDir) is null && !p.GetValue(check)
                    && Path.GetRelativePath(repository, fork) is var relative && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                    throw new NamesException($"{fork} is inside this repository; the declarations belong in the fork's own tree");
                using var symbols = Declarations.OpenSymbols(p.GetValue(db));
                if (symbols is null) error.WriteLine("note: no symbol database, so a layout naming one of Script Extender's own structs cannot be sized");
                var declarations = new Declarations(fork, symbols);
                var result = declarations.Run(Load(record, error), p.GetValue(check));
                foreach (var problem in result.Invalid) output.WriteLine($"INVALID {problem}");
                if (result.DeclaredUpstream.Count > 0)
                    output.WriteLine($"{result.DeclaredUpstream.Count} layouts are left out: Script Extender declares the type itself now"
                                     + $" ({string.Join(", ", result.DeclaredUpstream.Take(5))}{(result.DeclaredUpstream.Count > 5 ? ", ..." : "")})");
                output.WriteLine($"{result.Layouts} layouts, {result.Emitted} in the build, {result.InvalidInBuild} of those invalid");
                if (result.InvalidInBuild > 0) return 1;
                if (result.HeaderText is null || result.EnumerationText is null) return 0;
                string header = declarations.Header, enumerations = declarations.Enumerations;
                bool mark = File.ReadAllBytes(enumerations).AsSpan().StartsWith(Encoding.UTF8.Preamble);
                if (p.GetValue(outDir) is { } folder)
                {
                    Directory.CreateDirectory(folder);
                    header = Path.Combine(folder, "Coverage.h");
                    enumerations = Path.Combine(folder, "ECS.inl");
                }
                File.WriteAllText(header, result.HeaderText, new UTF8Encoding(false));
                File.WriteAllText(enumerations, result.EnumerationText, new UTF8Encoding(mark));
                output.WriteLine($"wrote {header} and the names into {enumerations}");
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // demand
        {
            var demand = new Command("demand", "a demand file (`gatekit mod demand`: what a set of mods uses of the engine) "
                + "joined with the record: how far the record is from right on exactly those types, fields and functions");
            var top = new Option<int>("--top") { DefaultValueFactory = _ => 25 };
            var report = new Command("report", "(a) the demanded types never seen alive, (b) seen and not verified, "
                + "(c) the demanded fields with no evidence-backed name, (d) the demanded types no described system reads, writes or adds, "
                + "(e) the most used Osiris functions and events, with whether the data index holds the arities the mods use") { top, demandFile };
            report.SetAction(p => Run(p, (record, output, error) =>
            {
                var rows = Demand.DemandFile.Read(p.GetValue(demandFile) ?? Demand.DemandFile.PathIn(record));
                var result = Demand.DemandReport.Build(rows, Demand.RecordState.Load(record, error.WriteLine));
                if (p.GetValue(json)) Json(output, result.ToJson(p.GetValue(top)));
                else result.Print(output, p.GetValue(top));
                return 0;
            }));
            demand.Subcommands.Add(report);
            root.Subcommands.Add(demand);
        }
        return root;

        void Report(ParseResult p, TextWriter output, List<NameWritten> written, bool wrote)
        {
            if (p.GetValue(json))
            {
                Json(output, new JsonObject { ["written"] = wrote, ["names"] = FieldNames.ToJson(written) });
                return;
            }
            foreach (var name in written)
                output.WriteLine($"{name.Component}+0x{name.Offset:X}: {name.Was} -> {name.Name}  ({name.Source})");
            output.WriteLine(wrote ? $"{written.Count} names written" : $"{written.Count} names would be written; nothing was");
        }
    }
}
