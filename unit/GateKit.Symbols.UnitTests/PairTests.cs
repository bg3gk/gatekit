using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>`gatekit decomp pair` on two toy builds: a source whose functions are named and a
/// target that knows a few of the names. Functions are paired from the labels both carry, then
/// along the calls, and nothing below the measured bar becomes a name.</summary>
public sealed class PairTests
{
    static PairSide Side(string binary, long id, (long Rva, string Name)[] functions, (long Caller, long Callee)[] calls, params (long Rva, string Label)[] labels)
    {
        var side = new PairSide { Binary = binary, BinaryId = id };
        foreach (var (rva, name) in functions)
        {
            side.ByRva[rva] = new PairFunction(id * 1000 + rva, rva, name, 100);
            side.RvaOfId[id * 1000 + rva] = rva;
        }
        foreach (var (caller, callee) in calls)
        {
            if (!side.Callees.TryGetValue(caller, out var a))
                side.Callees[caller] = a = [];
            a.Add(callee);
            if (!side.Callers.TryGetValue(callee, out var b))
                side.Callers[callee] = b = [];
            b.Add(caller);
        }
        foreach (var (rva, label) in labels)
            side.Labels[rva] = label;
        return side;
    }

    /// <summary>Source: Update calls Step1, Step2 and a helper; Step1 and Step2 are told apart only
    /// by their numbers. The target knows Update's name and has the same shape.</summary>
    static (PairSide Source, PairSide Target, Dictionary<long, long[]> Features) Toy()
    {
        var source = Side("Mac", 1,
            [(10, "toy::Sys::Update"), (20, "toy::Sys::Step1"), (30, "toy::Sys::Step2"), (40, "toy::Get"), (50, "toy::Lonely")],
            [(10, 20), (10, 30), (20, 40), (30, 40)],
            (10, "toy::Sys::Update"), (20, "toy::Sys::Step1"), (30, "toy::Sys::Step2"), (40, "toy::Get"), (50, "toy::Lonely"));
        var target = Side("Game.exe", 2,
            [(110, "toy::Sys::Update"), (120, "FUN_120"), (130, "FUN_130"), (140, "toy::Get"), (150, "FUN_150")],
            [(110, 120), (110, 130), (120, 140), (130, 140)],
            (110, "toy::Sys::Update"), (140, "toy::Get"));
        var features = new Dictionary<long, long[]>
        {
            [1020] = [1, 2, 3, 4], [1030] = [5, 6, 7, 8], [1050] = [70, 71, 72],
            [2120] = [1, 2, 3, 4], [2130] = [5, 6, 7, 9], [2150] = [80, 81, 82],
        };
        return (source, target, features);
    }

    [Fact]
    public void ALabelOneFunctionOfEachBuildCarriesIsASeed()
    {
        var (source, target, features) = Toy();
        var result = PairCommand.Pair(source, target, features, (_, _) => null, 0, 0.98, 4, new PairCommand.RoundBars(), 1, _ => { });
        var seeds = result.Pairs.Where(p => p.State == "seed").Select(p => (p.Source, p.Target)).ToList();
        Assert.Equal([(10L, 110L), (40L, 140L)], seeds);
        // nothing was held out, so no bar was measured: the pairs found along the calls stay candidates
        Assert.False(result.Calibrated);
        var candidates = result.Pairs.Where(p => p.State == "candidate").Select(p => (p.Source, p.Target)).ToList();
        Assert.Equal([(20L, 120L), (30L, 130L)], candidates);
        Assert.DoesNotContain(result.Pairs, p => p.State == "accepted");
        // the lonely function shares nothing with anything
        Assert.DoesNotContain(result.Pairs, p => p.Target == 150);
    }

    [Fact]
    public void EvidenceSaysWhatThePairShares()
    {
        var (source, target, features) = Toy();
        var result = PairCommand.Pair(source, target, features, (_, _) => null, 0, 0.98, 4, new PairCommand.RoundBars(), 1, _ => { });
        var step1 = result.Pairs.Single(p => p.Target == 120);
        Assert.Contains("calls 1 labelled alike (toy::Get)", step1.Evidence);
        Assert.Contains("called by 1 labelled alike (toy::Sys::Update)", step1.Evidence);
        Assert.Contains("4 texts and numbers shared of 4 and 4", step1.Evidence);
        Assert.Equal("the label toy::Sys::Update", result.Pairs.Single(p => p.Target == 110).Evidence);
    }

