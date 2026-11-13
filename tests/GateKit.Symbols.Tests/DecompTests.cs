using System.CommandLine;
using System.Text;
using System.Text.Json;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.Tests;

/// <summary>The pseudo-code pipeline on synthetic data: a functions.jsonl written here, a
/// decomp.sqlite and a small symbol database in a temporary folder.</summary>
public sealed class DecompTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gatekit-decomp-tests-" + Guid.NewGuid().ToString("N"));
    readonly DecompSettings settings;

    const string Update = """
        void __thiscall toy::Widget::Update(Widget *this)

        {
          int iVar1;

          iVar1 = *(int *)(this + 0x10);
          if (iVar1 == 0x1234) {
            FUN_180002000(this);
          }
          *(int *)(this + 0x18) = 5;
          return;
        }

        """;

    const string Ground = """
        undefined8 FUN_180002000(longlong param_1)

        {
          if (*(int *)(param_1 + 8) == 7) {
            return GroundMaterialComponent(param_1, 0x40);
          }
          return 0;
        }

        """;

    const string Register = """
        void FUN_140001000(void)

        {
          _Init_thread_header(&DAT_140100000);
          DAT_140200008 = 8;
          DAT_140200010 = DAT_140300000;
          DAT_140300000 = &DAT_140200000;
          DAT_140200000 = "Sneaking";
          DAT_140400000 = &DAT_140200018;
          return;
        }

        """;

    const string Uses = """
        bool FUN_140001100(longlong *param_1)

        {
          return *param_1 == *DAT_140400000;
        }

        """;

    static string Line(string rva, string? name, string? code, string? error = null, int size = 100, params string[] callees) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["rva"] = rva, ["name"] = name, ["sig"] = name == null ? null : "void " + name + "(void)", ["size"] = size,
            ["thunk"] = false, ["callees"] = callees, ["code"] = code, ["error"] = error,
        }.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value));

    public DecompTests()
    {
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(Path.Combine(work, "Toy.dll"));
        Directory.CreateDirectory(Path.Combine(work, "Game.exe"));
        File.WriteAllText(Path.Combine(work, "Toy.dll", "functions.jsonl"), string.Join("\n",
            Line("1000", "toy::Widget::Update", Update, callees: "2000"),
            Line("2000", "FUN_180002000", Ground),
            Line("3000", "toy::Widget::Broken", null, "Decompiler timed out"),
            "{\"rva\":\"4000\",\"name\":\"cut sh") + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(work, "Game.exe", "functions.jsonl"), string.Join("\n",
            Line("1000", "FUN_140001000", Register),
            Line("1100", "FUN_140001100", Uses)) + "\n", new UTF8Encoding(false));

        var symbols = Path.Combine(root, "symbols.sqlite");
        using (var db = Sql.OpenWrite(symbols))
        {
            db.Execute("""
                CREATE TABLE dlls (id INTEGER PRIMARY KEY, name TEXT);
                CREATE TABLE symbols (dll_id INTEGER, rva INTEGER, full_name TEXT, kind TEXT);
                CREATE TABLE functions (dll_id INTEGER, rva INTEGER, size INTEGER);
                CREATE TABLE chunks (dll_id INTEGER, rva INTEGER, parent INTEGER);
                CREATE TABLE se_types (id INTEGER PRIMARY KEY, engine_class TEXT);
                CREATE TABLE se_fields (type_id INTEGER, name TEXT, offset INTEGER, checked INTEGER, idx INTEGER);
                CREATE TABLE accessors (class TEXT, field TEXT, offset INTEGER, game_offset INTEGER, bit INTEGER);
                INSERT INTO dlls VALUES (1, 'Toy.dll');
                INSERT INTO symbols VALUES (1, 8192, 'toy::Ground::Find', 'function');
                INSERT INTO functions VALUES (1, 4096, 64), (1, 20480, 32);
                INSERT INTO chunks VALUES (1, 20480, 4096);
                INSERT INTO se_types VALUES (1, 'toy::Widget');
                INSERT INTO se_fields VALUES (1, 'Health', 16, 1, 0);
                INSERT INTO accessors VALUES ('toy::Widget', 'Mana', 24, NULL, NULL);
                """);
        }
        settings = new DecompSettings(work, Path.Combine(root, "tools"), Path.Combine(root, "game"), Path.Combine(root, "toolkit"), symbols, null);
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

    (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        int exit = DecompCommands.Create(() => settings).Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    void Load()
    {
        var (exit, output, error) = Run("load");
        Assert.True(exit == 0, error);
        Assert.Contains("Game.exe: 2 functions, 0 without pseudo-code", output);
        Assert.Contains("Toy.dll: 3 functions, 1 without pseudo-code, 1 named from the export table", output);
    }

    [Fact]
    public void LoadThenCodeByName()
    {
        Load();
        var (exit, output, _) = Run("code", "toy::Widget::Update", "--plain");
        Assert.Equal(0, exit);
        Assert.Equal("// Toy.dll+0x1000  toy::Widget::Update\n" + Update + "\n", output);
    }

    [Fact]
    public void CodeAnnotatesFieldsAndUnnamedFunctions()
    {
        Load();
        var (_, output, _) = Run("code", "toy::Widget::Update");
        Assert.Contains("iVar1 = *(int *)(this + 0x10);   // +0x10 Health [SE layout]", output);
        Assert.Contains("*(int *)(this + 0x18) = 5;   // +0x18 Mana [accessor, Toolkit offset]", output);
        Assert.Contains("FUN_180002000 /* = toy::Ground::Find [export] */(this);", output);
    }

    [Fact]
    public void CodeByAddressAbsoluteAndInside()
    {
        Load();
        Assert.StartsWith("// Toy.dll+0x1000  toy::Widget::Update", Run("code", "Toy.dll+0x1000").Out);
        Assert.StartsWith("// Toy.dll+0x1000  toy::Widget::Update", Run("code", "Toy+0x180001000").Out);
        // an address in a separate part of the function: the symbol database's chunks know the parent
        var (_, output, error) = Run("code", "Toy.dll+0x5008");
        Assert.StartsWith("// Toy.dll+0x1000  toy::Widget::Update", output);
        Assert.Equal("// Toy.dll+0x5008 is not a function start: the address is inside this function\n", error);
        // without that knowledge, the nearest start before the address
        (_, output, error) = Run("code", "Toy.dll+0x2010");
        Assert.StartsWith("// Toy.dll+0x2000  toy::Ground::Find", output);
        Assert.Contains("nearest function start before the address; not proven to contain it", error);
    }

    [Fact]
    public void CodeShowsFailuresListsAndMisses()
    {
        Load();
        Assert.Equal("// Toy.dll+0x3000  toy::Widget::Broken\n// not decompiled: Decompiler timed out\n", Run("code", "Toy.dll+0x3000").Out);
        var (exit, output, error) = Run("code", "Widget");
        Assert.Equal(0, exit);
        Assert.Equal(2, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("Toy.dll+0x1000     100 bytes  toy::Widget::Update   void toy::Widget::Update(void)", output);
        Assert.Contains("2 functions match", error);
        (exit, _, error) = Run("code", "nothing::Like::This");
        Assert.Equal(1, exit);
        Assert.Equal("no such function\n", error);
    }

    [Fact]
    public void CodeExcerpt()
    {
        Load();
        var (_, output, _) = Run("code", "toy::Widget::Update", "--plain", "--grep", "0X1234|return", "-C", "0");
        Assert.Equal("// Toy.dll+0x1000  toy::Widget::Update\n    7    if (iVar1 == 0x1234) {\n   ...\n   11    return;\n", output);
        Assert.Equal("// no line contains 'zzz' (2 lines)", Annotations.Excerpt("a\nb\n", "zzz", 3));
    }

    [Fact]
    public void GrepFindsWordsInsideNames()
    {
        Load();
        var (_, output, _) = Run("grep", "MaterialComp");
        Assert.Equal("Toy.dll+0x2000  toy::Ground::Find\n    return GroundMaterialComponent(param_1, 0x40);\n", output);
        Assert.Equal("", Run("grep", "MaterialComp", "--word").Out);
        Assert.StartsWith("Toy.dll+0x2000", Run("grep", "GroundMaterialComponent", "--word", "--lines", "0").Out);
        Assert.Equal("nothing found\n", Run("grep", "Nonexistent").Err);
        using var db = Sql.OpenReadOnly(settings.DbPath);
        Assert.Equal("(\"groundmaterialcomponent\") AND (\"ivar1\")", GrepCommand.SubstringQuery(db, "material var1"));
        Assert.Null(GrepCommand.SubstringQuery(db, "qqqq"));
    }

    [Fact]
    public void FixedStringsAreReadFromRegistrations()
    {
        Load();
        var (exit, output, error) = Run("fixedstrings", "Sneak", "--binary", "Game.exe");
        Assert.Equal(0, exit);
        Assert.Equal("1 global FixedStrings found in Game.exe\n", error);
        Assert.Equal("""
            Game.exe+0x200018  value    "Sneaking"   (registered in Game.exe+0x1000)
            Game.exe+0x400000  pointer  "Sneaking"   (registered in Game.exe+0x1000)
            Game.exe+0x200000  entry    "Sneaking"   (registered in Game.exe+0x1000)

            """.Replace("\r\n", "\n"), output);
        Assert.Contains("*DAT_140400000 /* -> FixedString \"Sneaking\" */", Run("code", "Game.exe+0x1100").Out);
        using var queries = new DecompQueries(settings);
        Assert.Equal(new FixedStringGlobal("Game.exe", 0x200018, "value", "Sneaking", 0x1000), queries.FixedStringAt("Game.exe", 0x140200018));
        Assert.Null(queries.FixedStringAt("Game.exe", 0x200008));
    }

    [Fact]
    public void QueriesAndStatus()
    {
        Load();
        using var queries = new DecompQueries(settings);
        var code = queries.Code("Toy.dll", 0x1004, plain: true);
        Assert.NotNull(code);
        Assert.Equal(0x1000, code.Rva);
        Assert.Equal(Update, code.Text);
        Assert.Single(code.Notes);
        Assert.Null(queries.Code("Other.dll", 0x1000));
        Assert.Null(queries.CodeMatchName("Game.exe", 0x1000));
        Assert.Empty(queries.SystemFactories("Anything"));
        var status = Run("status").Out;
        Assert.Contains("Toy.dll: output 0 MB, running or never finished", status);
        Assert.Contains("  in database: Toy.dll: 3 functions, 1 without pseudo-code (loaded ", status);
    }

    [Fact]
    public void MissingDatabaseIsAnExpectedFailure()
    {
        var (exit, _, error) = Run("code", "x");
        Assert.Equal(1, exit);
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public void ZlibBlobsMatchPython()
    {
        // zlib.compress(b"hello", 6) in Python
        byte[] python = [0x78, 0x9c, 0xcb, 0x48, 0xcd, 0xc9, 0xc9, 0x07, 0x00, 0x06, 0x2c, 0x02, 0x15];
        Assert.Equal("hello", DecompStore.Decompress(python));
        var blob = DecompStore.Compress("pseudo-code é\n");
        Assert.Equal(0x78, blob[0]);
        Assert.Equal("pseudo-code é\n", DecompStore.Decompress(blob));
    }
}
