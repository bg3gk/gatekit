using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Copies;

namespace GateKit.Testing;

/// <summary>How much of the world is put back before a test, cheapest first (docs/contracts/cli.md
/// 3.3):
/// 1. <see cref="Fixture"/> - OUR OWN CLEAN-UP IN THE SCENE (remove what was added, spawn anew): instant,
///    the one to rely on always when it can do the job; its cleanliness is ours to prove, so it is
///    verified every time.
/// 2. <see cref="Venue"/> - RELOADING THE LEVEL (its checkpoint, in the same process): real time
///    even in the simple test level; near-absolutely clean, the game rebuilds the level from
///    nothing - and what survives a level load (lua-api.md 14) is put back or flagged.
/// 3. <see cref="Game"/> - RESTARTING THE GAME: the last resort; only when nothing else can do (a
///    new extender, a hang, a crash, state that survives a level load). Absolutely clean.
/// <see cref="None"/>: nothing is done.</summary>
public enum ResetRung
{
    None,
    Fixture,
    Venue,
    Game,
}

public static class ResetRungs
{
    public static string Name(this ResetRung rung) => rung.ToString().ToLowerInvariant();

    /// <summary>"none", "fixture", "venue" or "game"; anything else is refused by name.</summary>
    public static ResetRung Parse(string text) => text.Trim().ToLowerInvariant() switch
    {
        "none" => ResetRung.None,
        "fixture" => ResetRung.Fixture,
        "venue" => ResetRung.Venue,
        "game" => ResetRung.Game,
        _ => throw new CopyException($"reset \"{text}\" is not one of none, fixture, venue, game"),
    };
}

/// <summary>One rung tried: which, whether it held, how long, and what it said.</summary>
public sealed record ResetStep(ResetRung Rung, bool Ok, TimeSpan Took, string Detail);

/// <summary>What a ladder did before a test: the rung that held (or none), every rung tried, and
/// what GK.Stage.Clean said it could not undo.</summary>
public sealed record ResetOutcome(ResetRung Needed, ResetRung? Used, IReadOnlyList<ResetStep> Steps, IReadOnlyList<string> NotUndone)
{
    public bool Ok => Used is not null;

    /// <summary>No rung held and the baseline was taken anew from the world as it was then.</summary>
    public bool Rebased { get; init; }
    public TimeSpan Took => Steps.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Took);

    /// <summary>"fixture 1.2 s" or "fixture failed (dirty: ...) -> venue 38.0 s".</summary>
    public string Line() => Steps.Count == 0 ? "none (the world is as it was left)"
        : string.Join(" -> ", Steps.Select(s => string.Create(CultureInfo.InvariantCulture,
            $"{s.Rung.Name()} {(s.Ok ? "" : "failed ")}{s.Took.TotalSeconds:0.0} s") + (s.Ok && s.Detail.Length == 0 ? "" : $" ({s.Detail})")));
}

/// <summary>What the ladder needs from the game, so its decisions can be tested without one.</summary>
public interface IResetActions
{
    /// <summary>GK.Stage.Begin: the world now is the baseline. Returns whether an older baseline was kept.</summary>
    Task<bool> BeginAsync(bool fresh);
    /// <summary>GK.Stage.Clean on the server, the client's windows and aims, then GK.Stage.Verify
    /// until clean or given up. Returns (clean, what differs or could not be undone).</summary>
    Task<(bool Clean, IReadOnlyList<string> Problems)> CleanAsync();
    /// <summary>Why the level cannot be reloaded in place (null: it can).</summary>
    string? VenueProblem();
    Task ReloadVenueAsync();
    /// <summary>Why the game cannot be restarted (null: it can).</summary>
    string? GameProblem();
    Task RestartGameAsync();
}

/// <summary>
/// The reset ladder: one decision for every caller (`gatekit test`, `systems check --clean`, the
/// sweep's replays through this public API) of how to give a test a clean world. A test says what
/// it NEEDS (`test{reset = ...}`; by default `fixture` in a simple test level, `venue`
/// elsewhere); the ladder starts at the cheapest rung that satisfies it, verifies, and climbs one
/// rung only when the rung fails or the verify finds the world dirty, saying which rung held and why.
/// </summary>
public sealed class ResetLadder(IResetActions actions, TextWriter output)
{
    public IResetActions Actions { get; } = actions;

