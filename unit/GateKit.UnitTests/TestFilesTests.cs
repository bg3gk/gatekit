using GateKit.Copies;
using GateKit.Platform;
using GateKit.Testing;

namespace GateKit.UnitTests;

/// <summary>Folder tests, the files a test keeps beside it, and how the runner puts them into a copy
/// and takes them out again (docs/writing-tests.md, "Files beside a test").</summary>
public sealed class TestFilesTests : FakeGameRuns
{
    string Write(string relative, string text = "function run(game) end") => _game.Temp.Write(relative, text);

    [Fact]
    public void A_folder_that_holds_test_lua_is_one_test_addressed_by_the_folder()
    {
        Write("tests/startup/videos/test.lua");
        Write("tests/startup/videos/_shared.lua", "return {}");
        Write("tests/startup/videos/ONE.bk2", "data");
        Write("tests/startup/videos/deeper/not_a_test.lua", "error('never read')");
        Write("tests/startup/plain.lua");
        Write("tests/_files/shared.txt", "data");
        Write("tests/_files/never.lua", "error('never read')");
        var root = _game.Temp["tests"];

        Assert.Equal(["startup/plain", "startup/videos"], GameTest.Discover(root).Select(t => t.Name));
        Assert.Equal(["startup/videos"], GameTest.Discover(root, "startup/videos").Select(t => t.Name));
        Assert.Equal(["videos"], GameTest.Discover(_game.Temp["tests/startup/videos"]).Select(t => t.Name));
        Assert.Equal("videos", GameTest.Load(_game.Temp["tests/startup/videos/test.lua"]).Name);
        Assert.Equal(Path.GetFullPath(root), GameTest.Discover(root, "startup/videos")[0].Tree, ignoreCase: true);
    }

    [Fact]
    public void Beside_test_lua_every_other_lua_is_shared()
    {
        Write("tests/one/test.lua");
        Write("tests/one/second.lua");
        var problem = Assert.Throws<CopyException>(() => GameTest.Discover(_game.Temp["tests"]));
        Assert.Contains("begins with `_`", problem.Message);
    }

    [Fact]
    public void A_folder_test_and_a_file_of_its_name_do_not_stand_side_by_side()
    {
        Write("tests/startup/videos/test.lua");
        Write("tests/startup/videos.lua");
        var problem = Assert.Throws<CopyException>(() => GameTest.Discover(_game.Temp["tests"]));
        Assert.Contains("two tests have the address startup/videos", problem.Message);
    }

    [Fact]
    public void A_file_is_looked_up_beside_the_test_then_in_files_folders_up_to_the_tree()
    {
        var tree = _game.Temp["tests"];
        var own = _game.Temp["tests/a/b/test"];
        Write("tests/_files/x.txt", "top");
        Write("tests/a/_files/x.txt", "a");
        Write("tests/a/b/test/_files/x.txt", "own _files");
        Write("tests/a/b/test/x.txt", "beside");
        Write("_files/y.txt", "above the tree");
        string? Found(string name) => GameTest.FindFile(own, tree, name) is { } path ? File.ReadAllText(path) : null;

        Assert.Equal("beside", Found("x.txt"));
        File.Delete(_game.Temp["tests/a/b/test/x.txt"]);
        Assert.Equal("own _files", Found("x.txt"));
        File.Delete(_game.Temp["tests/a/b/test/_files/x.txt"]);
        Assert.Equal("a", Found("x.txt"));
        File.Delete(_game.Temp["tests/a/_files/x.txt"]);
        Assert.Equal("top", Found("x.txt"));
        Assert.Null(Found("y.txt"));   // never above the tree
    }

    [Fact]
    public void The_header_files_are_checked_and_found_when_the_test_is_read()
    {
        Write("tests/v/ONE.bk2", "video");
        Write("tests/v/test.lua", """test { files = { ["Data/Video/GK_ONE.bk2"] = "ONE.bk2" } } function run(game) end""");
        var test = GameTest.Discover(_game.Temp["tests"]).Single();
        Assert.Equal("Data/Video/GK_ONE.bk2", test.Files.Single().Destination);
        Assert.Equal(Path.GetFullPath(_game.Temp["tests/v/ONE.bk2"]), test.Files.Single().Source, ignoreCase: true);

        foreach (var (header, says) in new[]
        {
            ("""files = { ["../outside.txt"] = "ONE.bk2" }""", "not a place in the copy's game folder"),
            ("""files = { ["C:/x.txt"] = "ONE.bk2" }""", "not a place in the copy's game folder"),
            ("""files = { ["Data/x.bk2"] = "TWO.bk2" }""", "no TWO.bk2 beside the test"),
            ("""files = { ["Data/x.bk2"] = "../ONE.bk2" }""", "is not a file beside the test"),
            ("""files = "ONE.bk2" """, "files = {"),
        })
        {
            Write("tests/v/test.lua", $"test {{ {header} }} function run(game) end");
            Assert.Contains(says, Assert.Throws<CopyException>(() => GameTest.Discover(_game.Temp["tests"])).Message);
        }
    }

