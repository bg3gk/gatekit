using System.CommandLine;

namespace GateKit.Symbols.Decomp;

/// <summary>The `decomp` command: Ghidra pseudo-code of the game and the Toolkit, kept in
/// decomp.sqlite in the work folder (BG3_DECOMP_WORK). Every subcommand writes to the
/// invocation's output and error writers; an expected failure prints its message and exits with 1.</summary>
public static class DecompCommands
{
    const string About = """
        Pseudo-code of the game and of the Toolkit's engine DLLs, made with Ghidra and kept in SQLite.

            gatekit decomp run bg3.exe            analyse and decompile (hours for the game executable)
            gatekit decomp run EoCClient.dll      a Toolkit DLL, found in the Toolkit folder
            gatekit decomp load                   put finished output into decomp.sqlite
            gatekit decomp refresh                give the project what changed: the registry's confirmed set (minutes)
            gatekit decomp serve                  keep the decompiler open; `code` asks it for typed text
            gatekit decomp code esv::RollSystem::RollDice   our reading: the certain in the code, guesses beside
            gatekit decomp code bg3.exe+0x1D6FA90 --raw     the decompiler's text untouched
            gatekit decomp grep "ShowWeapon"      search names and pseudo-code
        gatekit decomp helpers --rebuild      the component helpers (get, has, add, remove) and their families
            gatekit decomp tally                  how much reads like code: names, fields, typed locals, helpers
            gatekit decomp status
            gatekit decomp stamp                  record which build each binary is, from its bytes

        Needs Ghidra and the JDK it asks for (JDK 25 for Ghidra 12), under BG3_DECOMP_TOOLS or GHIDRA_HOME.
        Projects, output and the database live in the work folder (BG3_DECOMP_WORK). The pseudo-code is
        derived from the game's binaries: keep it to yourself.
        """;

    /// <summary>The `decomp` command with all its subcommands, settings from the environment.</summary>
    public static Command Create() => Create(DecompSettings.FromEnvironment);

    /// <summary>The same with the settings taken from a factory (called once per invocation).</summary>
    public static Command Create(Func<DecompSettings> settings) => Create(settings, null);

    /// <summary>The whole group as the command line has it: these subcommands and `vtable`, which
    /// reads the symbol database. This is the factory `gatekit` finds (see ProjectCommands.Discover).</summary>
    public static Command CreateCommand() => SymbolsCommands.CreateDecompCommand();

