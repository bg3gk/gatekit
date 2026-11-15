using GateKit.Symbols.Queries;

namespace GateKit.Symbols.UnitTests;

/// <summary>The guard reader over pseudo-code written here, in the shape Ghidra prints it.</summary>
public sealed class PseudoGuardTests
{
    static GuardNames Names()
    {
        var n = new GuardNames();
        n.Helpers["ecs::GetComponent<toy::Health>"] = ("get", "toy::Health");
        n.Helpers["ecs::HasComponent<toy::Dead>"] = ("has", "toy::Dead");
        n.Helpers["ecs::AddComponent<toy::Event>"] = ("add", "toy::Event");
        n.Helpers["FUN_140009000"] = ("has", "toy::Busy");
        n.AddType("toy::Health", new Dictionary<long, string> { [0x10] = "Hp" });
        n.AddType("toy::camp::State", new Dictionary<long, string> { [0] = "State" });
        return n;
    }

    static string Fn(string body, string sig = "void toy::Sys::Update(Sys *this,State *param_2)") =>
        $"/* a comment with {{ braces }} */\n\n{sig}\n\n{{\n  longlong lVar1;\n  char cVar2;\n  Health *pHVar3;\n  \n{body}}}\n";

    static GuardResult Guard(string body, string call = "ecs::AddComponent<toy::Event>", int occurrence = 0, string? sig = null) =>
        PseudoGuard.Find(sig == null ? Fn(body) : Fn(body, sig), call, occurrence, Names());

    [Fact]
    public void NestedIfsOverAComponentAndItsField()
    {
        var g = Guard("""
              lVar1 = ecs::GetComponent<toy::Health>(this,1);
              if (lVar1 != 0) {
                if (*(int *)(lVar1 + 0x10) == 0) {
                  ecs::AddComponent<toy::Event>(this,1);
                }
              }

            """);
        Assert.Equal("extracted", g.Status);
        Assert.Equal(["has(toy::Health)", "toy::Health.Hp == 0"], g.Terms);
        Assert.Equal(0, g.Unnamed);
        Assert.Equal("when has(toy::Health) and toy::Health.Hp == 0", g.Text);
    }

    [Fact]
    public void ElseEarlyReturnAndATypedParameter()
    {
        var g = Guard("""
              cVar2 = ecs::HasComponent<toy::Dead>(this);
              if (cVar2 != '\0') {
                return;
              }
              if (param_2->State == 2) {
                lVar1 = 0;
              }
              else {
                ecs::AddComponent<toy::Event>(this,1);
              }

            """);
        Assert.True(g.Terms.Count > 0, g.Status + " " + g.Note);
        Assert.Equal(["not has(toy::Dead)", "toy::camp::State.State != 2"], g.Terms);
        Assert.False(g.Partial);
    }

    [Fact]
    public void AForwardGotoASwitchCaseAndAnUnnamedOffset()
    {
        var g = Guard("""
              if (*(int *)(lVar1 + 0x158) == 5) goto LAB_140001200;
              switch(*(int *)(this + 8)) {
              case 3:
                ecs::AddComponent<toy::Event>(this,1);
                break;
              }
            LAB_140001200:
              return;

            """);
        Assert.Equal(["[lVar1+0x158] != 5", "Sys+0x8 == 3"], g.Terms);
        Assert.Equal(2, g.Unnamed);
    }

    [Fact]
    public void ACallInsideAConditionHasTheConjunctsBeforeIt()
    {
        var g = Guard("""
              if ((cVar2 == '\x01') && (lVar1 = ecs::GetComponent<toy::Health>(this,1), lVar1 != 0)) {
                lVar1 = 0;
              }

            """, "ecs::GetComponent<toy::Health>");
        Assert.Equal("extracted", g.Status);
        Assert.Equal(["cVar2 == '\\x01'"], g.Terms);
    }

    [Fact]
    public void AfterAWhileTrueLoopItsOneBreakHoldsAndANamedCallStandsForItsLocal()
    {
        var g = Guard("""
              do {
                while( true ) {
                  if (lVar1 == 0) {
                    return;
                  }
                  if ((cVar2 = toy::World::IsAlive(this,lVar1), cVar2 != '\0')) break;
                  lVar1 = lVar1 + 8;
                }
                ecs::AddComponent<toy::Event>(this,1);
              } while( true );

            """);
        Assert.Equal(["World::IsAlive() != 0"], g.Terms);
        // a loop over a list says so instead of its two pointers
        Assert.Equal(["for each item of a list", "has(toy::Dead)"], Guard(
            "  for (; puVar1 != puVar2; puVar1 = puVar1 + 2) {\n    if (ecs::HasComponent<toy::Dead>(this) != '\\0') {\n      ecs::AddComponent<toy::Event>(this,1);\n    }\n  }\n").Terms);
        // a template pointer is no structure of the record
        var t = Guard("  if (*(int *)(pIVar3 + 8) == 1) {\n    ecs::AddComponent<toy::Event>(this,1);\n  }\n",
                      sig: "void toy::Sys::Update(Sys *this,ID<ecs::EntityHandleTraits> *pIVar3)");
        Assert.Equal(["[pIVar3+0x8] == 1"], t.Terms);
    }

    [Fact]
    public void ALabelMakesItOnePathAndNothingIsAlways()
    {
        var partial = Guard("""
              if (lVar1 == 0) {
            LAB_140001100:
                ecs::AddComponent<toy::Event>(this,1);
              }

            """);
        Assert.True(partial.Partial);
        Assert.Contains("one path", partial.Text);

        Assert.Equal("always", Guard("  ecs::AddComponent<toy::Event>(this,1);\n").Status);
        Assert.Equal("not_extracted", Guard("  FUN_1(1);\n").Status);
        Assert.Equal("not_extracted", Guard("LAB_1:\n  ecs::AddComponent<toy::Event>(this,1);\n").Status);
    }

    [Fact]
    public void AHelperStillUnnamedInThePseudoCodeIsKnownByItsFunctionName()
    {
        var g = Guard("""
              if (FUN_140009000(this) == '\0') {
                if (pHVar3->Hp < 3) {
                  ecs::AddComponent<toy::Event>(this,1);
                }
              }

            """);
        Assert.Equal(["not has(toy::Busy)", "toy::Health.Hp < 3"], g.Terms);
        // the second call of the same helper is told apart by its place
        const string two = "  ecs::AddComponent<toy::Event>(this,1);\n  if (lVar1 == 7) {\n    ecs::AddComponent<toy::Event>(this,2);\n  }\n";
        Assert.Equal("always", Guard(two, occurrence: 0).Status);
        Assert.Equal(["lVar1 == 7"], Guard(two, occurrence: 1).Terms);
    }
}