    // --- the files in a copy ---

    CopyLayout Instance()
    {
        var settings = _game.Settings("one");
        new Instance("one", settings, _game.Platform).SyncGame(new HashSet<string>(), null, false);
        return new CopyLayout(settings, _game.Platform);
    }

    [Fact]
    public void The_installed_game_gets_no_files()
    {
        var layout = new CopyLayout(_game.Settings(), _game.Platform);
        var problem = Assert.Throws<CopyException>(() => new TestFiles(layout).Place("t", [("Data/x.txt", Write("src/x.txt", "x"))]));
        Assert.Contains("never into the installed game", problem.Message);
        Assert.False(File.Exists(_game.Temp["game/Data/x.txt"]));
    }

    [Fact]
    public void A_file_goes_into_a_new_folder_and_both_go_again()
    {
        var layout = Instance();
        var files = new TestFiles(layout);
        files.Place("t", [("Data/GK/deep/one.txt", Write("src/one.txt", "one"))]);
        var placed = Path.Combine(layout.Game, "Data", "GK", "deep", "one.txt");
        Assert.Equal("one", File.ReadAllText(placed));
        Assert.True(File.Exists(layout.TestFilesRecord));

        Assert.Empty(files.Remove());
        Assert.False(Directory.Exists(Path.Combine(layout.Game, "Data", "GK")));
        Assert.False(File.Exists(layout.TestFilesRecord));
    }

    [Fact]
    public void A_place_through_a_link_is_made_real_and_linked_again_and_the_game_is_untouched()
    {
        var layout = Instance();
        var localization = Path.Combine(layout.Game, "Data", "Localization");
        Assert.True(FileTree.IsLink(localization));
        var files = new TestFiles(layout);

        files.Place("t", [("Data/Localization/GK.txt", Write("src/gk.txt", "ours"))]);
        Assert.False(FileTree.IsLink(localization));
        Assert.Equal("loca", File.ReadAllText(Path.Combine(localization, "English.pak")));   // the game's file still seen
        Assert.Equal("ours", File.ReadAllText(Path.Combine(localization, "GK.txt")));
        Assert.False(File.Exists(_game.Temp["game/Data/Localization/GK.txt"]));             // the real game untouched

        Assert.Empty(files.Remove());
        Assert.True(FileTree.IsLink(localization));
        Assert.Equal("loca", File.ReadAllText(Path.Combine(localization, "English.pak")));
        Assert.Equal(["English.pak"], Directory.EnumerateFiles(_game.Temp["game/Data/Localization"]).Select(Path.GetFileName));
    }

    [Fact]
    public void A_file_that_stands_there_is_never_overwritten()
    {
        var layout = Instance();
        var source = Write("src/x.txt", "x");
        var files = new TestFiles(layout);
        // the game's own file, seen through a folder link
        Assert.Contains("already exists", Assert.Throws<CopyException>(() => files.Place("t", [("Data/Localization/English.pak", source)])).Message);
        Assert.True(FileTree.IsLink(Path.Combine(layout.Game, "Data", "Localization")));    // nothing was made real for it
        Assert.Contains("already exists", Assert.Throws<CopyException>(() => files.Place("t", [("Data/Video/Intro.bk2", source)])).Message);
        // checked before anything is written: the first, free, place is not written either
        Assert.Throws<CopyException>(() => files.Place("t", [("Data/A/free.txt", source), ("Data/Video/Intro.bk2", source)]));
        Assert.False(Directory.Exists(Path.Combine(layout.Game, "Data", "A")));
        Assert.Throws<CopyException>(() => TestFiles.Target(layout.Game, "Data/../../x"));
        Assert.Equal("video", File.ReadAllText(_game.Temp["game/Data/Video/Intro.bk2"]));
    }

