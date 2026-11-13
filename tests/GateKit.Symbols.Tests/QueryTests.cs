using System.CommandLine;
using GateKit.Symbols.Queries;

namespace GateKit.Symbols.Tests;

/// <summary>The query verbs of `symbols` on a tiny synthetic database: one Toolkit DLL with a class
/// toy::Widget, one game executable where its Update method and its virtual table were found.</summary>
public sealed class QueryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gatekit-symbols-tests-" + Guid.NewGuid().ToString("N"));
    readonly string dbPath;
    readonly SymbolsEnvironment env;

    public QueryTests()
    {
        Directory.CreateDirectory(root);
        dbPath = Path.Combine(root, "symbols.sqlite");
        env = new SymbolsEnvironment(Path.Combine(root, "no-decomp", "decomp.sqlite"), null, Path.Combine(root, "game"), Path.Combine(root, "toolkit"));
        using var db = Sql.OpenWrite(dbPath);
        db.Execute(SymbolSchema.Tables);
        db.Execute(SymbolSchema.Indexes);
        db.Execute("INSERT INTO meta VALUES ('built', '2026-11-13 12:00:00')");
        db.Execute("INSERT INTO dlls VALUES (1, 'EoCClient.dll', 'EoCClient.dll', 100, 0, 'x64', 0, 3, 1, NULL, 'toolkit', 3)");
        db.Execute("INSERT INTO dlls VALUES (2, 'bg3.exe', 'bin/bg3.exe', 200, 0, 'x64', 0, 0, 0, NULL, 'game', 3)");
        void Symbol(int id, long rva, string mangled, string full, string method, string signature, string kind) =>
            db.Execute("INSERT INTO symbols VALUES (?, 1, NULL, ?, NULL, ?, ?, 'toy', 'Widget', ?, ?, 'public', ?)", id, rva, mangled, full, method, signature, kind);
        Symbol(1, 0x1000, "?Update@Widget@toy@@QEAAXXZ", "toy::Widget::Update", "Update", "public: void toy::Widget::Update(void)", "method");
        Symbol(2, 0x1100, "?Draw@Widget@toy@@UEAAXH@Z", "toy::Widget::Draw", "Draw", "public: virtual void toy::Widget::Draw(int)", "method");
        Symbol(3, 0x5000, "??_7Widget@toy@@6B@", "toy::Widget::`vftable'", "`vftable'", "const toy::Widget::`vftable'", "vftable");
        db.Execute("INSERT INTO vtable_slots VALUES (3, 1, 0, 4352), (3, 1, 1, 4608)");
        db.Execute("INSERT INTO functions VALUES (1, 4096, 64, 0), (1, 4352, 32, 0), (1, 4608, 16, 1), (2, 8192, 128, 0), (2, 12288, 256, 0), (2, 12544, 48, 0)");
        db.Execute("INSERT INTO chunks VALUES (2, 12544, 12288)");
        db.Execute("INSERT INTO matches VALUES (1, 4096, 2, 8192, 2, 'hello', 'strings', 64, 128, 'same')");
        db.Execute("INSERT INTO calls VALUES (2, 12288, 2, 8192, NULL, 1), (2, 12544, 2, 8192, NULL, 2), (1, 4096, 1, 4352, NULL, 1), (1, 4096, NULL, NULL, 'CreateFileW', 1)");
        db.Execute("INSERT INTO ecs_ids VALUES (2, 'toy::WidgetComponent', 'ecs::ComponentTypeIdContext', 36864, 40960, 'record')");
        db.Execute("INSERT INTO xrefs VALUES (2, 12544, 36864, 'r', 1), (2, 8192, 36864, 'w', 2)");
        db.Execute("INSERT INTO vtabs VALUES (1, 2, 24576, 2, NULL), (2, 1, 20480, 2, 3)");
        db.Execute("INSERT INTO vtab_slots VALUES (1, 0, 8192), (1, 1, 12288), (2, 0, 4352), (2, 1, 4608)");
        db.Execute("INSERT INTO vtab_matches VALUES (2, 1, 1)");
        db.Execute("INSERT INTO se_types VALUES (1, 'WidgetComponent', 'toy', 'Widget', 'toy::WidgetComponent', 'component', NULL, 'Toy.h', 10, 16)");
        db.Execute("INSERT INTO se_fields VALUES (1, 0, 'Health', 'int32_t', NULL, 0, 0, 4, 1), (1, 1, 'Flags', 'uint8_t', 'field_8', 1, NULL, NULL, NULL)");
        db.Execute("INSERT INTO enums VALUES (1, 'WidgetFlags', 'toy', 'uint8_t', 1, 'Toy.h')");
        db.Execute("INSERT INTO enum_values VALUES (1, 'Visible', 1), (1, 'Hidden', 2)");
        db.Execute("INSERT INTO accessors VALUES (1, 1, 'toy::Widget', 'Health', 'get', 16, 4, 'int', 2, 8192, 24, NULL)");
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
        }
    }

    (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        int exit = SymbolsCommands.CreateCommand(() => env).Parse(["--db", dbPath, .. args]).Invoke(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void SchemaMatchesThePythonTool()
    {
        Assert.StartsWith("\nCREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);\nCREATE TABLE dlls (\n    id INTEGER PRIMARY KEY,", SymbolSchema.Tables);
        Assert.EndsWith("    ordinal INTEGER\n);\n", SymbolSchema.Tables);
        Assert.EndsWith("CREATE INDEX calls_import ON calls(import) WHERE import IS NOT NULL;\n", SymbolSchema.Indexes);
    }

    [Fact]
    public void FindSortsByMethodAndCutsNothingShort()
    {
        var (exit, output, error) = Run("find", "Widget");
        Assert.Equal(0, exit);
        Assert.Equal(Lines(
            $"{"EoCClient.dll+0x1100",-32} public: virtual void toy::Widget::Draw(int)",
            $"{"EoCClient.dll+0x1000",-32} public: void toy::Widget::Update(void)",
            $"{"EoCClient.dll+0x5000",-32} const toy::Widget::`vftable'"), output);
        Assert.Equal("", error);
        (_, output, error) = Run("find", "Widget", "--limit", "1", "--mangled");
        Assert.Equal(Lines($"{"EoCClient.dll+0x1100",-32} public: virtual void toy::Widget::Draw(int)", $"{"",-32} ?Draw@Widget@toy@@UEAAXH@Z"), output);
        Assert.Equal("... 1 of 3 shown; raise --limit or narrow the search\n", error);
        (_, output, error) = Run("find", "NoSuchThing");
        Assert.Equal("", output);
        Assert.Equal("nothing found\n", error);
    }

    [Fact]
    public void ClassAcceptsThePartAfterTheNamespace()
    {
        var exact = Run("class", "toy::Widget").Out;
        Assert.Equal(exact, Run("class", "Widget").Out);
        Assert.Contains("EoCClient.dll+0x5000", exact);
        Assert.Equal("    3 V toy::Widget   [EoCClient.dll]\n", Run("classes", "Widg").Out);
    }

    [Fact]
    public void VtableOfAClassAndAtAnAddress()
    {
        Assert.Equal(Lines(
            "EoCClient.dll+0x5000  const toy::Widget::`vftable'  (2 slots)",
            "  [  0] +0x000  0x1100  public: virtual void toy::Widget::Draw(int)",
            "  [  1] +0x008  0x1200  (not exported)"), Run("vtable", "toy::Widget").Out);
        Assert.Equal(Lines(
            "bg3.exe+0x6000  2 slots  [database]  = toy::Widget::`vftable'",
            "  [  0] +0x000  bg3.exe+0x2000  = toy::Widget::Update   (128 bytes)",
            "  [  1] +0x008  bg3.exe+0x3000   (256 bytes)  <-- the address asked for"), Run("vtable", "bg3.exe+0x6008").Out);
        var (exit, _, error) = Run("vtable", "bg3.exe+0x7000");
        Assert.Equal(1, exit);
        Assert.Contains("was not found to read it", error);
    }

    [Fact]
    public void CallersFoldFunctionParts()
    {
        Assert.Equal(Lines("bg3.exe+0x2000  = toy::Widget::Update   <- 1 callers", "    bg3.exe+0x3000   x3"), Run("callers", "bg3.exe+0x2000").Out);
        Assert.Equal(Lines(
            "EoCClient.dll+0x1000  toy::Widget::Update   -> 2 callees",
            "    import CreateFileW",
            "    EoCClient.dll+0x1100  toy::Widget::Draw"), Run("callees", "Widget::Update").Out);
    }

    [Fact]
    public void UsersWithTheirCallers()
    {
        var (exit, output, _) = Run("users", "toy::WidgetComponent", "--callers");
        Assert.Equal(0, exit);
        Assert.Equal(Lines(
            "bg3.exe+0x9000  toy::WidgetComponent  [ComponentTypeIdContext]   <- 2 functions",
            $"    {"bg3.exe+0x2000  = toy::Widget::Update",-60}    128 bytes  wx2",
            $"        <- {"bg3.exe+0x3000",-56}    304 bytes  x3",
            $"    {"bg3.exe+0x3000",-60}    304 bytes  r",
            "        <- slot 1 of the virtual table bg3.exe+0x6000 (2 slots)   (gatekit symbols vtable bg3.exe+0x6000)"), output);
        Assert.Equal(3, Run("users", "toy::WidgetComponent").Out.Count(c => c == '\n'));
    }

    [Fact]
    public void LayoutFieldsAndEnum()
    {
        Assert.Equal(Lines(
            "component  toy::WidgetComponent   (Lua: Widget)   size 0x10   [Toy.h:10]",
            "  +0x000 = int32_t Health",
            "    ?      uint8_t Flags   (was field_8)   [hidden]"), Run("layout", "toy::WidgetComponent").Out);
        Assert.Equal(Lines(
            "toy::Widget   (from accessors exported by the Toolkit; offsets are the Toolkit's unless a game offset is shown)",
            "  +0x010  get Health : int  [4 bytes]   game +0x18 (bg3.exe+0x2000)"), Run("fields", "toy::Widget").Out);
        Assert.Equal(Lines(
            "bitmask toy::WidgetFlags : uint8_t   [Toy.h]",
            "       1  0x1  Visible",
            "       2  0x2  Hidden"), Run("enum", "WidgetFlags").Out);
        Assert.Equal(Lines("label    WidgetFlags::Hidden = 2"), Run("enum", "Hidden").Out);
    }

    [Fact]
    public void ExportWritesWhatPythonWrote()
    {
        Assert.Equal(
            "2000\tfunction\ttoy::Widget::Update\t?Update@Widget@toy@@QEAAXXZ\ttoolkit match by strings\n"
            + "6000\tdata\ttoy::Widget::`vftable'\t??_7Widget@toy@@6B@\tvirtual table match\n"
            + "9000\tdata\tTypeIndex<toy::WidgetComponent>@ComponentTypeIdContext\t\tECS registration record\n",
            Run("export", "--format", "ghidra").Out);
        string json = Run("export").Out;
        Assert.StartsWith("{\n \"module\": \"bg3.exe\",\n \"labels\": [\n  {\n   \"rva\": 8192,\n   \"kind\": \"function\",", json);
        Assert.Contains("\"name\": \"TypeIndex<toy::WidgetComponent>@ComponentTypeIdContext\",\n   \"mangled\": null,", json);
        Assert.EndsWith("   \"aliases\": []\n  }\n ]\n}", json);
        Assert.Contains("    (0x6000, \"toy::Widget::`vftable'\"),\n", Run("export", "--format", "ida").Out);
    }

    [Fact]
    public void MissingDatabaseIsAnExpectedFailure()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = SymbolsCommands.CreateCommand(() => env).Parse(["--db", Path.Combine(root, "none.sqlite"), "stats"])
            .Invoke(new InvocationConfiguration { Output = output, Error = error });
        Assert.Equal(1, exit);
        Assert.Contains("does not exist", error.ToString());
    }

    [Fact]
    public void DecompCommandGetsVtable()
    {
        var decomp = SymbolsCommands.CreateDecompCommand(new Command("decomp"), () => env);
        var output = new StringWriter { NewLine = "\n" };
        int exit = decomp.Parse(["vtable", "bg3.exe+0x6000", "--db", dbPath]).Invoke(new InvocationConfiguration { Output = output, Error = new StringWriter() });
        Assert.Equal(0, exit);
        Assert.StartsWith("bg3.exe+0x6000  2 slots  [database]", output.ToString());
    }

    [Fact]
    public void StructuredQueries()
    {
        using var q = new SymbolQueries(dbPath, env);
        var found = q.Search("Widget");
        Assert.Contains(found, r => (string?)r["kind"] == "class" && (string?)r["name"] == "toy::Widget" && (string?)r["more"] == "gatekit about toy::Widget");
        Assert.Contains(found, r => (string?)r["kind"] == "layout" && (string?)r["name"] == "toy::WidgetComponent");
        var about = q.AboutClass("toy::Widget");
        Assert.Equal(3, ((List<Dictionary<string, object?>>)about["exports"]!).Count);
        var address = q.AboutAddress("bg3.exe", 0x2010);
        Assert.Equal(0x2000L, ((Dictionary<string, object?>)address["function"]!)["rva"]);
        Assert.Equal(2, ((List<Dictionary<string, object?>>)address["callers"]!).Count);
        var hook = q.Hook("Widget::Update");
        var f = Assert.Single((List<Dictionary<string, object?>>)hook["functions"]!);
        Assert.Equal("bg3.exe+0x2000", f["address"]);
        Assert.Equal("function found, sizes agree", f["trust"]);
        var schema = (Dictionary<string, object?>)q.Schema()["tables"]!;
        var dlls = (Dictionary<string, object?>)schema["dlls"]!;
        Assert.Equal(2L, dlls["rows"]);
        var columns = (List<Dictionary<string, object?>>)dlls["columns"]!;
        Assert.Equal("file name, e.g. EoCClient.dll", columns.Single(c => (string?)c["name"] == "name")["comment"]);
        var sql = q.RunSql("SELECT name FROM dlls ORDER BY id", 1);
        Assert.True((bool)sql["truncated"]!);
        Assert.Equal("EoCClient.dll", ((List<List<object?>>)sql["rows"]!)[0][0]);
        Assert.ThrowsAny<Exception>(() => q.RunSql("DELETE FROM dlls"));
        Assert.Equal(3, q.GameLabels().Labels.Count);
    }

    [Fact]
    public void PythonValueDisplay()
    {
        Assert.Equal("\"it's\"", PyText.Repr("it's"));
        Assert.Equal("'a\\x00b\\u2028'", PyText.Repr("a\0b\u2028"));
        Assert.Equal("{'method': 2, None: 1}", PyText.Dict([("method", 2L), (null, 1L)]));
        Assert.Equal("-1", PyText.Hex(-1));
        Assert.Equal(-16, PyText.Int0("-0x10"));
        Assert.Null(PyText.Int0("010"));
        Assert.Equal("{\n \"a\": [\n  1,\n  \"\\u00e9\"\n ],\n \"b\": {}\n}", PyJson.Dumps(new Dictionary<string, object?> { ["a"] = new List<object?> { 1L, "é" }, ["b"] = new Dictionary<string, object?>() }, 1));
    }
}
