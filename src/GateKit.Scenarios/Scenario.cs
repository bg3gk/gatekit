using System.Reflection;
using System.Text;
using GateKit.Stand;
using MoonSharp.Interpreter;

namespace GateKit.Scenarios;

/// <summary>The start a scenario needs. Scenarios that want the same one share a game session.</summary>
/// <param name="Level">MODULE:LEVEL for a fresh test game, as `up --level`.</param>
/// <param name="Mods">Mod folders besides the base game; null means the player's own list.</param>
/// <param name="Save">A save to load instead of a fresh level.</param>
/// <param name="Clean">Return the world to the state right after the start before this scenario.</param>
public sealed record ScenarioStart(string? Level = null, IReadOnlyList<string>? Mods = null, string? Save = null, bool Clean = false)
{
    /// <summary>What two scenarios must agree on to share one game.</summary>
    public (string?, string, bool, string?) Key => (Level, string.Join(",", Mods ?? []), Mods is null, Save);
}

/// <summary>
/// A scenario: a Lua file that declares the start it needs and defines <c>run(game)</c>.
///
/// <code>
/// scenario { level = "GustavX:TUT_Avernus_C", mods = {}, clean = true }
///
/// function run(game)
///     assert(game.server("return Osi.GetHostCharacter()"), "no host character")
/// end
/// </code>
///
/// The file runs on the host in an embedded Lua interpreter (MoonSharp), not in the game;
/// <c>game.server(...)</c> and friends are what reach the game.
/// </summary>
public sealed class Scenario
{
    Scenario(string path, string code)
    {
        Path = path;
        Code = code;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public string Path { get; }
    public string Name { get; }
    public string Code { get; }
    public ScenarioStart Start { get; private set; } = new();

    static string? _prelude;

    /// <summary>The helpers every scenario gets (Prelude.lua, an embedded resource).</summary>
    public static string Prelude => _prelude ??= Read();

    static string Read()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Prelude.lua", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
    }

    /// <summary>A Lua interpreter with the prelude loaded; print goes to <paramref name="output"/>.</summary>
    public static Script NewScript(TextWriter output, string? scenarioFolder = null)
    {
        var script = new Script(CoreModules.Preset_SoftSandbox)
        {
            Options = { DebugPrint = line => output.WriteLine("  " + line) },
        };
        if (scenarioFolder is not null) script.Options.ScriptLoader = new MoonSharp.Interpreter.Loaders.FileSystemScriptLoader { ModulePaths = [System.IO.Path.Combine(scenarioFolder, "?.lua")] };
        // The interpreter's own string.byte does not know characters above ASCII, and the game
        // counts the bytes of its UTF-8: the host answers that question.
        script.Globals["bytes"] = DynValue.NewCallback((_, a) =>
            DynValue.NewNumber(Encoding.UTF8.GetByteCount(a[0].CastToString() ?? "")), "bytes");
        script.DoString(Prelude, null, "prelude");
        return script;
    }

    /// <summary>Reads a scenario file and runs its declaration (not its body).</summary>
    public static Scenario Load(string path)
    {
        var scenario = new Scenario(System.IO.Path.GetFullPath(path), File.ReadAllText(path, Encoding.UTF8));
        var script = NewScript(TextWriter.Null, System.IO.Path.GetDirectoryName(scenario.Path));
        scenario.Declare(script);
        if (script.Globals.Get("run").Type != DataType.Function)
            throw new StandException($"{scenario.Name}: a scenario must define `function run(game)`");
        return scenario;
    }

    /// <summary>Runs the file's top level, which sets the globals and may call `scenario { ... }`.</summary>
    public Script Declare(Script script)
    {
        script.Globals["scenario"] = DynValue.NewCallback((_, a) =>
        {
            var options = a[0].Type == DataType.Table ? a[0].Table : null;
            Start = new ScenarioStart(LuaValues.OptionalString(options, "level"), LuaValues.OptionalList(options, "mods"),
                LuaValues.OptionalString(options, "save"), LuaValues.Flag(options, "clean"));
            return DynValue.Nil;
        }, "scenario");
        try { script.DoString(Code, null, Name); }
        catch (SyntaxErrorException e) { throw new StandException($"{Name}: {e.DecoratedMessage ?? e.Message}", e); }
        return script;
    }

    /// <summary>Every <c>*.lua</c> in a folder whose name contains <paramref name="only"/>, in name order.</summary>
    public static IReadOnlyList<Scenario> Discover(string folder, string? only = null)
    {
        if (!Directory.Exists(folder)) throw new StandException($"no scenario folder {folder}");
        var found = new List<Scenario>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.lua").Order(StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (only is { Length: > 0 } && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith('_')) continue; // a shared file, not a scenario
            found.Add(Load(path));
        }
        if (found.Count == 0) throw new StandException($"no scenarios in {folder}");
        return found;
    }
}
