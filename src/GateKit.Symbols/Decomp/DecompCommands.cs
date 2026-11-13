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
            gatekit decomp code ecl::TimelineSystem::TimelineSystem
            gatekit decomp code bg3.exe+0x1D6FA90
            gatekit decomp grep "ShowWeapon"      search names and pseudo-code
            gatekit decomp status

        Needs Ghidra and the JDK it asks for (JDK 25 for Ghidra 12), under BG3_DECOMP_TOOLS or GHIDRA_HOME.
        Projects, output and the database live in the work folder (BG3_DECOMP_WORK). The pseudo-code is
        derived from the game's binaries: keep it to yourself.
        """;

    /// <summary>The `decomp` command with all its subcommands, settings from the environment.</summary>
    public static Command Create() => Create(DecompSettings.FromEnvironment);

    /// <summary>The same with the settings taken from a factory (called once per invocation).</summary>
    public static Command Create(Func<DecompSettings> settings)
    {
        int cpus = Math.Max(1, Environment.ProcessorCount - 2);
        var root = new Command("decomp", About);

        int Invoke(ParseResult p, Func<DecompSession, int> body)
        {
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
        // code
        {
            var name = new Argument<string>("name") { Description = "a function name, binary+0xRVA, or an absolute address" };
            var all = new Option<bool>("--all") { Description = "print every function that matches" };
            var plain = new Option<bool>("--plain") { Description = "print the pseudo-code as Ghidra wrote it, without field names and hints" };
            var grep = new Option<string?>("--grep") { Description = "print only the lines that contain the text (a|b: either of the texts), with line numbers", HelpName = "TEXT" };
            var context = new Option<int>("--context", "-C") { Description = "lines around each line found by --grep (default 3)", DefaultValueFactory = _ => 3 };
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 40 };
            var c = new Command("code", "pseudo-code of a function by name or binary+0xRVA") { name, all, plain, grep, context, limit };
            c.SetAction(p => Invoke(p, s => CodeCommand.Run(s, p.GetValue(name)!, p.GetValue(all), p.GetValue(plain), p.GetValue(grep), p.GetValue(context), p.GetValue(limit))));
            root.Subcommands.Add(c);
        }
        // grep
        {
            var text = new Argument<string>("text");
            var binary = new Option<string?>("--binary");
            var lines = new Option<int>("--lines") { Description = "matching lines to show per function (0 for none)", DefaultValueFactory = _ => 3 };
            var raw = new Option<bool>("--raw") { Description = "pass the text to SQLite FTS5 as it is" };
            var word = new Option<bool>("--word") { Description = "whole tokens only (faster); by default a word also matches inside longer names" };
            var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
            var c = new Command("grep", "full-text search over names and pseudo-code") { text, binary, lines, raw, word, limit };
            c.SetAction(p => Invoke(p, s => GrepCommand.Run(s, p.GetValue(text)!, p.GetValue(binary), p.GetValue(lines), p.GetValue(raw), p.GetValue(word), p.GetValue(limit))));
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
        return root;
    }
}
