using GateKit.Mcp;

namespace GateKit.Tests;

/// <summary>
/// What `bg3_api` answers. The bridge is written against local aliases - `local Act = GK.Act`,
/// then `function Act.Lockpick(...)` - and a reader that only knew `function GK.x` left out some
/// four hundred functions, GK.Act, GK.Cheat and GK.Stage among them. The one tool that says what
/// exists was silent about the half an agent is meant to use, so that silence is tested for here.
/// </summary>
public sealed class BridgeApiTests : IDisposable
{
    readonly FakeGame _game = new();

    public void Dispose() => _game.Dispose();

    void Bridge(string side, string file, string lua)
    {
        var path = Path.Combine(_game.Temp["mods"], "GateKit", "Mods", "Gwynerva_GateKit",
            "ScriptExtender", "Lua", side, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, lua);
    }

    static string? About(System.Text.Json.Nodes.JsonArray found, string name) => found
        .FirstOrDefault(entry => entry?["function"]?.GetValue<string>() == name)?["about"]?.GetValue<string>();

    [Fact]
    public void FunctionsWrittenThroughALocalAliasAreListedUnderTheirRealName()
    {
        Bridge("Server", "Act.lua", """
            local Act = GK.Act
            local See = GK.See

            --- Picks a lock as the context menu does.
            function Act.Lockpick(who, item) end

            --- What the party can see.
            function See.Around(who) end

            --- Straight on GK, as a few functions are.
            function GK.Unsheath(drawn, who) end
            """);
        var found = CoreTools.BridgeApi(_game.Stand(), null);

        Assert.Equal("Picks a lock as the context menu does.", About(found, "GK.Act.Lockpick"));
        Assert.Equal("What the party can see.", About(found, "GK.See.Around"));
        Assert.Equal("Straight on GK, as a few functions are.", About(found, "GK.Unsheath"));
        Assert.All(found, entry => Assert.StartsWith("GK.", entry!["function"]!.GetValue<string>(), StringComparison.Ordinal));
    }

    [Fact]
    public void ALocalTableThatIsNotPartOfGKStaysPrivate()
    {
        Bridge("Server", "Act.lua", """
            local Act = GK.Act
            local helpers = {}

            --- An implementation detail, not API.
            function helpers.sort(a, b) end

            --- The one that is API.
            function Act.Move(who) end
            """);
        var found = CoreTools.BridgeApi(_game.Stand(), null);

        Assert.Equal("The one that is API.", About(found, "GK.Act.Move"));
        Assert.DoesNotContain(found, entry => entry!["function"]!.GetValue<string>().Contains("sort", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAliasIsTheOneBoundInThisFile()
    {
        Bridge("Server", "Act.lua", "local A = GK.Act\n--- Acting.\nfunction A.Jump(who) end\n");
        Bridge("Client", "Gui.lua", "local A = GK.Gui\n--- Pressing.\nfunction A.Press(name) end\n");
        var found = CoreTools.BridgeApi(_game.Stand(), null);

        Assert.Equal("Acting.", About(found, "GK.Act.Jump"));
        Assert.Equal("Pressing.", About(found, "GK.Gui.Press"));
    }

    [Fact]
    public void TheFilterNarrowsByAnyText()
    {
        Bridge("Server", "Act.lua", "local Act = GK.Act\n--- Picks a lock.\nfunction Act.Lockpick(who, item) end\n"
            + "--- Opens a door.\nfunction Act.Open(who, door) end\n");
        var only = CoreTools.BridgeApi(_game.Stand(), "lockpick");

        Assert.Equal("GK.Act.Lockpick", Assert.Single(only)!["function"]!.GetValue<string>());
    }
}