    [Fact]
    public void TheBarIsReadOffTheHeldOutNames()
    {
        // every name held out: the target's two names must be found again from the rest
        var (source, target, features) = Toy();
        target.ByRva[120] = target.ByRva[120] with { Name = "toy::Sys::Step1" };
        target.Labels[120] = "toy::Sys::Step1";
        var result = PairCommand.Pair(source, target, features, (_, _) => null, 1.0, 0.98, 4, new PairCommand.RoundBars(), 1, _ => { }, minChecked: 1);
        Assert.Equal(3, result.Held);
        Assert.True(result.Calibrated);
        Assert.Equal(0, result.HeldDiffer);
        Assert.True(result.HeldAgree >= 1);
        // in the run with every name the unnamed Step2 is judged by that bar
        var step2 = result.Pairs.Single(p => p.Target == 130);
        Assert.Equal(30, step2.Source);
        Assert.Equal(step2.Score >= result.MinScore && step2.Margin >= result.MinMargin ? "accepted" : "candidate", step2.State);
    }

    [Fact]
    public void CalibrationTakesTheWidestBarThatIsPreciseEnough()
    {
        var held = new List<(double, double, bool)>();
        for (int i = 0; i < 100; i++)
            held.Add((0.9, 0.5, true));
        for (int i = 0; i < 50; i++)
            held.Add((0.5, 0.2, i % 2 == 0));          // half wrong below 0.9
        var bar = PairCommand.Calibrate(held, 0.98, 30);
        Assert.NotNull(bar);
        Assert.True(bar.Value.Score > 0.5 || bar.Value.Margin > 0.2);
        Assert.Equal((100, 0), (bar.Value.Agree, bar.Value.Differ));
        // too few to judge, or never precise enough: no bar
        Assert.Null(PairCommand.Calibrate(held.Take(10), 0.98, 30));
        Assert.Null(PairCommand.Calibrate(held.Skip(100), 0.98, 10));
    }

    [Fact]
    public void NamesAreSpelledAlikeAcrossDemanglers()
    {
        Assert.Equal(PairCommand.Normalize("ls::GetTypeName<struct ecs::Foo const>"), PairCommand.Normalize("ls::GetTypeName<ecs::Foo_const>"));
        Assert.Equal(PairCommand.Normalize("a::B<class a::C,1>::f"), PairCommand.Normalize("a::B<a::C,1>::f"));
        Assert.NotEqual(PairCommand.Normalize("a::f"), PairCommand.Normalize("a::g"));
    }

    [Fact]
    public void TheSameFunctionsAreHeldOutEveryRun()
    {
        var held = Enumerable.Range(0, 10000).Select(i => PairCommand.HeldOut(0x1000 + i * 16, 0.4)).ToList();
        Assert.Equal(held, Enumerable.Range(0, 10000).Select(i => PairCommand.HeldOut(0x1000 + i * 16, 0.4)).ToList());
        Assert.InRange(held.Count(h => h), 3500, 4500);
        Assert.All(Enumerable.Range(0, 100), i => Assert.False(PairCommand.HeldOut(i * 16, 0)));
    }

    [Fact]
    public void CalleesAreOrderedByTheirFirstMention()
    {
        var side = Side("Game.exe", 2, [(1, "FUN_1"), (2, "ns::A::Get"), (3, "FUN_140000030"), (4, "ns::B<int>::Set"), (5, "ns::C::Get"), (6, "ns::Unseen")],
            [(1, 2), (1, 3), (1, 4), (1, 5), (1, 6)]);
        // Set is mentioned first; the two Get share a short name and are left out; Unseen is not in the text;
        // TrySet is another name, not a mention of Set
        const string code = "void FUN_1(void)\n{\n  TrySet(1);\n  B<int>::\n  Set(2);\n  FUN_140000030();\n  Get();\n  Set(3);\n}\n";
        Assert.Equal([4L, 3L], PairCommand.CallOrder(side, 1, code));
        Assert.Empty(PairCommand.CallOrder(side, 1, null));
        Assert.Equal("Set", PairCommand.Short("ns::B<x::y>::Set"));
        Assert.Equal("f<a::b>", PairCommand.Short("ns::f<a::b>"));
    }

