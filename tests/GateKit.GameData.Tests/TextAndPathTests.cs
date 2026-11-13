using GateKit.GameData.Text;

namespace GateKit.GameData.Tests;

public class TextAndPathTests
{
    [Fact]
    public void StatsEntries()
    {
        const string text = """
            new entry "WPN_Test"
            type "Weapon"
            using "WPN_Base"
            data "Damage" "1d8"
            data "Damage" "1d10"
            data "RootTemplate" "11111111-2222-3333-4444-555555555555"

            new entry "ARM_Test"
            type "Armor"
            """;
        var entries = TextParsers.ReadStats(text.Replace("\r\n", "\n")).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(("WPN_Test", "Weapon", "WPN_Base"), (entries[0].Name, entries[0].Type, entries[0].Parent));
        Assert.Equal("1d10", entries[0].Fields["Damage"]);
        Assert.Equal(["Damage", "RootTemplate"], entries[0].Fields.Keys.ToList());
        Assert.Equal(("ARM_Test", "Armor", null), (entries[1].Name, entries[1].Type, entries[1].Parent));
    }

    [Fact]
    public void OsirisHeader()
    {
        const string text = """
            alias_type {GUIDSTRING,5} CHARACTER
            enum_type {ABILITY}
            event TemplateEquipped((GUIDSTRING)_Item, (CHARACTER)_Character) (3,0,12,1)
            query QRY_Thing([in](INTEGER)_A, [out](STRING)_B) (1,2,3,1)
            call DoNothing() (4,5,6,1)
            """;
        var (functions, types) = TextParsers.ReadOsirisHeader(text.Replace("\r\n", "\n"));
        Assert.Equal(3, functions.Count);
        Assert.Equal(new OsirisFunction("TemplateEquipped", "event", "(GUIDSTRING)_Item, (CHARACTER)_Character", "3,0,12,1", 2), functions[0]);
        Assert.Equal(0, functions[2].Arity);
        Assert.Equal(new OsirisType("GUIDSTRING", "alias_type", "GUIDSTRING,5} CHARACTER"), types[0]);
        Assert.Equal("enum_type", types[1].Kind);
    }

    [Fact]
    public void GoalUsesByRole()
    {
        const string text = """
            Version 1
            SubGoalCombiner SGC_AND
            INITSECTION
            DB_Thing(1);
            KBSECTION
            IF
            TemplateEquipped(_Item, _Char)
            AND
            DB_Thing(_X)
            THEN
            DoSomething(_Char); // DoNotCount()
            PROC
            PROC_Mine((CHARACTER)_C)
            THEN
            DoSomething(_C);
            EXITSECTION
            DB_Thing(1);
            ENDEXITSECTION
            """;
        var uses = TextParsers.ReadGoal(text.Replace("\r\n", "\n"));
        Assert.Equal(2, uses[("DB_Thing", "init")] + uses[("DB_Thing", "exit")]);
        Assert.Equal(1, uses[("TemplateEquipped", "trigger")]);
        Assert.Equal(1, uses[("DB_Thing", "condition")]);
        Assert.Equal(2, uses[("DoSomething", "action")]);
        Assert.Equal(1, uses[("PROC_Mine", "define")]);
        Assert.False(uses.ContainsKey(("DoNotCount", "action")));
        Assert.DoesNotContain(uses.Keys, k => k.Name == "CHARACTER");
    }

    [Theory]
    [InlineData("Public/Gustav/Timeline/Generated/CAMP_X.lsx", "timeline")]
    [InlineData("Public/Gustav/Timeline/Generated/CAMP_X_Scene.lsx", "scene")]
    [InlineData("Public/Gustav/Content/Generated/[PAK]_GeneratedDialogTimelines/Bank.lsx", "bank")]
    [InlineData("Mods/Gustav/Story/DialogsBinary/Camp/CAMP_X.lsx", "dialog")]
    [InlineData("Public/Shared/RootTemplates/_merged.lsx", "templates")]
    [InlineData("Mods/Gustav/Levels/WLD_Main_A/Characters/_merged.lsx", "entities")]
    [InlineData("Public/Gustav/Voice/SpeakerGroups.lsx", "speakergroups")]
    [InlineData("Localization/English/Soundbanks/x.lsx", "voice")]
    [InlineData("Public/Shared/Flags/f.lsx", "generic")]
    [InlineData("Mods/Gustav/Story/RawFiles/Goals/G.txt", "text")]
    [InlineData("Public/Shared/Assets/model.GR2", null)]
    public void Classify(string path, string? kind) => Assert.Equal(kind, FileKinds.Classify(path));

    [Fact]
    public void TextKinds()
    {
        Assert.Equal("goal", FileKinds.TextKind("Mods/Gustav/Story/RawFiles/Goals/G.txt"));
        Assert.Equal("osiris header", FileKinds.TextKind("Mods/Gustav/Story/RawFiles/story_header.div"));
        Assert.Equal("stats", FileKinds.TextKind("Public/Shared/Stats/Generated/Data/Weapon.txt"));
        Assert.Equal("interface", FileKinds.TextKind("Public/Game/GUI/x.xaml"));
        Assert.Equal("script", FileKinds.TextKind("Public/Shared/Scripts/x.lua"));
    }

    [Fact]
    public void LoadOrder()
    {
        var order = FileKinds.PackageOrder(["Patch8_HotFix10", "Assets", "Gustav", "Patch8_HotFix9", "DiceSet10", "DiceSet02", "Shared", "Engine"]);
        Assert.Equal(["Engine", "Shared", "Gustav", "Assets", "DiceSet02", "DiceSet10", "Patch8_HotFix9", "Patch8_HotFix10"], order);
        Assert.Equal("Gustav", FileKinds.ModOf("Mods/Gustav/Story/x.lsx"));
        Assert.Equal("", FileKinds.ModOf("Engine/x.lsx"));
        Assert.True(FileKinds.ModRank("GustavDev") > FileKinds.ModRank("Gustav"));
    }

    [Theory]
    [InlineData(341.953125f, "341.95313")]
    [InlineData(1.1920929E-07f, "0.00000011920929")]
    [InlineData(-4.7683716E-07f, "-0.00000047683716")]
    [InlineData(0.5f, "0.5")]
    [InlineData(1f, "1")]
    [InlineData(1e20f, "100000000000000000000")]
    [InlineData(17.257812f, "17.257813")]
    public void FloatsInPlainShortestForm(float value, string text) => Assert.Equal(text, GateKit.GameData.Resources.NumberText.Float(value));

    [Fact]
    public void PythonStyleJson()
    {
        Assert.Equal("{\"a\": \"1\", \"b\": null, \"c\": \"\\u00e9\"}", PyJson.Dumps(new Dictionary<string, string?> { ["c"] = "é", ["a"] = "1", ["b"] = null }));
    }
}
