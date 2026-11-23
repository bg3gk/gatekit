-- A return to the main menu is one load, told once begun and once ended, and the menu is ready
-- again when the camera's descent ends. The game menu's Main Menu, confirmed, takes the client
-- Running -> StartLoading (`loading`, from Running), Disconnect, UnloadLevel, UnloadSession,
-- LoadMenu - a new Lua state of the client, which opens the load again with `continues`, not told
-- twice - StopLoading, Menu: the loading screen hidden, `loading_ended` (to Menu), then `menu`. The
-- press-any-key page shows again, and its key plays the descent again: MainMenu/MM_Accept
-- PlaybackState 0 -> 2 (`menu_descent`) and 2 -> 0 (`menu_descent_ended`).
-- Sources: Ext.StartupVideos.Timeline() read after returns to the menu
-- (research/studies/startup-videos.md); the conditions in docs/contracts/bridge-protocol.md,
-- "Attention", "Loads".
-- Venue: the sandbox; the test leaves the game in the main menu. Classes: Escape, the game menu's
-- button and the box's yes, the page's key (fair); reading the attention (observation). Its time
-- is the load's and the descent's (about 20 s).
-- Run: gatekit --instance <copy> test tests -k startup/loads/to_menu

test { level = "GustavX:Basic_Level_A", surprises = { "box" },
       about = "A return to the main menu is one load, told once begun and once ended at Menu; the menu is ready again when the camera's descent ends." }

local S = require("_loads")

function run(game)
    game.wait_state("Running", "client", 120)
    local before = game.mark()
    step("the game menu's Main Menu, confirmed", "fair", function()
        game.key("escape")
        check(game.wait("ui", "GK.Gui.IsOpen('GameMenu')", 10), "Escape opens the game menu")
        check(game.ui("return GK.Gui.Click('GameMenu', 'MainMenuButton')").ok, "Main Menu clicked")
        check(game.ui("return GK.Gui.Resolve{ box = { leave = true } }").ok, "the box's yes")
    end)
    step("one load, through the reset of the client's Lua, to the menu", "observation", function()
        local begun, ended, after = S.one_load(game, before, 120)
        eq(begun.from, "Running", "the load left Running")
        eq(ended.to, "Menu", "the load ends as the client enters Menu")
        check(S.boot(after) ~= S.boot(before), "the client's Lua was reset inside the load", before, after)
        local menu = S.next(game, { "menu" }, S.boot_start(game), 30)
        eq(menu.state, "Menu", "the main menu waits")
    end)
    S.descent(game)
end