    [Fact]
    public void TheCommonOrderIsTheLongest()
    {
        var common = PairCommand.CommonOrder([1, 2, 3, 4, 5], [2, 1, 3, 5, 4]);
        Assert.Equal(3, common.Count);
        Assert.All(common, c => Assert.Equal(new long[] { 1, 2, 3, 4, 5 }[c.A], new long[] { 2, 1, 3, 5, 4 }[c.B]));
        Assert.Empty(PairCommand.CommonOrder([1, 2], [3, 4]));
    }

    /// <summary>A paired caller calls Get, then one function nothing else tells apart, then Tail.</summary>
    static (PairSide Source, PairSide Target, Func<PairSide, long, string?> Code) Chain()
    {
        var source = Side("Mac", 1, [(10, "toy::Update"), (20, "toy::Get"), (30, "toy::Middle"), (40, "toy::Tail"), (50, "toy::Other")],
            [(10, 20), (10, 30), (10, 40)],
            (10, "toy::Update"), (20, "toy::Get"), (30, "toy::Middle"), (40, "toy::Tail"), (50, "toy::Other"));
        var target = Side("Game.exe", 2, [(110, "toy::Update"), (120, "toy::Get"), (130, "FUN_130"), (140, "toy::Tail")],
            [(110, 120), (110, 130), (110, 140)],
            (110, "toy::Update"), (120, "toy::Get"), (140, "toy::Tail"));
        string? Code(PairSide side, long rva) => (side.Binary, rva) switch
        {
            ("Mac", 10) => "void Update(void)\n{\n  Get();\n  Middle();\n  Tail();\n}\n",
            ("Game.exe", 110) => "void Update(void)\n{\n  Get();\n  FUN_130();\n  Tail();\n}\n",
            _ => null,
        };
        return (source, target, Code);
    }

    [Fact]
    public void TheOneUnpairedCallBetweenTheSamePairedCallsIsTheSameFunction()
    {
        var (source, target, code) = Chain();
        var result = PairCommand.Pair(source, target, new Dictionary<long, long[]>(), code, 0, 0.98, 4, new PairCommand.RoundBars(), 1, _ => { });
        var middle = result.Pairs.Single(p => p.Target == 130);
        Assert.Equal((30L, "calls", 1), (middle.Source, middle.Rule, middle.Votes));
        Assert.Equal("candidate", middle.State);          // no bar was measured
        Assert.StartsWith("the one unpaired call between the same lined-up calls in 1 paired caller or matched table (+0x6E)", middle.Evidence);
    }

    [Fact]
    public void TwoUnpairedCallsAgainstOneInAStretchSayNothing()
    {
        var (source, target, code) = Chain();
        source.ByRva[35] = new PairFunction(1035, 35, "toy::Second", 100);
        source.Labels[35] = "toy::Second";
        source.Callees[10].Add(35);
        string? Two(PairSide side, long rva) => side.Binary == "Mac" ? "void Update(void)\n{\n  Get();\n  Middle();\n  Second();\n  Tail();\n}\n" : code(side, rva);
        var result = PairCommand.Pair(source, target, new Dictionary<long, long[]>(), Two, 0, 0.98, 4, new PairCommand.RoundBars(), 1, _ => { });
        Assert.DoesNotContain(result.Pairs, p => p.Target == 130);
    }

