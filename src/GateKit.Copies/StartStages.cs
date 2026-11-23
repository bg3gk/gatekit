using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Bridge;

namespace GateKit.Copies;

/// <summary>
/// A start followed by its stages as Script Extender's fork records them
/// (`Ext.StartupVideos.Timeline()`): a page of the interface entered (a `UIState` row under
/// `GameRoot`: `Eula`, `SplashScreen`, `MainMenu`, a first-launch screen) and the client leaving
/// its `Menu` state (a `State` row). Each <see cref="NextAsync"/> is one observed chunk
/// (`GK.Observe`): the client answers when the next such row is there, never on a timer of the
/// host's; its limit is only a guard. A build without the timeline, or whose timeline records no
/// interface state, gives none (<see cref="OpenAsync"/>), and the start is followed by looking at
/// what holds it (`GK.Gui.Stuck`).
/// </summary>
public sealed class StartStages
{
    int _at;   // the timeline's rows already read

    StartStages() { }

    /// <summary>One stage: a page entered (<paramref name="Page"/>), the client gone from `Menu` to
    /// <paramref name="Left"/>, the timeline full (<paramref name="Full"/>: it records no more rows),
    /// or no stage within the limit (<paramref name="None"/>).</summary>
    public sealed record Stage(string? Page, string? Left, bool Full, bool None);

    const string Has = """
        if not GK.Verbs.Has("StartupVideos.Timeline") then return false end
        for _, e in ipairs(Ext.StartupVideos.Timeline()) do
            if e.What == "UIState" then return true end
        end
        return false
        """;

    // FROM (the rows already read) and WITHIN (ms of a running game) are set before it. A look
    // every third tick: the timeline is copied into Lua at each look.
    const string Next = """
        local function scan()
            local rows = Ext.StartupVideos.Timeline()
            for i = FROM + 1, #rows do
                local e = rows[i]
                if e.Side == "Client" then
                    if e.What == "UIState" and e.Change == "Entered" and e.Parent == "GameRoot" and e.Name ~= "Loading" then
                        return { page = e.Name, at = i }
                    elseif e.What == "State" and e.From == "Menu" then
                        return { left = e.To, at = i }
                    end
                end
            end
            if (rows.Dropped or 0) > 0 then return { full = true, at = #rows } end
            return nil, #rows
        end
        local now = scan()
        if now then return now end
        local looked = 0
        return GK.Observe({ waiting = true }, function(ticks, final)
            if not final and ticks - looked < 3 then return nil end
            looked = ticks
            local found, at = scan()
            if found then return found end
            if final then return { none = true, at = at } end
        end, WITHIN)
        """;

    /// <summary>The stages of this start, or null when the extender's build does not record the
    /// interface's pages (or the client does not answer).</summary>
    public static async Task<StartStages?> OpenAsync(GameSession session, CancellationToken cancel = default)
    {
        JsonNode? has;
        try { has = await session.LuaAsync("client", Has, TimeSpan.FromSeconds(20), cancel); }
        catch (CopyException) { return null; }
        return has is JsonValue value && value.TryGetValue<bool>(out var yes) && yes ? new StartStages() : null;
    }

    /// <summary>The next stage after those already read, answered when its row is recorded;
    /// <paramref name="within"/> (time of a running game, counted in the client's ticks) is the guard.</summary>
    public async Task<Stage> NextAsync(GameSession session, TimeSpan within, CancellationToken cancel = default)
    {
        var code = string.Create(CultureInfo.InvariantCulture, $"local FROM, WITHIN = {_at}, {within.TotalMilliseconds:0}\n") + Next;
        var answer = await session.LuaAsync("client", code, within + TimeSpan.FromSeconds(30), cancel);
        if (answer.Field("at") is not JsonValue at || !at.TryGetValue<double>(out var row))
            throw new CopyException("the start's timeline was not read: " + (answer.Field("error")?.ToString() ?? "no stage in the answer"));
        _at = (int)row;
        return new Stage(answer.Field("page")?.GetValue<string>(), answer.Field("left")?.GetValue<string>(),
            answer.Field("full")?.GetValue<bool>() == true, answer.Field("none")?.GetValue<bool>() == true);
    }
}
