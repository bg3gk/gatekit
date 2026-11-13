using System.CommandLine;
using System.CommandLine.Parsing;
using GateKit.Symbols.Build;
using GateKit.Symbols.Decomp;
using GateKit.Symbols.Managed;
using GateKit.Symbols.Queries;

namespace GateKit.Symbols;

/// <summary>The `symbols` command: a database of the engine's code (the Toolkit's exported C++
/// classes, the game executables, Script Extender's patterns and layouts) and the questions it
/// answers. Every subcommand writes to the invocation's output and error writers; an expected
/// failure prints its message and exits with 1.</summary>
public static class SymbolsCommands
{
    const string About = """
        Symbol database of the engine's code: the Toolkit DLLs export their C++ classes by decorated name;
        the game executables are matched to them by text constants, calls and virtual tables.

            gatekit symbols build                        read the Toolkit and the game, write the database
            gatekit symbols install                      fetch the same database from the published release instead
            gatekit symbols find ShowWeapon              substring search over declarations
            gatekit symbols class ecl::EquipmentVisualsSystem
            gatekit symbols vtable ecl::TLPreviewDummy   slots of an exported virtual table (or binary+0xRVA)
            gatekit symbols users ecl::TLPreviewDummy --callers 2
            gatekit symbols system ecl::EquipmentVisualsSystem
            gatekit symbols signature bg3.exe+0x1899220
            gatekit symbols export --format ghidra --out names.tsv
            gatekit symbols stats

        The database lives in BG3_SYMBOLS_DB (default <local>/symbols/symbols.sqlite). It holds facts about the
        game's binaries - names, addresses, sizes, layouts, calls, strings - and no code of the game; the
        pseudo-code the decomp pipeline produces is never published and stays on the machine that made it.
        """;

    /// <summary>The `symbols` command with its subcommands; other files from the environment.</summary>
    public static Command CreateCommand() => CreateCommand(SymbolsEnvironment.FromEnvironment);