    /// <summary>The rung a test needs: what it declares, else `fixture` where the level is a simple
    /// test level and `venue` where it is not, or not known.</summary>
    public static ResetRung Needed(ResetRung? declared, bool? simpleLevel) =>
        declared ?? (simpleLevel == true ? ResetRung.Fixture : ResetRung.Venue);

    /// <summary>Opens the stage scope on a game found running: a baseline already there is kept
    /// (returns true: tests may have run since, so the next one is prepared), else the world as it
    /// is found becomes the baseline (false: it counts as untouched). A bridge without the stage
    /// ledger answers null.</summary>
    public async Task<bool?> OpenAsync()
    {
        try { return await Actions.BeginAsync(fresh: false); }
        catch (CopyException) { return null; }
    }

    /// <summary>Gives the next test a clean world. <paramref name="untouched"/>: nothing has run
    /// since the world was known clean (just started or reloaded), so the scope is opened and no
    /// rung is climbed. Never throws for a failed rung; an outcome with no rung that held means
    /// the test should not run.</summary>
    public async Task<ResetOutcome> PrepareAsync(ResetRung needed, bool untouched = false)
    {
        var steps = new List<ResetStep>();
        var notUndone = new List<string>();
        if (needed == ResetRung.None || untouched)
        {
            var watch = Stopwatch.StartNew();
            try { await Actions.BeginAsync(fresh: untouched); }
            catch (CopyException e)
            {
                return new ResetOutcome(needed, ResetRung.None, [new ResetStep(ResetRung.None, true, watch.Elapsed, "no stage scope: " + First(e.Message))], notUndone);
            }
            return new ResetOutcome(needed, ResetRung.None, steps, notUndone);
        }
        for (var rung = needed; rung <= ResetRung.Game; rung++)
        {
            if (rung == ResetRung.None) continue;
            var watch = Stopwatch.StartNew();
            string detail;
            try
            {
                switch (rung)
                {
                    case ResetRung.Fixture:
                        var (clean, problems) = await Actions.CleanAsync();
                        if (clean)
                        {
                            steps.Add(new ResetStep(rung, true, watch.Elapsed, ""));
                            return new ResetOutcome(needed, rung, steps, notUndone);
                        }
                        notUndone.AddRange(problems);
                        detail = "dirty: " + string.Join("; ", problems.Take(4)) + (problems.Count > 4 ? $"; and {problems.Count - 4} more" : "");
                        break;
                    case ResetRung.Venue:
                        if (Actions.VenueProblem() is { } noLevel) { detail = noLevel; break; }
                        await Actions.ReloadVenueAsync();
                        await BeginQuietlyAsync();
                        steps.Add(new ResetStep(rung, true, watch.Elapsed, ""));
                        return new ResetOutcome(needed, rung, steps, notUndone);
                    default:
                        if (Actions.GameProblem() is { } noProcess) { detail = noProcess; break; }
                        await Actions.RestartGameAsync();
                        await BeginQuietlyAsync();
                        steps.Add(new ResetStep(rung, true, watch.Elapsed, ""));
                        return new ResetOutcome(needed, rung, steps, notUndone);
                }
            }
            catch (CopyException e) { detail = First(e.Message); }
            steps.Add(new ResetStep(rung, false, watch.Elapsed, detail));
            output.WriteLine($"  reset: {rung.Name()} did not hold ({detail}); " + (rung < ResetRung.Game ? "next rung" : "no rung left"));
        }
        // No rung held: this test is not run, and the world as it is now becomes the baseline, so the
        // tests after it are judged by what they change themselves, not by what this one found.
        // One more clean first, so the new baseline is not taken in the middle of what the game was
        // still doing (a fight that began late); what it cannot undo stays in the baseline.
        var rebased = false;
        try { await Actions.CleanAsync(); }
        catch (CopyException) { }
        try { await Actions.BeginAsync(fresh: true); rebased = true; }
        catch (CopyException e) { output.WriteLine("  reset: no new baseline either (" + First(e.Message) + ")"); }
        return new ResetOutcome(needed, null, steps, notUndone) { Rebased = rebased };
    }

