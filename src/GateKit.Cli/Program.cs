using System.CommandLine;
using System.Reflection;
using System.Text;
using GateKit.Cli;
using GateKit.Platform;

// Before anything else, because the console window exists from the moment this process starts and
// every instruction before this one is a moment longer that it is on the screen taking somebody's
// keyboard. Only for `deck`, which opens a window and leaves: every other command has something to
// say and a console of its own is where it says it.
// The command is the first word after the global options (`--instance NAME`), wherever it stands.
var verbAt = RunCopy.VerbAt(args);
var verb = verbAt < 0 ? null : args[verbAt];
if (verb is "deck" && !(verbAt + 1 < args.Length && args[verbAt + 1] == "close")) RunCopy.HideOwnConsoleWindow();

// Helper modes the platform runs in a copy of this program (reading the game's console on Windows).
if (PlatformHelper.TryRun(args, out var helperExit)) return helperExit;

// The two commands that stay up for hours run from a copy of this folder rather than out of bin,
// so that the repository can still be rebuilt while they are: a running program keeps the
// assemblies it loaded locked, and a build that cannot write over them fails in a way that reads
// like broken code. `RunCopy` says when this applies and when it does not.
// These two speak over the pipes their caller gave this process - the MCP server to its client,
// the test suite to whoever is watching it run - so this process stays around the copy and
// passes them through. Both run long enough that a build during one used to fail on locked files.
// The deck relaunches itself from inside its own command, where it can say where its log is.
if (verb is "mcp" or "test" && RunCopy.Relaunch(verb, args, Console.Error) is { } copied) return copied;

// Redirected output (a pipe, a file, an agent) is UTF-8 whatever the console's code page.
if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
if (Console.IsErrorRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

var root = new RootCommand("GateKit: a modding toolkit for Baldur's Gate 3. It starts the game or a copy of it, talks to it "
    + "through the bridge mod and puts the player's saves and mod list back afterwards.");
root.Add(CopyCommands.InstanceOption);
root.Add(CopyCommands.InstalledGameOption);
foreach (var command in CopyCommands.Create()) root.Add(command);
foreach (var command in ProjectCommands.Discover(root)) root.Add(command);

// `@file` is GateKit's own: `gatekit exec server @scene.lua` runs that file's Lua. System.
// CommandLine claims the same spelling for response files and expands the file into arguments
// before anything of ours is reached, so a Lua script came back as a hundred "unrecognized
// command or argument" lines, one per word. Our meaning wins; response files are not a thing
// this program has ever offered.
// A database that is not there, or a gatekit.json that cannot be read, is a sentence for the
// person whatever command met it - never a stack trace, and never an empty answer.
try
{
    return await root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null })
        .InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (Exception e) when (e is GateKit.Home.MissingDatabaseException or GateKit.Home.HomeException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception e)
{
    // What System.CommandLine's own handler said, which this replaces.
    Console.Error.WriteLine("Unhandled exception: " + e);
    return 1;
}

namespace GateKit.Cli
{
    /// <summary>
    /// The one place where the other GateKit projects join the command tree.
    ///
    /// Convention: a project that has commands (GameData, Symbols, Mcp, Tests, ...) declares a
    /// public static class with <c>public static Command CreateCommand()</c> (System.CommandLine 2.0.15,
    /// the version the whole solution uses) returning its top-level command, e.g. `data`, `symbols`,
    /// `mcp`, `test`. Every GateKit.*.dll next to this program is searched; what exists is
    /// registered, in assembly name order, and nothing else has to change here. A name that is
    /// already taken (a command of the copies) is skipped with a warning on stderr.
    /// </summary>
    static class ProjectCommands
    {
        public static IEnumerable<Command> Discover(RootCommand root)
        {
            var taken = root.Subcommands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var self = typeof(ProjectCommands).Assembly.GetName().Name;
            var files = Directory.EnumerateFiles(AppContext.BaseDirectory, "GateKit.*.dll").Order(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name == self) continue;
                Assembly assembly;
                try { assembly = Assembly.Load(new AssemblyName(name)); }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) { continue; }
                foreach (var type in assembly.GetExportedTypes().Where(t => t.IsAbstract && t.IsSealed).OrderBy(t => t.FullName, StringComparer.Ordinal))
                {
                    var factory = type.GetMethod("CreateCommand", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
                    if (factory is null || !typeof(Command).IsAssignableFrom(factory.ReturnType)) continue;
                    if (factory.Invoke(null, null) is not Command command) continue;
                    if (!taken.Add(command.Name))
                    {
                        Console.Error.WriteLine($"warning: {type.FullName}.CreateCommand() returns `{command.Name}`, which already exists; skipped");
                        continue;
                    }
                    yield return command;
                }
            }
        }
    }
}
