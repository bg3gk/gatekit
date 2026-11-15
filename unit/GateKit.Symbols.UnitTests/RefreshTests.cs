using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>`gatekit decomp refresh` with no Ghidra: a small loaded database, a symbol database
/// that learns a name, a coverage record that learns a field, and a decompiler written here that
/// answers the way ApplyNames.java, ApplyTypes.java and ExportDecomp.java do together.</summary>
public sealed class RefreshTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("refresh");
    readonly string work, symbols, coverage;
    readonly DecompSettings settings;
    readonly List<(string? Names, string? Types)> passes = [];
    readonly List<string?> helperPasses = [];
    readonly List<(string File, string Line)> refusals = [];
    int decompilerExit;
    string? brokenScript;

    static string Line(string rva, string name, string code, params string[] callees) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["rva"] = rva, ["name"] = name, ["sig"] = "void " + name + "(void)", ["size"] = 100, ["thunk"] = false, ["callees"] = callees, ["code"] = code,
        });

    static string Caller(string callee) => $"void FUN_140001000(void)\n\n{{\n  {callee}(7);\n  return;\n}}\n";

    static string Body(string name) => $"void {name}(int param_1)\n\n{{\n  DAT_140100000 = param_1;\n  return;\n}}\n";

    const string Widget = "void toy::Widget::Tick(Widget *this)\n\n{\n  *(float *)(this + 4) = 0.0;\n  return;\n}\n";
    const string WidgetNamed = "void toy::Widget::Tick(Widget *this)\n\n{\n  this->Timer = 0.0;\n  return;\n}\n";

    public RefreshTests()
    {
        work = Path.Combine(root, "work");
        coverage = Path.Combine(root, "coverage");
        symbols = Path.Combine(root, "symbols.sqlite");
        Directory.CreateDirectory(Path.Combine(work, "Game.exe", "project.rep"));
        Directory.CreateDirectory(Path.Combine(coverage, "layouts", "alive"));
        File.WriteAllText(Path.Combine(work, "Game.exe", "functions.jsonl"), string.Join("\n",
            Line("1000", "FUN_140001000", Caller("FUN_140002000"), "2000"),
            Line("2000", "FUN_140002000", Body("FUN_140002000")),
            Line("3000", "toy::Widget::Tick", Widget),
            Line("4000", "FUN_140004000", Body("FUN_140004000"))) + "\n", new UTF8Encoding(false));
        using (var db = Sql.OpenWrite(symbols))
        {
            db.Execute("""
                CREATE TABLE dlls (id INTEGER PRIMARY KEY, name TEXT, origin TEXT);
                CREATE TABLE symbols (id INTEGER PRIMARY KEY, dll_id INTEGER, rva INTEGER, full_name TEXT, mangled TEXT, kind TEXT);
                CREATE TABLE matches (game_dll INTEGER, game_rva INTEGER, toolkit_dll INTEGER, toolkit_rva INTEGER, method TEXT, relation TEXT, evidence TEXT);
                CREATE TABLE se_symbols (dll_id INTEGER, rva INTEGER, name TEXT, mapping TEXT);
                CREATE TABLE type_names (dll_id INTEGER, func_rva INTEGER, type TEXT);
                CREATE TABLE ecs_ids (dll_id INTEGER, index_rva INTEGER, type TEXT, context TEXT);
                CREATE TABLE vtabs (id INTEGER PRIMARY KEY, dll_id INTEGER, rva INTEGER, symbol_id INTEGER);
                CREATE TABLE vtab_matches (game_vtab INTEGER, toolkit_vtab INTEGER);
                INSERT INTO dlls VALUES (1, 'Game.exe', 'game');
                """);
        }
        settings = new DecompSettings(work, Path.Combine(root, "tools"), Path.Combine(root, "game"), Path.Combine(root, "toolkit"), symbols, null);
        var (exit, _, error) = Run("load");
        Assert.True(exit == 0, error);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
            // a file still open on Windows: the temporary folder is cleaned up later
        }
    }

    /// <summary>What Ghidra does with a pass: a renamed function and its caller are decompiled
    /// again with the name, and a function of a structure that got its fields, with them.</summary>
    int Decompile(DecompSession s, RefreshPass pass)
    {
        passes.Add((pass.Names == null ? null : File.ReadAllText(pass.Names), pass.Types == null ? null : File.ReadAllText(pass.Types)));
        helperPasses.Add(pass.Helpers == null ? null : File.ReadAllText(pass.Helpers));
        if (decompilerExit != 0)
            return decompilerExit;
        // what Ghidra prints: each script's last line, or a compile error and the scripts after it skipped
        var console = new StringBuilder();
        bool skipping = false;
        foreach (var script in RefreshCommand.Scripts(pass))
        {
            skipping |= script == brokenScript;
            if (script == brokenScript)
                console.Append(CultureInfo.InvariantCulture, $"{script}.java:12: error: unclosed character literal\n"
                                                            + $"ERROR REPORT SCRIPT ERROR:  {script}.java : The class could not be found. It must be the public class of the .java file\n");
            else if (!skipping)
                console.Append(CultureInfo.InvariantCulture, $"INFO  {script}.java> gatekit-script: {script} done (GhidraScript)\n");
        }
        File.AppendAllText(Path.Combine(pass.Work, "console.log"), console.ToString());
        if (skipping)
            return 0;
        // what Ghidra refuses, ApplyNames.java and ApplyHelpers.java write beside their file
        foreach (var (which, line) in refusals)
            if ((which == "names" ? pass.Names : pass.Helpers) is string file)
                File.AppendAllText(file + RefreshCommand.Refused, line + "\n");
        var lines = new List<string>();
        foreach (var name in (passes[^1].Names ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')))
        {
            if (name[0] == "2000")
            {
                lines.Add(Line("1000", "FUN_140001000", Caller(name[2]), "2000"));
                lines.Add(Line("2000", name[2], Body(name[2])));
            }
            else
                lines.Add(Line(name[0], name[2], Body(name[2])));
        }
        if ((passes[^1].Types ?? "").Contains("toy::Widget\t", StringComparison.Ordinal))
            lines.Add(Line("3000", "toy::Widget::Tick", WidgetNamed));
        foreach (var helper in (helperPasses[^1] ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')))
            lines.Add(helper[1] == "-" ? Line(helper[0], "FUN_14000" + helper[0], Body("FUN_14000" + helper[0])) : Line(helper[0], helper[3], Body(helper[3])));
        File.WriteAllText(pass.Output, string.Concat(lines.Select(l => l + "\n")), new UTF8Encoding(false));
        return 0;
    }

    (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        int exit = DecompCommands.Create(() => settings, Decompile).Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    (int Exit, string Out, string Err) Refresh(params string[] more) => Run(["refresh", "Game.exe", "--coverage", coverage, .. more]);

    void Name(long rva, string name)
    {
        using var db = Sql.OpenWrite(symbols);
        db.Execute("DELETE FROM se_symbols WHERE rva = ?", rva);
        db.Execute("INSERT INTO se_symbols VALUES (1, ?, ?, 'test')", rva, name);
    }

    /// <summary>The registry's type of toy::Widget, its field at 4 named <paramref name="field"/> on <paramref name="rung"/>.</summary>
    void Layout(string field, string rung = "game")
    {
        var folder = Path.Combine(coverage, "registry", "types", "toy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Widget.json"),
            "{\"schema\": \"registry/type/1\", \"id\": \"toy::Widget\", \"kind\": \"struct\", \"size\": 16, \"fields\": ["
            + "{\"offset\": 0, \"name\": \"Owner\", \"type\": \"FixedString\", \"rung\": \"game\"}, {\"offset\": 4, \"name\": \"" + field + "\", \"type\": \"float\", \"rung\": \"" + rung + "\"},"
            + "{\"offset\": 8, \"name\": \"Things\", \"type\": \"Array<int32_t>\", \"rung\": \"toolkit\"}]}\n");
    }

    string Code(string name)
    {
        var (exit, output, error) = Run("code", name, "--raw");
        Assert.True(exit == 0, error);
        return output;
    }

    long RowId(long rva)
    {
        using var db = Sql.OpenReadOnly(settings.DbPath);
        return Convert.ToInt64(db.Scalar("SELECT f.id FROM functions f WHERE f.rva = ?", rva));
    }

    [Fact]
    public void TheCommandLineFindsTheGroup()
    {
        // the convention of ProjectCommands.Discover in the command line: a public static class
        // with a public, static, parameterless CreateCommand
        var factory = typeof(DecompCommands).GetMethod("CreateCommand", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        Assert.NotNull(factory);
        Assert.True(typeof(DecompCommands).IsPublic && typeof(DecompCommands).IsAbstract && typeof(DecompCommands).IsSealed);
        var decomp = Assert.IsAssignableFrom<Command>(factory.Invoke(null, null));
        Assert.Equal("decomp", decomp.Name);
        foreach (var name in new[] { "run", "load", "refresh", "code", "grep", "status", "vtable" })
            Assert.Contains(decomp.Subcommands, c => c.Name == name);
    }

    [Fact]
    public void ARenamedFunctionReachesItsCallersAndNothingElseIsTouched()
    {
        Name(0x2000, "esv__Thing__Do");
        long untouched = RowId(0x4000);
        var (exit, output, error) = Refresh();
        Assert.True(exit == 0, error);
        Assert.Contains("Game.exe: 1 names and 0 structures applied, 2 functions decompiled again", output);

        Assert.Contains("esv::Thing::Do(7);", Code("Game.exe+0x1000"));
        Assert.Contains("void esv::Thing::Do(int param_1)", Code("esv::Thing::Do"));
        Assert.Equal(untouched, RowId(0x4000));
        var (_, found, _) = Run("grep", "esv::Thing::Do");
        Assert.Contains("Game.exe+0x1000", found);
        Assert.Contains("Game.exe+0x2000", found);
        var (_, old, _) = Run("grep", "FUN_140002000", "--word");
        Assert.DoesNotContain("Game.exe+0x", old);
        using (var db = Sql.OpenReadOnly(settings.DbPath))
        {
            Assert.Equal(4L, db.Scalar("SELECT functions FROM binaries WHERE name = 'Game.exe'"));
            Assert.Equal(1L, db.Scalar("SELECT count(*) FROM calls WHERE caller = 4096"));
            Assert.Equal(4L, db.Scalar("SELECT count(*) FROM code"));
        }
        var (_, status, _) = Run("status");
        Assert.Contains("refreshed: Game.exe: 1 names, 0 structures, 2 functions decompiled again", status);

        // the pass was given only what changed, and a second one has nothing to do
        var pass = Assert.Single(passes);
        Assert.Equal("2000\tlabel\tesv::Thing::Do\t\tScript Extender pattern test\n", pass.Names);
        Assert.Null(pass.Types);
        (exit, output, _) = Refresh();
        Assert.Equal(0, exit);
        Assert.Contains("nothing was renamed since the last pass", output);
        Assert.Single(passes);

        // a later `load` builds the same database: the output file carries the pass too
        Assert.False(File.Exists(Path.Combine(work, "Game.exe", DecompStore.ExportName + ".merging")));
        Assert.False(File.Exists(Path.Combine(work, "Game.exe", DecompStore.FormerExportName)));     // renamed once, by the first command that met it
        Assert.Equal(4, File.ReadAllLines(Path.Combine(work, "Game.exe", DecompStore.ExportName)).Length);
        (exit, output, error) = Run("load");
        Assert.True(exit == 0, error);
        Assert.Contains("Game.exe: 4 functions, 0 without pseudo-code", output);
        Assert.Contains("esv::Thing::Do(7);", Code("Game.exe+0x1000"));
    }

    [Fact]
    public void OnlyWhatWasRenamedSinceTheLastPassIsHandedOver()
    {
        Name(0x2000, "esv__Thing__Do");
        Assert.Equal(0, Refresh().Exit);
        Name(0x4000, "esv__Thing__Undo");
        Name(0x5000, "esv__Thing__Found");          // a function the first pass did not know: Ghidra makes it
        var (exit, output, error) = Refresh();
        Assert.True(exit == 0, error);
        Assert.Contains("2 names and 0 structures applied, 2 functions decompiled again (1 of them new)", output);
        Assert.Equal(2, passes.Count);
        Assert.DoesNotContain("esv::Thing::Do\t", passes[1].Names);
        Assert.Contains("void esv::Thing::Undo(int param_1)", Code("Game.exe+0x4000"));
        Assert.Contains("void esv::Thing::Found(int param_1)", Code("Game.exe+0x5000"));

        // a name that changes again is a rename like any other
        Name(0x4000, "esv__Thing__Redo");
        Assert.Equal(0, Refresh().Exit);
        Assert.Equal("4000\tlabel\tesv::Thing::Redo\t\tScript Extender pattern test\n", passes[2].Names);
        Assert.Contains("void esv::Thing::Redo(int param_1)", Code("Game.exe+0x4000"));
    }

    [Fact]
    public void FieldNamesOfTheRecordBecomeStructures()
    {
        Layout("field_4");
        var (exit, output, error) = Refresh();
        Assert.True(exit == 0, error);
        Assert.Contains("0 names and 1 structures applied", output);
        Assert.Equal("toy::Widget\t16\t0\t4\tuint\tOwner\tFixedString\n"
                     + "toy::Widget\t16\t4\t4\tfloat\tfield_4\tfloat\n"
                     + "toy::Widget\t16\t8\t8\tbytes\tThings\tArray<int32_t>\n", passes[0].Types);
        Assert.Contains("this->Timer = 0.0;", Code("toy::Widget::Tick"));

        Assert.Contains("nothing was renamed", Refresh().Out);
        Layout("Timer");                                  // the names session names the field
        (exit, output, error) = Refresh();
        Assert.True(exit == 0, error);
        Assert.Contains("0 names and 1 structures applied, 1 functions decompiled again", output);
        Assert.Contains("\tTimer\tfloat\n", passes[1].Types);
        Assert.Null(passes[1].Names);
    }

    [Fact]
    public void Only_what_the_registry_applies_reaches_the_project_and_a_field_moved_below_the_line_leaves_it()
    {
        Layout("Timer", "inferred");                      // a shown field: never handed over
        Assert.Equal(0, Refresh().Exit);
        Assert.Equal("toy::Widget\t16\t0\t4\tuint\tOwner\tFixedString\n"
                     + "toy::Widget\t16\t8\t8\tbytes\tThings\tArray<int32_t>\n", passes[0].Types);

        // a structure the project was given and the registry no longer gives is cleared to its size
        File.Delete(Path.Combine(coverage, "registry", "types", "toy", "Widget.json"));
        File.WriteAllText(Path.Combine(coverage, "registry", "types", "toy", "Other.json"),
            "{\"schema\": \"registry/type/1\", \"id\": \"toy::Other\", \"kind\": \"struct\", \"size\": 8, \"fields\": [{\"offset\": 0, \"name\": \"Guess\", \"rung\": \"guess\", \"guess\": {\"rests_on\": \"r\", \"would_confirm\": \"w\"}}]}\n");
        Assert.Equal(0, Refresh().Exit);
        Assert.Equal("toy::Widget\t16\t0\t0\tbytes\t_\tnot given\n", passes[1].Types);
        Assert.Contains("nothing was renamed", Refresh().Out);
    }

    [Fact]
    public void A_function_of_the_registry_on_an_applied_rung_is_named_and_its_first_parameter_typed()
    {
        Layout("Timer");
        var registry = Path.Combine(coverage, "registry");
        Directory.CreateDirectory(Path.Combine(registry, "functions", "toy", "Thing"));
        File.WriteAllText(Path.Combine(registry, "functions", "toy", "Thing", "Do.json"),
            "{\"schema\": \"registry/function/1\", \"id\": \"toy::Thing::Do\", \"rung\": \"confirmed\", \"parameters\": [{\"storage\": \"reg:rcx\", \"name\": \"widget\"}],"
            + " \"ties\": [{\"type\": \"toy::Widget\", \"via\": \"widget\"}], \"evidence\": [{\"kind\": \"watch\", \"says\": \"a watch caught it\"}]}\n");
        File.WriteAllText(Path.Combine(registry, "functions", "toy", "Thing", "Guessed.json"),
            "{\"schema\": \"registry/function/1\", \"id\": \"toy::Thing::Guessed\", \"rung\": \"inferred\"}\n");
        var store = new Registry.RegistryStore(registry);
        store.WriteTable("100", "Game.exe", [new Registry.BuildRow("toy::Thing::Do", 0x2000, null, null), new Registry.BuildRow("toy::Thing::Guessed", 0x4000, null, null)]);
        using (var db = DecompStore.OpenWrite(settings.DbPath))
        {
            db.Execute("CREATE TABLE IF NOT EXISTS binary_meta (binary TEXT PRIMARY KEY, sha256 TEXT, size INTEGER, kind TEXT, game_build TEXT, verdict TEXT, evidence TEXT, stamped TEXT)");
            db.Execute("INSERT OR REPLACE INTO binary_meta (binary, game_build) VALUES ('Game.exe', '100')");
        }
        var (exit, _, error) = Refresh();
        Assert.True(exit == 0, error);
        Assert.Equal("2000\tfunction\ttoy::Thing::Do\t\tregistry confirmed\n", passes[0].Names);     // the inferred one is not handed over
        var systems = File.ReadAllText(Path.Combine(work, "Game.exe", RefreshCommand.SystemsState));
        Assert.Equal("2000\tregistry\ttoy::Thing::Do\ttoy::Widget\tregistry confirmed\n", systems);
    }

    void Helper(long rva, string family, string type, string returns)
    {
        using var db = DecompStore.OpenWrite(settings.DbPath);
        db.Execute("DELETE FROM ecs_helpers WHERE rva = ?", rva);
        db.Execute("INSERT INTO ecs_helpers VALUES ('Game.exe', ?, ?, ?, ?, 'high', 'test basis')", rva, family, type, returns);
    }

    [Fact]
    public void ComponentHelpersAreNamedAndTypedWhenTheRecordHasTheStructure()
    {
        Layout("Timer");
        Helper(0x4000, "get", "toy::Widget", "component");
        Helper(0x2000, "has", "toy::Widget", "bool");
        var (exit, output, error) = Refresh();
        Assert.True(exit == 0, error);
        Assert.Contains("0 names and 1 structures and 2 component helpers applied", output);
        Assert.Equal("2000\thas\ttoy::Widget\tecs::HasComponent<toy::Widget>\t\ttest basis\n"
                     + "4000\tget\ttoy::Widget\tecs::GetComponent<toy::Widget>\ttoy::Widget\ttest basis\n", helperPasses[0]);
        Assert.Contains("void ecs::GetComponent<toy::Widget>(int param_1)", Code("Game.exe+0x4000"));

        // only what changed is handed over again; a helper the symbol database names is left to that name
        Helper(0x4000, "get", "toy::Gadget", "component");               // no structure of that type: named, not typed
        Name(0x2000, "esv__Thing__Do");
        (exit, _, error) = Refresh();
        Assert.True(exit == 0, error);
        Assert.Equal("4000\tget\ttoy::Gadget\tecs::GetComponent<toy::Gadget>\t\ttest basis\n", helperPasses[1]);
        Assert.Contains("nothing was renamed", Refresh().Out);

        // one the classifier no longer finds takes its default name back
        using (var db = DecompStore.OpenWrite(settings.DbPath))
            db.Execute("DELETE FROM ecs_helpers WHERE rva = 16384");
        (exit, output, error) = Refresh();
        Assert.True(exit == 0, error);
        Assert.Equal("4000\t-\ttoy::Gadget\t\t\tno longer found a helper\n", helperPasses[^1]);
        Assert.Contains("void FUN_140004000(int param_1)", Code("Game.exe+0x4000"));
        Assert.Contains("nothing was renamed", Refresh().Out);
    }

    [Fact]
    public void WhatGhidraRefusesIsSaidAndOfferedAgain()
    {
        Name(0x2000, "esv__Thing__Do");
        Name(0x4000, "esv__Thing__Undo");
        Helper(0x3000, "get", "toy::Widget", "component");
        refusals.Add(("names", "4000\tesv::Thing::Undo\tInvalidInputException: no"));
        refusals.Add(("helpers", "3000\tecs::GetComponent<toy::Widget>\tthe function is already named toy::Widget::Tick"));
        var (exit, _, error) = Refresh();
        Assert.True(exit == 0, error);
        Assert.Contains("Game.exe: 1 names refused by Ghidra (rva, name, reason); the next pass offers them again:\n  4000\tesv::Thing::Undo\tInvalidInputException: no", error);
        Assert.Contains("Game.exe: 1 component helpers refused by Ghidra", error);
        Assert.DoesNotContain("4000\t", File.ReadAllText(Path.Combine(work, "Game.exe", RefreshCommand.NamesState)));
        Assert.DoesNotContain("3000\t", File.ReadAllText(Path.Combine(work, "Game.exe", RefreshCommand.HelpersState)));

        refusals.Clear();
        (exit, var output, error) = Refresh();
        Assert.True(exit == 0, error);
        Assert.Contains("1 names and 0 structures and 1 component helpers applied", output);
        Assert.Equal("4000\tlabel\tesv::Thing::Undo\t\tScript Extender pattern test\n", passes[^1].Names);
    }

    [Fact]
    public void ADryRunSaysWhatChangedAndStartsNothing()
    {
        Name(0x2000, "esv__Thing__Do");
        Layout("Timer");
        var before = File.ReadAllBytes(settings.DbPath);
        var (exit, output, error) = Refresh("--dry-run");
        Assert.True(exit == 0, error);
        Assert.Contains("Game.exe: 1 names and 1 structures changed since the last pass", output);
        Assert.Contains("at least 2 functions would be decompiled again", output);
        Assert.Empty(passes);
        Assert.Equal(before, File.ReadAllBytes(settings.DbPath));
        Assert.False(File.Exists(Path.Combine(work, "Game.exe", RefreshCommand.NamesState)));
    }

    [Fact]
    public void AFailedPassChangesNothingAndIsMadeAgain()
    {
        Name(0x2000, "esv__Thing__Do");
        var before = File.ReadAllBytes(settings.DbPath);
        decompilerExit = 3;
        var (exit, _, error) = Refresh();
        Assert.Equal(1, exit);
        Assert.Contains("the decompiler ended with 3", error);
        Assert.Equal(before, File.ReadAllBytes(settings.DbPath));
        Assert.False(File.Exists(Path.Combine(work, "Game.exe", RefreshCommand.NamesState)));

        decompilerExit = 0;
        Assert.Equal(0, Refresh().Exit);
        Assert.Equal(passes[0].Names, passes[1].Names);
        Assert.Contains("esv::Thing::Do(7);", Code("Game.exe+0x1000"));
    }

    [Fact]
    public void AScriptThatDidNotCompileFailsThePassThoughGhidraEndedWithZero()
    {
        Name(0x2000, "esv__Thing__Do");
        Layout("Timer");
        var before = File.ReadAllBytes(settings.DbPath);
        brokenScript = "ApplyNames";
        var (exit, _, error) = Refresh();
        Assert.Equal(1, exit);
        Assert.Contains("its scripts did not all run", error);
        Assert.Contains("ApplyNames.java did not load or compile", error);
        Assert.Contains("ApplyTypes.java never printed its last line", error);
        Assert.Contains("ExportDecomp.java never printed its last line", error);
        Assert.Equal(before, File.ReadAllBytes(settings.DbPath));
        foreach (var state in new[] { RefreshCommand.NamesState, RefreshCommand.TypesState, RefreshCommand.HelpersState })
            Assert.False(File.Exists(Path.Combine(work, "Game.exe", state)), state);

        // the old log of the failed launch does not fail the next one, which is made from the same difference
        brokenScript = null;
        var (again, output, error2) = Refresh();
        Assert.True(again == 0, error2);
        Assert.Contains("1 names and 1 structures applied", output);
        Assert.Equal(passes[0].Names, passes[1].Names);
    }

    [Fact]
    public void AReaderIsNeverShownHalfAPass()
    {
        Name(0x2000, "esv__Thing__Do");
        const string text = "SELECT f.name FROM functions f WHERE f.rva = 8192";
        int exit;
        string error;
        using (var reader = Sql.OpenReadOnly(settings.DbPath))
        {
            Assert.Equal("FUN_140002000", reader.Scalar(text));
            (exit, _, error) = Refresh("--wait", "1");
            // whatever the operating system let the move do, the connection opened before the pass reads the old text whole
            Assert.Equal("FUN_140002000", reader.Scalar(text));
            Assert.Equal(4L, reader.Scalar("SELECT count(*) FROM code"));
        }
        if (exit != 0)
        {
            // Windows keeps a file that is open where it is: the copy waits beside it and nothing is remembered as applied
            Assert.Contains("was held open by a reader", error);
            Assert.True(File.Exists(Path.ChangeExtension(settings.DbPath, ".refreshing")));
            Assert.False(File.Exists(Path.Combine(work, "Game.exe", RefreshCommand.NamesState)));
            (exit, _, error) = Refresh("--wait", "1");
        }
        Assert.True(exit == 0, error);
        Assert.False(File.Exists(Path.ChangeExtension(settings.DbPath, ".refreshing")));
        using var after = Sql.OpenReadOnly(settings.DbPath);
        Assert.Equal("esv::Thing::Do", after.Scalar(text));
    }

    [Fact]
    public void WhatIsMissingIsSaidWithTheWayToGetIt()
    {
        Name(0x2000, "esv__Thing__Do");
        var (exit, _, error) = Run("refresh", "Other.exe", "--coverage", coverage);
        Assert.Equal(1, exit);
        Assert.Contains("has no pseudo-code of Other.exe", error);
        Assert.Contains("gatekit decomp run Other.exe", error);

        Directory.Delete(Path.Combine(work, "Game.exe", "project.rep"));
        (exit, _, error) = Refresh();
        Assert.Equal(1, exit);
        Assert.Contains("has no Ghidra project of Game.exe", error);
        Assert.Empty(passes);
    }

    [Fact]
    public void LayoutsBecomeFieldsThatFitTheirRoom()
    {
        RecordType Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return RecordTypes.Parse(doc.RootElement)!;
        }

        var t = Parse("""
            {"component": "ecl::Thing", "size": 40, "fields": [
              {"name": "Flags", "offset": 0, "type": "uint8_t"},
              {"name": "Pad", "offset": 1, "type": "uint8_t[3]"},
              {"name": "Squeezed", "offset": 4, "type": "uint64_t"},
              {"name": "Position", "offset": 8, "type": "glm::vec3"},
              {"name": "Owner", "offset": 24, "type": "EntityHandle"},
              {"name": "Next", "offset": 32, "type": "ecl::Thing*"},
              {"name": "no offset", "type": "bool"}
            ]}
            """);
        Assert.Equal(("ecl::Thing", 40L), (t.Name, t.Size));
        Assert.Equal([(0L, 1, "uint"), (1L, 3, "bytes"), (4L, 4, "bytes"), (8L, 12, "float"), (24L, 8, "uint"), (32L, 8, "ptr")],
                     t.Fields.Select(f => (f.Offset, f.Size, f.Kind)));

        // no size in the layout: the structure ends where its last field does
        var open = Parse("""{"component": "Loose", "fields": [{"name": "A B", "offset": 8, "type": "Guid"}]}""");
        Assert.Equal(24L, open.Size);
        Assert.Equal("A_B", open.Fields[0].Name);
        using (var none = JsonDocument.Parse("""{"component": "Empty", "fields": []}"""))
            Assert.Null(RecordTypes.Parse(none.RootElement));

        var blocks = RecordTypes.Split(RecordTypes.Render([t, open]));
        Assert.Equal(["Loose", "ecl::Thing"], blocks.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(open.Lines(), blocks["Loose"]);
    }

    [Fact]
    public void TheOutputFileTakesAPassInPlace()
    {
        var main = Path.Combine(root, "merge.jsonl");
        File.WriteAllText(main, "{\"rva\":\"10\",\"name\":\"a\"}\n{\"rva\":\"20\",\"name\":\"b\"}\nnot a line\n{\"rva\":\"10\",\"name\":\"again\"}\n");
        RefreshCommand.MergeOutput(main, new Dictionary<long, string> { [0x10] = "{\"rva\":\"10\",\"name\":\"A\"}", [0x30] = "{\"rva\":\"30\",\"name\":\"C\"}" });
        Assert.Equal("{\"rva\":\"10\",\"name\":\"A\"}\n{\"rva\":\"20\",\"name\":\"b\"}\nnot a line\n{\"rva\":\"30\",\"name\":\"C\"}\n", File.ReadAllText(main));
        Assert.Equal(0x1A2BL, FunctionWriter.RvaOf("{\"rva\":\"1A2B\",\"name\":null}"));
        Assert.Null(FunctionWriter.RvaOf("{\"name\":\"x\",\"rva\":\"10\"}"));
    }
}
