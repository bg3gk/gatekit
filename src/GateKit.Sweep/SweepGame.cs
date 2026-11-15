using System.Text.Json.Nodes;
using GateKit.Mcp;
using GateKit.Copies;
using ModelContextProtocol.Protocol;

namespace GateKit.Sweep;

/// <summary>What a sweep needs of a game copy: Lua on a side, the capture's tools, and a way back
/// when the game stops answering. The real one is <see cref="CopyGame"/>; tests give their own.</summary>
public interface ISweepGame
{
    /// <summary>The level the copy is started in (MODULE:LEVEL), which recipes declare.</summary>
    string Level { get; }

    /// <summary>The research/coverage folder the capture's tools write.</summary>
    string CoverageRoot { get; }

    Task<JsonNode?> LuaAsync(string side, string code, TimeSpan timeout, CancellationToken cancel);

    /// <summary>One of the MCP tools (bg3_seen_start, bg3_seen_new, bg3_seen_drain), its answer as JSON.</summary>
    Task<JsonNode?> ToolAsync(string name, JsonObject arguments, CancellationToken cancel);

    /// <summary>Whether the game process is there at all.</summary>
    bool Alive();

    /// <summary>The game is up, playable, in <see cref="Level"/>; starts it when it is not running.</summary>
    Task EnsureUpAsync(CancellationToken cancel);

    /// <summary>Closes the copy and starts it again in <see cref="Level"/>.</summary>
    Task RestartAsync(CancellationToken cancel);

    /// <summary>Saves the world as it is now, for <see cref="ResetWorldAsync"/>.</summary>
    Task CheckpointAsync(CancellationToken cancel);

    /// <summary>Returns the world to the checkpoint (about 15-40 s): the clean state.</summary>
    Task ResetWorldAsync(CancellationToken cancel);

    /// <summary>A game copy, for replaying a recipe through the test runner; null when there is none (tests).</summary>
    GameSession? Session { get; }

    Copies.GameCopy? GameCopy { get; }
}

/// <summary>A game copy, with the coverage tools of the MCP server called in this process.</summary>
public sealed class CopyGame(Copies.GameCopy copy, ToolHost host, string level) : ISweepGame
{
    readonly GateKitServer _server = new(host);

    public string Level { get; } = level;

    public string CoverageRoot { get; } = Record.CoverageRoot()
        ?? throw new SweepException("the coverage record lives in the repository, and this is not a checkout of it");

    public GameSession? Session => copy.Session();

    public Copies.GameCopy? GameCopy => copy;

    public Task<JsonNode?> LuaAsync(string side, string code, TimeSpan timeout, CancellationToken cancel) =>
        copy.Session().LuaAsync(side, code, timeout, cancel);

    public async Task<JsonNode?> ToolAsync(string name, JsonObject arguments, CancellationToken cancel)
    {
        var result = await _server.CallAsync(name, Args.From(arguments), cancel);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        if (result.IsError == true) throw new CopyException($"{name}: {text}");
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return text; }
    }

    public bool Alive() => copy.GamePids().Count > 0;

    public async Task EnsureUpAsync(CancellationToken cancel)
    {
        if (!Alive())
        {
            await copy.UpAsync(new UpOptions(Level), cancel);
            return;
        }
        await copy.Session().WaitStateAsync("Running", GateKit.Bridge.Side.Server, TimeSpan.FromSeconds(120), cancel: cancel);
    }

    public async Task RestartAsync(CancellationToken cancel)
    {
        copy.Down();
        await copy.UpAsync(new UpOptions(Level), cancel);
    }

    public async Task CheckpointAsync(CancellationToken cancel) => await copy.Session().CheckpointAsync();

    public async Task ResetWorldAsync(CancellationToken cancel) => await copy.Session().ResetAsync(TimeSpan.FromSeconds(180));
}
