using System.Reflection;
using System.Text;
using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.Testing;

/// <summary>The start a test needs. Tests that want the same one share a game session.</summary>
/// <param name="Level">MODULE:LEVEL for a fresh test game, as `up --level`.</param>
/// <param name="Mods">Mod folders besides the base game; null means the player's own list.</param>
/// <param name="Save">A save to load instead of a fresh level.</param>
/// <param name="Reset">What the test needs before it runs (test{reset = "none"|"fixture"|"venue"|"game"});
/// null: the reset ladder's default for its level.</param>
public sealed record TestStart(string? Level = null, IReadOnlyList<string>? Mods = null, string? Save = null,
    bool MainMenu = false, ResetRung? Reset = null)
{
    /// <summary>The rung declared.</summary>
    public ResetRung? Needs => Reset;

    /// <summary>What two tests must agree on to share one game.</summary>
    public (string?, string, bool, string?) Key => (MainMenu ? "<main menu>" : Level, string.Join(",", Mods ?? []), Mods is null, Save);
}

/// <summary>
/// A test: a Lua file that declares the start it needs and defines <c>run(game)</c>.
///
/// <code>
/// test { level = "GustavX:TUT_Avernus_C", mods = {}, reset = "venue" }
///
/// function run(game)
///     assert(game.server("return Osi.GetHostCharacter()"), "no host character")
/// end
/// </code>
///
/// The file runs on the host in an embedded Lua interpreter (MoonSharp), not in the game;
/// <c>game.server(...)</c> and friends are what reach the game.
/// </summary>
public sealed class GameTest
{
    GameTest(string path, string code)
    {
        Path = path;
        Code = code;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public string Path { get; }
    /// <summary>The test's id: its path under the folder it was found in (<see cref="Discover"/>), else its file name.</summary>
    public string Name { get; private set; }

    /// <summary>The same test once more, named <c>name#n</c> (`test --repeat`).</summary>
    public GameTest Again(int n)
    {
        var again = new GameTest(Path, Code) { Name = Name + "#" + n, Start = Start, Declared = Declared, _about = _about, Root = Root,
            _surprises = _surprises, SpeedCeiling = SpeedCeiling, CeilingFinding = CeilingFinding, Before = Before, OnlyCase = OnlyCase };
        again._cases.AddRange(_cases);
        return again;
    }

    /// <summary>The folder the test was found under (<see cref="Discover"/>); a file loaded alone
    /// has none, and every _suite.json above it counts.</summary>
    public string Root { get; private set; } = "";

    /// <summary>The highest game speed at which the test gives its verdict (`test{ speed_ceiling = 1,
    /// ceiling_finding = "..." }`): the runner never starts it faster. A ceiling is refused without
    /// `ceiling_finding`, which names where the finding that proves the game, not our API, is the cause
    /// is written (a study's hypothesis, a test).</summary>
    public double? SpeedCeiling { get; private set; }

    /// <summary>Where the finding behind <see cref="SpeedCeiling"/> is written.</summary>
    public string? CeilingFinding { get; private set; }

    /// <summary>The test's cases, in the order the file gives them (`case(name, fn)` at its top
    /// level): variations of its one rule, each run from a clean scene. Empty for a test without.</summary>
    public IReadOnlyList<string> Cases => _cases;
    readonly List<string> _cases = [];

    /// <summary>The global function the header names in `before`: the staging every case shares,
    /// run again before each case.</summary>
    public string? Before { get; private set; }

    /// <summary>The one case `-k &lt;test id&gt;/&lt;case&gt;` selects; null: every case.</summary>
    public string? OnlyCase { get; set; }

    /// <summary>What a case's name may be: lower case, no `/` - one level of an address.</summary>
    public static string? CaseNameProblem(string name) =>
        name.Length == 0 ? "a case has a name"
        : name.Contains('/') ? $"case \"{name}\": a name has no `/` (an address is <test>/<case>/<step>)"
        : name != name.ToLowerInvariant() ? $"case \"{name}\": a name is lower case"
        : null;

    IReadOnlyList<string> _surprises = [];

    /// <summary>The surprises of the event log this test allows (<see cref="Surprises"/>): its header's
    /// `surprises` and those of every _suite.json from its folder up to <see cref="Root"/>.</summary>
    public IReadOnlySet<string> AllowedSurprises()
    {
        var allowed = new HashSet<string>(_surprises, StringComparer.Ordinal);
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        // never above the folder given: a file run alone reads its own folder's _suite.json only
        try { allowed.UnionWith(Surprises.AllowedBySuites(folder, Root.Length > 0 ? Root : folder)); }
        catch (Exception e) when (e is GateKit.Json.JsonProblemException or IOException) { throw new CopyException($"{Name}: {e.Message}", e); }
        return allowed;
    }
    public string Code { get; }
    public TestStart Start { get; private set; } = new();

    /// <summary>What the test shows and the rule of the game behind it: `test{ about = "..." }`,
    /// else the comment lines at the top of the file. Shown with a failure, so its reader has the rule
    /// in front of them.</summary>
    public string About => _about ?? Leading(Code);

    string? _about;

    /// <summary>The leading comment block of a Lua file, without the dashes (blank lines end it).</summary>
    public static string Leading(string code)
    {
        var lines = new List<string>();
        foreach (var raw in code.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("--", StringComparison.Ordinal) || line.StartsWith("--[[", StringComparison.Ordinal)) break;
            lines.Add(line.TrimStart('-').Trim());
        }
        return string.Join("\n", lines).Trim();
    }