    /// <summary>What lets a ladder whose rungs all failed climb next time, from the details of its
    /// rungs (a missing checkpoint, a game this command did not start), or null when nothing does.</summary>
    public static string? Remedy(ResetOutcome outcome)
    {
        var venue = outcome.Steps.FirstOrDefault(s => s.Rung == ResetRung.Venue && !s.Ok);
        var game = outcome.Steps.FirstOrDefault(s => s.Rung == ResetRung.Game && !s.Ok);
        var parts = new List<string>();
        if (venue is not null && venue.Detail.Contains("checkpoint", StringComparison.Ordinal))
            parts.Add("make a checkpoint of this level while it is clean (`checkpoint`) so the venue rung can reload it");
        if (game is not null && game.Detail.StartsWith("not allowed", StringComparison.Ordinal))
            parts.Add("or run without --attach, so the game rung may restart it");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    // After a reload or a restart the world is the checkpoint's: a new baseline. A bridge
    // without the stage ledger has none to take, and the rung still held.
    async Task BeginQuietlyAsync()
    {
        try { await Actions.BeginAsync(fresh: true); }
        catch (CopyException e) { output.WriteLine("  reset: no stage scope after it (" + First(e.Message) + ")"); }
    }

    static string First(string message) => message.Split('\n')[0];
}

/// <summary>The ladder's rungs in a real game: GK.Stage on both sides, the copy's checkpoint
/// reset, and a restart through `up`.</summary>
public sealed class GameResetActions : IResetActions
{
    readonly Copies.GameCopy _copy;
    readonly UpOptions? _restart;

    /// <param name="restart">How to start the game again for the game rung; null: that rung is
    /// not allowed (a game somebody else started, `--attach`).</param>
    public GameResetActions(Copies.GameCopy copy, GameSession session, UpOptions? restart)
    {
        _copy = copy;
        Session = session;
        _restart = restart;
    }

    /// <summary>The session after the last rung (a new one after a restart).</summary>
    public GameSession Session { get; private set; }

    /// <summary>What the attention record saw during the last test that nobody staged on purpose
    /// (a fight that began, a dialogue, a death, a window), as GK.Stage.Clean and the client read it.</summary>
    public List<string> SideEffects { get; } = [];

    /// <summary>How long a clean world must stay clean before it counts, in seconds of a running
    /// game counted in the server's ticks (Verify's `ticks`): a window or a fight the clean itself
    /// set off shows in the attention record by then. GAP: a settle for want of the game's event
    /// that what the clean set going has ended.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>How long Verify is asked again while deletions are pending or values settle, in
    /// seconds of a running game counted in the server's ticks; a game that stands still for
    /// <see cref="Bridge.BridgeClient.StallGuard"/> ends it too (a hang guard).</summary>
    public TimeSpan VerifyFor { get; init; } = TimeSpan.FromSeconds(4);

    static long? TicksOf(JsonObject verify) =>
        verify["ticks"] is JsonValue v && v.TryGetValue<double>(out var t) ? (long)t : null;

