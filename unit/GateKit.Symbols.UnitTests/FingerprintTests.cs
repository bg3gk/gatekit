using GateKit.Symbols.Registry;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.UnitTests;

/// <summary>The fingerprint of a function: facts that stay when the code around it moves, read from
/// a symbol database and a body written here.</summary>
public sealed class FingerprintTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("fingerprint");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    const ulong Base = 0x140000000, End = 0x150000000;

    /// <summary>A made-up body of 20 instructions: a RIP-relative load, a near call and an absolute address among them.</summary>
    static byte[] Body(int call, int rip, ulong absolute, byte register = 0xC1)
    {
        var code = new List<byte>();
        code.AddRange([0x48, 0x8B, 0x05, .. BitConverter.GetBytes(rip)]);               // mov rax, [rip+rip]
        code.AddRange([0xE8, .. BitConverter.GetBytes(call)]);                           // call rel32
        code.AddRange([0x48, 0xB9, .. BitConverter.GetBytes(absolute)]);                 // mov rcx, imm64
        for (var i = 0; i < 16; i++) code.AddRange([0x48, 0x01, register]);              // add rcx, rax
        code.Add(0xC3);                                                                  // ret
        while (code.Count < 80) code.Add(0xCC);
        return [.. code];
    }

    [Fact]
    public void A_body_moved_elsewhere_hashes_the_same_and_another_body_does_not()
    {
        var one = Fingerprint.BodyHash(Body(0x1000, 0x2000, 0x140123456), Base + 0x1000, Base, End);
        var moved = Fingerprint.BodyHash(Body(0x7777, 0x0042, 0x14ABCDEF0), Base + 0x9000, Base, End);
        var other = Fingerprint.BodyHash(Body(0x1000, 0x2000, 0x140123456, register: 0xC2), Base + 0x1000, Base, End);
        var constant = Fingerprint.BodyHash(Body(0x1000, 0x2000, 0x12345), Base + 0x1000, Base, End);
        Assert.NotNull(one);
        Assert.Matches("^[0-9a-f]{64}$", one);
        Assert.Equal(one, moved);           // a call target, a RIP displacement and an address in the image are masked
        Assert.NotEqual(one, other);        // another register is another body
        Assert.NotEqual(one, constant);     // a number outside the image is part of the body
    }

    [Fact]
    public void A_body_too_short_to_be_safe_gets_no_hash()
    {
        Assert.Null(Fingerprint.BodyHash(new byte[] { 0x31, 0xC0, 0xC3 }, Base, Base, End));
        var fewInstructions = new byte[80];
        Array.Fill(fewInstructions, (byte)0xCC);
        fewInstructions[0] = 0xC3;
        Assert.Null(Fingerprint.BodyHash(fewInstructions, Base, Base, End));    // int3 padding decodes, but only as filler: still below the bar
    }

    [Fact]
    public void The_facts_are_read_from_the_symbol_database_sorted_and_named()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "symbols.sqlite");
        using (var db = Sql.OpenWrite(path))
        {
            db.Execute(GateKit.Symbols.Queries.SymbolSchema.Tables);
            db.Execute("INSERT INTO dlls (id, name, path) VALUES (1, 'Game.exe', 'bin/Game.exe'), (2, 'Other.dll', 'Other.dll')");
            db.Execute("INSERT INTO functions VALUES (1, 0x1000, 0x40, 0), (1, 0x2000, 0x10, 0), (1, 0x3000, 8, 0), (1, 0x4000, 8, 0)");
            db.Execute("INSERT INTO chunks VALUES (1, 0x2000, 0x1000)");
            db.Execute("INSERT INTO calls VALUES (1, 0x1000, 1, 0x3000, NULL, 1), (1, 0x2000, 1, 0x4000, NULL, 1), (1, 0x1000, NULL, NULL, 'kernel32.dll!Sleep', 2), (1, 0x1000, 2, 0x10, NULL, 1)");
            db.Execute("INSERT INTO symbols (id, dll_id, rva, full_name) VALUES (1, 1, 0x3000, 'ns::Named'), (2, 2, 0x10, 'other::Thing'), (3, 1, 0x5000, 'ns::Foo::`vftable''')");
            db.Execute("INSERT INTO strings VALUES (1, 1, 0x9000, 'zeta'), (2, 1, 0x9010, 'alpha')");
            db.Execute("INSERT INTO string_refs VALUES (1, 0x1000), (2, 0x2000)");
            db.Execute("INSERT INTO ecs_ids VALUES (1, 'eoc::HealthComponent', 'ctx', 0x8000, 0, 'test')");
            db.Execute("INSERT INTO xrefs VALUES (1, 0x1000, 0x8000, 'r', 1)");
            db.Execute("INSERT INTO vtabs VALUES (1, 1, 0x5000, 4, 3), (2, 1, 0x6000, 2, NULL)");
            db.Execute("INSERT INTO vtab_slots VALUES (1, 2, 0x1000), (2, 0, 0x1000)");
        }
        using var symbols = Sql.OpenReadOnly(path);
        var reader = new FingerprintReader(symbols, null, (binary, rva) => rva == 0x4000 ? "ns::OurName" : null);
        var print = reader.Read("game.exe", 0x1000);
        var place = reader.Place("game.exe", 0x1000);
        Assert.Equal(0x50, place.Size);                                    // the split-off part counts
        Assert.Null(place.Body);
        Assert.Contains("no file", reader.BodyNote);

        // a callee with a registry id by its id; one without by the hash of its own facts, never by an
        // address or by another source's name; an import and the strings by hashes of their text
        Assert.Contains("ns::OurName", print.Calls);
        Assert.Contains("import:" + Fingerprint.TextHash("kernel32.dll!Sleep"), print.Calls);
        Assert.Equal(1, print.Calls.Count(c => c.StartsWith("fp:", StringComparison.Ordinal)));   // two callees of the same facts are one hash
        Assert.DoesNotContain(print.Calls, c => c.Contains("Named", StringComparison.Ordinal) || c.Contains("Thing", StringComparison.Ordinal));
        Assert.Equal(print.Calls.Order(StringComparer.Ordinal), print.Calls);
        Assert.Equal(new[] { Fingerprint.TextHash("alpha"), Fingerprint.TextHash("zeta") }.Order(StringComparer.Ordinal), print.Strings);
        Assert.DoesNotContain(print.Strings, s => s.Contains("alpha", StringComparison.Ordinal));
        Assert.Equal(["eoc::HealthComponent"], print.Components);
        Assert.Equal([((string?)null, 0), ("ns::Foo::`vftable'", 2)], print.Vtables);

        var again = Fingerprint.FromJson(print.ToJson());
        Assert.Equal(print.Token(), again.Token());
        Assert.Equal(8, print.Token().Length);
        Assert.Matches("^[0-9a-f]+$", print.Token());
    }

    [Fact]
    public void The_facts_do_not_change_when_the_code_moves()
    {
        Directory.CreateDirectory(root);
        string Make(string name, long shift)
        {
            var path = Path.Combine(root, name);
            using var db = Sql.OpenWrite(path);
            db.Execute(GateKit.Symbols.Queries.SymbolSchema.Tables);
            db.Execute("INSERT INTO dlls (id, name, path) VALUES (1, 'Game.exe', 'bin/Game.exe')");
            db.Execute("INSERT INTO functions VALUES (1, ?, 0x40, 0), (1, ?, 8, 0)", 0x1000 + shift, 0x3000 + shift);
            db.Execute("INSERT INTO calls VALUES (1, ?, 1, ?, NULL, 1)", 0x1000 + shift, 0x3000 + shift);
            db.Execute("INSERT INTO strings VALUES (1, 1, ?, 'text')", 0x9000 + shift);
            db.Execute("INSERT INTO string_refs VALUES (1, ?), (1, ?)", 0x1000 + shift, 0x3000 + shift);
            return path;
        }
        using var before = Sql.OpenReadOnly(Make("before.sqlite", 0));
        using var after = Sql.OpenReadOnly(Make("after.sqlite", 0x5550));
        var one = new FingerprintReader(before, null).Read("Game.exe", 0x1000);
        var other = new FingerprintReader(after, null).Read("Game.exe", 0x1000 + 0x5550);
        Assert.Equal(one.ToJson().ToJsonString(), other.ToJson().ToJsonString());
        Assert.Equal(one.Token(), other.Token());
    }
}
