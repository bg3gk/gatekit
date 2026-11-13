-- The rule the whole toolkit rests on: reading and driving a game never depends on its window
-- being on top, visible, or anywhere in particular. This one checks it where it is easiest to
-- break - in a deck, where the window has been taken away from the desktop and put in a tile,
-- possibly out of view behind a tile that is held open.
--
-- It passes without a deck too (then there is simply no tile to find), so it belongs in the suite.
-- Run it with a deck open, and with another window over the deck, to check the thing that matters:
--     gatekit deck                              (in one terminal)
--     gatekit --instance deck1 test deck        (in another, with any window over the deck)

scenario { level = "GustavX:TUT_Avernus_C", mods = {} }

function run(game)
    local windows = game.windows()
    check(windows, "no window report at all")
    local mine = nil
    for _, copy in ipairs(windows.copies or {}) do
        if copy.pid and copy.instance then mine = mine or copy end
    end
    check(mine, "window list did not name a single running copy")
    print("copies: " .. #(windows.copies or {}) .. ", deck: " .. (windows.deck and ("pid " .. windows.deck.pid) or "none"))
    if windows.deck then
        local tiled = 0
        for _, copy in ipairs(windows.copies) do
            if copy.tile then
                tiled = tiled + 1
                print(("tile %d,%d %s  %s%s"):format(copy.tile.column, copy.tile.row, copy.tile.state,
                    copy.instance, copy.label and (" - " .. copy.label) or ""))
            end
        end
        check(tiled > 0, "a deck is running but no copy has a tile")
    end

    -- 1. The bridge: Lua on both sides, whatever the window is doing.
    local host = game.server("return Osi.GetHostCharacter()")
    check(host, "no host character: the server side did not answer")
    local state = game.client("return GK.Client().gameState")
    check(state == "Running", "the client is not running", state)

    -- 2. A screenshot of a window that may be a child of the deck, behind it, or out of view.
    local picture = game.shot("deck.jpg")
    check(game.exists(picture), "no screenshot was written", picture)

    -- 3. A key is posted to the game's window wherever that window hangs. This is the part the
    --    deck broke once and must never break again: a window adopted into a tile is a child
    --    window, which the usual enumeration does not reach, and `key` then failed outright with
    --    "game window not found". Delivery is what is checked here, not what the game makes of a
    --    key it was sent while something else has the keyboard - for acting on the game the
    --    toolkit uses its own input injection, below, which needs no window at all.
    game.key("escape")
    print("a key was posted to the game's window, tile or no tile")

    local injected = game.client("return GK.Key('ESCAPE')")
    check(injected, "the game's own key injection did not run")

    -- 4. And nothing of this disturbed the game.
    check(game.client("return GK.Client().gameState") == "Running", "the game stopped running")
end
