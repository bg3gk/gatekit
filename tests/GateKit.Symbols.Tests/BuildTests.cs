using GateKit.Symbols.Build;
using GateKit.Symbols.Pe;
using GateKit.Symbols.ScriptExtender;

namespace GateKit.Symbols.Tests;

public sealed class BuildTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "gk-symbols-" + Guid.NewGuid().ToString("N"));

    public BuildTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public void PeReaderSeesExportsImportsFunctionsAndReferences()
    {
        var pe = new PeImage(SyntheticPe.Build(0x180000000, 0, exports: true));
        Assert.Equal("x64", pe.Machine);
        var exports = pe.Exports().ToList();
        Assert.Equal(SyntheticPe.ExportNames, exports.Select(e => e.Name));
        Assert.Equal(SyntheticPe.B(0), exports[1].Rva);
        Assert.Equal([new PeImport("kernel32.dll", "Sleep", null)], pe.Imports().ToList());

        var ranges = pe.FunctionRanges();
        Assert.Contains(new FunctionRange(SyntheticPe.L(0), SyntheticPe.L(0) + 6, true), ranges);
        Assert.Equal(5, ranges.Count);
        Assert.Equal(SyntheticPe.A(0), pe.ChunkParents()[SyntheticPe.C(0)]);
        Assert.Equal(SyntheticPe.B(0), pe.Owner(SyntheticPe.B(0) + 3));

        var (texts, refs) = pe.StringRefs();
        Assert.Equal(SyntheticPe.TextConstant, texts[SyntheticPe.Text]);
        Assert.Equal([SyntheticPe.A(0)], refs[SyntheticPe.Text]);
        Assert.Contains(SyntheticPe.Vtable, pe.LeaTargets);

        var tables = pe.FindVtables(new HashSet<uint>());
        Assert.Equal([SyntheticPe.A(0), SyntheticPe.B(0)], tables[SyntheticPe.Vtable]);

        var (calls, _) = pe.Calls();
        Assert.Equal([Callee.Function(SyntheticPe.B(0)), Callee.Import("kernel32.dll", "Sleep")], calls[SyntheticPe.A(0)]);
    }

    [Theory]
    [InlineData(new byte[] { 0x48, 0x8B, 0x41, 0x38, 0xC3 }, "get", 0x38, 8, null)]
    [InlineData(new byte[] { 0x0F, 0xB6, 0x81, 0xC5, 0, 0, 0, 0xC3 }, "get", 0xC5, 1, null)]
    [InlineData(new byte[] { 0x8B, 0x81, 0xA8, 0, 0, 0, 0xD1, 0xE8, 0x24, 0x01, 0xC3 }, "get", 0xA8, 4, 1)]
    [InlineData(new byte[] { 0x48, 0x8D, 0x41, 0x38, 0xC3 }, "ref", 0x38, null, null)]
    [InlineData(new byte[] { 0x88, 0x91, 0xC5, 0, 0, 0, 0xC3 }, "set", 0xC5, 1, null)]
    public void AccessorBodiesDecode(byte[] code, string kind, int offset, int? width, int? bit) =>
        Assert.Equal((kind, offset, width, bit), Accessors.Decode(code));

    [Fact]
    public void AccessorRejectsOtherShapes()
    {
        Assert.Null(Accessors.Decode([0x49, 0x8B, 0x41, 0x38, 0xC3]));          // base r9
        Assert.Null(Accessors.Decode([0x48, 0x8B, 0x41, 0x38, 0x90, 0xC3]));    // not followed by ret
        Assert.Equal(("get", "Value"), Accessors.FieldName("GetValue"));
        Assert.Equal(("get", "IsHidden"), Accessors.FieldName("IsHidden"));
        Assert.Null(Accessors.FieldName("Update"));
        Assert.Equal("__int64", Accessors.ReturnType("public: __int64 ns::Foo::GetValue(void)const"));
    }

    [Fact]
    public void DescribeSplitsScopesAndKinds()
    {
        var d = SymbolBuilder.Describe("?GetValue@Foo@ns@@QEBA_JXZ");
        Assert.Equal(("ns::Foo::GetValue", "GetValue", "public", "method"), (d.FullName, d.Method, d.Access, d.Kind));
        Assert.Equal(["ns", "Foo"], d.Scopes);
        Assert.Equal("vftable", SymbolBuilder.Describe("??_7Foo@ns@@6B@").Kind);
        Assert.Equal("c", SymbolBuilder.Describe("plain_c_function").Kind);
        Assert.Equal(["a", "b<c::d>", "operator<"], SymbolBuilder.SplitScopes("a::b<c::d>::operator<"));
        Assert.Equal("query", SymbolBuilder.TypeKind("ecs::query::spec::Spec<x>"));
        Assert.Equal("system", SymbolBuilder.TypeKind("class ns::FooSystem"));
    }

    [Fact]
    public void VtableMatcherPairsTablesBySupport()
    {
        var a = Node.Function(1, 0x10);
        var b = Node.Function(1, 0x20);
        var tk = new Dictionary<long, List<Node>> { [1] = [a, b] };
        var game = new Dictionary<long, List<uint>> { [2] = [0x110, 0x120], [3] = [0x110, 0x130, 0x140] };
        var (tables, functions) = VtableMatcher.Match(tk, game, new Dictionary<Node, uint> { [a] = 0x110 });
        Assert.Equal((2L, 1), tables[1]);
        Assert.Equal(0x120u, functions[b].Game);
    }

    [Fact]
    public void LayoutsComputeOffsetsAndResynchronise()
    {
        var root = Path.Combine(dir, "se");
        var defs = Path.Combine(root, "BG3Extender", "GameDefinitions");
        Directory.CreateDirectory(Path.Combine(defs, "Enumerations"));
        File.WriteAllText(Path.Combine(defs, "Test.h"), """
            BEGIN_NS(test)
            struct Thing : public BaseComponent
            {
                DEFINE_COMPONENT(Thing, "test::ThingComponent")
                uint8_t Flag;
                FixedString Name;
                [[bg3::legacy(field_10)]] uint64_t Big;
                Kind What;
                Array<int32_t> Items;
            };
            END_NS()
            """);
        File.WriteAllText(Path.Combine(defs, "Enumerations", "Test.inl"), """
            BEGIN_ENUM(Kind, uint32_t)
                E(None)
                EV(Some, 0x10)
                E(More)
            END_ENUM()
            """);
        var types = SeLayouts.ReadHeaders(root);
        var t = Assert.Single(types);
        Assert.Equal(("component", "test::ThingComponent", "Thing"), (t.Kind, t.EngineClass, t.ExtName));
        Assert.Equal([0L, 4, 0x10, 0x18, 0x20], t.Fields.Select(f => f.Offset!.Value));
        Assert.Equal(2, t.Fields[2].Checked);          // field_10 re-synchronises after the 4-byte name at +4 (computed +8)
        var e = Assert.Single(SeLayouts.ReadEnums(root));
        Assert.Equal([("None", (long?)0), ("Some", 16), ("More", 17)], e.Values);
        Assert.Equal(16, SeLayouts.ParseInt("0x10"));
        Assert.Null(SeLayouts.ParseInt("1 << 3"));
    }

    [Fact]
    public void PatternsFindSymbolsAndConditions()
    {
        var root = Path.Combine(dir, "se2");
        Directory.CreateDirectory(Path.Combine(root, "BG3Extender", "GameHooks"));
        var a = SyntheticPe.A(0);
        File.WriteAllText(Path.Combine(root, SePatterns.MappingsPath), $"""
            <BinaryMappings><Mappings Version="1" Default="true">
              <Mapping Name="Run">
                @start 48 8D 05 ?? ?? ?? ?? // lea rax, [text]
                E8 ?? ?? ?? ??
                <Condition Type="String" Offset="@start" Value="{SyntheticPe.TextConstant}" />
                <Target Type="Absolute" Offset="@start" Symbol="ns__Foo__Run" />
                <Target Type="Indirect" Offset="7" Symbol="ns__Foo__GetValue" />
              </Mapping>
              <Mapping Name="Missing">C7 05 11 22 33 44</Mapping>
            </Mappings></BinaryMappings>
            """);
        var result = SePatterns.Scan(new PeImage(SyntheticPe.Build(0x140000000, 0, false)), root);
        Assert.Equal([("ns__Foo__Run", a), ("ns__Foo__GetValue", SyntheticPe.B(0))], result.Symbols.Select(s => (s.Name, s.Rva)));
        Assert.Equal("not found", result.Report[1].Status);
    }

    [Fact]
    public void BuildMatchesToolkitFunctionsInTheGame()
    {
        var toolkit = Path.Combine(dir, "toolkit");
        var game = Path.Combine(dir, "game");
        Directory.CreateDirectory(toolkit);
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        File.WriteAllBytes(Path.Combine(toolkit, "Engine.dll"), SyntheticPe.Build(0x180000000, 0, exports: true));
        File.WriteAllText(Path.Combine(toolkit, "readme.txt"), "not a binary");
        File.WriteAllBytes(Path.Combine(toolkit, "Broken.dll"), [1, 2, 3]);
        const uint shift = 0x100;
        File.WriteAllBytes(Path.Combine(game, "bin", "bg3.exe"), SyntheticPe.Build(0x140000000, shift, exports: false));
        var db = Path.Combine(dir, "out", "symbols.sqlite");
        var output = new StringWriter();
        SymbolBuilder.Run(new BuildOptions
        {
            Db = db, Toolkit = toolkit, Game = game, Bg3Se = null, NoPseudoCode = true, Jobs = 2,
            DecompDb = Path.Combine(dir, "none.sqlite"),
        }, output, new StringWriter());
        Assert.Contains("3 files, 5 exports", output.ToString());

        using var c = Sql.OpenReadOnly(db);
        Assert.Equal("not read: not an MZ file", c.Scalar("SELECT note FROM dlls WHERE name = 'Broken.dll'"));
        Assert.Equal("ns", c.Scalar("SELECT namespace FROM symbols WHERE method = 'GetValue'"));
        Assert.Equal("Foo", c.Scalar("SELECT class FROM symbols WHERE method = 'GetValue'"));
        Assert.Equal("public: __int64 ns::Foo::GetValue(void)const ", c.Scalar("SELECT signature FROM symbols WHERE method = 'GetValue'"));
        var matches = c.Query("SELECT toolkit_rva, game_rva, method, relation FROM matches ORDER BY toolkit_rva")
            .Select(r => (Convert.ToUInt32(r[0]), Convert.ToUInt32(r[1]), (string)r[2]!, (string?)r[3])).ToList();
        Assert.Equal(
        [
            (SyntheticPe.A(0), SyntheticPe.A(shift), "strings", "same"),
            (SyntheticPe.B(0), SyntheticPe.B(shift), "calls", "same"),
            (SyntheticPe.D(0), SyntheticPe.D(shift), "vtable user", "same"),
        ], matches);
        Assert.Equal(1L, c.Scalar("SELECT count(*) FROM vtab_matches"));
        Assert.Equal(19L, c.Scalar("SELECT f.size FROM functions f JOIN dlls d ON d.id = f.dll_id WHERE d.name = 'bg3.exe' AND f.rva = ?", SyntheticPe.A(shift)));
        Assert.Equal((long)SyntheticPe.A(shift), c.Scalar("SELECT parent FROM chunks c JOIN dlls d ON d.id = c.dll_id WHERE d.name = 'bg3.exe'"));
        var accessor = c.One("SELECT class, field, kind, offset, width, type, game_offset FROM accessors")!;
        Assert.Equal(new object?[] { "ns::Foo", "Value", "get", 0x38L, 8L, "__int64", 0x38L }, accessor);
        Assert.Equal(1L, c.Scalar("SELECT count(*) FROM xrefs WHERE kind = 'a' AND target_rva = ?", SyntheticPe.Vtable));
        Assert.Equal(2L, c.Scalar("SELECT count(*) FROM calls WHERE import = 'Sleep'"));
    }
}