    const string Lsx = """
        <?xml version="1.0" encoding="UTF-8"?>
        <save>
          <version major="4" minor="8" revision="0" build="500"/>
          <region id="Templates">
            <node id="Templates">
              <children>
                <node id="GameObjects">
                  <attribute id="MapKey" type="FixedString" value="0b1d5c0d-0000-4000-8000-000000000001"/>
                  <attribute id="Name" type="LSString" value="GK_Trigger"/>
                  <attribute id="MainMenuStartEventType" type="uint8" value="2"/>
                </node>
              </children>
            </node>
          </region>
        </save>
        """;

    [Fact]
    public void An_lsx_placed_at_an_lsf_place_is_written_as_lsf_and_taken_out_again()
    {
        var layout = Instance();
        var files = new TestFiles(layout);
        var source = Write("src/trigger.lsx", Lsx);
        files.Place("t", [("Data/Mods/GK/Levels/L/Triggers/_merged.lsf", source), ("Data/Mods/GK/as_text.lsx", source)]);

        var placed = Path.Combine(layout.Game, "Data", "Mods", "GK", "Levels", "L", "Triggers", "_merged.lsf");
        LSLib.LS.Resource read;
        using (var stream = File.OpenRead(placed))
        using (var reader = new LSLib.LS.LSFReader(stream))
            read = reader.Read();
        var node = read.Regions["Templates"].Children["GameObjects"].Single();
        Assert.Equal("GK_Trigger", node.Attributes["Name"].Value.ToString());
        Assert.Equal((byte)2, node.Attributes["MainMenuStartEventType"].Value);
        Assert.Equal(Lsx, File.ReadAllText(Path.Combine(layout.Game, "Data", "Mods", "GK", "as_text.lsx")));   // an lsx place: as written

        Assert.Empty(files.Remove());   // the record holds the converted bytes, so the file is known as the test's
        Assert.False(Directory.Exists(Path.Combine(layout.Game, "Data", "Mods", "GK")));
    }

    [Fact]
    public void An_lsx_the_writer_cannot_read_is_refused_before_anything_is_written()
    {
        var layout = Instance();
        var files = new TestFiles(layout);
        var problem = Assert.Throws<CopyException>(() => files.Place("t",
            [("Data/Mods/GK/a.txt", Write("src/a.txt", "a")), ("Data/Mods/GK/b.lsf", Write("src/b.lsx", "<save><region"))]));
        Assert.Contains("is not a resource in LSX", problem.Message);
        Assert.Contains("nothing was written", problem.Message);
        Assert.False(Directory.Exists(Path.Combine(layout.Game, "Data", "Mods", "GK")));
    }

    [Fact]
    public void A_record_of_a_run_that_is_gone_is_cleaned_and_a_changed_file_is_left()
    {
        var layout = Instance();
        var files = new TestFiles(layout);
        files.Place("t", [("Data/GK/one.txt", Write("src/one.txt", "one")), ("Data/GK/two.txt", Write("src/two.txt", "two"))]);
        Assert.Null(files.CleanStale());                                            // this process wrote it: not stale
        var record = JsonFiles.ReadObject(layout.TestFilesRecord);
        record["started"] = DateTime.UnixEpoch.ToString("o");                       // its writer gone (the id taken by another)
        JsonFiles.WriteObject(layout.TestFilesRecord, record);
        File.WriteAllText(Path.Combine(layout.Game, "Data", "GK", "two.txt"), "changed by someone");

        var line = files.CleanStale();
        Assert.Contains("2 left in the copy by a run of t", line);
        Assert.Contains("changed since the test wrote it", line);
        Assert.False(File.Exists(Path.Combine(layout.Game, "Data", "GK", "one.txt")));
        Assert.True(File.Exists(Path.Combine(layout.Game, "Data", "GK", "two.txt")));
        Assert.False(File.Exists(layout.TestFilesRecord));
    }

    [Fact]
    public void Down_cleans_what_a_killed_run_left()
    {
        var layout = Instance();
        new TestFiles(layout).Place("t", [("Data/GK/one.txt", Write("src/one.txt", "one"))]);
        var record = JsonFiles.ReadObject(layout.TestFilesRecord);
        record["started"] = DateTime.UnixEpoch.ToString("o");
        JsonFiles.WriteObject(layout.TestFilesRecord, record);
        _game.GameCopy("one").Down();
        Assert.False(Directory.Exists(Path.Combine(layout.Game, "Data", "GK")));
        Assert.Contains("taken out", _game.Output.ToString());
    }

