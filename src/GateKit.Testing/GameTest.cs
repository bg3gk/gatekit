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
/// <param name="StartupVideos">The game plays its startup videos, the game's and the mods', as `up --startup-videos`
/// (test{startup_videos = true}); every other start hides them.</param>
/// <param name="UntilLua">The test runs as soon as the bridge first answers, as `up --until-lua`
/// (test{until_lua = true}): no menu, no level, no server.</param>
/// <param name="FirstLaunch">The test runs on the game's first first-launch page, as `up --first-launch`
/// (test{first_launch = true}): the pages are the test's to go through.</param>
public sealed record TestStart(string? Level = null, IReadOnlyList<string>? Mods = null, string? Save = null,
    bool MainMenu = false, ResetRung? Reset = null, bool StartupVideos = false, bool UntilLua = false, bool FirstLaunch = false)
{
    /// <summary>The rung declared.</summary>
    public ResetRung? Needs => Reset;

    /// <summary>A start with no level and no server: the main menu, the moment the bridge first answers,
    /// or the first-launch pages. Such a test has no scene to clean and runs as the place is.</summary>
    public bool NoLevel => MainMenu || UntilLua || FirstLaunch;

    /// <summary>What two tests must agree on in their start to share one game (their files too: <see cref="GameTest.Group"/>).</summary>
    public (string?, string, bool, string?, bool) Key =>
        (FirstLaunch ? "<first launch>" : UntilLua ? "<until lua>" : MainMenu ? "<main menu>" : Level, string.Join(",", Mods ?? []), Mods is null, Save, StartupVideos);
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
        Name = IsFolderTest(path) ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path))! : System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>The file that makes a folder one test: the folder's other `.lua` are shared, the rest data.</summary>
    public const string FolderTestFile = "test.lua";

    /// <summary>The folder of shared data files, at any level of the tree (<see cref="FindFile"/>).</summary>
    public const string SharedFilesFolder = "_files";

    /// <summary>Whether <paramref name="path"/> is the <c>test.lua</c> of a folder test.</summary>
    public static bool IsFolderTest(string path) => string.Equals(System.IO.Path.GetFileName(path), FolderTestFile, StringComparison.Ordinal);

    /// <summary>The files the header's `files` puts into the copy before its game starts: each destination
    /// (relative to the game folder) with the file found for it (<see cref="FindFile"/>).</summary>
    public IReadOnlyList<(string Destination, string Source)> Files { get; private set; } = [];

    /// <summary>What two tests must agree on to share one game: their start (<see cref="TestStart.Key"/>)
    /// and their files, each destination with its source, which go into the copy before that start.</summary>
    public ((string?, string, bool, string?, bool) Start, string Files) Group =>
        (Start.Key, string.Join("\n", Files.Select(f => f.Destination + "=" + f.Source)));

    /// <summary>The root of the tree of tests the test is in: the nearest folder above it named
    /// `tests`, else the folder it was found under, else its own folder. A test writes nothing
    /// inside it, and its files are looked up no higher.</summary>
    public string Tree
    {
        get
        {
            var own = System.IO.Path.GetDirectoryName(Path)!;
            for (var folder = new DirectoryInfo(own); folder is not null; folder = folder.Parent)
                if (string.Equals(folder.Name, "tests", StringComparison.OrdinalIgnoreCase)) return folder.FullName;
            return Root.Length > 0 ? Root : own;
        }
    }

    /// <summary>The file a test names in `files`: first in the test's own folder, then in `_files/` of
    /// its folder and of each folder above it up to <paramref name="tree"/>, as `require` finds
    /// shared Lua. Null when none has it.</summary>
    public static string? FindFile(string testFolder, string tree, string name)
    {
        var parts = name.Split('/');
        var own = System.IO.Path.Combine([testFolder, .. parts]);
        if (File.Exists(own)) return System.IO.Path.GetFullPath(own);
        var top = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(tree));
        for (var folder = new DirectoryInfo(testFolder); folder is not null; folder = folder.Parent)
        {
            var shared = System.IO.Path.Combine([folder.FullName, SharedFilesFolder, .. parts]);
            if (File.Exists(shared)) return System.IO.Path.GetFullPath(shared);
            if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(folder.FullName), top, StringComparison.OrdinalIgnoreCase)
                || !folder.FullName.StartsWith(top, StringComparison.OrdinalIgnoreCase)) break;
        }
        return null;
    }

    /// <summary>Why a file name in `files` is not one (null = it is): relative, parts joined by `/`, no `.` or `..`.</summary>
    static string? FileNameProblem(string name) =>
        name.Length > 0 && !name.Contains('\\') && !name.Contains(':') && !System.IO.Path.IsPathRooted(name)
            && name.Split('/').All(p => p.Length > 0 && p != "." && p != "..")
            ? null
            : $"\"{name}\" is not a file beside the test: a name, or a relative path joined by `/`, with no `.` or `..`";

    /// <summary>The header's `files = { ["Data/Video/GK_ONE.bk2"] = "ONE.bk2" }`, checked and found.</summary>
    IReadOnlyList<(string Destination, string Source)> ReadFiles(Table? header)
    {
        if (header?.Get("files") is not { } value || value.IsNil()) return [];
        if (value.Type != DataType.Table) throw new ScriptRuntimeException($"{Name}: files = {{ [\"<place in the game folder>\"] = \"<file name>\" }}");
        var list = new List<(string Destination, string Source)>();
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        foreach (var pair in value.Table.Pairs)
        {
            if (pair.Key.Type != DataType.String || pair.Value.Type != DataType.String)
                throw new ScriptRuntimeException($"{Name}: files = {{ [\"<place in the game folder>\"] = \"<file name>\" }}: every key and value is a string");
            var destination = pair.Key.String;
            var name = pair.Value.String;
            if (TestFiles.DestinationProblem(destination) is { } bad) throw new ScriptRuntimeException($"{Name}: files: {bad}");
            if (FileNameProblem(name) is { } wrong) throw new ScriptRuntimeException($"{Name}: files: {wrong}");
            var found = FindFile(folder, Tree, name)
                ?? throw new ScriptRuntimeException($"{Name}: files: no {name} beside the test nor in a {SharedFilesFolder}/ of its folder or above it up to {Tree}");
            list.Add((destination, found));
        }
        return [.. list.OrderBy(f => f.Destination, StringComparer.Ordinal)];
    }

    public string Path { get; }
    /// <summary>The test's id: its path under the folder it was found in (<see cref="Discover"/>), else its file name.</summary>
    public string Name { get; private set; }

    /// <summary>The same test once more, named <c>name#n</c> (`test --repeat`).</summary>
    public GameTest Again(int n)
    {
        var again = new GameTest(Path, Code) { Name = Name + "#" + n, Start = Start, Declared = Declared, _about = _about, Root = Root,
            _surprises = _surprises, SpeedCeiling = SpeedCeiling, CeilingFinding = CeilingFinding, Before = Before, OnlyCase = OnlyCase, Files = Files };
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

    /// <summary>The header's `drawn`: why the test needs drawn frames (a picture, a window's look),
    /// or null. In a headless run (`--headless`) such a test is not run: UNMET with this reason.</summary>
    public string? Drawn { get; private set; }

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

    /// <summary>Reads a test file and runs its declaration (not its body); <paramref name="root"/> is
    /// the folder it was found under (<see cref="Root"/>).</summary>
    public static GameTest Load(string path, string? root = null)
    {
        var test = new GameTest(System.IO.Path.GetFullPath(path), File.ReadAllText(path, Encoding.UTF8));
        if (root is not null) test.Root = System.IO.Path.GetFullPath(root);
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
        ["about", "level", "mods", "save", "menu", "reset", "surprises", "speed_ceiling", "ceiling_finding", "before", "files", "startup_videos", "until_lua", "first_launch", "drawn"];

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
            Drawn = LuaValues.OptionalString(options, "drawn");
            Files = ReadFiles(options);
            if (SpeedCeiling is not null && string.IsNullOrWhiteSpace(CeilingFinding))
                throw new ScriptRuntimeException($"{Name}: a speed_ceiling names its finding (ceiling_finding = \"where it is written\"): "
                    + "a test that fails only when fast is first a suspicion against our own API (docs/writing-tests.md, Speed)");
            Start = new TestStart(LuaValues.OptionalString(options, "level"), LuaValues.OptionalList(options, "mods"),
                LuaValues.OptionalString(options, "save"), LuaValues.Flag(options, "menu"),
                LuaValues.OptionalString(options, "reset") is { } reset ? ParseReset(reset) : null,
                LuaValues.Flag(options, "startup_videos"), LuaValues.Flag(options, "until_lua"), LuaValues.Flag(options, "first_launch"));
            if (Start.UntilLua && (Start.Level is not null || Start.Save is not null || Start.MainMenu))
                throw new ScriptRuntimeException($"{Name}: until_lua runs the test when the bridge first answers, before any menu or level: it takes no level, save or menu");
            if (Start.FirstLaunch && (Start.Level is not null || Start.Save is not null || Start.MainMenu || Start.UntilLua))
                throw new ScriptRuntimeException($"{Name}: first_launch runs the test on the game's first first-launch page: it takes no level, save, menu or until_lua");
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
    /// parts and without .lua (<c>combat/attack/melee_miss</c>; a file of the folder itself: its name);
    /// a folder test's (<c>startup/videos/test.lua</c>) is its folder's path (<c>startup/videos</c>).</summary>
    public static string IdOf(string folder, string path)
    {
        if (!IsFolderTest(path)) return System.IO.Path.ChangeExtension(System.IO.Path.GetRelativePath(folder, path), null)!.Replace('\\', '/');
        var own = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        var relative = System.IO.Path.GetRelativePath(folder, own);
        return (relative == "." ? System.IO.Path.GetFileName(own) : relative).Replace('\\', '/');
    }

    /// <summary>Every test of a folder and its subfolders, at any depth, whose id
    /// (<see cref="IdOf"/>) <see cref="Selects"/> <paramref name="only"/>, in id order; the test is named by its id.
    /// A test is a <c>*.lua</c>, or a folder that holds <c>test.lua</c>. A file whose name starts with
    /// `_` is shared code, not a test; a `_files` folder holds data; a subfolder whose _suite.json
    /// says `"read": "named"` is read only when it is the folder given. A file that declares no
    /// test is left out, and said so on <paramref name="skipped"/>. Two tests of one address - a
    /// folder test beside a file of its name - are refused.</summary>
    public static IReadOnlyList<GameTest> Discover(string folder, string? only = null, TextWriter? skipped = null)
    {
        if (!Directory.Exists(folder)) throw new CopyException($"no test folder {folder}");
        var found = new List<GameTest>();
        var passed = 0;
        var files = LuaFiles(folder).Select(p => (Path: p, Id: IdOf(folder, p))).OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        foreach (var twice in files.GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            throw new CopyException($"two tests have the address {twice.Key}: {string.Join(" and ", twice.Select(f => f.Path))}; "
                + "a folder and a test of the same name do not stand side by side");
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
                var test = Load(path, folder);
                test.Name = id;
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

    /// <summary>The tests' files of a folder: a folder test's <c>test.lua</c> alone (its other `.lua`
    /// must be shared, and nothing below it is a test), else every <c>*.lua</c> and those of its
    /// subfolders but `_files`.</summary>
    static IEnumerable<string> LuaFiles(string folder)
    {
        var folderTest = System.IO.Path.Combine(folder, FolderTestFile);
        if (File.Exists(folderTest))
        {
            foreach (var other in Directory.EnumerateFiles(folder, "*.lua"))
                if (!IsFolderTest(other) && !System.IO.Path.GetFileName(other).StartsWith('_'))
                    throw new CopyException($"{other}: beside {FolderTestFile} every other .lua is shared and its name begins with `_` (a folder that holds {FolderTestFile} is one test)");
            yield return folderTest;
            yield break;
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*.lua")) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            if (string.Equals(System.IO.Path.GetFileName(sub), SharedFilesFolder, StringComparison.Ordinal) || ReadOnlyWhenNamed(sub)) continue;
            foreach (var file in LuaFiles(sub)) yield return file;
        }
    }
}

/// <summary>A file that declares no test (<see cref="GameTest.NotReplayable"/>).</summary>
public sealed class NotATestException(string message, Exception? inner = null) : CopyException(message, inner);
