using System.Text.Json.Nodes;
using GateKit.GameData.Query;
using GateKit.Home;
using GateKit.Copies;
using Microsoft.Data.Sqlite;

namespace GateKit.UnitTests;

/// <summary>The one home: how it is found, what overrides one location, how a missing database
/// fails, and what doctor says - on a configured home and on none.</summary>
public sealed class HomeTests : IDisposable
{
    readonly TempFolder _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    /// <summary>A search that sees only this test's folders and variables.</summary>
    HomeSearch Search(Dictionary<string, string>? env = null, string? working = null, string? program = null, string? user = null) =>
        new(name => env?.GetValueOrDefault(name), working ?? _temp["work"], program, user, _temp["default-home"]);

    GateKitHome Resolve(Dictionary<string, string>? env = null, string? working = null, string? program = null, string? user = null)
    {
        Directory.CreateDirectory(working ?? _temp["work"]);
        return GateKitHome.Resolve(Search(env, working, program, user));
    }

    static string Json(string path) => System.Text.Json.JsonSerializer.Serialize(path);

    /// <summary>A database with one table and <paramref name="rows"/> rows in it.</summary>
    static void Database(string path, string table, int rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var create = db.CreateCommand();
        create.CommandText = $"CREATE TABLE {table} (id INTEGER PRIMARY KEY)";
        create.ExecuteNonQuery();
        for (var i = 0; i < rows; i++)
        {
            using var insert = db.CreateCommand();
            insert.CommandText = $"INSERT INTO {table} DEFAULT VALUES";
            insert.ExecuteNonQuery();
        }
    }

    // --- where the home is ---

    [Fact]
    public void Without_any_setting_the_home_is_the_per_user_default_and_says_so()
    {
        var home = Resolve();
        Assert.False(home.Configured);
        Assert.Equal(_temp["default-home"], home.Folder);
        Assert.Equal(LocationSource.Default, home.Index.Source);
        Assert.Equal(Path.Combine(_temp["default-home"], "db", "index", "gameindex.sqlite"), home.Index.Path);
    }

    [Fact]
    public void The_variable_beats_every_gatekit_json()
    {
        _temp.Write("work/gatekit.json", $$"""{ "home": {{Json(_temp["from-file"])}} }""");
        var home = Resolve(new() { ["GATEKIT_HOME"] = _temp["from-variable"] });
        Assert.Equal(_temp["from-variable"], home.Folder);
        Assert.Equal(LocationSource.Variable, home.Source);
        Assert.Equal(LocationSource.Home, home.State.Source);
    }

    [Fact]
    public void The_nearest_gatekit_json_of_the_working_folder_wins_then_the_programs_then_the_users()
    {
        _temp.Write("gatekit.json", """{ "home": "outer" }""");
        _temp.Write("work/deep/gatekit.json", """{ "game": "g" }""");          // nearer, but says nothing of the home
        _temp.Write("program/gatekit.json", """{ "home": "program-home" }""");
        _temp.Write("user/gatekit.json", """{ "home": "user-home" }""");
        Directory.CreateDirectory(_temp["work/deep/er"]);

        var home = Resolve(working: _temp["work/deep/er"], program: _temp["program/bin"], user: _temp["user/gatekit.json"]);
        Assert.Equal(_temp["outer"], home.Folder);                              // relative to the file that said it
        Assert.Equal(_temp["gatekit.json"], home.Origin);
        Assert.Equal(_temp["work/deep/g"], home.Game.Path);                     // a key from the nearest file that has it

        File.Delete(_temp["gatekit.json"]);
        Assert.Equal(_temp["program/program-home"], Resolve(working: _temp["work/deep/er"], program: _temp["program/bin"], user: _temp["user/gatekit.json"]).Folder);

        Assert.Equal(_temp["user/user-home"], Resolve(working: _temp["work/deep/er"], user: _temp["user/gatekit.json"]).Folder);
    }

