-- A save is told while the client is in Save, and a save's load in a session is one load told
-- once begun and once ended, although the client's Lua is reset inside it. The quick save takes
-- the client Running -> Save -> Running: `saving` (from Running), `saving_ended` (to Running). The
-- quick load takes it Running -> StartLoading (`loading`, from Running), Idle, UnloadLevel,
-- UnloadSession, Idle - a new Lua state of the client: the old one told the begin, the new one
-- opens it again with `continues`, which the host does not tell twice - LoadSession, LoadLevel,
-- SwapLevel, StopLoading, PrepareRunning: the loading screen hidden, `loading_ended` (to
-- PrepareRunning), then Running.
-- Sources: Ext.StartupVideos.Timeline() read after quick loads (research/studies/startup-videos.md);
-- the conditions in docs/contracts/bridge-protocol.md, "Attention", "Loads".
-- Venue: the sandbox. Classes: the quick save and the quick load as the game menu's own commands
-- (fair); reading the attention (observation). Its time is the load's (about 8 s).
-- Run: gatekit --instance <copy> test tests -k startup/loads/save_load

test { level = "GustavX:Basic_Level_A",
       about = "A quick save is told as saving while the client is in Save; a quick load is one load, told once begun and once ended through the reset of the client's Lua." }

local S = require("_loads")

function run(game)
    game.wait_state("Running", "client", 120)
    local saved = game.mark()
    step("quick save", "fair", function()
        check(game.ui("return GK.Gui.QuickSave()").ok, "the quick save ran")
    end)
    step("the save is told", "observation", function()
        local kinds = { "saving", "saving_ended" }
        local begun = S.next(game, kinds, saved, 60)
        eq(begun.what, "saving", "the save begins")
        eq(begun.from, "Running", "the save left Running")
        local ended = S.next(game, kinds, S.past(saved, begun), 60)
        eq(ended.what, "saving_ended", "the save ends")
        eq(ended.to, "Running", "the client is back in Running")
    end)
    local before = game.mark()
    step("quick load", "fair", function()
        check(game.ui("return GK.Gui.QuickLoad()").ok, "the quick load ran")
    end)
    step("one load, through the reset of the client's Lua", "observation", function()
        local begun, ended, after = S.one_load(game, before, 120)
        eq(begun.from, "Running", "the load left Running")
        eq(ended.to, "PrepareRunning", "the load ends as the loading screen is hidden: the client enters PrepareRunning")
        check(S.boot(after) ~= S.boot(before), "the client's Lua was reset inside the load", before, after)
    end)
end
