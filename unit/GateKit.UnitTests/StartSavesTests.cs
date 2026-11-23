using GateKit.Copies;
using GateKit.Testing;

namespace GateKit.UnitTests;

/// <summary>The saves of a level's start that a running game loads instead of a new process, and
/// which groups of tests a running game may be taken into.</summary>
public sealed class StartSavesTests : IDisposable
{
    readonly FakeGame _game = new();

    public void Dispose() => _game.Dispose();

    [Fact]
    public void A_start_save_is_found_by_level_and_mod_list_while_its_folder_is_there()
    {
        var layout = new CopyLayout(_game.Settings("one"), _game.Platform);
        var saves = new StartSaves(layout);
        Directory.CreateDirectory(Path.Combine(layout.Saves, "A__AutoSave_0"));
        saves.Record("GustavX:Basic_Level_A", [], "A__AutoSave_0");
        saves.Record("GustavX:TUT_Avernus_C", ["Mod1"], "B__AutoSave_1");

        Assert.Equal("A__AutoSave_0", saves.Find("GustavX:Basic_Level_A", []));
        Assert.Equal("A__AutoSave_0", saves.Find("Shared:basic_level_a", []));
        Assert.Null(saves.Find("GustavX:Basic_Level_A", ["Mod1"]));
        Assert.Null(saves.Find("GustavX:Basic_Level_A", null));
        Assert.Null(saves.Find("GustavX:TUT_Avernus_C", ["Mod1"]));   // its folder is gone

        Directory.CreateDirectory(Path.Combine(layout.Saves, "C__AutoSave_2"));
        saves.Record("GustavX:Basic_Level_A", [], "C__AutoSave_2");
        Assert.Equal("C__AutoSave_2", saves.Find("GustavX:Basic_Level_A", []));
        Assert.Equal(2, File.ReadAllLines(layout.StartSavesRecord).Length);
    }

    [Fact]
    public void Only_a_level_or_a_save_with_no_files_and_no_test_of_the_start_keeps_a_game_up()
    {
        using var temp = new TempFolder();
        GameTest Test(string relative, string header)
        {
            var path = temp.Write("tests/" + relative, "test { " + header + " }\nfunction run(game) end");
            if (header.Contains("files", StringComparison.Ordinal)) temp.Write("tests/" + Path.GetDirectoryName(relative) + "/x.txt");
            return GameTest.Load(path);
        }

        Assert.True(TestRunner.Keepable([Test("gui/a.lua", "level = 'GustavX:Basic_Level_A'")]));
        Assert.True(TestRunner.Keepable([Test("gui/b.lua", "save = 'Some__QuickSave_0'")]));
        Assert.False(TestRunner.Keepable([Test("gui/c.lua", "menu = true")]));
        Assert.False(TestRunner.Keepable([Test("gui/d.lua", "level = 'GustavX:Basic_Level_A', startup_videos = true")]));
        Assert.False(TestRunner.Keepable([Test("gui/e.lua", "level = 'GustavX:Basic_Level_A', files = { ['Data/GateKit_x/x.txt'] = 'x.txt' }")]));
        Assert.False(TestRunner.Keepable([Test("startup/loads/f.lua", "level = 'GustavX:Basic_Level_A'")]));
    }
}
