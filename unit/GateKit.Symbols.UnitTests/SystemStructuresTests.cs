using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>The structure of a system and the functions that take the system first, on a toy
/// database: an update entry that hands its first parameter on, one that does not, a constructor
/// that stores the virtual table, and a function two systems share.</summary>
public sealed class SystemStructuresTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("systems");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    [Fact]
    public void TheReachOfAFunctionIsCountedInBytes()
    {
        // an integer: the offsets are bytes
        Assert.Equal(0x38, SystemStructures.Furthest("void f(longlong param_1)\n{\n  x = *(int *)(param_1 + 0x38);\n  y = *(char *)(param_1 + 8);\n}\n"));
        // a pointer to 8 bytes: an index and a sum count in elements, a cast makes the sum bytewise
        Assert.Equal(0x40, SystemStructures.Furthest("void f(longlong *param_1)\n{\n  x = param_1[8];\n  y = *(int *)((longlong)param_1 + 0x1c);\n}\n"));
        Assert.Equal(0x18, SystemStructures.Furthest("void f(undefined8 *param_1,int param_2)\n{\n  x = *(param_1 + 3);\n}\n"));
        Assert.Equal(-1, SystemStructures.Furthest("void f(longlong param_1)\n{\n  return;\n}\n"));
        // a structure already: its arithmetic says nothing about bytes
        Assert.Equal(-1, SystemStructures.Furthest("void f(TickSystem *param_1)\n{\n  x = *(int *)(param_1 + 2);\n}\n"));
        Assert.Equal(4, SystemStructures.Step("void f(uint *param_1)\n{\n}\n"));
    }

    [Fact]
    public void OnlyFunctionsKnownToTakeTheSystemFirstAreTyped()
    {
        Directory.CreateDirectory(root);
        using var db = DecompStore.OpenWrite(Path.Combine(root, "decomp.sqlite"));
        db.Execute("INSERT INTO binaries (id, name) VALUES (2, 'Game.exe')");
        void Add(long id, long rva, string name, string code)
        {
            db.Execute("INSERT INTO functions (id, binary_id, rva, name, size, thunk) VALUES (?, 2, ?, ?, 100, 0)", id, rva, name);
            db.Execute("INSERT INTO code VALUES (?, ?)", id, DecompStore.Compress(code));
        }
        Add(1, 0x1000, "FUN_140001000", "void FUN_140001000(longlong param_1,longlong param_2)\n{\n  undefined1 local_38 [32];\n\n  FUN_140002000(param_1,local_38);\n}\n");
        Add(2, 0x2000, "FUN_140002000", "void FUN_140002000(longlong param_1,longlong *param_2)\n{\n  if (*(int *)(param_1 + 0x58) != 0) {\n    *(char *)(param_1 + 0x61) = 1;\n  }\n}\n");
        Add(3, 0x3000, "FUN_140003000", "void FUN_140003000(longlong param_1,longlong param_2)\n{\n  FUN_140004000(param_2);\n}\n");
        Add(4, 0x4000, "FUN_140004000", "void FUN_140004000(longlong param_1)\n{\n  *(int *)(param_1 + 0x900) = 0;\n}\n");
        Add(5, 0x5000, "FUN_140005000", "void FUN_140005000(undefined8 *param_1)\n{\n  *param_1 = &PTR_FUN_140100000;\n  param_1[9] = 0;\n}\n");
        Add(6, 0x6000, "FUN_140006000", "void FUN_140006000(undefined8 *param_1)\n{\n  param_1[1] = 0;\n}\n");
        Add(7, 0x7000, "FUN_140007000", "void FUN_140007000(longlong param_1)\n{\n  return;\n}\n");
        db.Execute("""
            INSERT INTO system_updates VALUES (2, 'toy::TickSystem', 4096, 8192, ''), (2, 'toy::OtherSystem', 12288, 16384, ''),
                                              (2, 'toy::A', 28672, NULL, ''), (2, 'toy::B', 28672, NULL, '');
            INSERT INTO system_factories VALUES (2, 'toy::TickSystem', 1, 20480, 1048576, ''), (2, 'toy::OtherSystem', 2, 24576, NULL, '');
            """);
        var (sizes, functions, skipped) = SystemStructures.Build(db, "Game.exe");
        Assert.Equal(
            [(0x1000L, "toy::TickSystem", "update entry"), (0x2000L, "toy::TickSystem", "update"), (0x3000L, "toy::OtherSystem", "update entry"), (0x5000L, "toy::TickSystem", "constructor")],
            functions.OrderBy(f => f.Rva).Select(f => (f.Rva, f.System, f.Role)).ToList());
        // the furthest reach is the update's 0x61, then the next 8 bytes
        Assert.Equal(0x70, sizes["toy::TickSystem"]);
        Assert.Equal(8, sizes["toy::OtherSystem"]);
        Assert.False(sizes.ContainsKey("toy::A"));
        Assert.Equal(1, skipped["the entry does not hand its first parameter to the update"]);
        Assert.Equal(1, skipped["the constructor stores nothing at the start of its first parameter"]);
        Assert.Equal(2, skipped["shared by two systems"]);
        Assert.Contains("the update entry +0x1000 hands it its first parameter", functions.Single(f => f.Rva == 0x2000).Basis);

        // a structure of its size with nothing in it, unless the record already has one of that name
        Assert.Equal("toy::OtherSystem\t8\t0\t0\tbytes\t_\tsystem\ntoy::TickSystem\t112\t0\t0\tbytes\t_\tsystem\n", SystemStructures.TypeLines(sizes, new HashSet<string>()));
        Assert.Equal("toy::OtherSystem\t8\t0\t0\tbytes\t_\tsystem\n", SystemStructures.TypeLines(sizes, new HashSet<string> { "toy::TickSystem" }));
        Assert.StartsWith("1000\tupdate entry\ttoy::TickSystem\ttoy::TickSystem\t", SystemStructures.FunctionLines(functions));
    }
}
