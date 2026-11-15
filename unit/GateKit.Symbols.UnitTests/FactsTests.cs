using System.CommandLine;
using System.Text.Json.Nodes;
using GateKit.Symbols.Decomp;
using GateKit.Symbols.Queries;

namespace GateKit.Symbols.UnitTests;

/// <summary>The component operations of the systems (`uses` of describe-systems) and
/// `symbols creators`, on a pseudo-code database written here: system updates, calls and
/// classified helpers of two game executables.</summary>
public sealed class FactsTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("facts");
    readonly string dbPath;
    readonly SymbolsEnvironment env;

    public FactsTests()
    {
        Directory.CreateDirectory(root);
        dbPath = Path.Combine(root, "symbols.sqlite");
        env = new SymbolsEnvironment(Path.Combine(root, "decomp", "decomp.sqlite"), null, Path.Combine(root, "game"), Path.Combine(root, "toolkit"));
        using (var db = Sql.OpenWrite(dbPath))
        {
            db.Execute(SymbolSchema.Tables);
            db.Execute(SymbolSchema.Indexes);
            db.Execute("INSERT INTO dlls VALUES (1, 'bg3_dx11.exe', 'bin/bg3_dx11.exe', 200, 0, 'x64', 0, 0, 0, NULL, 'game', 3)");
            db.Execute("INSERT INTO dlls VALUES (2, 'bg3.exe', 'bin/bg3.exe', 200, 0, 'x64', 0, 0, 0, NULL, 'game', 3)");
        }
        using (var decomp = DecompStore.OpenWrite(env.DecompDb))
        {
            decomp.Execute("INSERT INTO binaries VALUES (16, 'bg3_dx11.exe', '', 0, 0), (2, 'bg3.exe', '', 0, 0)");
            void Fn(long binary, long rva, string name) => decomp.Execute("INSERT INTO functions (binary_id, rva, name, size) VALUES (?, ?, ?, 64)", binary, rva, name);
            void Call(long binary, long caller, long callee) => decomp.Execute("INSERT INTO calls VALUES (?, ?, ?)", binary, caller, callee);
            void Helper(string binary, long rva, string family, string type) =>
                decomp.Execute("INSERT INTO ecs_helpers VALUES (?, ?, ?, ?, 'component', 'high', 'test')", binary, rva, family, type);
            foreach (var (rva, name) in new[] { (0x100L, "FUN_140000100"), (0x200, "FUN_140000200"), (0x300, "FUN_140000300"), (0x400, "ecs::Engine::Work"), (0x500, "FUN_140000500") })
                Fn(16, rva, name);
            Fn(2, 0x100, "FUN_140000100");
            decomp.Execute("INSERT INTO system_updates VALUES (16, 'toy::TickSystem', 4096, 256, 'x'), (16, 'toy::OtherSystem', 4097, 768, 'x'),"
                           + " (2, 'toy::OldSystem', 4096, 256, 'x')");
            Helper("bg3_dx11.exe", 0x900, "add", "toy::HealthComponent");
            Helper("bg3_dx11.exe", 0x910, "get", "toy::ManaComponent");
            Helper("bg3_dx11.exe", 0x920, "remove", "toy::ManaComponent");
            Helper("bg3_dx11.exe", 0x930, "registration", "toy::ManaComponent");
            Helper("bg3_dx11.exe", 0x940, "has", "toy::DeepComponent");
            Helper("bg3.exe", 0x900, "remove", "toy::HealthComponent");
            Call(16, 0x100, 0x900);     // the update adds Health itself
            Call(16, 0x100, 0x200);
            Call(16, 0x200, 0x910);     // a function it calls gets Mana
            Call(16, 0x200, 0x930);     // a registration is not an operation
            Call(16, 0x100, 0x400);
            Call(16, 0x400, 0x920);     // the engine's own function: not the system's
            Call(16, 0x100, 0x300);
            Call(16, 0x300, 0x940);     // another system's update: not this one's
            Call(16, 0x500, 0x900);     // someone else adds Health too
            Call(2, 0x100, 0x900);
        }
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
        }
    }

    (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        int exit = SymbolsCommands.CreateCommand(() => env).Parse(["--db", dbPath, .. args]).Invoke(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void Uses_are_the_operations_the_update_performs_to_depth_two()
    {
        var output = Path.Combine(root, "systems");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "survey.csv"), "system\ntoy::TickSystem\ntoy::OldSystem\n");
        var (exit, _, error) = Run("describe-systems", "--out", output);
        Assert.True(exit == 0, error);

        var tick = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "toy", "TickSystem.json")))!["symbols"]!["uses"]!.AsArray();
        Assert.Equal(["add toy::HealthComponent bg3_dx11.exe+0x100 1", "get toy::ManaComponent bg3_dx11.exe+0x200 2"],
                     tick.Select(u => $"{u!["kind"]} {u["type"]} {u["function"]} {u["depth"]}"));
        Assert.Null(tick[0]!["site"]);                       // the binary's file is not there to read the call from
        Assert.Equal("high", (string)tick[0]!["confidence"]!);

        // no update entry in the binary a copy starts: bg3.exe's is walked
        var old = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "toy", "OldSystem.json")))!["symbols"]!["uses"]!.AsArray();
        Assert.Equal("remove toy::HealthComponent bg3.exe+0x100", string.Join(";", old.Select(u => $"{u!["kind"]} {u["type"]} {u["function"]}")));
    }

    [Fact]
    public void Creators_are_the_callers_of_the_add_helpers_with_their_systems()
    {
        var (exit, output, error) = Run("creators", "HealthComponent");
        Assert.True(exit == 0, error);
        Assert.Equal("bg3_dx11.exe+0x100  FUN_140000100  (adds through bg3_dx11.exe+0x900)  update of toy::TickSystem\n"
                     + "bg3_dx11.exe+0x500  FUN_140000500  (adds through bg3_dx11.exe+0x900)\n", output);
        Assert.Contains("toy::HealthComponent: 2 functions add it, 1 of them reached from a system's update (depth 2); 1 systems", error);

        (_, output, error) = Run("creators", "toy::ManaComponent");
        Assert.Equal("", output);
        Assert.Contains("no add helper of toy::ManaComponent is known", error);
    }
}
