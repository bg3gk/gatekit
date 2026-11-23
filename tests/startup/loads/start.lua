-- The start of the game is one load, told once begun and once ended, and the main menu is ready
-- when the camera's descent ends. The client enters StartLoading before any Lua exists, so the
-- bridge's first Lua state (in LoadModule) opens `loading` itself: `from` = InitMenu, the state
-- the load left, and `startedMs` = the time the timeline recorded for InitMenu -> StartLoading
-- (Ext.StartupVideos.Timeline(), ms since the process was created). The load ends when the client
-- enters Menu, the loading screen hidden in that tick: `loading_ended` with `to` = Menu, then the
-- waiting state `menu`. On the press-any-key page's key the camera descends - MainMenu/MM_Accept's
-- PlaybackState 0 -> 2, `menu_descent` - and comes to rest, 2 -> 0, `menu_descent_ended`.
-- The code: the hidden row is the fork's read of code:ls::LoadingScreenManager.Visible, and the
-- descent is read at code:eoc::TimelineDataComponent.PlaybackState.
-- Sources: research/studies/startup-videos.md, "The start, step by step"; the conditions in
-- docs/contracts/bridge-protocol.md, "Attention", "Loads".
-- Venue: a start of its own, run when the bridge first answers (`until_lua`); the test follows the
-- rest of the start live. Its time is the start's (LoadMenu takes 15-20 s), not ten seconds.
-- Classes: reading the rows and the timeline (observation); the page's key (fair).
-- Run: gatekit --instance <copy> test tests -k startup/loads/start

test { until_lua = true,
       about = "The start is told as one load, begun at its StartLoading and ended at the main menu; the menu is ready when the camera's descent ends." }

local S = require("_loads")

function run(game)
    local from = S.boot_start(game)
    local begun, ended, after
    -- the load opened at the boot, before the test began: told from the boot's start
    step("one load, from the first Lua state to the main menu", "observation", function()
        begun, ended, after = S.one_load(game, from, 180)
        eq(begun.from, "InitMenu", "the load left InitMenu")
        check(begun.state ~= "StartLoading", "the first Lua state found the load already under way", begun.state)
        check(type(begun.startedMs) == "number" and begun.startedMs > 0, "the begin carries the timeline's StartLoading time", begun)
        eq(ended.to, "Menu", "the load ends as the client enters Menu")
    end)
    step("the timeline agrees", "observation", function()
        local t = game.client([[
            local r = {}
            for _, e in ipairs(Ext.StartupVideos.Timeline()) do
                if e.Side == "Client" and e.What == "State" and e.To == "StartLoading" and not r.started then
                    r.started, r.from = e.Ms, tostring(e.From)
                elseif e.What == "LoadingScreenHidden" and r.started and not r.hidden then
                    r.hidden = e.Ms
                elseif e.Side == "Client" and e.What == "State" and e.To == "Menu" and not r.menu then
                    r.menu = e.Ms
                end
            end
            return r
        ]])
        game.save("timeline.txt", fmt(t) .. "\n")
        check(math.abs(begun.startedMs - t.started) < 0.01, "startedMs is the timeline's InitMenu -> StartLoading", begun.startedMs, t)
        eq(t.from, "InitMenu", "the timeline's load left InitMenu")
        check(t.hidden and t.menu and math.abs(t.hidden - t.menu) < 50, "the loading screen is hidden as the client enters Menu", t)
    end)
    step("the main menu waits", "observation", function()
        -- written in the tick of the load's end, so looked for from the boot's start
        local menu = S.next(game, { "menu" }, from, 30)
        check(menu.seq > ended.seq, "the menu opens after the load's end", menu, ended)
        eq(menu.state, "Menu", "the menu is the client's Menu")
        local again = game.events(after, S.KINDS).rows
        eq(#again, 0, "no other load after the start's")
    end)
    S.descent(game)
end