    /// <summary>How long, in the game's own time (GK.Speed.Clock), Verify is asked again while it
    /// says the only differences are the game's own work under way (`pending`): a turn resource it
    /// refills out of combat takes a round of 6 game seconds.</summary>
    public TimeSpan PendingFor { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>What the last clean's Verify found the game made and keeps at its default (not dirt), a line each.</summary>
    public List<string> Kept { get; } = [];

    const string UiBegin = """
        local names = {}
        local ok, open = pcall(function() return GK.Gui.Open() end)
        for _, w in ipairs(ok and open or {}) do if not w.hud then names[w.name] = true end end
        if FRESH or GKStageWindows == nil then GKStageWindows = names end
        if FRESH or GKStageLabel == nil then GKStageLabel = ProbeSeenState and ProbeSeenState.Label or false end
        if FRESH or GKStageAttention == nil then
            local waiting = {}
            for key in pairs(GK.Attention and GK.Attention.Waiting or {}) do waiting[key] = true end
            GKStageAttention = { seq = GK.Attention and GK.Attention.Mark and GK.Attention.Mark() or 0, waiting = waiting }
        end
        if FRESH or GKStageFrameCap == nil then
            local ok, cap = pcall(function() return GK.FrameCap().frameCap end)
            GKStageFrameCap = ok and cap or false
        end
        if FRESH or GKStageGlobals == nil then
            GKStageGlobals = {}
            for key in pairs(_ENV) do GKStageGlobals[key] = true end
            GKStageGlobals.GKStageGlobals = true
        end
        return true
        """;

    // The client's half of a clean: the aim given back, a window that holds the game (a film,
    // game over is not: that is a load) gone through, windows opened since the baseline closed,
    // the capture label put back.
    const string UiClean = """
        local out = { did = {}, problems = {} }
        pcall(function() if GK.Gui.Aiming and GK.Gui.Aiming() then GK.Gui.CancelAim() end end)
        local ok, stuck = pcall(function() return GK.Gui.Stuck() end)
        if ok and stuck == "gameover" then out.problems[#out.problems + 1] = "the game-over box is up (a load is the way out)"
        elseif ok and stuck then pcall(GK.Cheat.Recover); out.did[#out.did + 1] = "recovered from " .. tostring(stuck) end
        local base = GKStageWindows or {}
        local fine, open = pcall(function() return GK.Gui.Open() end)
        for _, w in ipairs(fine and open or {}) do
            if not w.hud and not base[w.name] then
                pcall(GK.Gui.Close, w.name)
                out.did[#out.did + 1] = "closed " .. tostring(w.name)
            end
        end
        if GKStageLabel ~= nil and ProbeSeenState and ProbeSeenState.Label ~= (GKStageLabel or "idle") then
            pcall(GK.Seen.Mark, GKStageLabel or "idle")
        end
        -- the frame cap lives outside the level: put back as it was - unless a deck runs, which
        -- sets every copy's cap itself (its floor for the copies nobody watches)
        if GKStageFrameCap and not DECK then
            local ok, cap = pcall(function() return GK.FrameCap().frameCap end)
            if ok and cap ~= GKStageFrameCap then pcall(GK.SetFrameCap, GKStageFrameCap); out.did[#out.did + 1] = "frame cap set back" end
        end
        -- the client's own watches and freezes of GK.Memory (the server's go with its ledger)
        local mem = GK.Memory and GK.Memory.Available and GK.Memory.Available() and GK.Memory.Clear()
        if mem and (mem.watches > 0 or mem.freezes > 0) then
            out.did[#out.did + 1] = string.format("GK.Memory: %d watches and %d freezes of the client cleared", mem.watches, mem.freezes)
        end
        if GK.Trace and GK.Trace.State and GK.Trace.State.on then pcall(GK.Trace.End); out.did[#out.did + 1] = "the client's trace ended" end
        -- a test's plain globals in this state are forgotten, as on the server (GK.Stage.Clean)
        if GKStageGlobals then
            local gone = {}
            for key, value in pairs(_ENV) do
                local kind = type(value)
                if not GKStageGlobals[key] and (kind == "string" or kind == "number" or kind == "boolean") then gone[#gone + 1] = key end
            end
            for _, key in ipairs(gone) do _ENV[key] = nil end
        end
        -- what the client's attention record saw since the baseline: the side effects
        local A, mark = GK.Attention, GKStageAttention
        out.sideEffects = {}
        if A and A.Since and mark then
            for _, e in ipairs(A.Since(mark.seq).events or {}) do
                out.sideEffects[#out.sideEffects + 1] = tostring(e.what) .. (e.window and (" " .. tostring(e.window)) or "")
            end
            for key, item in pairs(A.Waiting or {}) do
                if not mark.waiting[key] and (item.what == "loading" or item.what == "menu" or item.what == "gameover") then
                    out.problems[#out.problems + 1] = "attention: " .. tostring(item.what) .. " (" .. key .. ")"
                end
            end
            mark.seq = A.Mark()
        end
        return out
        """;

    /// <summary>Whether a deck runs: it owns every copy's frame cap, so the clean leaves that alone.</summary>
    string DeckLine() => "local DECK = " + (new DeckRecord(_copy.Settings.StateRoot).Running() is not null ? "true" : "false") + "\n";

    const string UiVerify = """
        local out = {}
        local ok, stuck = pcall(function() return GK.Gui.Stuck() end)
        if ok and stuck then out[#out + 1] = "the client is held by " .. tostring(stuck) end
        local base = GKStageWindows or {}
        local fine, open = pcall(function() return GK.Gui.Open() end)
        for _, w in ipairs(fine and open or {}) do
            if not w.hud and not base[w.name] then out[#out + 1] = "window " .. tostring(w.name) .. " is open" end
        end
        -- the frame cap is put back by the clean but not judged here: a deck - of this checkout or
        -- of any other, which this one cannot see - sets every copy's cap itself (seen 2026-11-14:
        -- 10 against a baseline of 0 sent four cleans up the ladder)
        local A, mark = GK.Attention, GKStageAttention
        if A and mark then
            for key, item in pairs(A.Waiting or {}) do
                if not mark.waiting[key] then out[#out + 1] = "attention: " .. key .. ": " .. tostring(item.what) .. " is waiting" end
            end
        end
        return out
        """;

    public async Task<bool> BeginAsync(bool fresh)
    {
        var answer = await Session.ServerAsync($"return GK.Stage.Begin({{ fresh = {(fresh ? "true" : "false")} }})", TimeSpan.FromSeconds(20));
        if (answer is not JsonObject begun || begun["level"] is null)
            throw new CopyException("GK.Stage.Begin gave no baseline (a bridge without the stage ledger?)");
        try { await Session.UiAsync($"local FRESH = {(fresh ? "true" : "false")}\n" + UiBegin, TimeSpan.FromSeconds(10)); }
        catch (CopyException) { }   // no client UI: the client half is left out of the baseline
        return begun["kept"]?.GetValue<bool>() == true;
    }

    public async Task<(bool Clean, IReadOnlyList<string> Problems)> CleanAsync()
    {
        var problems = new List<string>();
        for (var pass = 1; pass <= 2; pass++)
        {
            problems.Clear();
            var cleaned = await Session.ServerAsync("return GK.Stage.Clean()", TimeSpan.FromSeconds(30)) as JsonObject
                ?? throw new CopyException("GK.Stage.Clean gave no answer (a bridge without the stage ledger?)");
            foreach (var item in cleaned["notUndone"] as JsonArray ?? []) if (item?.ToString() is { } text) problems.Add(text);
            if (pass == 1) SideEffects.Clear();
            foreach (var item in cleaned["sideEffects"] as JsonArray ?? []) if (item?.ToString() is { } text) SideEffects.Add("server: " + text);
            try
            {
                if (await Session.UiAsync(DeckLine() + UiClean, TimeSpan.FromSeconds(10)) is JsonObject ui)
                {
                    foreach (var item in ui["problems"] as JsonArray ?? []) if (item?.ToString() is { } text) problems.Add(text);
                    foreach (var item in ui["sideEffects"] as JsonArray ?? []) if (item?.ToString() is { } text) SideEffects.Add("client: " + text);
                }
            }
            catch (CopyException) { }
            if (problems.Count > 0) return (false, problems);
            var watch = Stopwatch.StartNew();
            var still = Stopwatch.StartNew();
            double? pendingSince = null;
            var lastGameTime = double.NaN;
            long? settledAt = null, startTicks = null, lastTicks = null, gameTimeMoved = null;
            var pending = false;
            while (true)
            {
                var verify = await Session.ServerAsync("return GK.Stage.Verify()", TimeSpan.FromSeconds(20)) as JsonObject
                    ?? throw new CopyException("GK.Stage.Verify gave no answer");
                // the limits are of a running game, in the server's ticks; without them, the host's clock
                var ticks = TicksOf(verify);
                startTicks ??= ticks;
                if (ticks != lastTicks) { lastTicks = ticks; still.Restart(); }
                bool Past(TimeSpan limit, long? since) => ticks is { } now && since is { } from
                    ? now - from >= Bridge.BridgeClient.LimitTicks(limit) || still.Elapsed > Bridge.BridgeClient.StallGuard
                    : watch.Elapsed > limit;
                var differences = (verify["differences"] as JsonArray ?? []).Select(d => d?.ToString() ?? "").ToList();
                Kept.Clear();
                Kept.AddRange((verify["kept"] as JsonArray ?? []).Select(d => d?.ToString() ?? "").Where(d => d.Length > 0));
                List<string> client = [];
                try
                {
                    if (await Session.UiAsync(DeckLine() + UiVerify, TimeSpan.FromSeconds(10)) is JsonArray seen)
                        client = seen.Select(d => d?.ToString() ?? "").ToList();
                }
                catch (CopyException) { }
                if (verify["clean"]?.GetValue<bool>() == true && client.Count == 0)
                {
                    // settled: still clean a moment later (a window or a fight the clean itself
                    // set off would show in the attention record by then)
                    settledAt ??= ticks ?? 0;
                    if (ticks is null ? watch.Elapsed > Settle : Past(Settle, settledAt)) return (true, []);
                    await Task.Delay(50);
                    continue;
                }
                settledAt = null;
                problems = [.. differences, .. client];
                pending = verify["pending"]?.GetValue<bool>() == true;
                if (pending && client.Count == 0 && verify["time"] is JsonValue t && t.TryGetValue<double>(out var gameTime))
                {
                    // the game still at its own work (a refill, a weapon put away, a deletion): waited
                    // for in game time, which holds at any speed; a clock that stands still (a paused
                    // game) ends the wait after VerifyFor. GAP: Verify is read again; a row written on
                    // the game's own events of the end (GK.Stage.Verify) is to replace the reading.
                    pendingSince ??= gameTime;
                    if (gameTime != lastGameTime) { lastGameTime = gameTime; gameTimeMoved = ticks; }
                    gameTimeMoved ??= ticks;
                    if (gameTime - pendingSince > PendingFor.TotalSeconds || Past(VerifyFor, gameTimeMoved)) break;
                }
                else if (Past(VerifyFor, startTicks)) break;
                await Task.Delay(250);
            }
            // a second clean: for what the game had not done yet, and for what began after the first
            // (a fight a creature's late reaction started, as an attack on a neutral one does)
        }
        return (false, problems);
    }

    public string? VenueProblem()
    {
        if (!Session.HasCheckpoint) return "no checkpoint of this copy to reload";
        string? here = null;
        try { here = Session.ServerAsync("return GK.Stage.CurrentLevel()", TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()?.ToString(); }
        catch (CopyException) { }
        var made = Session.CheckpointLevel;
        // Either not known (a checkpoint from before the ladder, a game that does not say): loaded as before.
        if (made is not null && here is not null && !string.Equals(made, here, StringComparison.OrdinalIgnoreCase))
            return $"the checkpoint is of {made}, the game is in {here}";
        return null;
    }

    /// <summary>The checkpoint loaded, and then what lives outside the level put back as the old
    /// baseline had it (the game speed, the global switches, the frame cap: lua-api.md 14, "What
    /// survives a level load"). The Lua state does not outlive the load, so the host carries it.</summary>
    public async Task ReloadVenueAsync()
    {
        JsonNode? outOfLevel = null;
        JsonNode? frameCap = null;
        try { outOfLevel = await Session.ServerAsync("return GK.Stage.BaselineOutOfLevel()", TimeSpan.FromSeconds(10)); }
        catch (CopyException) { }
        try { frameCap = await Session.UiAsync("return GKStageFrameCap", TimeSpan.FromSeconds(10)); }
        catch (CopyException) { }
        await Session.ResetAsync();
        if (outOfLevel is JsonObject saved)
        {
            try
            {
                await Session.ServerAsync("return GK.Stage.RestoreOutOfLevel(Ext.Json.Parse([==[" + saved.ToJsonString() + "]==]))", TimeSpan.FromSeconds(10));
            }
            catch (CopyException) { }
        }
        if (frameCap is JsonValue cap && cap.TryGetValue<double>(out var fps) && new DeckRecord(_copy.Settings.StateRoot).Running() is null)
        {
            try { await Session.UiAsync(string.Create(CultureInfo.InvariantCulture, $"return GK.SetFrameCap({fps})"), TimeSpan.FromSeconds(10)); }
            catch (CopyException) { }
        }
    }

    public string? GameProblem() => _restart is null ? "not allowed here (a game this command did not start)" : null;

    public async Task RestartGameAsync()
    {
        _copy.Down();
        Session = await _copy.UpAsync(_restart!);
        if (CheckpointAfterRestart && _restart!.Save is null) await CheckpointQuietlyAsync();
    }

    /// <summary>A new checkpoint after a restart, for a later `venue` rung - only when one may be
    /// needed (a simple level climbs from a clean straight to a restart, which costs about the
    /// same as a reload there).</summary>
    public bool CheckpointAfterRestart { get; set; }

    async Task CheckpointQuietlyAsync()
    {
        try { await Session.CheckpointAsync(); }
        catch (CopyException) { }
    }
}

/// <summary>Whether the level the game is in is a simple test level (GK.Stage.Census against the
/// copy's settings).</summary>
public static class SimpleLevel
{
    /// <summary>True or false, or null when the game does not say.</summary>
    public static async Task<bool?> IsSimpleAsync(GameSession session, CopySettings settings)
    {
        JsonObject? census;
        try { census = await session.ServerAsync("return GK.Stage.Census()", TimeSpan.FromSeconds(20)) as JsonObject; }
        catch (CopyException) { return null; }
        return Judge(census, settings);
    }

    public static bool? Judge(JsonObject? census, CopySettings settings)
    {
        if (census?["level"]?.ToString() is not { } level) return null;
        if (settings.SimpleLevels.Contains(level, StringComparer.OrdinalIgnoreCase)) return true;
        return census["characters"] is JsonValue v && v.TryGetValue<double>(out var n) ? n <= settings.SimpleLevelCharacters : null;
    }
}
