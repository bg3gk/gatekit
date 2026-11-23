-- A new game is one load, told once begun and once ended, although the client's Lua is reset
-- inside it and the client enters StartLoading twice. New Game and the settings' Confirm take the
-- client Menu -> StartLoading (`loading`, from Menu), StartServer, Idle, InitNetwork,
-- InitConnection, StartLoading again (inside the same load: nothing told), Idle, UnloadLevel,
-- UnloadSession, Idle - a new Lua state, which opens the load again with `continues`, not told
-- twice - LoadSession, LoadLevel, SwapLevel, StopLoading, PrepareRunning: the loading screen
-- hidden, `loading_ended` (to PrepareRunning). The client stays in PrepareRunning while the new
-- game's opening film plays (`film` waits), and enters Running when it ends.
-- Sources: Ext.StartupVideos.Timeline() read after new games (research/studies/startup-videos.md);
-- the conditions in docs/contracts/bridge-protocol.md, "Attention", "Loads".
-- Venue: the main menu; the test leaves the game in the opening film. Classes: New Game and
-- Confirm (fair); reading the attention (observation). Its time is the load's (about 7 s).
-- Run: gatekit --instance <copy> test tests -k startup/loads/new_game

test { menu = true,
       about = "A new game is one load, told once begun at the menu's StartLoading and once ended as the loading screen is hidden." }

local S = require("_loads")

function run(game)
    local before = game.mark()
    step("New Game, then Confirm", "fair", function()
        check(game.ui("return GK.Gui.NewGame()").ok, "the new-game settings open")
        check(game.ui("return GK.Gui.NewGame()").ok, "Confirm: the game leaves the menu")
    end)
    step("one load, through the reset of the client's Lua", "observation", function()
        local begun, ended, after = S.one_load(game, before, 120)
        eq(begun.from, "Menu", "the load left the main menu")
        eq(ended.to, "PrepareRunning", "the load ends as the loading screen is hidden: the client enters PrepareRunning")
        check(S.boot(after) ~= S.boot(before), "the client's Lua was reset inside the load", before, after)
    end)
    step("the opening film follows the load", "observation", function()
        local film = game.wait_attention({ "film" }, 60, before)
        eq(film.what, "film", "the new game's opening film waits for the player")
    end)
end