    [Fact]
    public void A_locations_own_variable_moves_that_location_alone()
    {
        var home = Resolve(new() { ["GATEKIT_HOME"] = _temp["h"], ["BG3_INDEX_DB"] = _temp["elsewhere/index.sqlite"], ["GATEKIT_STATE"] = _temp["st"] });
        Assert.Equal(_temp["elsewhere/index.sqlite"], home.Index.Path);
        Assert.Equal(LocationSource.Variable, home.Index.Source);
        Assert.Equal("BG3_INDEX_DB", home.Index.Origin);
        Assert.Equal(_temp["st"], home.State.Path);
        Assert.Equal(Path.Combine(_temp["h"], "db", "symbols", "symbols.sqlite"), home.Symbols.Path);
        Assert.Equal(LocationSource.Home, home.Symbols.Source);
        Assert.Equal(Path.Combine(_temp["h"], "mods", "library"), home.ModLibrary.Path);
        Assert.Equal(Path.Combine(_temp["h"], "downloads", "mods"), home.StudyMods.Path);
        Assert.Equal(LocationSource.Unset, home.SeFork.Source);
        Assert.Null(home.SeFork.Path);
    }

    [Fact]
    public void A_broken_gatekit_json_is_an_explained_error()
    {
        _temp.Write("work/gatekit.json", "{ not json");
        var error = Assert.Throws<HomeException>(() => Resolve());
        Assert.Contains(_temp["work/gatekit.json"], error.Message);
    }

    [Fact]
    public void A_run_copy_reads_the_settings_of_the_folder_it_was_copied_from()
    {
        _temp.Write("checkout/gatekit.json", $$"""{ "home": {{Json(_temp["shared"])}} }""");
        // The copy's own folder is elsewhere; the folder it came from is what is searched.
        Assert.Equal(_temp["shared"], Resolve(program: _temp["checkout/bin/Debug"]).Folder);
    }

    // --- a database that is not there ---

    [Fact]
    public void A_missing_database_is_an_error_that_names_the_path_the_setting_and_doctor()
    {
        var home = Resolve(new() { ["GATEKIT_HOME"] = _temp["h"] });
        var path = home.Index.Path!;
        var error = Assert.Throws<MissingDatabaseException>(() => HomeDatabase.OpenReadOnly(path, home));
        Assert.Contains(path, error.Message);
        Assert.Contains("does not exist", error.Message);
        Assert.Contains("BG3_INDEX_DB", error.Message);
        Assert.Contains("gatekit doctor", error.Message);
        Assert.Contains("gatekit data build", error.Message);
        Assert.False(File.Exists(path));                         // reading never creates it

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        Assert.Contains("empty file", Assert.Throws<MissingDatabaseException>(() => HomeDatabase.OpenReadOnly(path, home)).Message);

        File.Delete(path);
        using (var stub = new SqliteConnection($"Data Source={path};Pooling=False")) { stub.Open(); using var c = stub.CreateCommand(); c.CommandText = "PRAGMA user_version = 1"; c.ExecuteNonQuery(); }
        Assert.Contains("no tables", Assert.Throws<MissingDatabaseException>(() => HomeDatabase.OpenReadOnly(path, home)).Message);

        File.Delete(path);
        Database(path, "objects", 3);
        using var db = HomeDatabase.OpenReadOnly(path, home);
        Assert.Null(HomeDatabase.Problem(path));
    }

    [Fact]
    public void An_offline_query_fails_without_its_database_and_notes_what_an_optional_one_left_out()
    {
        var index = _temp["db/index.sqlite"];
        var symbols = _temp["db/symbols.sqlite"];
        var decomp = _temp["db/decomp.sqlite"];
        using (var q = new OfflineQueries(new OfflineDatabases(index, symbols, decomp)))
            Assert.Contains("gatekit doctor", Assert.Throws<DataQueryException>(() => q.RunSql("index", "SELECT 1")).Message);

        Database(index, "files", 0);
        using var query = new OfflineQueries(new OfflineDatabases(index, symbols, decomp));
        query.Schema();
        Assert.Contains(query.Missing, note => note.Contains(symbols, StringComparison.Ordinal) && note.Contains("left out", StringComparison.Ordinal));
        Assert.Contains(query.Missing, note => note.Contains(decomp, StringComparison.Ordinal));
        Assert.DoesNotContain(query.Missing, note => note.Contains(index, StringComparison.Ordinal));
    }