    /// <summary>The same with another decompiler than Ghidra for `refresh` (the tests' own).</summary>
    internal static Command Create(Func<DecompSettings> settings, Func<DecompSession, RefreshPass, int>? decompiler)
    {
        int cpus = Math.Max(1, Environment.ProcessorCount - 2);
        var root = new Command("decomp", About);

        int Invoke(ParseResult p, Func<DecompSession, int> body)
        {
            if (CopyFree.Refuse(p, "decomp") is not 0 and var refused) return refused;
            var output = p.InvocationConfiguration.Output;
            var error = p.InvocationConfiguration.Error;
            try
            {
                using var s = new DecompSession(settings(), output, error);
                return body(s);
            }
            catch (SymbolsException e)
            {
                error.WriteLine(e.Message);
                return 1;
            }
        }

        // run
        {
            var binary = new Argument<string>("binary") { Description = "bg3.exe, bg3_dx11.exe, a Toolkit DLL name, or a path" };
            var memory = new Option<string>("--memory") { Description = "Java heap for Ghidra (default 20G)", DefaultValueFactory = _ => "20G" };
            var cpu = new Option<int>("--cpu") { Description = "processors Ghidra may use", DefaultValueFactory = _ => cpus };
            var range = new Option<string?>("--range") { Description = "only functions in this range of RVAs, FIRST-LAST in hex (to split or resume)" };
            var fresh = new Option<bool>("--fresh") { Description = "import and analyse again even if a project exists" };
            var c = new Command("run", "analyse and decompile a binary with Ghidra") { binary, memory, cpu, range, fresh };
            c.SetAction(p => Invoke(p, s => GhidraCommand.Run(s, p.GetValue(binary)!, p.GetValue(memory)!, p.GetValue(cpu), p.GetValue(range), p.GetValue(fresh))));
            root.Subcommands.Add(c);
        }
        // load
        {
            var binary = new Argument<string?>("binary") { Arity = ArgumentArity.ZeroOrOne, Description = "only this binary" };
            var c = new Command("load", "load finished output into decomp.sqlite") { binary };
            c.SetAction(p => Invoke(p, s => LoadCommand.Run(s, p.GetValue(binary))));
            root.Subcommands.Add(c);
        }
        // refresh
        {
            var binary = new Argument<string>("binary")
            {
                Arity = ArgumentArity.ZeroOrOne, Description = "the binary whose project and pseudo-code are refreshed (default bg3_dx11.exe, the build a copy runs)",
                DefaultValueFactory = _ => "bg3_dx11.exe",
            };
            var coverage = new Option<string?>("--coverage") { Description = "the coverage record beside the registry (default GATEKIT_COVERAGE, else research/coverage of the repository)" };
            var memory = new Option<string>("--memory") { Description = "Java heap for Ghidra (default 20G)", DefaultValueFactory = _ => "20G" };
            var cpu = new Option<int>("--cpu") { Description = "processors Ghidra may use", DefaultValueFactory = _ => cpus };
            var dry = new Option<bool>("--dry-run") { Description = "only say what was renamed since the last pass; start nothing, change nothing" };
            var wait = new Option<int>("--wait") { Description = "seconds to wait for readers to let go of decomp.sqlite before giving up (default 60)", DefaultValueFactory = _ => 60 };
            var c = new Command("refresh", "give the decompiler's project what changed since the last pass - the databases' names and the registry's applied set, never a shown rung - and decompile again the functions that show it")
                { binary, coverage, memory, cpu, dry, wait };
            c.SetAction(p => Invoke(p, s => RefreshCommand.Run(s, p.GetValue(binary)!, p.GetValue(coverage), p.GetValue(memory)!, p.GetValue(cpu),
                                                               p.GetValue(dry), p.GetValue(wait), decompiler)));
            root.Subcommands.Add(c);
        }
        // serve
        {
            var binary = new Argument<string>("binary")
            {
                Arity = ArgumentArity.ZeroOrOne, Description = "the binary whose project is opened (default bg3_dx11.exe)", DefaultValueFactory = _ => "bg3_dx11.exe",
            };
            var stop = new Option<bool>("--stop") { Description = "end the decompiler kept open on the binary" };
            var idle = new Option<int>("--idle") { Description = "minutes without a request after which it ends (default 30)", DefaultValueFactory = _ => 30 };
            var memory = new Option<string>("--memory") { Description = "Java heap for Ghidra (default 20G)", DefaultValueFactory = _ => "20G" };
            var cpu = new Option<int>("--cpu") { Description = "processors Ghidra may use", DefaultValueFactory = _ => cpus };
            var ask = new Option<string?>("--ask") { Description = "time one function (binary+0xRVA) decompiled again by the decompiler already open; prints the milliseconds only", HelpName = "ADDRESS" };
            var param = new Option<string[]>("--param") { Description = "with --ask: index=type, a registry type put on that parameter first (every field: the project is open read-only)", AllowMultipleArgumentsPerToken = true };
            var count = new Option<int>("--count") { Description = "with --ask: repetitions (default 3)", DefaultValueFactory = _ => 3 };
            var c = new Command("serve", "keep the decompiler open on a binary's project, read-only, decompiling one function on request (`decomp code` asks it while it runs)")
                { binary, stop, idle, memory, cpu, ask, param, count };
            c.SetAction(p => Invoke(p, s =>
            {
                if (p.GetValue(stop)) return DecompServer.Stop(s, p.GetValue(binary)!);
                if (p.GetValue(ask) is { } address)
                {
                    var store = CodeCommand.Store();
                    return DecompServer.Measure(s, address, p.GetValue(param) ?? [], Math.Max(1, p.GetValue(count)), id => store?.Get(id)?.Json);
                }
                return DecompServer.Serve(s, p.GetValue(binary)!, p.GetValue(memory)!, p.GetValue(cpu), Math.Max(1, p.GetValue(idle)));
            }));
            root.Subcommands.Add(c);
        }
        // code
        {
            var name = new Argument<string>("name") { Description = "a registry id (or another source's name the registry keeps), a function name, binary+0xRVA, or an absolute address" };
            var all = new Option<bool>("--all") { Description = "print every function that matches" };
            var raw = new Option<bool>("--raw") { Description = "the decompiler's text untouched: no header, no reading, no hints" };
            var asJson = new Option<bool>("--json") { Description = "for a program: per function the decompiler's text untouched and the list of annotations our reading makes" };
            var grep = new Option<string?>("--grep") { Description = "print only the lines that contain the text (a|b: either of the texts), with line numbers", HelpName = "TEXT" };
            var context = new Option<int>("--context", "-C") { Description = "lines around each line found by --grep (default 3)", DefaultValueFactory = _ => 3 };
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 40 };
            var binary = new Option<string?>("--binary") { Description = "only functions of this binary (its name or the start of it: bg3_macos_x64, bg3_dx11.exe)" };
            var c = new Command("code", "our reading of a function by id, name or binary+0xRVA: what is certain written into the code, what is supposed beside it") { name, all, raw, asJson, grep, context, limit, binary };
            c.SetAction(p => Invoke(p, s => CodeCommand.Run(s, p.GetValue(name)!, p.GetValue(all), p.GetValue(raw), p.GetValue(asJson), p.GetValue(grep), p.GetValue(context), p.GetValue(limit), p.GetValue(binary))));
            root.Subcommands.Add(c);
        }
        // at - name only, for an address from any external tool (a debugger, the memory watchpoint)
        {
            var name = new Argument<string[]>("address") { Description = "module+0xRVA, an absolute address, or a function name; several in one call (a stack, a profiler's list), each answered in turn", Arity = ArgumentArity.OneOrMore };
            var asJson = new Option<bool>("--json") { Description = "the names as JSON (an array when several were asked)" };
            var c = new Command("at", "the function at an address and its name, from the decompiler and symbol databases (no pseudo-code)") { name, asJson };
            c.SetAction(p => Invoke(p, s => NameAtCommand.RunMany(s, p.GetValue(name)!, p.GetValue(asJson))));
            root.Subcommands.Add(c);
        }
        // name, names: the functions named by evidence
        {
            var address = new Argument<string>("address") { Description = "module+0xRVA, anywhere inside the function (as a memory watch reports the instruction)" };
            var name = new Argument<string>("name") { Description = "the name, with :: between its parts" };
            var source = new Option<string>("--source") { Description = "the rung: game, toolkit, confirmed, inferred, extender or unknown", DefaultValueFactory = _ => "inferred" };
            var evidence = new Option<string>("--evidence") { Description = "what backs the name: what an instrument read, forced or repeated, and where", DefaultValueFactory = _ => "" };
            var evidenceKind = new Option<string?>("--evidence-kind") { Description = "watch, read, pairing, game-name, toolkit-name, extender-name or text (default: what the rung implies; confirmed needs watch, read or test)" };
            var does = new Option<string?>("--does") { Description = "our own sentence on what the function does" };
            var coverage = new Option<string?>("--coverage") { Description = "the coverage record beside the registry (default GATEKIT_COVERAGE, else research/coverage of the repository)" };
            var dry = new Option<bool>("--dry-run") { Description = "check and say what would change; write nothing" };
            var c = new Command("name", "give the function at an address a name, with its rung and what backs it; written to the registry (registry/functions/ and the build's table) and shown by `decomp at`") { address, name, source, evidence, evidenceKind, does, coverage, dry };
            c.SetAction(p => Invoke(p, s => FunctionNames.Set(s, p.GetValue(address)!, p.GetValue(name)!, p.GetValue(source)!, p.GetValue(evidence)!, p.GetValue(does), p.GetValue(coverage), p.GetValue(dry), p.GetValue(evidenceKind))));
            root.Subcommands.Add(c);

            var text = new Argument<string?>("text") { Arity = ArgumentArity.ZeroOrOne, Description = "part of a name, an address or a sentence" };
            var asJson = new Option<bool>("--json") { Description = "the rows as JSON" };
            var listCoverage = new Option<string?>("--coverage") { Description = "the coverage record (default GATEKIT_COVERAGE, else research/coverage of the repository)" };
            var list = new Command("names", "the functions named by evidence: the registry's functions placed in a build, and the live layer's, marked live") { text, asJson, listCoverage };
            list.SetAction(p => Invoke(p, s => FunctionNames.List(s, p.GetValue(text), p.GetValue(listCoverage), p.GetValue(asJson))));
            root.Subcommands.Add(list);

            var varsAddress = new Argument<string>("address") { Description = "module+0xRVA of a function already named" };
            var pairs = new Argument<string[]>("pairs") { Description = "old=new for each variable or parameter (param_1=stream); old= takes a name off", Arity = ArgumentArity.ZeroOrMore };
            var points = new Option<string[]>("--struct") { Description = "var=Structure: the variable points at this structure, whose named fields `decomp code` then lists above the function", AllowMultipleArgumentsPerToken = true };
            var varsCoverage = new Option<string?>("--coverage") { Description = "the coverage record (default GATEKIT_COVERAGE, else research/coverage of the repository)" };
            var vars = new Command("vars", "name the variables and parameters of a named function; `decomp code` of it shows them at once") { varsAddress, pairs, points, varsCoverage };
            vars.SetAction(p => Invoke(p, s => FunctionNames.Vars(s, p.GetValue(varsAddress)!, p.GetValue(pairs) ?? [], p.GetValue(points) ?? [], p.GetValue(varsCoverage))));
            root.Subcommands.Add(vars);

            var structure = new Argument<string>("struct") { Description = "the structure, with :: between its parts (esv::roll::stream::Stream)" };
            var offset = new Argument<string>("offset") { Description = "the field's offset, hexadecimal (0x70)" };
            var fieldName = new Argument<string>("name");
            var fieldType = new Option<string?>("--type") { Description = "the field's type, when known (uint64, float, EntityHandle)" };
            var fieldSource = new Option<string>("--source") { Description = "game, toolkit, inferred, extender or unknown", DefaultValueFactory = _ => "inferred" };
            var fieldEvidence = new Option<string>("--evidence") { Description = "what backs the name", DefaultValueFactory = _ => "" };
            var fieldCoverage = new Option<string?>("--coverage") { Description = "the coverage record, which holds structs.jsonl" };
            var field = new Command("field", "name the field at an offset of a structure that is no component's (a manager's stream, a task of the cursor); a component's field is named with `gatekit names set`") { structure, offset, fieldName, fieldType, fieldSource, fieldEvidence, fieldCoverage };
            field.SetAction(p => Invoke(p, s => FunctionNames.SetField(s, p.GetValue(structure)!, p.GetValue(offset)!, p.GetValue(fieldName)!, p.GetValue(fieldType), p.GetValue(fieldSource)!, p.GetValue(fieldEvidence)!, p.GetValue(fieldCoverage))));
            root.Subcommands.Add(field);

            var which = new Argument<string?>("struct") { Arity = ArgumentArity.ZeroOrOne, Description = "a structure of the ledger; without it, the structures it has" };
            var showCoverage = new Option<string?>("--coverage") { Description = "the coverage record" };
            var show = new Command("struct", "the named fields of a structure of structs.jsonl") { which, showCoverage };
            show.SetAction(p => Invoke(p, s => FunctionNames.ShowStruct(s, p.GetValue(which), p.GetValue(showCoverage))));
            root.Subcommands.Add(show);
        }
        // grep
        {
            var text = new Argument<string>("text");
            var binary = new Option<string?>("--binary");
            var lines = new Option<int>("--lines") { Description = "matching lines to show per function (0 for none)", DefaultValueFactory = _ => 3 };
            var raw = new Option<bool>("--raw") { Description = "pass the text to SQLite FTS5 as it is" };
            var word = new Option<bool>("--word") { Description = "whole tokens only (faster); by default a word also matches inside longer names" };
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
            var within = new Option<string?>("--in") { Description = "search only the pseudo-code (code) or only the names (name); both by default", HelpName = "code|name" };
            var access = new Option<bool>("--access") { Description = "mark each function [writes] or [reads] the type searched for, from how it names it (Type vs Type const / Type_const); read off names, not proven" };
            var c = new Command("grep", "full-text search over names and pseudo-code; a namespace-qualified name (eoc::lock::LockComponent) is matched as that exact sequence, fast") { text, binary, lines, raw, word, limit, within, access };
            c.SetAction(p => Invoke(p, s => GrepCommand.Run(s, p.GetValue(text)!, p.GetValue(binary), p.GetValue(lines), p.GetValue(raw), p.GetValue(word), p.GetValue(limit), p.GetValue(within), p.GetValue(access))));
            root.Subcommands.Add(c);
        }
        // systems
        {
            var name = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne, Description = "part of a system name" };
            var rebuild = new Option<bool>("--rebuild") { Description = "read the pseudo-code again" };
            var binary = new Option<string>("--binary") { DefaultValueFactory = _ => "bg3.exe" };
            var c = new Command("systems", "where the game creates each ECS system: factory, constructor, virtual table") { name, rebuild, binary };
            c.SetAction(p => Invoke(p, s => SystemsCommand.Run(s, p.GetValue(name), p.GetValue(rebuild), p.GetValue(binary)!)));
            root.Subcommands.Add(c);
        }
        // helpers
        {
            var type = new Argument<string?>("type") { Arity = ArgumentArity.ZeroOrOne, Description = "part of a component name: list its helpers (without it, the count per family)" };
            var rebuild = new Option<bool>("--rebuild") { Description = "classify again" };
            var binary = new Option<string>("--binary") { DefaultValueFactory = _ => "bg3_dx11.exe" };
            var coverage = new Option<string?>("--coverage") { Description = "the coverage record, for the size of each type (default GATEKIT_COVERAGE, else research/coverage of the repository)" };
            var c = new Command("helpers", "the small functions keyed by one component's type index, and their family: get, has, add, remove, registration") { type, rebuild, binary, coverage };
            c.SetAction(p => Invoke(p, s => HelpersCommand.Run(s, p.GetValue(type), p.GetValue(rebuild), p.GetValue(binary)!, p.GetValue(coverage))));
            root.Subcommands.Add(c);
        }
        // tally
        {
            var binary = new Option<string[]>("--binary") { Description = "the binaries to count (default bg3_dx11.exe and bg3.exe)", DefaultValueFactory = _ => ["bg3_dx11.exe", "bg3.exe"] };
            var asJson = new Option<bool>("--json") { Description = "the counts as JSON" };
            var jobs = new Option<int>("--jobs") { Description = "threads that read the pseudo-code (default half of the processors)", DefaultValueFactory = _ => Math.Max(1, Environment.ProcessorCount / 2) };
            var c = new Command("tally", "how much of a binary reads like code (functions named, field accesses by name, typed locals, helpers, virtual tables, digest guards), and how much of what is known the registry describes, by kind and rung") { binary, asJson, jobs };
            c.SetAction(p => Invoke(p, s => TallyCommand.Run(s, p.GetValue(binary)!, p.GetValue(asJson), p.GetValue(jobs))));
            root.Subcommands.Add(c);
        }
        // pair
        {
            var source = new Option<string>("--source") { Description = "the build that gives (default bg3_macos_x64, whose functions carry the compiler's names)", DefaultValueFactory = _ => "bg3_macos_x64" };
            var target = new Option<string>("--target") { Description = "the build that receives (default bg3_dx11.exe)", DefaultValueFactory = _ => "bg3_dx11.exe" };
            var holdout = new Option<double>("--holdout") { Description = "share of the names both builds have that is held out to measure the bar (default 0.4)", DefaultValueFactory = _ => 0.4 };
            var precision = new Option<double>("--precision") { Description = "share of held-out names a bar must get right to be used (default 0.98)", DefaultValueFactory = _ => 0.98 };
            var rounds = new Option<int>("--rounds") { Description = "rounds of spreading along the call graph (default 6)", DefaultValueFactory = _ => 6 };
            var roundScore = new Option<double>("--round-score") { Description = "score a pair needs to be built upon in the next round (default 0.6)", DefaultValueFactory = _ => 0.6 };
            var roundMargin = new Option<double>("--round-margin") { Description = "lead over the runner-up it needs for that (default 0.3)", DefaultValueFactory = _ => 0.3 };
            var jobs = new Option<int>("--jobs") { DefaultValueFactory = _ => Math.Max(1, Environment.ProcessorCount / 2) };
            var dry = new Option<bool>("--dry-run") { Description = "measure and print, store nothing" };
            var misses = new Option<int>("--misses") { Description = "print this many held-out names that were proposed another name (names only)" };
            var c = new Command("pair", "pair the functions of two builds of the game from anchors both know, with the bar measured on held-out names")
                { source, target, holdout, precision, rounds, roundScore, roundMargin, jobs, dry, misses };
            c.SetAction(p => Invoke(p, s => PairCommand.Run(s, p.GetValue(source)!, p.GetValue(target)!, p.GetValue(holdout), p.GetValue(precision), p.GetValue(rounds),
                                                         new PairCommand.RoundBars(p.GetValue(roundScore), p.GetValue(roundMargin)), p.GetValue(jobs), p.GetValue(dry), p.GetValue(misses))));
            root.Subcommands.Add(c);
        }
        // queries
        {
            var queries = new Command("queries", "the queries a system's update walks and the component pointers it takes from them");
            var target = new Option<string>("--binary") { Description = "the Windows build (default bg3_dx11.exe)", DefaultValueFactory = _ => "bg3_dx11.exe", Recursive = true };
            var source = new Option<string>("--source") { Description = "the build that names its queries (default bg3_macos_x64)", DefaultValueFactory = _ => "bg3_macos_x64", Recursive = true };
            var coverage = new Option<string?>("--coverage") { Description = "the coverage record, for the size of each component (default GATEKIT_COVERAGE, else research/coverage of the repository)", Recursive = true };
            var asJson = new Option<bool>("--json") { Description = "the counts and the rows as JSON", Recursive = true };
            queries.Options.Add(target);
            queries.Options.Add(source);
            queries.Options.Add(coverage);
            queries.Options.Add(asJson);
            {
                var system = new Option<string?>("--system") { Description = "only systems whose name contains this" };
                var forms = new Option<int>("--forms") { Description = "list this many of the most frequent forms of the base expression (default 0)" };
                var c = new Command("survey", "count the shape: per update the query globals it reads, the values index * N + base, and whether one type of its macOS specs has that size") { system, forms };
                c.SetAction(p => Invoke(p, s => QueriesCommand.Survey(s, p.GetValue(target)!, p.GetValue(source)!, p.GetValue(coverage), p.GetValue(system), p.GetValue(asJson), p.GetValue(forms))));
                queries.Subcommands.Add(c);
            }
            {
                var dry = new Option<bool>("--dry-run") { Description = "measure and print, store nothing" };
                var rounds = new Option<int>("--rounds") { Description = "rounds of tying between globals already tied (default 4; 0 for position alone)", DefaultValueFactory = _ => 4 };
                var wait = new Option<int>("--wait") { Description = "seconds to wait for readers to let go of decomp.sqlite before giving up (default 60)", DefaultValueFactory = _ => 60 };
                var show = new Option<int>("--show") { Description = "list this many globals the systems disagree on" };
                var c = new Command("map", "tie each query global to a macOS spec by the order of use, keep those every system agrees on, and store them") { dry, rounds, wait, show };
                c.SetAction(p => Invoke(p, s => QueriesCommand.Map(s, p.GetValue(target)!, p.GetValue(source)!, p.GetValue(asJson), p.GetValue(dry), p.GetValue(rounds), p.GetValue(wait), p.GetValue(show))));
                queries.Subcommands.Add(c);
            }
            {
                var forms = new Option<int>("--forms") { Description = "list this many places and forms (default 30)", DefaultValueFactory = _ => 30 };
                var c = new Command("columns", "check the column rule: per place of the iterator's state, how often the stride is the size of the k-th type of the query") { forms };
                c.SetAction(p => Invoke(p, s => QueriesCommand.Columns(s, p.GetValue(target)!, p.GetValue(source)!, p.GetValue(coverage), p.GetValue(asJson), p.GetValue(forms))));
                queries.Subcommands.Add(c);
            }
            {
                var digests = new Option<string>("--digests") { Description = "a folder of JSON digests of a whole pass (symbols digest --all --json --out DIR)", Required = true };
                var c = new Command("guards", "where the digests' guards that name nothing stand, against the functions the typing reaches") { digests };
                c.SetAction(p => Invoke(p, s => QueriesCommand.Guards(s, p.GetValue(target)!, p.GetValue(source)!, p.GetValue(coverage), p.GetValue(digests)!, p.GetValue(asJson))));
                queries.Subcommands.Add(c);
            }
            root.Subcommands.Add(queries);
        }
        // match
        {
            var binary = new Option<string>("--binary") { DefaultValueFactory = _ => "bg3.exe" };
            var jobs = new Option<int>("--jobs") { DefaultValueFactory = _ => cpus };
            var source = new Option<string?>("--source") { Description = "take names from this one loaded binary (the macOS build) instead of the Toolkit DLLs" };
            var c = new Command("match", "pair Toolkit functions with game functions by their pseudo-code") { binary, jobs, source };
            c.SetAction(p => Invoke(p, s => CodeMatch.Run(s, p.GetValue(binary)!, p.GetValue(jobs), p.GetValue(source))));
            root.Subcommands.Add(c);
        }
        // matches
        {
            var text = new Argument<string>("text");
            var all = new Option<bool>("--all") { Description = "also pairs below the default thresholds" };
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 60 };
            var c = new Command("matches", "pairs found by pseudo-code: part of a Toolkit function name, or bg3.exe+0xRVA") { text, all, limit };
            c.SetAction(p => Invoke(p, s => CodeMatch.Matches(s, p.GetValue(text)!, p.GetValue(all), p.GetValue(limit))));
            root.Subcommands.Add(c);
        }
        // fixedstrings
        {
            var text = new Argument<string?>("text") { Arity = ArgumentArity.ZeroOrOne, Description = "part of the text, or bg3.exe+0xRVA of the global" };
            var rebuild = new Option<bool>("--rebuild") { Description = "read the pseudo-code again" };
            var binary = new Option<string>("--binary") { DefaultValueFactory = _ => "bg3.exe" };
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 60 };
            var c = new Command("fixedstrings", "global FixedStrings of the game: by part of the text, or by address of the global") { text, rebuild, binary, limit };
            c.SetAction(p => Invoke(p, s => FixedStringsCommand.Run(s, p.GetValue(text), p.GetValue(rebuild), p.GetValue(binary)!, p.GetValue(limit))));
            root.Subcommands.Add(c);
        }
        // dotnet
        {
            var dotnet = new Option<string?>("--dotnet")
            {
                Description = "folder of a .NET runtime (dotnet inside); default DOTNET_ROOT",
                DefaultValueFactory = _ => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ROOT")) ? null : Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            };
            var only = new Option<string?>("--only") { Description = "only assemblies whose name contains this" };
            var timeout = new Option<int>("--timeout") { Description = "seconds to give one assembly (default 900)", DefaultValueFactory = _ => 900 };
            var fresh = new Option<bool>("--fresh") { Description = "decompile again" };
            var c = new Command("dotnet", "decompile the Toolkit's managed assemblies (the editor) to C#") { dotnet, only, timeout, fresh };
            c.SetAction(p => Invoke(p, s => ManagedCommand.Dotnet(s, p.GetValue(dotnet), p.GetValue(only), p.GetValue(timeout), p.GetValue(fresh))));
            root.Subcommands.Add(c);
        }
        // csharp
        {
            var text = new Argument<string>("text");
            var file = new Option<bool>("--file") { Description = "the text is Assembly/path.cs: print that file" };
            var lines = new Option<int>("--lines") { DefaultValueFactory = _ => 3 };
            var raw = new Option<bool>("--raw");
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 40 };
            var c = new Command("csharp", "search the editor's decompiled C#, or print a file with --file") { text, file, lines, raw, limit };
            c.SetAction(p => Invoke(p, s => ManagedCommand.CSharp(s, p.GetValue(text)!, p.GetValue(file), p.GetValue(lines), p.GetValue(raw), p.GetValue(limit))));
            root.Subcommands.Add(c);
        }
        // status
        {
            var c = new Command("status", "what has been decompiled and loaded");
            c.SetAction(p => Invoke(p, LoadCommand.Status));
            root.Subcommands.Add(c);
        }
        // stamp
        {
            var binary = new Argument<string?>("binary") { Arity = ArgumentArity.ZeroOrOne, Description = "only this binary" };
            var c = new Command("stamp", "record what each binary of decomp.sqlite is - SHA-256 of the file its project imported, looked up in the reference lists - and the game build of the database") { binary };
            c.SetAction(p => Invoke(p, s => Provenance.Stamp(s, p.GetValue(binary))));
            root.Subcommands.Add(c);
        }
        return root;
    }
}
