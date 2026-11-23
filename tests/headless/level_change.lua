-- A story change of level, for a differential check (`gatekit logic-diff`): the party sent from
-- Basic_Level_A to WLD_Main_A by the story's own call (Osi.TeleportPartiesToLevelWithMovie, no
-- session unload), the server's Osiris events and throws recorded from the call to the moment the
-- new level's gameplay starts (LevelGameplayStarted: the recording is switched off in the same
-- Osiris event, so the record ends at the story's own moment, not at a poll's). The record of a run
-- drawn and a run with the fork's cuts must be the same. Event-driven, no sleeps. W3 round 4.

test { level = "GustavX:Basic_Level_A", mods = {}, reset = "venue" }

function run(game)
    game.wait_state("Running", "server", 60)
    check(game.server("return GK.Stage.Quiet()"), "the tutorials could not be marked as seen")
    local host = game.server("return Osi.GetHostCharacter()")
    -- the end of the scenario's stretch, in the game's own event: a digest of the host and the
    -- recording switched off in the tick the new level's gameplay starts
    -- A known race at the end (W3 round 6): the arrival cinematic's courier (S_FOR_Courier, given
    -- DYING as CRA_Crash starts) dies by a server timer - DYING lasts ~4.4 s (4.45 s native, 4.37 s
    -- headless, measured) - while LevelGameplayStarted follows the client reaching Running, which
    -- waits for the cinematic's resources and the textures' streaming (natively ~4 s after the
    -- dialogue starts, headless 1.3-2.9 s: nothing streams). Natively the death lands inside the
    -- stretch, headless just after it: its Died, game_death and its loot rows. The saved random
    -- (the loot) and the levels' rows are otherwise the same.
    game.server(([[
        GK.HeadlessLevel = { host = %q, done = false }
        if not GK.HeadlessLevelHooked then
            GK.HeadlessLevelHooked = true
            Ext.Osiris.RegisterListener("LevelGameplayStarted", 2, "after", function(level)
                local s = GK.HeadlessLevel
                if s and not s.done and level == "WLD_Main_A" then
                    s.done = true
                    s.saved = GK.Rolls.SavedRandom()
                    GK.Stage.Digest({ s.host }, "gameplay started in " .. level)
                    GK.EventLog.Off(nil, "the scenario ended")
                end
            end)
        end
        return true]]):format(host))
    -- the new level's setup writes ~5,000 rows (TemplateAddedTo, statuses ...): the log keeps them all
    game.server("return GK.EventLog.Keep(128, 24 * 1024 * 1024)")
    game.server('return GK.EventLog.On({ "osiris", "dice", "status" }, { seconds = 600 })')
    check(game.server("return GK.Rolls.PinSources(1000)").ok, "the dice's sources could not be pinned")
    game.server(('return GK.Stage.Digest({ "%s" }, "before the change")'):format(host))
    game.server('return GK.Stage.Level("WLD_Main_A")')
    game.wait("server", "GK.HeadlessLevel and GK.HeadlessLevel.done", 120)
    game.wait_state("Running", "server", 60)
    -- the raised bound stays for this Lua state (put back, it would retire the rows before the run
    -- copies them); the next load starts a new state with the default
    local level = game.server("return GK.Stage.CurrentLevel()")
    check(level == "WLD_Main_A", "the party is in WLD_Main_A", level)
    print("story change of level: Basic_Level_A -> " .. tostring(level))
    local saved = game.server("return GK.HeadlessLevel.saved")
    print(("the saved random at gameplay start: %s/%s"):format(tostring(saved and saved.initial), tostring(saved and saved.current)))
end