    [Fact]
    public void A_query_that_finds_none_of_its_databases_is_an_error_naming_every_path_not_an_empty_answer()
    {
        // search and about draw on the index and the symbols, schema on all three: with every
        // database missing each is an error naming the paths, never "nothing found".
        var index = _temp["db/index.sqlite"];
        var symbols = _temp["db/symbols.sqlite"];
        var decomp = _temp["db/decomp.sqlite"];
        using var q = new OfflineQueries(new OfflineDatabases(index, symbols, decomp));
        foreach (var asked in new Action[] { () => q.Search("ApplyStatus"), () => q.About("ApplyStatus"), () => q.Schema() })
        {
            var error = Assert.Throws<DataQueryException>(asked).Message;
            Assert.Contains(index, error, StringComparison.Ordinal);
            Assert.Contains(symbols, error, StringComparison.Ordinal);
        }
        Assert.Contains(decomp, Assert.Throws<DataQueryException>(() => q.Schema()).Message, StringComparison.Ordinal);
        Assert.Contains(decomp, Assert.Throws<DataQueryException>(() => q.Schema("decomp")).Message, StringComparison.Ordinal);
        Assert.False(File.Exists(index) || File.Exists(symbols) || File.Exists(decomp));   // nothing was created on the way
    }

    // --- doctor's table ---

    [Fact]
    public void Doctor_lists_every_location_of_a_configured_home_with_its_source_and_live_counts()
    {
        var root = _temp["h"];
        Database(Path.Combine(root, "db", "index", "gameindex.sqlite"), "objects", 3);
        Database(Path.Combine(root, "db", "decomp", "decomp.sqlite"), "functions", 2);
        Directory.CreateDirectory(Path.Combine(root, "state", "instances", "one"));
        Directory.CreateDirectory(Path.Combine(root, "state", "instances", "two"));
        _temp.Write("work/gatekit.json", $$"""{ "home": {{Json(root)}} }""");

        var report = HomeReport.Read(Resolve());
        var row = (string id) => report.Rows.Single(r => r.Location.Id == id);
        Assert.Equal("gatekit.json", row("home").Location.SourceName);
        Assert.Equal("home", row("index").Location.SourceName);
        Assert.True(row("index").Exists);
        Assert.Equal(3, row("index").Count);
        Assert.Equal(2, row("decomp-db").Count);
        Assert.False(row("symbols").Exists);
        Assert.Equal(2, row("state").Count);
        Assert.Equal("2 instances known", row("state").Counted);
        Assert.DoesNotContain(report.Findings, f => f.Kind == "default");

        var text = new StringWriter();
        report.Write(text);
        Assert.Contains("gatekit.json", text.ToString());
        Assert.Contains("3 objects", text.ToString());

        var json = report.ToJson();
        Assert.Equal(HomeReport.Schema, (string)json["schema"]!);
        Assert.True((bool)json["home"]!["configured"]!);
        var index = json["locations"]!.AsArray().Single(l => (string)l!["id"]! == "index")!;
        foreach (var key in new[] { "id", "path", "kind", "source", "origin", "exists", "bytes", "count", "counted", "change", "purpose" })
            Assert.True(index.AsObject().ContainsKey(key), key);
    }

    [Fact]
    public void Doctor_flags_a_home_not_configured_a_path_outside_it_and_a_checkouts_own_state()
    {
        // A checkout with a worktree, each with a leftover .state.
        _temp.Write("checkout/GateKit.slnx", "<Solution />");
        _temp.Write("checkout/.git/worktrees/w/gitdir", _temp["worktree/.git"]);
        _temp.Write("worktree/GateKit.slnx", "<Solution />");
        _temp.Write("worktree/.git", "gitdir: " + _temp["checkout/.git/worktrees/w"]);
        _temp.Write("checkout/.git/worktrees/w/commondir", "../..");
        Directory.CreateDirectory(_temp["checkout/.state"]);
        Directory.CreateDirectory(_temp["worktree/.state"]);
        var stateFile = Path.Combine(_temp["default-home"], "state", "script-extender.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        File.WriteAllText(stateFile, $$"""{ "scriptExtender": {{Json(_temp["outside/se"])}} }""");

        var report = HomeReport.Read(Resolve(working: _temp["worktree"]));
        Assert.Contains(report.Findings, f => f.Kind == "default" && f.Detail.Contains("not configured", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Kind == "leftover-state" && f.Path == _temp["checkout/.state"]);
        Assert.Contains(report.Findings, f => f.Kind == "leftover-state" && f.Path == _temp["worktree/.state"]);
        Assert.Contains(report.Findings, f => f.Kind == "outside-home" && f.Path == _temp["outside/se"]);
        Assert.All(report.Rows.Where(r => r.Location.Source is LocationSource.Home), _ => Assert.Fail("nothing is 'home' without one"));
    }
}