    [Fact]
    public async Task The_runner_puts_a_tests_files_in_before_it_and_takes_them_out_after_whatever_its_verdict()
    {
        Write("tests/files/ok/data.txt", "data");
        Write("tests/files/ok/test.lua", """
            test { files = { ["Data/GK/data.txt"] = "data.txt" } }
            function run(game) assert(game.read(game.folder .. "/Data/GK/data.txt") == "data", "the file is in the copy") end
            """);
        Write("tests/files/fails/test.lua", """
            test { files = { ["Data/GK/shared.txt"] = "shared.txt" } }
            function run(game) assert(game.exists(game.folder .. "/Data/GK/shared.txt")); error("a failure") end
            """);
        Write("tests/_files/shared.txt", "shared");
        var copy = CopyWithGame("one");
        Assert.Equal(1, await Runner(copy).RunSuiteAsync(_game.Temp["tests"], null, false));
        var output = _game.Output.ToString();
        Assert.Matches(@"PASS\s+files/ok", output);
        Assert.Contains("a failure", output);
        Assert.False(Directory.Exists(Path.Combine(copy.Layout.Game, "Data", "GK")));
        Assert.False(File.Exists(copy.Layout.TestFilesRecord));
    }

    [Fact]
    public void Tests_share_a_start_only_when_they_have_the_same_files()
    {
        Write("tests/_files/a.txt", "a");
        Write("tests/_files/b.txt", "b");
        const string A = """test { menu = true, files = { ["Data/GK/x.txt"] = "a.txt" } } function run(game) end""";
        Write("tests/one/test.lua", A);
        Write("tests/two/test.lua", A);
        Write("tests/other/test.lua", """test { menu = true, files = { ["Data/GK/x.txt"] = "b.txt" } } function run(game) end""");
        Write("tests/plain.lua", "test { menu = true } function run(game) end");
        Write("tests/plain_too.lua", "test { menu = true } function run(game) end");
        var tests = GameTest.Discover(_game.Temp["tests"]).ToDictionary(t => t.Name);

        Assert.Equal(tests["one"].Group, tests["two"].Group);          // the same files, found in one place
        Assert.NotEqual(tests["one"].Group, tests["other"].Group);     // the same place, another file
        Assert.NotEqual(tests["one"].Group, tests["plain"].Group);
        Assert.Equal(tests["plain"].Group, tests["plain_too"].Group);  // no files: shared as by the start alone
        Assert.Equal("", tests["plain"].Group.Files);
        Assert.Equal(tests["one"].Start.Key, tests["plain"].Start.Key);
    }

    [Fact]
    public async Task A_groups_files_are_in_the_copy_from_before_its_start_to_after_its_last_test()
    {
        Write("tests/_files/a.txt", "a");
        Write("tests/_files/b.txt", "b");
        const string A = """test { files = { ["Data/GK/x.txt"] = "a.txt" }, reset = "game" } function run(game) game.server("return 1") end""";
        Write("tests/a1/test.lua", A);
        Write("tests/a2/test.lua", A);
        Write("tests/b/test.lua", """test { files = { ["Data/GK/x.txt"] = "b.txt" } } function run(game) end""");
        Write("tests/plain.lua", "function run(game) end");
        Copies.GameCopy? made = null;
        var copy = made = CopyWithGame("one", code =>
        {
            // the checkpoint the `game` rung's level asks for: a save appears at once
            if (!code.Contains("Osi.AutoSave()", StringComparison.Ordinal)) return null;
            var save = Path.Combine(made!.Layout.Saves, "AutoSave_1");
            Directory.CreateDirectory(save);
            File.WriteAllText(Path.Combine(save, "AutoSave_1.lsv"), "save");
            return null;
        });
        var placed = Path.Combine(copy.Layout.Game, "Data", "GK", "x.txt");
        var seen = new List<string>();
        var bridges = _game.Platform.OnStart!;
        _game.Platform.OnStart = launch =>
        {
            seen.Add(File.Exists(placed) ? File.ReadAllText(placed) : "none");
            bridges(launch);
        };

        Assert.True(await Runner(copy).RunSuiteAsync(_game.Temp["tests"], null, false) == 0, _game.Output.ToString());
        // a1 and a2 share one group: its start and the `game` rung's restart before a2 find a.txt in place;
        // b, other files, gets a start of its own; a test without files starts with none
        Assert.Equal(["a", "a", "b", "none"], seen);
        Assert.Contains("files of a1, a2: Data/GK/x.txt", _game.Output.ToString());
        Assert.False(Directory.Exists(Path.Combine(copy.Layout.Game, "Data", "GK")));
        Assert.False(File.Exists(copy.Layout.TestFilesRecord));
    }