    [Fact]
    public void StoredPairsNameTheNamelessAndPassHelperFamiliesOn()
    {
        var root = GateKit.Testing.Scratch.NewPath("pair");
        Directory.CreateDirectory(root);
        try
        {
            var (source, target, code) = Chain();
            source.Binary = "bg3_macos_x64";
            string? Renamed(PairSide side, long rva) => code(side.Binary == "bg3_macos_x64" ? new PairSide { Binary = "Mac" } : side, rva);
            var result = PairCommand.Pair(source, target, new Dictionary<long, long[]>(), Renamed, 0, 0.98, 4, new PairCommand.RoundBars(), 1, _ => { });
            // the pair of the middle function, as a measured bar would have accepted it
            result.Pairs = [.. result.Pairs.Select(p => p.Target == 130 ? p with { State = "accepted" } : p)];
            using (var db = DecompStore.OpenWrite(Path.Combine(root, "decomp.sqlite")))
            {
                db.Execute("INSERT INTO binaries (id, name) VALUES (1, 'bg3_macos_x64'), (2, 'Game.exe')");
                foreach (var side in new[] { source, target })
                    foreach (var f in side.ByRva.Values)
                        db.Execute("INSERT INTO functions (id, binary_id, rva, name) VALUES (?, ?, ?, ?)", f.Id, side.BinaryId, f.Rva, f.Name);
                PairCommand.Store(db, source, target, result);
                var names = PairCommand.Names(db, "Game.exe");
                // only the nameless function takes a name; the seeds have their own
                Assert.Equal([130L], names.Keys.ToList());
                Assert.Equal("toy::Middle", names[130].Name);
                Assert.StartsWith("macos-anchor: bg3_macos_x64+0x1E, accepted by calls", names[130].Basis);
                // the other way round the same pairs are the source's partners
                Assert.Equal(4, PairCommand.Partners(db, "bg3_macos_x64").Count);

                var lines = RefreshCommand.PairLines(db, "Game.exe", new HashSet<long>(), new HashSet<long>(), out int count);
                Assert.Equal(1, count);
                Assert.StartsWith("82\tfunction\ttoy::Middle\t\tmacos-anchor: ", lines);
                Assert.Equal("", RefreshCommand.PairLines(db, "Game.exe", new HashSet<long> { 130 }, new HashSet<long>(), out _));
                Assert.Equal("", RefreshCommand.PairLines(db, "Game.exe", new HashSet<long>(), new HashSet<long> { 130 }, out _));

                // a helper no rule recognised takes the family of the helper it is paired with, when the type is the same
                HelpersCommand.Store(db, "bg3_macos_x64", [new EcsHelper(30, "get", "toy::Health", "component", "high", "a rule"), new EcsHelper(20, "has", "toy::Mana", "bool", "high", "a rule")]);
                var helpers = new List<EcsHelper> { new(130, "unknown", "toy::Health", "value", "none", "no rule applies"), new(120, "unknown", "toy::Health", "value", "none", "no rule applies") };
                Assert.Equal(1, HelpersCommand.FromPairs(db, "Game.exe", helpers));
                Assert.Equal(("get", "component", "medium"), (helpers[0].Family, helpers[0].Returns, helpers[0].Confidence));
                Assert.StartsWith("paired with bg3_macos_x64+0x1E (accepted by calls): a rule", helpers[0].Basis);
                Assert.Equal("unknown", helpers[1].Family);   // another type: not the same helper
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AMatchedVirtualTableLinesItsSlotsUp()
    {
        // nothing calls anything: the only evidence is the table both builds have
        var source = Side("Mac", 1, [(10, "toy::Widget::Draw"), (20, "toy::Widget::Hide"), (30, "toy::Widget::Tick"), (40, "toy::Gadget::Draw"), (50, "toy::Gadget::Tick")], [],
            (10, "toy::Widget::Draw"), (20, "toy::Widget::Hide"), (30, "toy::Widget::Tick"), (40, "toy::Gadget::Draw"), (50, "toy::Gadget::Tick"));
        source.Tables[1000] = [10, 20, 30];
        source.TableNames[1000] = "toy::Widget";
        source.Tables[2000] = [40, 50];
        source.TableNames[2000] = "toy::Gadget";
        var target = Side("Game.exe", 2, [(110, "toy::Widget::Draw"), (120, "FUN_120"), (130, "toy::Widget::Tick"), (140, "FUN_140"), (150, "FUN_150")], [],
            (110, "toy::Widget::Draw"), (130, "toy::Widget::Tick"));
        target.Tables[5000] = [110, 120, 130];
        target.Tables[6000] = [140, 150];              // no paired function in it: not matched
        var result = PairCommand.Pair(source, target, new Dictionary<long, long[]>(), (_, _) => null, 0, 0.98, 4, new PairCommand.RoundBars(), 1, _ => { });
        var table = Assert.Single(result.Tables);
        Assert.Equal((1000L, 5000L, "toy::Widget", 2, 3, 3), (table.Source, table.Target, table.Name, table.Support, table.SourceSlots, table.TargetSlots));
        var hide = result.Pairs.Single(p => p.Target == 120);
        Assert.Equal((20L, "calls", 1), (hide.Source, hide.Rule, hide.Votes));
        Assert.Contains("matched table (virtual table +0x1388)", hide.Evidence);
        Assert.DoesNotContain(result.Pairs, p => p.Target is 140 or 150);
    }

    [Fact]
    public void ATableThatFitsTwoIsLeftUnmatched()
    {
        var source = Side("Mac", 1, [(10, "toy::A"), (20, "toy::B"), (30, "toy::C"), (40, "toy::D")], [],
            (10, "toy::A"), (20, "toy::B"), (30, "toy::C"), (40, "toy::D"));
        // a base and a class that overrides nothing: the same slots
        source.Tables[1000] = [10, 20, 30];
        source.TableNames[1000] = "toy::Base";
        source.Tables[2000] = [10, 20, 40];
        source.TableNames[2000] = "toy::Derived";
        var target = Side("Game.exe", 2, [(110, "toy::A"), (120, "toy::B"), (130, "FUN_130")], [], (110, "toy::A"), (120, "toy::B"));
        target.Tables[5000] = [110, 120, 130];
        var result = PairCommand.Pair(source, target, new Dictionary<long, long[]>(), (_, _) => null, 0, 0.98, 4, new PairCommand.RoundBars(), 1, _ => { });
        Assert.Empty(result.Tables);
        Assert.Equal(1, result.TablesAmbiguous);
        Assert.DoesNotContain(result.Pairs, p => p.Target == 130);
    }

    [Fact]
    public void ANameAPairGaveIsNotEvidenceForPairing()
    {
        var root = GateKit.Testing.Scratch.NewPath("pair");
        Directory.CreateDirectory(Path.Combine(root, "Game.exe"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Game.exe", "names.tsv"),
                "82\tfunction\ttoy::Middle\t\tmacos-anchor: bg3_macos_x64+0x1E, accepted by calls\n6E\tfunction\ttoy::Update\t?Update@toy@@QEAAXXZ\ttoolkit match by vtable\n");
            var given = PairCommand.GivenByPairs(root, "Game.exe");
            Assert.Equal([0x82L], given.ToList());
            Assert.Empty(PairCommand.GivenByPairs(root, "Other.exe"));
            using var db = DecompStore.OpenWrite(Path.Combine(root, "decomp.sqlite"));
            db.Execute("INSERT INTO binaries (id, name) VALUES (2, 'Game.exe')");
            db.Execute("INSERT INTO functions (id, binary_id, rva, name, size, thunk) VALUES (1, 2, 110, 'toy::Update', 100, 0), (2, 2, 130, 'toy::Middle', 100, 0)");
            db.Execute("INSERT INTO calls VALUES (2, 110, 130)");
            var side = PairCommand.Load(db, "Game.exe", given);
            // the function the Toolkit names is labelled; the one a pair named is nameless again
            Assert.Equal(["toy::Update"], side.Labels.Values.ToList());
            Assert.Equal("FUN_82", side.ByRva[130].Name);
            Assert.Equal("toy::Middle", side.GivenNames[130]);
            // and it is still found in the text under the name it carries
            Assert.Equal([130L], PairCommand.CallOrder(side, 110, "void Update(void)\n{\n  Middle();\n}\n"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