    /// <summary>Whether the file called <c>test { ... }</c> when its top level ran.</summary>
    public bool Declared { get; private set; }

    /// <summary>The day <c>bg3_recipe</c> began writing tests; files under
    /// <c>tests/_coverage/</c> recorded before it are free-form Lua for the game, not tests.</summary>
    public const string RecipesReplayableSince = "2026-11-14";

    static string? _prelude;

    /// <summary>The helpers every test gets (Prelude.lua, an embedded resource).</summary>
    public static string Prelude => _prelude ??= Read();

    static string Read()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Prelude.lua", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
    }

    /// <summary>A Lua interpreter with the prelude loaded; print goes to <paramref name="output"/>.</summary>
    public static Script NewScript(TextWriter output, string? testFolder = null)
    {
        // no load, loadfile or dofile: a test reaches other code only through `require` of shared files
        var script = new Script(CoreModules.Preset_SoftSandbox)
        {
            Options = { DebugPrint = line => output.WriteLine("  " + line) },
        };
        if (testFolder is not null) script.Globals["require"] = Require(script, testFolder);
        // The interpreter's own string.byte does not know characters above ASCII, and the game
        // counts the bytes of its UTF-8: the host answers that question.
        script.Globals["bytes"] = DynValue.NewCallback((_, a) =>
            DynValue.NewNumber(Encoding.UTF8.GetByteCount(a[0].CastToString() ?? "")), "bytes");
        script.DoString(Prelude, null, "prelude");
        return script;
    }

    /// <summary>`require(name)`: a shared file - one whose name begins with `_` - beside the test
    /// (`require("_dialogue")`) or in a sibling folder (`require("dialogue/_dialogue")` from a
    /// folder beside `dialogue/`), run once and its value kept. Any other name is refused.</summary>
    static DynValue Require(Script script, string testFolder)
    {
        var loaded = new Dictionary<string, DynValue>(StringComparer.OrdinalIgnoreCase);
        var parent = System.IO.Path.GetDirectoryName(testFolder) ?? testFolder;
        return DynValue.NewCallback((_, a) =>
        {
            var name = a[0].Type == DataType.String ? a[0].String : throw new ScriptRuntimeException("require(name): the name of a shared file");
            var parts = name.Replace('\\', '/').Split('/');
            if (parts.Length > 2 || parts.Any(p => p.Length == 0 || p == "." || p == ".."))
                throw new ScriptRuntimeException($"require(\"{name}\"): a shared file beside the test (\"_name\") or in a sibling folder (\"folder/_name\")");
            if (!parts[^1].StartsWith('_'))
                throw new ScriptRuntimeException($"require(\"{name}\"): only shared files are required - a file whose name begins with `_`");
            foreach (var path in new[] { System.IO.Path.Combine(testFolder, name + ".lua"), System.IO.Path.Combine(parent, name + ".lua") })
            {
                var full = System.IO.Path.GetFullPath(path);
                if (loaded.TryGetValue(full, out var kept)) return kept;
                if (!File.Exists(full)) continue;
                var value = script.DoString(File.ReadAllText(full, Encoding.UTF8), null, name);
                loaded[full] = value.IsNil() ? DynValue.True : value;
                return loaded[full];
            }
            throw new ScriptRuntimeException($"require(\"{name}\"): no {name}.lua beside the test or in a sibling folder");
        }, "require");
    }

    /// <summary>Reads a test file and runs its declaration (not its body).</summary>
    public static GameTest Load(string path)
    {
        var test = new GameTest(System.IO.Path.GetFullPath(path), File.ReadAllText(path, Encoding.UTF8));
        var script = NewScript(TextWriter.Null, System.IO.Path.GetDirectoryName(test.Path));
        test.Declare(script);
        if (script.Globals.Get("run").Type != DataType.Function && test.Cases.Count == 0)
            throw new CopyException($"{test.Name}: a test must define `function run(game)` or its cases (`case(name, fn)`)");
        return test;
    }

    /// <summary>The line a file that declares no test is skipped with: neither
    /// <c>test { ... }</c> nor <c>function run(game)</c>. Such files are the free-form recipes
    /// <c>bg3_recipe</c> wrote before it wrote tests - Lua meant for the game, which fails at once
    /// when the host runs its top level.</summary>
    public static string NotReplayable(string name) =>
        $"{name}: not replayable: a free-form recipe from before {RecipesReplayableSince} (it declares no `test {{ }}` and no `function run(game)`)";

    /// <summary>The fields a test's header <c>test { ... }</c> may hold (docs/writing-tests.md, "The file").</summary>
    public static readonly IReadOnlyList<string> HeaderFields =
        ["about", "level", "mods", "save", "menu", "reset", "surprises", "speed_ceiling", "ceiling_finding", "before"];

    /// <summary>Why a header holds a field it does not know (null when every field is known): a field
    /// nobody reads would otherwise be ignored without a word.</summary>
    public static string? HeaderProblem(Table? header)
    {
        if (header is null) return null;
        var unknown = header.Keys
            .Select(k => k.Type == DataType.String ? k.String : k.ToPrintString())
            .Where(k => !HeaderFields.Contains(k, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        return unknown.Count == 0 ? null
            : $"the header test {{ ... }} does not know {string.Join(", ", unknown.Select(k => $"'{k}'"))}; it knows {string.Join(", ", HeaderFields)}";
    }

    ResetRung ParseReset(string text)
    {
        try { return ResetRungs.Parse(text); }
        catch (CopyException e) { throw new ScriptRuntimeException($"{Name}: {e.Message}"); }
    }

    /// <summary>Runs the file's top level, which sets the globals and may call `test { ... }`.</summary>
    public Script Declare(Script script)
    {
        _cases.Clear();
        var bodies = new Table(script);
        var called = false;
        script.Globals["__gk_cases"] = bodies;
        script.Globals["case"] = DynValue.NewCallback((_, a) =>
        {
            called = true;
            var name = a[0].Type == DataType.String ? a[0].String : throw new ScriptRuntimeException($"{Name}: case(name, fn): a case has a name");
            if (CaseNameProblem(name) is { } problem) throw new ScriptRuntimeException($"{Name}: {problem}");
            if (a[1].Type is not (DataType.Function or DataType.ClrFunction)) throw new ScriptRuntimeException($"{Name}: case \"{name}\": its body is a function");
            if (_cases.Contains(name)) throw new ScriptRuntimeException($"{Name}: case \"{name}\" is given twice");
            _cases.Add(name);
            bodies.Set(name, a[1]);
            return DynValue.Nil;
        }, "case");
        script.Globals["test"] = DynValue.NewCallback((_, a) =>
        {
            var options = a[0].Type == DataType.Table ? a[0].Table : null;
            Declared = true;
            if (HeaderProblem(options) is { } unknown) throw new ScriptRuntimeException($"{Name}: {unknown}");
            _about = LuaValues.OptionalString(options, "about");
            _surprises = LuaValues.OptionalList(options, "surprises") ?? [];
            SpeedCeiling = options?.Get("speed_ceiling") is { Type: DataType.Number } ceiling && ceiling.Number > 0 ? ceiling.Number : null;
            CeilingFinding = LuaValues.OptionalString(options, "ceiling_finding");
            Before = LuaValues.OptionalString(options, "before");
            if (SpeedCeiling is not null && string.IsNullOrWhiteSpace(CeilingFinding))
                throw new ScriptRuntimeException($"{Name}: a speed_ceiling names its finding (ceiling_finding = \"where it is written\"): "
                    + "a test that fails only when fast is first a suspicion against our own API (docs/writing-tests.md, Speed)");
            Start = new TestStart(LuaValues.OptionalString(options, "level"), LuaValues.OptionalList(options, "mods"),
                LuaValues.OptionalString(options, "save"), LuaValues.Flag(options, "menu"),
                LuaValues.OptionalString(options, "reset") is { } reset ? ParseReset(reset) : null);
            return DynValue.Nil;
        }, "test");
        try { script.DoString(Code, null, Name); }
        catch (SyntaxErrorException e) { throw new CopyException($"{Name}: {e.DecoratedMessage ?? e.Message}", e); }
        catch (InterpreterException e)
        {
            if (!Declared && !called && script.Globals.Get("run").Type != DataType.Function) throw new NotATestException(NotReplayable(Name), e);
            throw new CopyException($"{Name}: {e.DecoratedMessage ?? e.Message}", e);
        }
        if (!Declared && script.Globals.Get("run").Type != DataType.Function && _cases.Count == 0) throw new NotATestException(NotReplayable(Name));
        if (_cases.Count > 0 && script.Globals.Get("run").Type == DataType.Function)
            throw new CopyException($"{Name}: a test either has cases or it does not: with case(...) it defines no run(game) "
                + "(the staging its cases share goes in a function the header names in `before`)");
        if (Before is { } before && script.Globals.Get(before).Type != DataType.Function)
            throw new CopyException($"{Name}: the header's before = \"{before}\" names no function of the file");
        if (Before is not null && _cases.Count == 0) throw new CopyException($"{Name}: `before` is the staging of cases, and the test has none");
        return script;
    }

    /// <summary>The body of a case in a script <see cref="Declare"/> ran.</summary>
    public static DynValue CaseBody(Script script, string name) => script.Globals.Get("__gk_cases").Table.Get(name);

    /// <summary>The id of a test under a folder: its path relative to the folder, with `/` between the
    /// parts and without .lua (<c>combat/attack/melee_miss</c>; a file of the folder itself: its name).</summary>
    public static string IdOf(string folder, string path) =>
        System.IO.Path.ChangeExtension(System.IO.Path.GetRelativePath(folder, path), null)!.Replace('\\', '/');

    /// <summary>Every <c>*.lua</c> of a folder and its subfolders, at any depth, whose id
    /// (<see cref="IdOf"/>) <see cref="Selects"/> <paramref name="only"/>, in id order; the test is named by its id.
    /// A file whose name starts with `_` is shared code, not a test; a subfolder whose _suite.json
    /// says `"read": "named"` is read only when it is the folder given. A file that declares no
    /// test is left out, and said so on <paramref name="skipped"/>.</summary>
    public static IReadOnlyList<GameTest> Discover(string folder, string? only = null, TextWriter? skipped = null)
    {
        if (!Directory.Exists(folder)) throw new CopyException($"no test folder {folder}");
        var found = new List<GameTest>();
        var passed = 0;
        var files = Files(folder).Select(p => (Path: p, Id: IdOf(folder, p))).OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        var ids = files.Where(f => !System.IO.Path.GetFileName(f.Path).StartsWith('_')).Select(f => f.Id).ToList();
        // `-k <test id>/<case>`: that test, that case of it alone
        string? onlyCase = null;
        if (only is { Length: > 0 } && !ids.Contains(only, StringComparer.OrdinalIgnoreCase) && only.LastIndexOf('/') is var cut && cut > 0
            && ids.FirstOrDefault(i => string.Equals(i, only[..cut], StringComparison.OrdinalIgnoreCase)) is { } owner)
        {
            onlyCase = only[(cut + 1)..];
            only = owner;
        }
        foreach (var path in files.Select(f => f.Path))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var id = IdOf(folder, path);
            if (!Selects(only, id, ids)) continue;
            if (name.StartsWith('_')) continue; // a shared file, not a test
            try
            {
                var test = Load(path);
                test.Name = id;
                test.Root = System.IO.Path.GetFullPath(folder);
                if (onlyCase is not null)
                {
                    if (!test.Cases.Contains(onlyCase))
                        throw new CopyException($"{id} has no case \"{onlyCase}\"" + (test.Cases.Count > 0 ? "; its cases: " + string.Join(", ", test.Cases) : "; it has no cases"));
                    test.OnlyCase = onlyCase;
                }
                found.Add(test);
            }
            catch (NotATestException e)
            {
                skipped?.WriteLine("SKIP  " + e.Message);
                passed++;
            }
        }
        if (found.Count == 0)
            throw new CopyException($"no tests in {folder}" + (passed > 0 ? $" ({passed} file(s) skipped: they declare no test)" : ""));
        return found;
    }

    /// <summary>Whether `-k <paramref name="only"/>` picks the test <paramref name="id"/> among
    /// <paramref name="ids"/>: text that is a whole id (any case) picks that test alone, other text
    /// every id that contains it; no text picks all.</summary>
    public static bool Selects(string? only, string id, IReadOnlyCollection<string> ids)
    {
        if (only is not { Length: > 0 }) return true;
        if (ids.Contains(only, StringComparer.OrdinalIgnoreCase)) return string.Equals(id, only, StringComparison.OrdinalIgnoreCase);
        return id.Contains(only, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a folder's _suite.json (tests/suite) says it is read only when named itself.</summary>
    static bool ReadOnlyWhenNamed(string folder)
    {
        var suite = System.IO.Path.Combine(folder, "_suite.json");
        if (!File.Exists(suite)) return false;
        try { return GateKit.Json.JsonText.Text(GateKit.Json.JsonText.Read(suite, "tests/suite")["read"]) == "named"; }
        catch (Exception e) when (e is GateKit.Json.JsonProblemException or IOException) { throw new CopyException($"{suite}: {e.Message}", e); }
    }

    static IEnumerable<string> Files(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.lua")) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            if (ReadOnlyWhenNamed(sub)) continue;
            foreach (var file in Files(sub)) yield return file;
        }
    }
}

/// <summary>A file that declares no test (<see cref="GameTest.NotReplayable"/>).</summary>
public sealed class NotATestException(string message, Exception? inner = null) : CopyException(message, inner);
