using System.Text;
using System.Text.Json;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>`gatekit decomp queries` on a toy work folder: a Windows build whose systems read
/// query globals and take component pointers from the columns of an iterator on the stack, and a
/// macOS build whose `SystemUpdate&lt;S&gt;` names the specs. Nothing here is the game's: the
/// functions are written for the test in the decompiler's style.</summary>
public sealed class QueriesTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("queries");
    readonly DecompSettings settings;
    readonly string coverage;

    const string SpecA = "ecs::query::spec::Spec<ls::TypeList<toy::HealthComponent, toy::ArmorComponent>, ls::TypeList<>, ls::TypeList<toy::TagComponent const>, ecs::QueryTypePersistentTag, ecs::QueryTypeAliveTag>";
    const string SpecB = "ecs::query::spec::Spec<ls::TypeList<toy::ArmorComponent>, ls::TypeList<>, ls::TypeList<>, ecs::QueryTypePersistentTag, ecs::QueryTypeAliveTag>";

    static string Index(string spec) => $"ls::TypeId<{spec}, ecs::QueryRegistry>::m_TypeIndex";

    /// <summary>The symbol table of the toy macOS build: one index by name, one behind a pointer slot.</summary>
    static (Dictionary<long, string>, Dictionary<long, string>)? Names(DecompSettings settings, string binary, Action<string> log) =>
        (new Dictionary<long, string> { [0x9000] = Index(SpecA), [0x9008] = Index(SpecB), [0x9010] = "ls::TypeId<toy::HealthComponent, ecs::ComponentTypeIdContext>::m_TypeIndex" },
         new Dictionary<long, string> { [0x100008000] = Index(SpecB) });

    /// <summary>An update in the decompiler's style: it takes the description of each query
    /// (<paramref name="globals"/>), builds an iterator on the stack and reads its columns.</summary>
    static string Update(string name, string[] globals, string loop) =>
        $"void {name}(longlong param_1,longlong *param_2)\n\n{{\n  longlong lVar1;\n  longlong lVar2;\n  longlong *plVar3;\n  longlong local_a8 [3];\n  undefined1 local_90 [8];\n"
        + "  longlong *local_88;\n  longlong local_48;\n  int local_38;\n  \n"
        + string.Concat(globals.Select(g => $"  local_a8[0] = (longlong){g} * 0x58 + *(longlong *)param_2[3];\n  FUN_140009000(local_90,*param_2,local_a8);\n"))
        + loop + "  return;\n}\n";

    public QueriesTests()
    {
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        coverage = Path.Combine(root, "coverage");
        Directory.CreateDirectory(Path.Combine(coverage, "layouts"));
        File.WriteAllText(Path.Combine(coverage, "coverage.json"),
            """{"components":[{"name":"toy::HealthComponent","inlineSize":24},{"name":"toy::ArmorComponent","inlineSize":32},{"name":"toy::TagComponent","inlineSize":1}]}""", new UTF8Encoding(false));
        settings = new DecompSettings(work, Path.Combine(root, "tools"), Path.Combine(root, "game"), Path.Combine(root, "toolkit"), Path.Combine(root, "symbols.sqlite"), null);
        using var db = DecompStore.OpenWrite(settings.DbPath);
        db.Execute("INSERT INTO binaries (id, name) VALUES (2, 'Game.exe'), (3, 'Mac')");
        long id = 0;
        void Add(long binary, long rva, string name, string code)
        {
            db.Execute("INSERT INTO functions (id, binary_id, rva, name, size, thunk) VALUES (?, ?, ?, ?, 100, 0)", ++id, binary, rva, name);
            db.Execute("INSERT INTO code VALUES (?, ?)", id, DecompStore.Compress(code));
            db.Execute("INSERT INTO code_fts (rowid, name, code) VALUES (?, ?, ?)", id, name, code);
        }
        // the registration of queries A (two components every entity has, one optional) and B
        Add(2, 0x8000, "FUN_140008000", "void FUN_140008000(longlong *param_1)\n\n{\n  undefined8 local_78;\n  ulonglong *local_38;\n  \n"
            + "  FUN_14000a000(&local_78,2,0,0,0,0,0,0,1,0,0);\n"
            + "  *(ushort *)local_38 = TypeIndex<toy::HealthComponent>_ComponentTypeIdContext & 0x7fff;\n"
            + "  *(ushort *)((longlong)local_38 + 2) =\n       TypeIndex<toy::ArmorComponent>_ComponentTypeIdContext & 0x7fff;\n"
            + "  *(ushort *)((longlong)local_38 + 4) = TypeIndex<toy::TagComponent>_ComponentTypeIdContext & 0x7fff;\n"
            + "  FUN_14000b000((longlong)DAT_140100010 * 0x58 + *param_1,&local_78);\n"
            + "  FUN_14000a000(&local_78,1,0,0,0,0,0,0,0,0,0);\n"
            + "  *(ushort *)local_38 = TypeIndex<toy::ArmorComponent>_ComponentTypeIdContext & 0x7fff;\n"
            + "  FUN_14000b000((longlong)DAT_140100020 * 0x58 + *param_1,&local_78);\n  return;\n}\n");
        // toy::TickSystem: the entry hands on to the update, which walks query A (two columns) and then query B
        Add(2, 0x1000, "FUN_140001000", "void FUN_140001000(longlong param_1,longlong *param_2)\n\n{\n  FUN_140002000(param_1,param_2);\n  return;\n}\n");
        Add(2, 0x2000, "FUN_140002000", Update("FUN_140002000", ["DAT_140100010", "DAT_140100020"],
            "  lVar1 = (longlong)local_38 * 0x18 + *(longlong *)*local_88;\n  lVar2 = (longlong)local_38 * 0x20 + *(longlong *)local_88[1];\n"
            + "  plVar3 = (longlong *)(local_48 + (longlong)local_38 * 8);\n  lVar2 = (longlong)local_38 * 0x30 + lVar1;\n"));
        // toy::OtherSystem: one function, query B only
        Add(2, 0x3000, "FUN_140003000", Update("FUN_140003000", ["DAT_140100020"], "  lVar1 = (longlong)local_38 * 0x20 + *(longlong *)*local_88;\n"));
        // toy::LostSystem reads a query the macOS build shows no spec for
        Add(2, 0x4000, "FUN_140004000", Update("FUN_140004000", ["DAT_140100030"], "  lVar1 = (longlong)local_38 * 0x18 + *(longlong *)*local_88;\n"));
        db.Execute("""
            INSERT INTO system_updates VALUES (2, 'toy::TickSystem', 4096, 8192, ''), (2, 'toy::OtherSystem', 12288, 12288, ''), (2, 'toy::LostSystem', 16384, 16384, '');
            """);
        const string head = "ecs::_private::SystemRegistrationHelper::SystemUpdate<";
        Add(3, 0x5000, head + "toy::TickSystem>", "void SystemUpdate<toy::TickSystem>(long param_1)\n\n{\n  toy::TickSystem::Update(param_1);\n  return;\n}\n");
        Add(3, 0x5800, "toy::TickSystem::Update", "void toy::TickSystem::Update(long param_1)\n\n{\n  lVar1 = (ulong)ls::\n          TypeId<ecs::query::spec::Spec<ls::TypeList<toy::HealthComponent,toy::ArmorComponent>,ls::TypeList<>,ls::TypeList<toy::TagComponent_const>,ecs::QueryTypePersistentTag,ecs::QueryTypeAliveTag>,ecs::QueryRegistry>\n"
            + "          ::m_TypeIndex * 0x58 + *(long *)(param_1 + 0x198);\n  uVar2 = *(uint *)PTR_m_TypeIndex_100008000;\n  return;\n}\n");
        Add(3, 0x6000, head + "toy::OtherSystem>", "void SystemUpdate<toy::OtherSystem>(long param_1)\n\n{\n  uVar2 = *(uint *)PTR_m_TypeIndex_100008000;\n  return;\n}\n");
        Add(3, 0x7000, head + "toy::LostSystem>", "void SystemUpdate<toy::LostSystem>(long param_1)\n\n{\n  return;\n}\n");
        db.Execute("INSERT INTO calls VALUES (2, 4096, 8192), (3, 20480, 22528)");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    (int Exit, string Out, string Err) Run(Func<DecompSession, int> body)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        using var s = new DecompSession(settings, output, error) { IndexNames = Names };
        int exit = body(s);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void ASpecIsReadIntoItsLists()
    {
        var spec = QuerySpec.Parse(SpecA)!;
        Assert.Equal([["toy::HealthComponent", "toy::ArmorComponent"], [], ["toy::TagComponent"]], spec.Lists);
        Assert.Equal(["ecs::QueryTypePersistentTag", "ecs::QueryTypeAliveTag"], spec.Tags);
        Assert.Equal(SpecA, QuerySpec.OfSymbol(Index(SpecA)));
        Assert.Null(QuerySpec.OfSymbol("ls::TypeId<toy::HealthComponent, ecs::ComponentTypeIdContext>::m_TypeIndex"));
        Assert.Null(QuerySpec.Parse("toy::HealthComponent"));
    }

    [Fact]
    public void AStrideIsCountedInBytes()
    {
        var values = QueryShapes.Strides("void f(longlong param_1)\n\n{\n  longlong lVar1;\n  longlong *plVar2;\n  Thing *pTVar3;\n  int local_38;\n  \n"
            + "  lVar1 = (longlong)local_38 * 0x18 + *(longlong *)*local_88;\n"        // bytes
            + "  plVar2 = plVar2 + (longlong)local_38 * 2;\n"                          // a pointer to 8 bytes: 16
            + "  lVar1 = (longlong)pTVar3 + (longlong)local_38 * 4;\n"                 // cast to an integer: bytes
            + "  lVar1 = pTVar3 + (longlong)local_38 * 4;\n"                           // a structure: unknown
            + "  lVar1 = (longlong)local_38 * 0x18 + lVar1 + 0x10;\n"                  // a field's address
            + "  lVar1 = lVar1 * 3;\n  lVar1 = lVar1 + 8;\n}\n");
        Assert.Equal([(0x18L, false), (16L, false), (4L, false), (-1L, false), (0x18L, true)], values.Select(v => (v.Stride, v.Offset)).ToList());
        Assert.Equal("*(longlong *)*S", QueryShapes.Form(values[0].Base));
        Assert.True(QueryShapes.IsGlobal("(longlong)DAT_140100010"));
        Assert.False(QueryShapes.IsGlobal("(longlong)local_38"));
    }

    [Fact]
    public void TheSurveyCountsGlobalsSpecsAndStrides()
    {
        var (exit, output, error) = Run(s => QueriesCommand.Survey(s, "Game.exe", "Mac", coverage, null, true, 5));
        Assert.True(exit == 0, error);
        using var doc = JsonDocument.Parse(output);
        var systems = doc.RootElement.GetProperty("systems");
        Assert.Equal(3, systems.GetProperty("total").GetInt32());
        Assert.Equal(3, systems.GetProperty("with_query_globals").GetInt32());
        Assert.Equal(2, systems.GetProperty("with_specs").GetInt32());
        Assert.Equal(2, systems.GetProperty("counts_agree").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("query_globals").GetProperty("distinct").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("specs").GetProperty("named").GetInt32());
        var classes = doc.RootElement.GetProperty("strides").GetProperty("classes");
        // TickSystem: 0x18 is Health, 0x20 is Armor (but that local takes 0x30 too), 8 is no component; OtherSystem: 0x20 is Armor
        Assert.Equal(2, classes.GetProperty("one type of that size").GetInt32());
        Assert.Equal(1, classes.GetProperty("one type of that size, but the local takes another stride too").GetInt32());
        Assert.Equal(1, classes.GetProperty("stride 8 and no type of that size (a table of pointers or handles)").GetInt32());
        Assert.Equal(1, classes.GetProperty("no type of that size").GetInt32());
        Assert.Equal(1, classes.GetProperty("the system's macOS entry names no spec").GetInt32());
        Assert.Equal(4, classes.GetProperty("the query's own description (global * 0x58)").GetInt32());
        var tick = doc.RootElement.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("system").GetString() == "toy::TickSystem");
        Assert.Equal(["100010", "100020"], tick.GetProperty("query_globals").EnumerateArray().Select(g => g.GetString()).ToList());
        Assert.Equal([SpecA, SpecB], tick.GetProperty("specs").EnumerateArray().Select(g => g.GetString()).ToList());

        var (exit2, text, _) = Run(s => QueriesCommand.Survey(s, "Game.exe", "Mac", coverage, "Tick", false, 0));
        Assert.Equal(0, exit2);
        Assert.Contains("with both, and as many globals as specs                1 of 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegistrationIsReadPerQuery()
    {
        var found = QueryShapes.Registrations(QueryShapes.Flat(
            "  FUN_14000a000(&local_78,1,0,0,0,0,0,0,1,0,0);\n  *(ushort *)local_38 = TypeIndex<toy::A>_ComponentTypeIdContext & 0x7fff;\n"
            + "  *(ushort *)((longlong)local_38 + 2) = TypeIndex<toy::B>_ComponentTypeIdContext & 0x7fff;\n  f((longlong)DAT_140100010 * 0x58 + *param_1);\n"
            // a use, not a registration: nothing was stored before it
            + "  x = (longlong)DAT_140100040 * 0x58 + *param_1;\n"
            // the counts do not add up to what was stored: read, and not whole
            + "  FUN_14000a000(&local_78,2,0,0,0,0,0,0,0,0,0);\n  *(ushort *)local_38 = TypeIndex<toy::C>_ComponentTypeIdContext & 0x7fff;\n  f((longlong)DAT_140100020 * 0x58 + *param_1);\n"), 0x140000000);
        Assert.Equal([0x100010L, 0x100020L], found.Select(r => r.Global).ToList());
        Assert.True(found[0].Whole);
        Assert.Equal(["toy::A"], found[0].Group(0));
        Assert.Equal(["toy::B"], found[0].Group(7));
        Assert.Empty(found[0].Group(3));
        Assert.False(found[1].Whole);
    }

    static SystemQueries System(string name, long[] globals, string[] specs) =>
        new() { System = name, Globals = [.. globals.Select(g => (0L, g))], Specs = [.. specs] };

    [Fact]
    public void AGlobalKeepsItsSpecOnlyWhenEverySystemAgrees()
    {
        var reading = new QueryReading
        {
            Systems =
            [
                System("toy::One", [1, 2], ["A", "B"]), System("toy::Two", [2, 3], ["B", "C"]),
                // says 3 is D, against toy::Two
                System("toy::Three", [3], ["D"]),
                // counts differ: 9 stands before the anchor 2 on one side only, 4 after it on both
                System("toy::Four", [9, 2, 4], ["B", "E"]),
            ],
        };
        var byPosition = QueryMap.Map(reading, 0);
        Assert.Equal([(1L, "A", 1), (2L, "B", 2)], byPosition.Kept.Select(g => (g.Rva, g.Spec, g.Systems)).ToList());
        Assert.Equal([3L], byPosition.Disagree.Select(d => d.Rva).ToList());
        Assert.Equal((2, 1), (byPosition.Checked, byPosition.CheckedAgree));
        Assert.Equal((4, 3), (byPosition.Statements, byPosition.StatementsAgree));

        var anchored = QueryMap.Map(reading, 4);
        Assert.Equal([(1L, "A", 0), (2L, "B", 0), (4L, "E", 1)], anchored.Kept.Select(g => (g.Rva, g.Spec, g.Round)).ToList());
        Assert.Equal(3, anchored.Kept.Single(g => g.Rva == 2).Systems);

        // the Windows build's own registration is the check: other types than the spec's, and the global is dropped
        reading.Specs["A"] = new QuerySpec("A", [["toy::HealthComponent"]], []);
        reading.Specs["B"] = new QuerySpec("B", [["toy::ArmorComponent"], ["toy::TagComponent"]], []);
        reading.Registrations[1] = [new QueryRegistration(1, null, ["toy::ArmorComponent"])];
        reading.Registrations[2] = [new QueryRegistration(2, null, ["toy::TagComponent", "toy::ArmorComponent", "toy::ArmorComponent"])];
        var check = QueryMap.Map(reading, 0);
        Assert.Equal([2L], check.Kept.Select(g => g.Rva).ToList());
        Assert.Equal([1L], check.RegistrationDiffers.Select(g => g.Rva).ToList());
        Assert.Equal([1, 0], check.RegisteredSeveral);
        Assert.Equal([0, 1], check.RegisteredOne);
    }

    [Fact]
    public void TheMapIsStoredWithWhatEachQueryRegisters()
    {
        var (exit, output, error) = Run(s => QueriesCommand.Map(s, "Game.exe", "Mac", true, false, 4, 5, 0));
        Assert.True(exit == 0, error);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(2, doc.RootElement.GetProperty("kept").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("checked_agree").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("registrations").GetProperty("tied_by_several_systems").GetProperty("same_types").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("registrations").GetProperty("tied_by_one_system").GetProperty("same_types").GetInt32());
        using var db = Sql.OpenReadOnly(settings.DbPath);
        var rows = db.Query("SELECT rva, spec, systems, registered, counts FROM query_globals ORDER BY rva").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal((0x100010L, SpecA, 1L), ((long)rows[0][0]!, (string)rows[0][1]!, (long)rows[0][2]!));
        Assert.Equal("""[["toy::HealthComponent","toy::ArmorComponent"],[],[],[],[],[],[],["toy::TagComponent"],[],[]]""", rows[0][3]);
        Assert.Equal("1,0,0,0,0,0,0,0,0,0", rows[1][4]);

        // --dry-run stores nothing: the table is as it was
        var (exit2, _, _) = Run(s => QueriesCommand.Map(s, "Game.exe", "Mac", false, true, 0, 5, 0));
        Assert.Equal(0, exit2);
    }

    [Fact]
    public void AValueIsTypedOnlyWhenItsStrideIsTheSizeOfOneColumnsType()
    {
        var (exit, output, error) = Run(s => QueriesCommand.Columns(s, "Game.exe", "Mac", coverage, true, 10));
        Assert.True(exit == 0, error);
        using var doc = JsonDocument.Parse(output);
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => (c.GetProperty("function").GetString(), c.GetProperty("column").GetInt32(), c.GetProperty("outcome").GetString(), c.GetProperty("type").GetString()))
            .OrderBy(c => c.Item1, StringComparer.Ordinal).ThenBy(c => c.Item2).ToList();
        Assert.Equal(
        [
            // TickSystem walks A and B: column 0 of 24 bytes can only be A's Health, column 1 of 32 only A's Armor
            ("2000", 0, "typed", "toy::HealthComponent"), ("2000", 1, "typed", "toy::ArmorComponent"),
            ("3000", 0, "typed", "toy::ArmorComponent"),
            // LostSystem's query has no registration: nothing says what its column holds
            ("4000", 0, "no registration of the system's queries was read", null),
        ], cases);
        var check = doc.RootElement.GetProperty("stride_check");
        Assert.Equal((1, 1), (check.GetProperty("one_query").GetProperty("cases").GetInt32(), check.GetProperty("one_query").GetProperty("agree").GetInt32()));
        Assert.Equal((3, 3), (check.GetProperty("any_query").GetProperty("cases").GetInt32(), check.GetProperty("any_query").GetProperty("agree").GetInt32()));
        // the pointer local stands 0x50 above the index local: the components every entity has
        var place = doc.RootElement.GetProperty("places").EnumerateArray().First(p => p.GetProperty("form").GetString() == "*(longlong *)*S");
        Assert.Equal(0x50, place.GetProperty("distance").GetInt32());

        Run(s =>
        {
            var reading = QueryReader.Read(s, "Game.exe", "Mac", _ => { });
            var typing = QueryTyping.Decide(s, reading, QueryReader.LedgerSizes(coverage));
            Assert.Equal("2000\t88\t0\t24\ttoy::HealthComponent\t100010\t0\n2000\t88\t1\t32\ttoy::ArmorComponent\t100010\t0\n3000\t88\t0\t32\ttoy::ArmorComponent\t100020\t0\n", QueryTyping.Lines(typing));
            return 0;
        });
    }

    [Fact]
    public void TheGuardsThatNameNothingAreCountedAgainstWhatTheTypingReaches()
    {
        var digests = Path.Combine(root, "digests", "toy");
        Directory.CreateDirectory(digests);
        File.WriteAllText(Path.Combine(digests, "TickSystem.json"), """
            {"system":"toy::TickSystem","binaries":{"Game.exe":{"operations":[
              {"kind":"get","type":"toy::HealthComponent","function":"0x2000","depth":1,"guard":{"status":"extracted","terms":["has(toy::HealthComponent)"]},"call_guard":null},
              {"kind":"get","type":"toy::ArmorComponent","function":"0x2000","depth":1,"guard":{"status":"extracted","terms":["[lVar1+0x10] != 0"]},"call_guard":null},
              {"kind":"add","type":"toy::TagComponent","function":"0x9000","depth":2,"guard":{"status":"extracted","terms":["lVar4 != 0"]},"call_guard":{"status":"always"}},
              {"kind":"add","type":"toy::TagComponent","function":"0x9000","depth":2,"guard":{"status":"always"},"call_guard":null}
            ]},"Other.exe":{"operations":[{"kind":"get","type":"x","function":"0x1","depth":1,"guard":{"status":"always"}}]}}}
            """, new UTF8Encoding(false));
        var (exit, output, error) = Run(s => QueriesCommand.Guards(s, "Game.exe", "Mac", coverage, Path.Combine(root, "digests"), true));
        Assert.True(exit == 0, error);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal((4, 1), (doc.RootElement.GetProperty("operations").GetInt32(), doc.RootElement.GetProperty("naming").GetInt32()));
        Assert.Equal([1, 2], doc.RootElement.GetProperty("not_naming_by_depth").EnumerateArray().Select(n => n.GetInt32()).ToList());
        Assert.Equal([1, 0], doc.RootElement.GetProperty("in_a_function_with_a_typed_value").EnumerateArray().Select(n => n.GetInt32()).ToList());
        Assert.Equal([1, 2], doc.RootElement.GetProperty("in_a_system_with_a_typed_value").EnumerateArray().Select(n => n.GetInt32()).ToList());
        Assert.Equal(1, doc.RootElement.GetProperty("with_a_raw_dereference_in_a_function_with_a_typed_value").GetInt32());

        var (missing, _, _) = RunCaught(s => QueriesCommand.Guards(s, "Game.exe", "Mac", coverage, Path.Combine(root, "nowhere"), false));
        Assert.Equal(1, missing);
    }

    (int Exit, string Out, string Err) RunCaught(Func<DecompSession, int> body)
    {
        try
        {
            return Run(body);
        }
        catch (SymbolsException e)
        {
            return (1, "", e.Message);
        }
    }
}
