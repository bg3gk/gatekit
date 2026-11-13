using GateKit.GameData.Index;
using GateKit.GameData.Query;
using static GateKit.GameData.Tests.Synthetic;

namespace GateKit.GameData.Tests;

/// <summary>Builds an index from a made-up game Data folder and asks it questions.</summary>
public sealed class IndexBuildTests : IDisposable
{
    private const string Hero = "11111111-2222-3333-4444-555555555555";
    private const string TimelineUuid = "aaaaaaaa-0000-0000-0000-000000000001";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gatekit-gamedata-tests-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "Data");
    private string Database => Path.Combine(_root, "index", "gameindex.sqlite");

    public IndexBuildTests()
    {
        var bank = Res(N("TimelineBank", N("Resource", ("ID", TimelineUuid), ("Name", "TEST_Scene1"),
            ("SourceFile", "Public/Shared/Timeline/Generated/TEST_Scene1.lsf"), ("DialogResourceId", "res-1"))));
        var templates = Res(N("Templates", N("GameObjects", ("MapKey", Hero), ("Name", "TEST_Hero"), ("Type", "character"), ("Stats", "WPN_Test"))));
        var scene = Res(N("TLScene", N("TLActors", N("TLActor", ("ActorType", 1), ("TemplateId", Hero)))));
        var flags = Res(N("Flags", N("Flag", ("UUID", "flag-1"), ("Name", "TEST_Flag"), ("Usage", 1))));
        const string stats = "new entry \"WPN_Test\"\r\ntype \"Weapon\"\r\ndata \"RootTemplate\" \"" + Hero + "\"\r\n";
        const string goal = "INITSECTION\nKBSECTION\nIF\nDialogEnded(\"TEST_Scene1\", _)\nTHEN\nDB_Done(1);\nEXITSECTION\nENDEXITSECTION\n";
        Pak(Path.Combine(Data, "Shared.pak"),
            ("Public/Shared/Timeline/Generated/TEST_Scene1.lsf", Lsf(Timeline(Hero))),
            ("Public/Shared/Timeline/Generated/TEST_Scene1_Scene.lsf", Lsf(scene)),
            ("Public/Shared/Content/Generated/[PAK]_GeneratedDialogTimelines/_merged.lsf", Lsf(bank)),
            ("Mods/Shared/Story/DialogsBinary/Test/TEST_Dialog.lsf", Lsf(Dialog("dialog-1", TimelineUuid, Hero))),
            ("Public/Shared/RootTemplates/_merged.lsf", Lsf(templates)),
            ("Public/Shared/Flags/flag-1.lsx", Lsx(flags)),
            ("Public/Shared/Stats/Generated/Data/Weapon.txt", Utf8(stats)),
            ("Mods/Shared/Story/RawFiles/Goals/TEST_Goal.txt", Utf8(goal)),
            ("Public/Shared/Assets/model.GR2", [1, 2, 3]));
        Pak(Path.Combine(Data, "Localization", "English.pak"),
            ("Localization/English/english.loca", Loca(("h00000001g0000g0000g0000g000000000001", "Hello there"), ("h00000002g0000g0000g0000g000000000002", "Goodbye"))));
        var header = Path.Combine(Data, "Mods", "Shared", "Story", "RawFiles");
        Directory.CreateDirectory(header);
        File.WriteAllText(Path.Combine(header, "story_header.div"), "event DialogEnded((STRING)_Dialog, (INTEGER)_Instance) (3,0,1,1)\n");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private IndexBuildResult Build(bool force = false) =>
        IndexBuilder.Build(new IndexBuildOptions { DataFolder = Data, Database = Database, Jobs = 2, Force = force });

    private long Count(ReadOnlyDb db, string table) => Convert.ToInt64(db.Scalar($"SELECT count(*) FROM {table}"));

    [Fact]
    public void BuildsEveryTable()
    {
        var result = Build();
        Assert.False(result.UpToDate);
        using var db = ReadOnlyDb.Open(Database, "missing");
        Assert.Equal(10, Count(db, "files"));
        Assert.Equal(1, Count(db, "timelines"));
        Assert.Equal(1, Count(db, "dialogs"));
        Assert.Equal(1, Count(db, "templates"));
        Assert.Equal(1, Count(db, "stats"));
        Assert.Equal(1, Count(db, "osiris_functions"));
        Assert.Equal(2, Count(db, "loca"));
        Assert.Equal(3, Count(db, "texts"));       // two from the package, the loose Osiris header
        Assert.Equal(0, Count(db, "problems"));
        Assert.Equal(2, Count(db, "track_keys"));

        // The bank gives the timeline its uuid; the dialog names that uuid, which links them.
        var t = db.One("SELECT uuid, dialog_id, has_scene, shadowed FROM timelines")!;
        Assert.Equal(TimelineUuid, t.S("uuid"));
        Assert.Equal(1L, t.L("dialog_id"));
        Assert.Equal(1L, t.L("has_scene"));
        Assert.Equal(Hero, db.Scalar("SELECT scene_template FROM timeline_actors"));
        Assert.Equal("TEST_Hero", db.Scalar("SELECT template_name FROM v_actor_objects"));
        Assert.Equal("TEST_Flag", db.Scalar("SELECT name FROM v_objects WHERE key = 'flag-1'"));
        Assert.Equal(1L, db.Scalar("SELECT uses FROM osiris_uses WHERE name = 'DialogEnded'"));
        Assert.Equal(1L, db.Scalar("SELECT count(*) FROM texts_fts WHERE texts_fts MATCH 'DB_Done'"));
    }

    [Fact]
    public void SecondBuildIsUpToDate()
    {
        Build();
        Assert.True(Build().UpToDate);
        Assert.False(Build(force: true).UpToDate);
    }

    [Fact]
    public void IndexQueriesAnswerInText()
    {
        Build();
        var output = new StringWriter();
        var error = new StringWriter();
        using (var q = IndexQueries.Open(Database, output, error))
        {
            q.Hidden("TLShowWeapon", null, null, "TEST_Hero", false, 100);
            q.Dialog("TEST_Dialog", true, "English", 100);
            q.Stat("WPN_Test", 100);
            q.Text("Hello", null, true, 100);
        }
        var text = output.ToString();
        Assert.Contains("TEST_Scene1   [dialog Test/TEST_Dialog]", text, StringComparison.Ordinal);
        Assert.Contains("speaker 0: TEST_Hero: hidden at 1.00-2.50 s", text, StringComparison.Ordinal);
        Assert.Contains("TagGreeting [0]  (Root, phase 0)", text, StringComparison.Ordinal);
        Assert.Contains("\"Hello there\"", text, StringComparison.Ordinal);
        Assert.Contains("if TEST_Flag", text, StringComparison.Ordinal);
        Assert.Contains("RootTemplate = " + Hero, text, StringComparison.Ordinal);
        Assert.Contains("dialog Test/TEST_Dialog (TagGreeting)", text, StringComparison.Ordinal);
        Assert.Throws<DataQueryException>(() =>
        {
            using var q = IndexQueries.Open(Database, output, error);
            q.Timeline("NOPE");
        });
    }

    [Fact]
    public void OfflineQueriesAnswerInJson()
    {
        Build();
        using var q = new OfflineQueries(new OfflineDatabases(Database, Path.Combine(_root, "none.sqlite"), Path.Combine(_root, "none2.sqlite")));
        var scene = q.Scene("TEST_Scene1");
        Assert.Equal("scene", scene["kind"]);
        var gear = (List<Dictionary<string, object?>>)scene["gear_changes"]!;
        Assert.Equal("TLShowWeapon", gear.Single()["type"]);
        Assert.Single((List<Row>)scene["story_goals"]!);

        var found = q.Search("TEST");
        Assert.Contains(found, r => r.S("kind") == "timeline" && r.S("more") == "gatekit data query scene TEST_Scene1");
        Assert.Contains(found, r => r.S("kind") == "root template");

        var about = q.About(Hero);
        Assert.Equal("object", about["kind"]);
        Assert.NotEmpty((List<Row>)about["template"]!);

        var sql = q.RunSql("index", "SELECT name FROM timelines", 500);
        Assert.Equal(new[] { "name" }, (string[])sql["columns"]!);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => q.RunSql("index", "DELETE FROM timelines"));

        var schema = q.Schema();
        Assert.True(((Dictionary<string, object?>)schema["symbols"]!).ContainsKey("missing"));
        var tables = (Dictionary<string, object?>)((Dictionary<string, object?>)schema["index"]!)["tables"]!;
        var timelines = (Dictionary<string, object?>)tables["timelines"]!;
        var columns = (List<Dictionary<string, object?>>)timelines["columns"]!;
        Assert.Equal("resource id from the timeline bank", columns.Single(c => (string?)c["name"] == "uuid")["comment"]);
        Assert.DoesNotContain("texts_fts_data", tables.Keys);
        Assert.Contains("\"kind\": \"scene\"", OfflineQueries.ToJson(scene), StringComparison.Ordinal);
    }

    [Fact]
    public void CheckModFindsMissingReferences()
    {
        Build();
        var mod = Path.Combine(_root, "MyMod.pak");
        var objects = Res(N("Templates",
            N("GameObjects", ("MapKey", "99999999-0000-0000-0000-000000000009"), ("Name", "MyThing"),
                ("ParentTemplateId", "88888888-0000-0000-0000-000000000008"), ("Icon", "missing_icon")),
            N("GameObjects", ("MapKey", Hero), ("Name", "OverrideHero"))));
        Pak(mod, ("Public/MyMod/RootTemplates/_merged.lsf", Lsf(objects)),
            ("Public/MyMod/Stats/Generated/Data/Mine.txt", Utf8("new entry \"MY_Weapon\"\ntype \"Weapon\"\nusing \"WPN_Missing\"\n")));
        var output = new StringWriter();
        using (var q = IndexQueries.Open(Database, output, new StringWriter()))
        {
            ModChecker.Check(q, mod, 5, false, 100);
        }
        var text = output.ToString();
        Assert.Contains("1 x uuid in ParentTemplateId not found", text, StringComparison.Ordinal);
        Assert.Contains("1 x stat in using not found", text, StringComparison.Ordinal);
        Assert.Contains("overrides 1 things the game defines", text, StringComparison.Ordinal);
    }
}
