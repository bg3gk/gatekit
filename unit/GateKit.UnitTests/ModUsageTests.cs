using System.Text;
using System.Text.Json.Nodes;
using GateKit.Mods;
using GateKit.Names;
using GateKit.Names.Demand;

namespace GateKit.UnitTests;

/// <summary>The static scan of a mod and the demand over mods, on tiny made-up mods and a made-up extender tree.</summary>
public sealed class ModUsageTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("mods");

    public ModUsageTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    void Write(string path, string text)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.ReplaceLineEndings("\n"), new UTF8Encoding(false));
    }

    static SeCatalog Catalog()
    {
        var catalog = new SeCatalog();
        catalog.ReadSource("""
            struct HealthComponent : public BaseComponent
            {
                DEFINE_COMPONENT(Health, "eoc::HealthComponent")
                int32_t Hp;
                int32_t MaxHp;
            };
            struct Item : public BaseProxyComponent
            {
                DEFINE_COMPONENT(ServerItem, "esv::Item")
                FixedString Key;
            };
            struct ArmorComponent : public BaseComponent
            {
                DEFINE_COMPONENT(Armor, "eoc::ArmorComponent")
                int32_t Level;
            };
            struct LevelComponent : public BaseComponent
            {
                DEFINE_COMPONENT(Level, "ls::LevelComponent")
                FixedString LevelName;
            };
            struct KeyComponent : public BaseComponent
            {
                DEFINE_COMPONENT(Key, "eoc::lock::KeyComponent")
                FixedString Key;
            };
            DEFINE_TAG_COMPONENT(eoc::summon, IsSummonComponent, IsSummon)
            DEFN_BOOST(ArmorClass, AC, {
                int32_t AC;
            })
            """, "Components.h");
        catalog.ReadSource("""
            void RegisterEntityLib()
            {
                DECLARE_MODULE(Entity, Both)
                BEGIN_MODULE()
                MODULE_FUNCTION(Get)
                MODULE_FUNCTION(Subscribe)
                END_MODULE()
            }
            """, "Entity.inl");
        catalog.ReadSource("EVENT_NS(Tick, Tick, lua, TickEventParams, Both, false, lua::RestrictionFlag::None)\n", "Events.inl");
        catalog.ReadSource("BEGIN_ENUM_NS(stats, FunctorId, StatsFunctorId, uint8_t)\n    E(ApplyStatus)\n    E(DealDamage)\n    EV(Extender, 127)\nEND_ENUM_NS()\n", "Stats.inl");
        catalog.ReadLua("function Ext.Require(fileName) end\nExt.Osiris = {}\nfunction Ext.Osiris.RegisterListener(name, arity, event, fn) end\n");
        return catalog;
    }

    // ApplyStatus as the game has it: the header's call /5 and the story's own procedures /3 and /4
    static readonly Dictionary<string, List<(string Kind, int Arity, int Out)>> Osiris = new()
    {
        ["ApplyStatus"] = [("proc", 3, 0), ("proc", 4, 0), ("call", 5, 0)],
        ["GetHostCharacter"] = [("query", 1, 1)],
        ["LevelGameplayStarted"] = [("event", 2, 0)],
        ["RemoveStatus"] = [("call", 3, 0)],
    };

    const string Meta = """
        <?xml version="1.0" encoding="UTF-8"?>
        <save><region id="Config"><node id="root"><children>
          <node id="ModuleInfo">
            <attribute id="Folder" type="LSString" value="Tiny"/>
            <attribute id="Name" type="LSString" value="Tiny &amp; Co"/>
            <attribute id="UUID" type="FixedString" value="11111111-2222-3333-4444-555555555555"/>
            <attribute id="Version64" type="int64" value="36028797018963968"/>
            <children><node id="PublishVersion"/></children>
          </node>
        </children></node></region></save>
        """;

    string TinyMod()
    {
        Write("tiny/Mods/Tiny/meta.lsx", Meta);
        Write("tiny/Mods/Tiny/ScriptExtender/Lua/Server.lua", """
            -- entity.Armor.Level = 3 is only a comment
            --[[ Ext.Entity.Get(x).Armor
                 is a comment too ]]
            local entity = Ext.Entity.Get(uuid)
            entity.Health.Hp = 5
            local max = entity.Health.MaxHp
            local item = entity:GetComponent("ServerItem")
            Ext.Entity.Subscribe("Health", function(e) end)
            if host["IsSummon"] ~= nil then end
            Ext.Events.Tick:Subscribe(function(e) if e.Key == "F1" then end end)
            local level = stats.Level
            Osi.ApplyStatus(entity, "X", 6.0)
            Osi.RemoveStatus(entity, "X")
            Osi.NotAFunction(entity)
            local host = Osi.GetHostCharacter()
            Ext.Osiris.RegisterListener("LevelGameplayStarted", 2, "after", function(level, editor) end)
            Ext.Net.PostMessageToServer("Tiny_Hello", "{}")
            Ext.Require("Other.lua")
            MyMod.Ext.Something()
            local stat = Ext.Stats.Get("WPN_Longsword")
            print(stat.Damage)
            """);
        Write("tiny/Mods/Tiny/ScriptExtender/Lua/Types.lua", "---@meta\nfunction Osi.ApplyStatus() end\nlocal x = entity.Armor\n");
        Write("tiny/Public/Tiny/Stats/Generated/Data/Tiny.txt", """
            new entry "TINY_STATUS"
            type "StatusData"
            data "StatusType" "BOOST"
            data "Boosts" "AC(2);NotABoost(1)"
            data "OnApplyFunctors" "ApplyStatus(SELF,BURNING,100,1);DealDamage(1d4,Fire)"
            data "StatusPropertyFlags" "DisableOverhead;IgnoreResting"
            """);
        Write("tiny/Mods/Tiny/Story/RawFiles/Goals/Tiny.txt", "IF\nLevelGameplayStarted(_Level, _)\nTHEN\nApplyStatus(S_Player, \"X\", 1.0, 1, NULL_00000000-0000-0000-0000-000000000000); // RemoveStatus(x)\n");
        Write("tiny/Mods/Tiny/GUI/Widgets/Tiny.xaml", "<Grid><TextBlock Text=\"{Binding Player.Name}\"/><Button Command=\"{Binding CloseCommand}\"/></Grid>");
        Write("tiny/Mods/Tiny/broken.pak", "not a package");
        return Path.Combine(root, "tiny");
    }

    ModUsage ScanTiny(Func<string, bool>? shipped = null)
    {
        using var source = ModSource.Open(TinyMod());
        return new UsageScanner(Catalog(), Osiris, shipped).Scan(source, "tiny");
    }

    [Fact]
    public void The_catalog_reads_the_extenders_macros()
    {
        var catalog = Catalog();
        Assert.Equal("eoc::HealthComponent", catalog.ExtToEngine["Health"]);
        Assert.Equal("eoc::summon::IsSummonComponent", catalog.ExtToEngine["IsSummon"]);
        Assert.Equal("eoc::ArmorClassBoostComponent", catalog.ExtToEngine["ArmorClassBoost"]);
        Assert.Equal("eoc::ArmorClassBoostComponent", catalog.Boosts["AC"]);
        Assert.Equal("Health", catalog.EngineToExt["eoc::HealthComponent"]);
        Assert.Contains("Level", catalog.MemberNames);
        Assert.Contains("Tick", catalog.Events);
        Assert.Contains("DealDamage", catalog.Functors);
        Assert.DoesNotContain("Extender", catalog.Functors);
        Assert.True(catalog.Defines("Entity", "Get"));
        Assert.False(catalog.Defines("Entity", "Nope"));
        Assert.True(catalog.Defines("Osiris", "RegisterListener"));
        Assert.True(catalog.Defines("Require", null));
    }

    [Fact]
    public void Lua_components_and_fields_are_read_from_the_text()
    {
        var usage = ScanTiny();
        Assert.Equal("Tiny & Co", usage.Name);
        var health = usage.Components["eoc::HealthComponent"];
        Assert.True(health.Write);        // entity.Health.Hp = 5
        Assert.True(health.Read);         // entity.Health.MaxHp
        Assert.True(health.Subscribe);    // Ext.Entity.Subscribe("Health")
        Assert.Equal(["Hp", "MaxHp"], health.Fields);
        Assert.True(usage.Components["esv::Item"].Read);
        Assert.True(usage.Components["eoc::summon::IsSummonComponent"].Read);   // host["IsSummon"]
        Assert.False(usage.Components.ContainsKey("eoc::ArmorComponent"));      // only in comments and a ---@meta file
        Assert.False(usage.Components.ContainsKey("eoc::lock::KeyComponent")); // e.Key of an event handler
        Assert.False(usage.Components.ContainsKey("ls::LevelComponent"));      // stats.Level: a plain member name
        Assert.Equal(1, usage.FilesRead["lua definitions (not scanned)"]);
    }

    [Fact]
    public void Calls_events_and_channels_are_named()
    {
        var usage = ScanTiny();
        Assert.Contains("Ext.Entity.Get", usage.Api);
        Assert.Contains("Ext.Entity.Subscribe", usage.Api);
        Assert.Contains("Ext.Osiris.RegisterListener", usage.Api);
        Assert.Contains("Ext.Require", usage.Api);
        Assert.DoesNotContain(usage.Api, a => a.Contains("Something", StringComparison.Ordinal));
        Assert.Equal(["Tick"], usage.Events);
        Assert.Equal(["Tiny_Hello"], usage.NetChannels);
        Assert.Equal(["ApplyStatus", "GetHostCharacter", "RemoveStatus"], usage.OsirisCalls.Keys);   // NotAFunction: not in the index
        Assert.Equal([3], usage.OsirisLuaArguments["ApplyStatus"]);
        Assert.Equal([0], usage.OsirisLuaArguments["GetHostCharacter"]);
        Assert.Equal([2], usage.OsirisListeners["LevelGameplayStarted"]);
        Assert.Contains("Damage", usage.StatsFieldsTouched);
    }

    [Fact]
    public void Data_files_name_boosts_functors_flags_goals_and_bindings()
    {
        var usage = ScanTiny(path => path == "Mods/Tiny/GUI/Widgets/Tiny.xaml");
        Assert.Equal(1, usage.StatsTypes["StatusData"]);
        Assert.Equal(1, usage.Boosts["AC"]);
        Assert.False(usage.Boosts.ContainsKey("NotABoost"));
        var boost = usage.Components["eoc::ArmorClassBoostComponent"];
        Assert.True(boost.Create);
        Assert.Equal(["stats"], boost.Via);
        Assert.Equal(["ApplyStatus", "DealDamage"], usage.Functors.Keys);
        Assert.Equal(["DisableOverhead", "IgnoreResting"], usage.Flags["StatusPropertyFlags"]);
        Assert.Equal(["ApplyStatus", "LevelGameplayStarted"], usage.GoalCalls);   // RemoveStatus is in a comment
        Assert.Equal(["CloseCommand", "Player.Name"], usage.XamlBindings);
        Assert.Equal(["CloseCommand"], usage.XamlCommands);
        Assert.Equal(["Mods/Tiny/GUI/Widgets/Tiny.xaml"], usage.Overrides);
    }

    [Fact]
    public void An_unreadable_package_is_listed_and_the_scan_goes_on()
    {
        var usage = ScanTiny();
        Assert.Single(usage.Unreadable);
        Assert.Equal("Mods/Tiny/broken.pak", usage.Unreadable[0].Path);
        Assert.NotEmpty(usage.Components);
    }

    [Fact]
    public void A_usage_document_reads_back_into_the_same_demand()
    {
        var usage = ScanTiny();
        var json = usage.ToJson();
        Assert.Equal(ModUsage.Schema, (string)json["schema"]!);
        var back = ModUsage.FromJson(JsonNode.Parse(json.ToJsonString())!.AsObject(), "tiny.json");
        var direct = DemandFile.Serialize(Mods.Demand.Rows([usage], null, Osiris));
        var round = DemandFile.Serialize(Mods.Demand.Rows([back], null, Osiris));
        Assert.Equal(direct, round);
        Assert.Throws<ModsException>(() => ModUsage.FromJson(new JsonObject { ["schema"] = "other" }, "x.json"));
    }

    static ModUsage Made(string name, params (string Type, string[] Fields, bool Write)[] components)
    {
        var usage = new ModUsage { Source = name };
        foreach (var (type, fields, write) in components)
        {
            var use = usage.Component(type, null);
            use.Read = true;
            use.Write = write;
            use.Via.Add("lua");
            foreach (var f in fields) use.Fields.Add(f);
        }
        return usage;
    }

    [Fact]
    public void Demand_counts_each_mod_once_per_name()
    {
        var a = Made("A", ("eoc::HealthComponent", ["Hp"], true), ("esv::Item", [], false));
        ModUsage.Add(a.OsirisCalls, "ApplyStatus");
        ModUsage.Add(a.OsirisLuaArguments, "ApplyStatus", 4);
        var b = Made("B", ("eoc::HealthComponent", ["Hp", "MaxHp"], false));
        b.GoalCalls.Add("ApplyStatus");
        var rows = Mods.Demand.Rows([b, a], null, Osiris);
        Assert.Equal("source", (string)rows[0]["kind"]!);
        Assert.Equal(2, (int)rows[0]["count"]!);
        var health = rows.First(r => (string?)r["type"] == "eoc::HealthComponent");
        Assert.Equal(2, (int)health["mods"]!);
        Assert.Equal(1, (int)health["uses"]!["write"]!);
        Assert.Equal("Hp", (string)health["fields"]![0]!["name"]!);
        Assert.Equal(2, (int)health["fields"]![0]!["mods"]!);
        Assert.Equal("eoc::HealthComponent", (string)rows[1]["type"]!);   // most mods first
        var apply = rows.First(r => (string?)r["kind"] == "osiris" && (string?)r["name"] == "ApplyStatus");
        Assert.Equal(2, (int)apply["called_by"]!);
        Assert.Equal(1, (int)apply["from_goals"]!);
        Assert.Equal(DemandFile.Serialize(rows), DemandFile.Serialize(Mods.Demand.Rows([a, b], null, Osiris)));   // order of input does not matter
    }

    // ---- the report, joined with a record

    string Record()
    {
        var record = Path.Combine(root, "record");
        Write("record/layouts/alive/eoc.HealthComponent.json", """
            {"component": "eoc::HealthComponent", "ext_name": "Health", "size": 8, "status": "confirmed", "fields": [
              {"name": "Hp", "offset": 0, "type": "int32_t", "source": "game", "evidence": "x"},
              {"name": "field_4", "offset": 4, "type": "int32_t", "source": "unknown", "evidence": "x", "legacy": "MaxHp"}]}
            """);
        Write("record/layouts/extender/esv.Item.json", """
            {"component": "esv::Item", "ext_name": "ServerItem", "size": 8, "status": "declared", "fields": [
              {"name": "Template", "offset": 0, "type": "void*", "source": "extender", "evidence": "x"}]}
            """);
        Write("record/coverage.json", """
            {"components": [
              {"side": "server", "name": "eoc::HealthComponent", "mapped": true, "verified": {"stage": "x"}},
              {"side": "server", "name": "esv::Item", "mapped": true, "verified": false},
              {"side": "server", "name": "eoc::NeverComponent", "mapped": true, "verified": false}]}
            """);
        Write("record/seen/index.json", """{"eoc::HealthComponent": {"server": {"max": 1}}, "esv::Item": {"server": {"max": 1}}}""");
        Write("record/systems/eoc/HealthSystem.json", """{"system": "eoc::HealthSystem", "symbols": {"reads": [], "writes": ["eoc::HealthComponent"]}, "uses": []}""");
        return record;
    }

    [Fact]
    public void The_report_tells_what_is_not_yet_right_about_what_mods_use()
    {
        var a = Made("A", ("eoc::HealthComponent", ["Hp", "MaxHp"], true), ("esv::Item", ["Template"], false), ("eoc::NeverComponent", [], false));
        ModUsage.Add(a.OsirisCalls, "RemoveStatus");
        ModUsage.Add(a.OsirisLuaArguments, "RemoveStatus", 2);
        ModUsage.Add(a.OsirisCalls, "GetHostCharacter");
        ModUsage.Add(a.OsirisLuaArguments, "GetHostCharacter", 0);
        ModUsage.Add(a.OsirisCalls, "ApplyStatus");
        ModUsage.Add(a.OsirisLuaArguments, "ApplyStatus", 3);
        ModUsage.Add(a.OsirisLuaArguments, "ApplyStatus", 4);
        var rows = Mods.Demand.Rows([a, Made("B", ("eoc::HealthComponent", [], false))], null, Osiris);
        var report = DemandReport.Build(rows, RecordState.Load(Record()));
        Assert.Equal(2, report.Mods);
        Assert.Equal(["eoc::NeverComponent"], report.NeverSeen.Select(t => t.Type));
        Assert.Equal(["esv::Item"], report.Unverified.Select(t => t.Type));
        var open = report.OpenFields.ToDictionary(f => $"{f.Type}.{f.Field}", f => f.State);
        Assert.Equal("machine", open["eoc::HealthComponent.MaxHp"]);   // found by its legacy name; the field is field_4 now
        Assert.Equal("extender", open["esv::Item.Template"]);
        Assert.DoesNotContain("eoc::HealthComponent.Hp", open.Keys);   // the game's own name
        Assert.Equal(["eoc::NeverComponent", "esv::Item"], report.NoSystem.Select(t => t.Type));
        var gaps = report.OsirisGaps().ToDictionary(o => (string)o["name"]!, o => (string)o["verdict"]!);
        Assert.Equal("index holds /3; no overload takes 2 arguments", gaps["RemoveStatus"]);
        Assert.DoesNotContain("GetHostCharacter", gaps.Keys);   // a query's out parameter is not passed from Lua
        Assert.DoesNotContain("ApplyStatus", gaps.Keys);        // each call has the overload of its arity

        ModUsage.Add(a.OsirisLuaArguments, "ApplyStatus", 6);
        var wrong = DemandReport.Build(Mods.Demand.Rows([a], null, Osiris), RecordState.Load(Record())).OsirisGaps()
            .ToDictionary(o => (string)o["name"]!, o => (string)o["verdict"]!);
        Assert.Equal("index holds /3, /4, /5; no overload takes 6 arguments", wrong["ApplyStatus"]);
    }

    [Fact]
    public void Worst_by_demand_puts_the_most_named_open_field_first()
    {
        var rows = Mods.Demand.Rows([
            Made("A", ("eoc::HealthComponent", ["MaxHp"], false), ("esv::Item", [], false)),
            Made("B", ("eoc::HealthComponent", ["MaxHp"], false), ("esv::Item", [], false)),
            Made("C", ("esv::Item", [], false))], null, null);
        var worst = DemandReport.Worst(rows, RecordState.Load(Record()), 10);
        Assert.Equal(("eoc::HealthComponent", "field_4", 2), ((string)worst[0]["type"]!, (string)worst[0]["field"]!, (int)worst[0]["field_mods"]!));
        Assert.Equal(("esv::Item", "Template", 0), ((string)worst[1]["type"]!, (string)worst[1]["field"]!, (int)worst[1]["field_mods"]!));
        Assert.Equal(2, worst.Count);   // Hp is the game's own name
    }

    [Fact]
    public void The_committed_demand_file_holds_together()
    {
        var path = DemandFile.PathIn(Path.Combine(GateKit.Copies.CopySettings.RepositoryRoot()!, "research", "coverage"));
        if (!File.Exists(path)) return;
        var rows = DemandFile.Read(path);
        Assert.Equal("source", (string)rows[0]["kind"]!);
        Assert.Equal(Mods.Demand.Schema, (string)rows[0]["schema"]!);
        Assert.Equal((int)rows[0]["count"]!, rows[0]["mods"]!.AsArray().Count);
        Assert.All(rows.Skip(1), r => Assert.True((int)r["mods"]! >= 1));
    }
}
