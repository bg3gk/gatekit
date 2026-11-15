using System.CommandLine;
using System.Text;
using System.Text.Json;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>`gatekit decomp tally` on a synthetic work folder: one binary with named and
/// default-named functions, field accesses by name, by a placeholder and raw, a typed local, two
/// helpers (one with a typed return), two virtual tables (one matched) and a guard summary.</summary>
public sealed class TallyTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("tally");
    readonly DecompSettings settings;

    static string Line(string rva, string name, string code) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["rva"] = rva, ["name"] = name, ["sig"] = "undefined " + name + "(void)", ["size"] = 100, ["thunk"] = false, ["callees"] = Array.Empty<string>(), ["code"] = code,
        });

    public TallyTests()
    {
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(Path.Combine(work, "Game.exe"));
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(work, "Game.exe", "functions.jsonl"), string.Join("\n",
            Line("1000", "FUN_140001000", "void FUN_140001000(longlong param_1)\n\n{\n  HealthComponent *pHVar1;\n\n  pHVar1 = ecs::GetComponent<toy::HealthComponent>(param_1);\n"
                + "  pHVar1->Hp = *(int *)(param_1 + 0x10);\n  pHVar1->field_8 = 0;\n  return;\n}\n"),
            Line("2000", "toy::Heal", "void toy::Heal(HealthComponent *param_1,longlong param_2)\n\n{\n  param_1->Hp = param_1->Hp + *(int *)(param_2 + 8);\n  other->Unrelated = 1;\n  return;\n}\n"),
            Line("3000", "thunk_FUN_140001000", "void thunk_FUN_140001000(void)\n\n{\n  return;\n}\n"),
            Line("4000", "ecs::GetComponent<toy::HealthComponent>", "HealthComponent * ecs::GetComponent<toy::HealthComponent>(longlong param_1)\n\n{\n  return 0;\n}\n")) + "\n", utf8);
        File.WriteAllText(Path.Combine(work, "Game.exe", "types.tsv"),
            "toy::HealthComponent\t24\t0\t4\tint\tHp\tint32_t\ntoy::HealthComponent\t24\t8\t1\tuint\tfield_8\tuint8_t\n", utf8);
        File.WriteAllText(Path.Combine(work, "Game.exe", "helpers.tsv"),
            "4000\tget\ttoy::HealthComponent\tecs::GetComponent<toy::HealthComponent>\ttoy::HealthComponent\ta rule\n"
            + "5000\thas\ttoy::HealthComponent\tecs::HasComponent<toy::HealthComponent>\t\ta rule\n", utf8);

        var symbols = Path.Combine(root, "symbols.sqlite");
        using (var db = Sql.OpenWrite(symbols))
        {
            db.Execute("""
                CREATE TABLE dlls (id INTEGER PRIMARY KEY, name TEXT);
                CREATE TABLE vtabs (id INTEGER PRIMARY KEY, dll_id INTEGER, rva INTEGER, slots INTEGER, symbol_id INTEGER);
                CREATE TABLE vtab_matches (toolkit_vtab INTEGER, game_vtab INTEGER, support INTEGER);
                INSERT INTO dlls VALUES (2, 'Game.exe');
                INSERT INTO vtabs VALUES (1, 2, 4096, 3, NULL), (2, 2, 8192, 5, NULL), (3, 1, 100, 3, 7);
                INSERT INTO vtab_matches VALUES (3, 1, 2);
                """);
        }
        settings = new DecompSettings(work, Path.Combine(root, "tools"), Path.Combine(root, "game"), Path.Combine(root, "toolkit"), symbols, null);
        var (exit, _, error) = Run("load");
        Assert.True(exit == 0, error);
        using (var db = DecompStore.OpenWrite(settings.DbPath))
            HelpersCommand.Store(db, "Game.exe",
            [
                new EcsHelper(0x4000, "get", "toy::HealthComponent", "component", "high", "a rule"),
                new EcsHelper(0x5000, "has", "toy::HealthComponent", "bool", "medium", "a rule"),
            ]);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    public void CountsWhatReadsLikeCode()
    {
        var (exit, output, error) = Run("tally", "--binary", "Game.exe", "--json");
        Assert.True(exit == 0, error);
        using var doc = JsonDocument.Parse(output);
        var b = doc.RootElement.GetProperty("binaries")[0];
        Assert.Equal(4, b.GetProperty("functions").GetProperty("total").GetInt32());
        Assert.Equal(2, b.GetProperty("functions").GetProperty("named").GetInt32());
        Assert.Equal(2, b.GetProperty("functions").GetProperty("default_named").GetInt32());
        // Hp three times and Unrelated once by name; Hp is the record's; field_8 is a placeholder
        Assert.Equal(4, b.GetProperty("fields").GetProperty("named_accesses").GetInt32());
        Assert.Equal(3, b.GetProperty("fields").GetProperty("record_field_accesses").GetInt32());
        Assert.Equal(1, b.GetProperty("fields").GetProperty("placeholder_accesses").GetInt32());
        Assert.Equal(2, b.GetProperty("fields").GetProperty("raw_dereferences").GetInt32());
        // a local in one, a parameter in another, a return type in the helper (its signature)
        Assert.Equal(3, b.GetProperty("typed").GetProperty("functions").GetInt32());
        Assert.Equal(2, b.GetProperty("typed").GetProperty("in_signature").GetInt32());
        Assert.Equal(1, b.GetProperty("helpers").GetProperty("get").GetProperty("typed_return").GetInt32());
        Assert.Equal(0, b.GetProperty("helpers").GetProperty("has").GetProperty("typed_return").GetInt32());
        Assert.Equal(2, b.GetProperty("vtables").GetProperty("total").GetInt32());
        Assert.Equal(1, b.GetProperty("vtables").GetProperty("named").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("guards").ValueKind);
    }

    [Fact]
    public void ReportsTheGuardsOfTheLastWholeDigestPass()
    {
        var (_, output, _) = Run("tally", "--binary", "Game.exe");
        Assert.Contains("digest guards          not counted", output);
        var folder = Path.Combine(settings.Work, "digests");
        Directory.CreateDirectory(folder);
        TallyCommand.WriteGuards(folder, new GuardTally("2026-11-14 10:00:00", 3, 40, 38, 15, 1, 1));
        (_, output, _) = Run("tally", "--binary", "Game.exe");
        Assert.Contains("40 operations of 3 systems: a guard for 38, 15 naming a component, a field or a call (37.5 %)", output);
        Assert.Contains("Game.exe\n  functions            4: 2 named (50.0 %), 2 default-named (50.0 %)", output);
        Assert.Contains("    get                     1  typed return     1, without     0", output);
    }

    [Fact]
    public void SaysWhatCouldNotBeCounted()
    {
        File.Delete(Path.Combine(settings.Work, "Game.exe", "types.tsv"));
        var (exit, output, _) = Run("tally", "--binary", "Game.exe");
        Assert.Equal(0, exit);
        Assert.Contains("typed functions      not counted", output);
        Assert.Contains("note: typed locals not counted", output);
        var (exit2, _, error) = Run("tally", "--binary", "Missing.exe");
        Assert.Equal(1, exit2);
        Assert.Contains("Missing.exe is not loaded", error);
    }

    [Fact]
    public void AComponentWithoutALayoutIsAStructureOfItsSize()
    {
        var coverage = Path.Combine(root, "coverage");
        Directory.CreateDirectory(Path.Combine(coverage, "layouts"));
        File.WriteAllText(Path.Combine(coverage, "layouts", "toy.HealthComponent.json"),
            "{\"component\": \"toy::HealthComponent\", \"size\": 24, \"fields\": [{\"name\": \"Hp\", \"offset\": 0, \"type\": \"int32_t\"}]}");
        File.WriteAllText(Path.Combine(coverage, "coverage.json"),
            "{\"components\": [{\"name\": \"toy::HealthComponent\", \"inlineSize\": 24}, {\"name\": \"toy::ManaComponent\", \"inlineSize\": 16},"
            + " {\"name\": \"toy::TagComponent\", \"inlineSize\": 0}, {\"inlineSize\": 8}]}");
        var types = RecordTypes.ReadWithSizes(coverage);
        // the layout stands; the component the ledger only knows the size of is a structure with nothing in it
        Assert.Equal(["toy::HealthComponent", "toy::ManaComponent"], types.Keys.ToList());
        Assert.Equal("Hp", types["toy::HealthComponent"].Fields[0].Name);
        Assert.Equal("toy::ManaComponent\t16\t0\t0\tbytes\t_\tsize only\n", types["toy::ManaComponent"].Lines());
        // and it is a structure of the project, but its one line is not a field anybody named
        File.WriteAllText(Path.Combine(settings.Work, "Game.exe", "types.tsv"), RecordTypes.Render(types.Values), new UTF8Encoding(false));
        var known = TallyCommand.Structures(settings.Work, "Game.exe");
        Assert.NotNull(known);
        Assert.Contains("ManaComponent", known.Value.Names);
        Assert.Equal(["Hp"], known.Value.Fields.ToList());
        // without a ledger the layouts stand alone
        File.Delete(Path.Combine(coverage, "coverage.json"));
        Assert.Single(RecordTypes.ReadWithSizes(coverage));
    }
}
