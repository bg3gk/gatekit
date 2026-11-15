using System.CommandLine;
using System.Text;
using System.Text.Json;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>`gatekit decomp systems` per binary and `gatekit decomp helpers` on synthetic data: two
/// executables loaded at different image bases, a symbol database with type indices and the
/// references to them, a macOS build whose templates carry their names, a coverage record with
/// the size of a type.</summary>
public sealed class HelpersTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("helpers");
    readonly string coverage;
    readonly DecompSettings settings;

    static string Line(string rva, string name, string code, int size = 100, params string[] callees) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["rva"] = rva, ["name"] = name, ["sig"] = "undefined " + name + "(void)", ["size"] = size, ["thunk"] = false, ["callees"] = callees, ["code"] = code,
        });

    /// <summary>A factory that creates one system: the constructor stores the virtual table, the
    /// registration helper takes the system's index, and the update entry stored calls the update.</summary>
    static string[] Factory(string hi, string system) =>
    [
        Line("1000", $"FUN_{hi}001000", $"void FUN_{hi}001000(longlong param_1)\n\n{{\n  undefined8 uVar1;\n  undefined1 local_10 [8];\n\n"
            + $"  FUN_{hi}002000(local_10);\n  uVar1 = TypeIndex<{system}>_SystemsContext;\n  *(code **)(local_10 + 8) = FUN_{hi}004000;\n"
            + $"  FUN_{hi}003000(param_1,uVar1,local_10);\n  return;\n}}\n", 100, "2000", "3000"),
        Line("2000", $"FUN_{hi}002000", $"void FUN_{hi}002000(undefined8 *param_1)\n\n{{\n  *param_1 = &PTR_FUN_{hi}100000;\n  return;\n}}\n"),
        Line("3000", $"FUN_{hi}003000", $"void FUN_{hi}003000(longlong param_1,undefined8 param_2,longlong param_3)\n\n{{\n  return;\n}}\n"),
        Line("4000", $"FUN_{hi}004000", $"void FUN_{hi}004000(longlong param_1,undefined8 param_2)\n\n{{\n  FUN_{hi}005000(param_1,param_2);\n  return;\n}}\n", 100, "5000"),
        Line("5000", $"FUN_{hi}005000", $"void FUN_{hi}005000(longlong param_1,undefined8 param_2)\n\n{{\n  FUN_140006000(param_1,param_2);\n  return;\n}}\n", 100, "6000", "7000"),
    ];

    static string Fn(string name, string returns, string body) => $"{returns} {name}(longlong param_1,undefined8 param_2)\n\n{{\n{body}}}\n";

    public HelpersTests()
    {
        var work = Path.Combine(root, "work");
        coverage = Path.Combine(root, "coverage");
        foreach (var b in new[] { "Game.exe", "Other.exe", "Bare.exe", "bg3_macos_x64" })
            Directory.CreateDirectory(Path.Combine(work, b));
        Directory.CreateDirectory(Path.Combine(coverage, "layouts", "alive"));
        File.WriteAllText(Path.Combine(coverage, "layouts", "alive", "toy.HealthComponent.json"),
            "{\"component\": \"toy::HealthComponent\", \"size\": 24, \"fields\": [{\"name\": \"Hp\", \"offset\": 0, \"type\": \"int32_t\"}]}");
        File.WriteAllText(Path.Combine(work, "Other.exe", "functions.jsonl"), string.Join("\n", Factory("180", "toy::TickSystem")) + "\n", new UTF8Encoding(false));
        var game = new List<string>(Factory("140", "toy::OtherSystem"))
        {
            Line("6000", "FUN_140006000", Fn("FUN_140006000", "longlong", "  return ecs::legacy::ImmediateWorldCache::GetChange(param_1,param_2);\n"), 100, "9000"),
            Line("6100", "FUN_140006100", Fn("FUN_140006100", "longlong", "  return ecs::ComponentFrameStorageAllocRaw(param_1,0x18,param_2);\n"), 100, "9100"),
            Line("6200", "FUN_140006200", Fn("FUN_140006200", "void", "  ecs::legacy::ImmediateWorldCache::GetChange(param_1,param_2);\n  return;\n"), 100, "9000"),
            Line("6300", "FUN_140006300", Fn("FUN_140006300", "ulonglong", "  return CONCAT71(0,ecs::EntityRef::IsNull(param_1) != 0);\n"), 100, "9200"),
            Line("6400", "FUN_140006400", Fn("FUN_140006400", "void", "  _Init_thread_header(&DAT_140300000);\n  return;\n")),
            Line("6500", "FUN_140006500", Fn("FUN_140006500", "void", "  return;\n")),
            Line("6600", "FUN_140006600", Fn("FUN_140006600", "void", "  return;\n")),
            Line("6700", "FUN_140006700", Fn("FUN_140006700", "longlong", "  return ecs::legacy::ImmediateWorldCache::GetChange(param_1,param_2);\n"), 100, "9000"),
            Line("6800", "FUN_140006800", Fn("FUN_140006800", "longlong", "  return ecs::legacy::ImmediateWorldCache::GetChange(param_1,param_2);\n"), 2000, "9000"),
            Line("6900", "FUN_140006900", Fn("FUN_140006900", "longlong", "  return ecs::legacy::ImmediateWorldCache::GetChange(param_1,param_2);\n"), 100, "9000", "9300"),
            Line("6A00", "FUN_140006a00", Fn("FUN_140006a00", "longlong", "  return ecs::legacy::ImmediateWorldCache::GetChange(param_1,param_2);\n"), 100, "9000", "6B00"),
            Line("6B00", "FUN_140006b00", Fn("FUN_140006b00", "longlong", "  return ecs::legacy::ImmediateWorldCache::GetChange(param_1,param_2);\n"), 100, "9000"),
            Line("7000", "FUN_140007000", Fn("FUN_140007000", "void", "  return;\n"), 100, "6000", "6100", "6200", "6300", "6500", "6700", "6800", "6900", "6A00"),
            Line("9300", "eoc::Passives::Get", Fn("eoc::Passives::Get", "longlong", "  return 0;\n")),
            Line("9000", "ecs::legacy::ImmediateWorldCache::GetChange", Fn("ecs::legacy::ImmediateWorldCache::GetChange", "longlong", "  return 0;\n")),
            Line("9100", "ecs::ComponentFrameStorageAllocRaw", Fn("ecs::ComponentFrameStorageAllocRaw", "longlong", "  return 0;\n")),
            Line("9200", "ecs::EntityRef::IsNull", Fn("ecs::EntityRef::IsNull", "bool", "  return 0;\n")),
        };
        File.WriteAllText(Path.Combine(work, "Game.exe", "functions.jsonl"), string.Join("\n", game) + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(work, "Bare.exe", "functions.jsonl"), string.Join("\n",
            Line("1000", "FUN_140001000", Fn("FUN_140001000", "void", "  FUN_140002000();\n  return;\n"), 100, "2000"),
            Line("2000", "FUN_140002000", Fn("FUN_140002000", "void", "  return;\n"))) + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(work, "bg3_macos_x64", "functions.jsonl"), string.Join("\n",
            Line("1000", "ecs::EntityWorld::GetComponent<toy::HealthComponent_const,false>", "void f(void)\n{\n}\n"),
            Line("2000", "ecs::legacy::ImmediateWorldCache::AddComponent<toy::HealthComponent>", "void f(void)\n{\n}\n")) + "\n", new UTF8Encoding(false));

        var symbols = Path.Combine(root, "symbols.sqlite");
        using (var db = Sql.OpenWrite(symbols))
        {
            db.Execute("""
                CREATE TABLE dlls (id INTEGER PRIMARY KEY, name TEXT);
                CREATE TABLE ecs_ids (dll_id INTEGER, type TEXT, context TEXT, index_rva INTEGER, context_rva INTEGER, source TEXT);
                CREATE TABLE xrefs (dll_id INTEGER, func_rva INTEGER, target_rva INTEGER, kind TEXT, uses INTEGER);
                CREATE TABLE chunks (dll_id INTEGER, rva INTEGER, parent INTEGER);
                INSERT INTO dlls VALUES (2, 'Game.exe');
                INSERT INTO ecs_ids VALUES (2, 'toy::HealthComponent', 'ecs::ComponentTypeIdContext', 2097152, 0, 'id registrant'),
                                           (2, 'toy::ManaComponent', 'ecs::ComponentTypeIdContext', 2097160, 0, 'id registrant');
                INSERT INTO xrefs VALUES (2, 24576, 2097152, 'r', 1), (2, 24832, 2097152, 'r', 1), (2, 25088, 2097152, 'r', 1),
                                         (2, 25344, 2097152, 'r', 1), (2, 25600, 2097152, 'w', 1), (2, 25872, 2097152, 'r', 1),
                                         (2, 26112, 2097152, 'r', 1), (2, 26368, 2097152, 'r', 1), (2, 26368, 2097160, 'r', 1),
                                         (2, 26624, 2097152, 'r', 1), (2, 40000, 2097152, 'r', 1), (2, 26880, 2097152, 'r', 1),
                                         (2, 27136, 2097152, 'r', 1), (2, 27392, 2097160, 'r', 1);
                INSERT INTO chunks VALUES (2, 40000, 25344);
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

    (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        int exit = DecompCommands.Create(() => settings).Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void EachBinaryHasItsOwnSystemsAtItsOwnImageBase()
    {
        // Other.exe is loaded at 0x180000000: its addresses are read from its own FUN_ names
        var (exit, output, error) = Run("systems", "--binary", "Other.exe", "--rebuild");
        Assert.True(exit == 0, error);
        Assert.Contains("1 systems found in Other.exe", error);
        Assert.Contains("toy::TickSystem\n    created in Other.exe+0x1000, constructor Other.exe+0x2000, virtual table Other.exe+0x100000", output);
        Assert.Contains("    per-frame update: Other.exe+0x5000   (entry stored by the factory: Other.exe+0x4000; the entry builds the views and calls the update)", output);
        Assert.DoesNotContain("OtherSystem", output);

        // rows of another binary do not stand for this one's: Game.exe is read on first use
        (exit, output, error) = Run("systems", "--binary", "Game.exe");
        Assert.True(exit == 0, error);
        Assert.Contains("1 systems found in Game.exe", error);
        Assert.Contains("toy::OtherSystem\n    created in Game.exe+0x1000, constructor Game.exe+0x2000, virtual table Game.exe+0x100000", output);
        Assert.DoesNotContain("TickSystem", output);
        (_, _, error) = Run("systems", "--binary", "Game.exe");
        Assert.DoesNotContain("systems found", error);
    }

    [Fact]
    public void HelpersAreClassifiedAndCheckedAgainstTheMacBuild()
    {
        var (exit, output, error) = Run("helpers", "--binary", "Game.exe", "--rebuild", "--coverage", coverage);
        Assert.True(exit == 0, error);
        Assert.Contains("8 helpers of Game.exe", error);
        Assert.Contains("checked against the macOS build's pseudo-code: add 1 of 1 (100.0 %), get 1 of 1 (100.0 %), has 0 of 1 (0.0 %), remove 0 of 1 (0.0 %)", error);
        Assert.Contains("registration       1 helpers,     1 types, 1 high, 0 return the component", output);

        using var db = Sql.OpenReadOnly(settings.DbPath);
        var rows = HelpersCommand.Load(db, "Game.exe");
        // 0x6A00 calls a helper of another type: two components, not a helper
        Assert.Equal([0x6000L, 0x6100, 0x6200, 0x6300, 0x6400, 0x6500, 0x6900, 0x6B00], rows.Keys.Order());
        Assert.Equal(("get", "component", "high"), (rows[0x6000].Family, rows[0x6000].Returns, rows[0x6000].Confidence));
        Assert.Equal(("add", "component", "high"), (rows[0x6100].Family, rows[0x6100].Returns, rows[0x6100].Confidence));
        Assert.Contains("of the record's size of the type (24 bytes)", rows[0x6100].Basis);
        Assert.Equal(("remove", "nothing", "medium"), (rows[0x6200].Family, rows[0x6200].Returns, rows[0x6200].Confidence));
        Assert.Contains("the macOS build has add/get templates of the type, not remove", rows[0x6200].Basis);
        Assert.Equal(("has", "bool"), (rows[0x6300].Family, rows[0x6300].Returns));     // found through a separate part of the function
        Assert.Equal(("registration", "high"), (rows[0x6400].Family, rows[0x6400].Confidence));
        Assert.Equal(("unknown", "none"), (rows[0x6500].Family, rows[0x6500].Confidence));   // no rule, but called: kept
        Assert.Equal(("unknown", "calls the game's eoc::Passives::Get: does more than a get"), (rows[0x6900].Family, rows[0x6900].Basis));
        Assert.Equal(("get", "toy::ManaComponent"), (rows[0x6B00].Family, rows[0x6B00].Type));

        (_, output, _) = Run("helpers", "Health", "--binary", "Game.exe");
        Assert.Contains("Game.exe+0x6000  ecs::GetComponent<toy::HealthComponent>  [get, high; returns component]", output);
        Assert.Contains("Game.exe+0x6500  ecs::ComponentHelper<toy::HealthComponent>  [unknown, none; returns nothing]", output);
    }

    [Fact]
    public void EmptyInputsAreASentenceNotZeroHelpers()
    {
        // a symbol database built without a Script Extender tree has no type indices
        using (var db = Sql.OpenWrite(settings.SymbolsDb))
            db.Execute("DELETE FROM ecs_ids");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var (exit, output, error) = Run("helpers", "--binary", "Game.exe", "--rebuild", "--coverage", coverage);
        Assert.Equal(1, exit);
        Assert.Contains("has no type indices of Game.exe (ecs_ids is empty for it)", error);
        Assert.Contains("BG3SE_SRC", error);
        Assert.DoesNotContain("0 helpers", error + output);

        // a binary whose pseudo-code shows no system registry
        (exit, _, error) = Run("systems", "--binary", "Bare.exe", "--rebuild");
        Assert.Equal(1, exit);
        Assert.Contains("no system factory was found", error);
        Assert.Contains("no function of Bare.exe mentions _SystemsContext", error);
        Assert.DoesNotContain("0 systems found", error);
    }

    [Fact]
    public void TheRulesReadTheDeclarationAndTheArguments()
    {
        Assert.Equal(("ulonglong", 2), HelpersCommand.Head("\n/* WARNING: x */\n\nulonglong FUN_1(longlong param_1,ulonglong *param_2)\n\n{\n}\n"));
        Assert.Equal(("void", 0), HelpersCommand.Head("void FUN_1(void)\n{\n}\n"));
        Assert.Equal("eoc::X", HelpersCommand.FirstArgument("ecs::EntityWorld::GetComponent<eoc::X_const,false>", "ecs::EntityWorld::GetComponent<"));
        Assert.Equal("eoc::Y<int>", HelpersCommand.FirstArgument("ecs::EntityRef::Has<eoc::Y<int> const>", "ecs::EntityRef::Has<"));
        Assert.Null(HelpersCommand.FirstArgument("ecs::Other<eoc::X>", "ecs::EntityRef::Has<"));
        Assert.True(HelpersCommand.AllocatesSize("p = ecs::ComponentFrameStorageAllocRaw(a,0x48,b);", 72));
        Assert.True(HelpersCommand.AllocatesSize("p = ecs::ComponentFrameStorageAllocRaw(a,72,b);", 72));
        Assert.False(HelpersCommand.AllocatesSize("p = ecs::ComponentFrameStorageAllocRaw(a,0x480,b);", 72));

        // a function that returns nothing and only looks the entity up is not taken for a remove
        var h = HelpersCommand.ClassifyOne(1, "t", "void f(longlong param_1)\n{\n}\n", new HashSet<string> { "ecs::EntityRef::IsNull" }, false, null, null);
        Assert.Equal("unknown", h.Family);
        Assert.Equal("ecs::RemoveComponent<eoc::X>", HelpersCommand.Name("remove", "eoc::X"));

        // what the engine's templates may call: the engine, the runtime, unnamed code, constructors and destructors
        Assert.True(HelpersCommand.EngineName("ecs::EntityRef::IsNull"));
        Assert.True(HelpersCommand.EngineName("FUN_140001000"));
        Assert.True(HelpersCommand.EngineName("eoc::spell::SpellId::~SpellId"));
        Assert.True(HelpersCommand.EngineName("eoc::spell::SpellId::SpellId"));
        Assert.False(HelpersCommand.EngineName("eoc::Passives::Get"));
        h = HelpersCommand.ClassifyOne(1, "t", "longlong f(longlong param_1)\n{\n}\n", new HashSet<string> { "ecs::EntityRef::IsNull" }, false, null, null);
        Assert.Equal(("get", "component"), (h.Family, h.Returns));
    }
}