    /// <summary>The same with the locations of the other files taken from a factory (called once per invocation).</summary>
    public static Command CreateCommand(Func<SymbolsEnvironment> environment)
    {
        var root = new Command("symbols", About);
        var db = new Option<string>("--db")
        {
            Description = "database file (default BG3_SYMBOLS_DB or <local>/symbols/symbols.sqlite)",
            DefaultValueFactory = _ => SymbolPaths.SymbolsDb,
            Recursive = true,
        };
        root.Options.Add(db);

        int Run(ParseResult p, Action<SymbolContext> body)
        {
            var output = p.InvocationConfiguration.Output;
            var error = p.InvocationConfiguration.Error;
            try
            {
                using var connection = SymbolContext.Connect(p.GetValue(db)!);
                using var c = new SymbolContext(connection, output, error, environment());
                body(c);
                return 0;
            }
            catch (SymbolsException e)
            {
                error.WriteLine(e.Message);
                return 1;
            }
        }

        // build
        {
            var defaults = new BuildOptions();
            var toolkit = new Option<string>("--toolkit") { Description = "Toolkit folder (or set BG3_TOOLKIT)", DefaultValueFactory = _ => SymbolPaths.Toolkit };
            var recursive = new Option<bool>("--recursive") { Description = "also read binaries in subfolders (Qt plugins)" };
            var game = new Option<string>("--game") { Description = "game folder (or set BG3_GAME)", DefaultValueFactory = _ => SymbolPaths.Game };
            var bg3se = new Option<string?>("--bg3se")
            {
                Description = "Script Extender source tree (or set BG3SE_SRC): byte patterns, struct layouts, enumerations",
                DefaultValueFactory = _ => SymbolPaths.Bg3Se,
            };
            var jobs = new Option<int>("--jobs") { Description = "parallel workers for disassembly", DefaultValueFactory = _ => defaults.Jobs };
            var exportsOnly = new Option<bool>("--exports-only") { Description = "skip strings, type names, virtual tables and matching" };
            var noPseudo = new Option<bool>("--no-pseudo-code") { Description = "do not take the pairs found by pseudo-code (gatekit decomp match) as seeds" };
            var check = new Option<bool>("--check-matching") { Description = "measure the call matching: hide a fifth of the text-matched pairs and see if it finds them" };
            var c = new Command("build", "read the Toolkit binaries and the game executables and write the database") { toolkit, recursive, game, bg3se, jobs, exportsOnly, noPseudo, check };
            c.SetAction(p =>
            {
                try
                {
                    SymbolBuilder.Run(new BuildOptions
                    {
                        Db = p.GetValue(db)!, Toolkit = p.GetValue(toolkit)!, Game = p.GetValue(game)!, Bg3Se = string.IsNullOrEmpty(p.GetValue(bg3se)) ? null : p.GetValue(bg3se),
                        Recursive = p.GetValue(recursive), Jobs = p.GetValue(jobs), ExportsOnly = p.GetValue(exportsOnly), NoPseudoCode = p.GetValue(noPseudo),
                        CheckMatching = p.GetValue(check),
                    }, p.InvocationConfiguration.Output, p.InvocationConfiguration.Error);
                    return 0;
                }
                catch (SymbolsException e)
                {
                    p.InvocationConfiguration.Error.WriteLine(e.Message);
                    return 1;
                }
            });
            root.Subcommands.Add(c);
        }

        // Building stays the first-class path; these two are the alternative for whoever would
        // rather fetch the same database than spend the minutes making it.
        root.Subcommands.Add(GateKit.Release.ReleaseCommands.Install(GateKit.Release.DatabaseKind.Symbols, db));
        root.Subcommands.Add(GateKit.Release.ReleaseCommands.Export(GateKit.Release.DatabaseKind.Symbols, db));

        // The searching verbs share --dll, --kind, --limit, --mangled and --full (symbols.py's query()).
        (Command Command, Func<ParseResult, QueryOptions> Options) Query(string name, string help)
        {
            var dll = new Option<string?>("--dll") { Description = "only this DLL (substring of the file name)" };
            var kind = new Option<string?>("--kind") { Description = "only this kind: method, function, data, vftable, special, c" };
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
            var mangled = new Option<bool>("--mangled") { Description = "also print decorated names" };
            var full = new Option<bool>("--full") { Description = "do not cut long declarations" };
            var c = new Command(name, help) { dll, kind, limit, mangled, full };
            root.Subcommands.Add(c);
            return (c, p => new QueryOptions(p.GetValue(dll), p.GetValue(kind), p.GetValue(limit), p.GetValue(mangled), p.GetValue(full)));
        }

        Argument<string[]> Words(bool required) => new("text")
        {
            Description = "text to look for; several words must all match",
            Arity = required ? ArgumentArity.OneOrMore : ArgumentArity.ZeroOrMore,
        };

        Argument<string> Name(string help) => new("name") { Description = help };

        // find
        {
            var (c, o) = Query("find", "substring search over qualified names (namespace::class::member)");
            var text = Words(true);
            var anywhere = new Option<bool>("--anywhere") { Description = "also match inside return and parameter types and decorated names (always on)" };
            c.Arguments.Add(text);
            c.Options.Add(anywhere);
            c.SetAction(p => Run(p, s => SymbolText.Find(s, p.GetValue(text)!, o(p))));
        }
        // classes
        {
            var (c, o) = Query("classes", "list classes whose qualified name contains the text");
            var text = Words(true);
            c.Arguments.Add(text);
            c.SetAction(p => Run(p, s => SymbolText.Classes(s, p.GetValue(text)!, o(p))));
        }
        // class
        {
            var (c, o) = Query("class", "every exported member of one class");
            var name = Name("qualified class name, e.g. ecl::EquipmentVisualsSystem");
            c.Arguments.Add(name);
            c.SetAction(p => Run(p, s => SymbolText.Class(s, p.GetValue(name)!, o(p))));
        }
        // strings
        {
            var (c, o) = Query("strings", "text constants code refers to, and the functions that use them");
            var text = Words(true);
            c.Arguments.Add(text);
            c.SetAction(p => Run(p, s => SymbolText.Strings(s, p.GetValue(text)!, o(p))));
        }
        // types
        {
            var (c, o) = Query("types", "type names kept in the game executable: ECS systems, components, queries");
            var text = Words(true);
            c.Arguments.Add(text);
            c.SetAction(p => Run(p, s => SymbolText.Types(s, p.GetValue(text)!, o(p))));
        }
        // game
        {
            var (c, o) = Query("game", "Toolkit functions found again in the game executable (by name or by text)");
            var text = Words(false);
            var unnamed = new Option<bool>("--unnamed") { Description = "also list matched functions the Toolkit does not export" };
            c.Arguments.Add(text);
            c.Options.Add(unnamed);
            c.SetAction(p => Run(p, s => SymbolText.Game(s, p.GetValue(text) ?? [], o(p), p.GetValue(unnamed))));
        }
        // vtable
        {
            var (c, o) = Query("vtable", "slots of the virtual tables a class exports, or of the table at binary+0xRVA");
            var name = Name("qualified class name (e.g. ecl::EquipmentVisualsSystem) or binary+0xRVA");
            c.Arguments.Add(name);
            c.SetAction(p => Run(p, s => SymbolText.Vtable(s, p.GetValue(name)!, o(p).Full)));
        }
        // se
        {
            var (c, o) = Query("se", "symbols Script Extender's patterns locate in the game executable");
            var text = Words(false);
            var report = new Option<bool>("--report") { Description = "list every pattern with its number of matches" };
            c.Arguments.Add(text);
            c.Options.Add(report);
            c.SetAction(p => Run(p, s => SymbolText.Se(s, p.GetValue(text) ?? [], o(p), p.GetValue(report))));
        }
        // ecs
        {
            var (c, o) = Query("ecs", "ECS types of the game: registration context and where the index is kept");
            var text = Words(true);
            var context = new Option<string?>("--context") { Description = "only this context, e.g. SystemsContext, ComponentTypeIdContext, Replicated" };
            c.Arguments.Add(text);
            c.Options.Add(context);
            c.SetAction(p => Run(p, s => SymbolText.Ecs(s, p.GetValue(text)!, o(p), p.GetValue(context))));
        }
        // layout, fields, enum
        foreach (var (verb, help, body) in new (string, string, Action<SymbolContext, string, int>)[]
                 {
                     ("layout", "fields of a type (component, system, struct) with offsets", SymbolText.Layout),
                     ("fields", "fields of a class from the Toolkit's accessors and Script Extender's layout", SymbolText.Fields),
                     ("enum", "an enumeration with its values, or the enumerations that have a label", SymbolText.Enum),
                 })
        {
            var name = new Argument<string>("name");
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
            var c = new Command(verb, help) { name, limit };
            c.SetAction(p => Run(p, s => body(s, p.GetValue(name)!, p.GetValue(limit))));
            root.Subcommands.Add(c);
        }
        // callers, callees
        foreach (var (verb, help, body) in new (string, string, Action<SymbolContext, string, int>)[]
                 {
                     ("callers", "functions that call the given one directly", SymbolText.Callers),
                     ("callees", "functions the given one calls directly", SymbolText.Callees),
                 })
        {
            var text = new Argument<string>("text") { Description = "part of a function name, or binary+0xRVA (e.g. bg3.exe+0x1D6FA90)" };
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
            var c = new Command(verb, help) { text, limit };
            c.SetAction(p => Run(p, s => body(s, p.GetValue(text)!, p.GetValue(limit))));
            root.Subcommands.Add(c);
        }
        // users
        {
            var (c, o) = Query("users", "game functions that touch an ECS type's index, a virtual table or an SE symbol");
            var text = Words(true);
            var per = new Option<int>("--per") { Description = "functions to list per target", DefaultValueFactory = _ => 25 };
            var callers = new Option<int>("--callers")
            {
                Description = "also list who calls each of those functions, DEPTH steps up (1 when no number is given)",
                HelpName = "DEPTH",
                Arity = ArgumentArity.ZeroOrOne,
                CustomParser = r => r.Tokens.Count == 0 ? 0 : ParseInt(r, r.Tokens[0].Value),
            };
            // absent: 0; given without a number: 1; given with one: that number
            int Depth(ParseResult p)
            {
                var r = p.GetResult(callers);
                return r == null || r.Implicit ? 0 : r.Tokens.Count == 0 ? 1 : p.GetValue(callers);
            }
            var perCallers = new Option<int>("--per-callers") { Description = "callers to list per function, largest first", DefaultValueFactory = _ => 6 };
            c.Arguments.Add(text);
            c.Options.Add(per);
            c.Options.Add(callers);
            c.Options.Add(perCallers);
            c.SetAction(p => Run(p, s =>
            {
                var q = o(p);
                SymbolText.Users(s, p.GetValue(text)!, q.Dll, q.Limit, p.GetValue(per), Depth(p), p.GetValue(perCallers));
            }));
        }
        // system
        {
            var (c, o) = Query("system", "one ECS system: registration, components, methods, virtual table, game addresses");
            var name = Name("system class, e.g. ecl::EquipmentVisualsSystem");
            c.Arguments.Add(name);
            c.SetAction(p => Run(p, s => SymbolText.System(s, p.GetValue(name)!, o(p))));
        }
        // savemodel
        {
            var name = Name("component or model name, or a part of it, e.g. esv::crowds::DetourIdlingComponent");
            var c = new Command("savemodel", "how a component is written to a savegame: save/load functions, model versions with sizes") { name };
            c.SetAction(p => Run(p, s => SymbolText.SaveModel(s, p.GetValue(name)!)));
            root.Subcommands.Add(c);
        }
        // replication
        {
            var name = Name("component class, e.g. eoc::RequestedRollComponent");
            var code = new Option<bool>("--code") { Description = "also print the pseudo-code of slots 1 and 2 of each handler table" };
            var dll = new Option<string?>("--dll") { Description = "game executable (default bg3.exe)" };
            var c = new Command("replication", "a replicated component: registration, handler tables, fields with types and offsets, macOS names") { name, code, dll };
            c.SetAction(p => Run(p, s => SymbolText.Replication(s, p.GetValue(name)!, p.GetValue(dll), p.GetValue(code))));
            root.Subcommands.Add(c);
        }
        // signature
        {
            var text = new Argument<string>("text") { Description = "bg3.exe+0xRVA, or part of a function name that was found in the game" };
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 5 };
            var c = new Command("signature", "a byte signature for a game function (Script Extender mapping syntax)") { text, limit };
            c.SetAction(p => Run(p, s => SymbolText.SignatureOf(s, p.GetValue(text)!, p.GetValue(limit))));
            root.Subcommands.Add(c);
        }
        // export
        {
            var format = new Option<string>("--format") { DefaultValueFactory = _ => "json" };
            format.AcceptOnlyFromAmong("json", "ghidra", "x64dbg", "ida");
            var dll = new Option<string?>("--dll") { Description = "game executable (default bg3.exe)" };
            var output = new Option<string?>("--out") { Description = "file to write (default: standard output)" };
            var c = new Command("export", "names known for a game executable, for a debugger or disassembler") { format, dll, output };
            c.SetAction(p => Run(p, s => SymbolText.Export(s, p.GetValue(dll), p.GetValue(format)!, p.GetValue(output))));
            root.Subcommands.Add(c);
        }
        // importers
        {
            var text = new Argument<string>("text");
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
            var c = new Command("importers", "which binaries import the exports matching the text") { text, limit };
            c.SetAction(p => Run(p, s => SymbolText.Importers(s, p.GetValue(text)!, p.GetValue(limit))));
            root.Subcommands.Add(c);
        }
        // stats
        {
            var c = new Command("stats", "what the database holds");
            c.SetAction(p => Run(p, SymbolText.Stats));
            root.Subcommands.Add(c);
        }
        root.Subcommands.Add(CreateManagedStringsCommand());
        return root;
    }

    static int ParseInt(ArgumentResult r, string text)
    {
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v))
            return v;
        r.AddError($"invalid int value: '{text}'");
        return 0;
    }

    /// <summary>`strings-managed ASSEMBLY`: per method, the string literals of a .NET assembly's code;
    /// or its types (--types), or its methods with their signatures (--members).</summary>
    public static Command CreateManagedStringsCommand()
    {
        var assembly = new Argument<string>("assembly") { Description = "a .NET assembly (.dll or .exe), e.g. the Toolkit's Glasses.exe" };
        var type = new Option<string?>("--type") { Description = "only types whose full name matches this regular expression (case-insensitive)" };
        var match = new Option<string?>("--match") { Description = "only strings (or, with --members, method names) that match this regular expression" };
        var types = new Option<bool>("--types") { Description = "list the types only" };
        var members = new Option<bool>("--members") { Description = "list the methods of each type with their signatures" };
        var c = new Command("strings-managed", "string literals per method of a .NET assembly (read without loading it)") { assembly, type, match, types, members };
        c.Aliases.Add("managed-strings");
        c.SetAction(p =>
        {
            var output = p.InvocationConfiguration.Output;
            try
            {
                using var reader = new ManagedStrings(p.GetValue(assembly)!);
                if (p.GetValue(types))
                    foreach (var t in reader.Types(p.GetValue(type)))
                        output.WriteLine(t);
                else if (p.GetValue(members))
                    foreach (var line in reader.Members(p.GetValue(type), p.GetValue(match)))
                        output.WriteLine(line);
                else
                    foreach (var m in reader.Strings(p.GetValue(type), p.GetValue(match)))
                        output.WriteLine($"{m.Type}::{m.Method}  ->  " + string.Join(" | ", m.Strings));
                return 0;
            }
            catch (Exception e) when (e is SymbolsException or ArgumentException or BadImageFormatException or IOException)
            {
                p.InvocationConfiguration.Error.WriteLine(e.Message);
                return 1;
            }
        });
        return c;
    }

    /// <summary>The `decomp` command (<see cref="DecompCommands.Create()"/>) with `vtable`, which
    /// prints the slots of the virtual table at binary+0xRVA (the same as `symbols vtable`).</summary>
    public static Command CreateDecompCommand() => CreateDecompCommand(DecompCommands.Create(), SymbolsEnvironment.FromEnvironment);

    /// <summary>Adds `vtable` to a `decomp` command made elsewhere.</summary>
    public static Command CreateDecompCommand(Command decomp, Func<SymbolsEnvironment> environment)
    {
        var name = new Argument<string>("name") { Description = "binary+0xRVA (or a class name)" };
        var db = new Option<string>("--db") { Description = "symbol database", DefaultValueFactory = _ => SymbolPaths.SymbolsDb };
        var full = new Option<bool>("--full") { Description = "do not cut long declarations" };
        var c = new Command("vtable", "slots of the virtual table at binary+0xRVA (the same as gatekit symbols vtable)") { name, db, full };
        c.SetAction(p =>
        {
            var error = p.InvocationConfiguration.Error;
            try
            {
                using var connection = SymbolContext.Connect(p.GetValue(db)!);
                using var s = new SymbolContext(connection, p.InvocationConfiguration.Output, error, environment());
                SymbolText.Vtable(s, p.GetValue(name)!, p.GetValue(full));
                return 0;
            }
            catch (SymbolsException e)
            {
                error.WriteLine(e.Message);
                return 1;
            }
        });
        decomp.Subcommands.Add(c);
        return decomp;
    }
}