    [Fact]
    public async Task A_start_that_fails_takes_its_groups_files_out()
    {
        Write("tests/_files/a.txt", "a");
        Write("tests/a/test.lua", """test { files = { ["Data/GK/x.txt"] = "a.txt" } } function run(game) end""");
        var layout = Instance();
        var settings = _game.Settings("one");
        var quick = new Copies.GameCopy(settings with { StartTimeout = TimeSpan.FromSeconds(1) }, _game.Platform, _game.Output, _game.GameCopy("one").Packer);
        var placed = Path.Combine(layout.Game, "Data", "GK", "x.txt");
        var atStart = false;
        _game.Platform.OnStart = _ => atStart = File.Exists(placed);   // no bridge: no heartbeat, so `up` gives up

        Assert.Equal(1, await new TestRunner(quick, _game.Output).RunSuiteAsync(_game.Temp["tests"], null, false));
        Assert.True(atStart);
        Assert.Contains("game did not start", _game.Output.ToString());
        Assert.False(Directory.Exists(Path.Combine(layout.Game, "Data", "GK")));
        Assert.False(File.Exists(layout.TestFilesRecord));
    }

    [Fact]
    public async Task A_test_with_files_is_not_run_in_a_game_already_up()
    {
        Write("tests/_files/a.txt", "a");
        Write("tests/a/test.lua", """test { files = { ["Data/GK/x.txt"] = "a.txt" } } function run(game) game.server("return 1") end""");
        var copy = CopyWithGame("one");
        await copy.UpAsync(new UpOptions());

        Assert.Equal(1, await Runner(copy).RunSuiteAsync(_game.Temp["tests"], null, false, attach: true));
        Assert.Contains("its files go into the copy before its game starts", _game.Output.ToString());
        Assert.False(Directory.Exists(Path.Combine(copy.Layout.Game, "Data", "GK")));
        Assert.NotEmpty(copy.GamePids());
        copy.Down();
    }

    [Fact]
    public async Task A_test_writes_nothing_under_the_tree_of_tests()
    {
        Write("tests/writes.lua", $"function run(game) game.save([[{_game.Temp["tests/made.txt"]}]], \"x\") end");
        Write("tests/writes_out.lua", """function run(game) game.save("made.txt", "x") end""");
        var copy = CopyWithGame("one");
        Assert.Equal(1, await Runner(copy).RunSuiteAsync(_game.Temp["tests"], null, false));
        Assert.Contains("a test writes nothing under", _game.Output.ToString());
        Assert.False(File.Exists(_game.Temp["tests/made.txt"]));
        Assert.Matches(@"PASS\s+writes_out", _game.Output.ToString());
    }

    /// <summary>The cap on a file kept beside a test (docs/writing-tests.md, "Files beside a test").</summary>
    const long Cap = 1024 * 1024;

    [Fact]
    public void Every_file_beside_a_test_in_the_repository_is_under_the_cap()
    {
        var tests = Path.Combine(CopySettings.RepositoryRoot()!, "tests");
        var data = new List<string>();
        void Gather(string folder, bool inData)
        {
            var here = inData || File.Exists(Path.Combine(folder, GameTest.FolderTestFile)) || Path.GetFileName(folder) == GameTest.SharedFilesFolder;
            if (here) data.AddRange(Directory.EnumerateFiles(folder));
            foreach (var sub in Directory.EnumerateDirectories(folder))
                if (!FileTree.IsLink(sub) && Path.GetFileName(sub) is not ("bin" or "obj")) Gather(sub, here);
        }
        Gather(tests, false);
        var over = data.Where(f => new FileInfo(f).Length > Cap).Select(f => Path.GetRelativePath(tests, f)).ToList();
        Assert.True(over.Count == 0, "over the cap of 1 MiB: " + string.Join(", ", over));
    }
}
