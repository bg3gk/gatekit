-- What every test under tests/startup/loads reads: the attention a caller is told about one load
-- (docs/contracts/bridge-protocol.md, "Attention", "Loads"). A load is told as `loading` when the
-- client enters StartLoading and `loading_ended` when the loading screen is hidden, once each,
-- even when the client's Lua is reset inside the load (a new boot opens it again with
-- `continues`, which the host does not tell twice).

local S = {}

S.KINDS = { "loading", "loading_ended" }
S.DESCENT = "d0fbd7c8-89e5-b1cf-5528-55fb12deb971"   -- MainMenu/MM_Accept, the camera's descent

--- The client's boot a cursor names.
function S.boot(cursor) return tonumber(string.match(cursor or "", "client:(%d+)")) end

--- A cursor at the start of the client's current boot: every row of it is still to be told.
function S.boot_start(game)
    return (string.gsub(game.mark(), "client:(%d+)%.%d+", "client:%1.0"))
end

--- A cursor right past a told item of the client, in the boot `since` names: for rows of one
--- boot (no load between), so that a row written in the same tick is not passed over.
function S.past(since, item)
    return (string.gsub(since, "client:(%d+)%.%d+", "client:%1." .. tostring(item.seq)))
end

--- The first told item of `kinds` after `since`, and the cursor right after it was told.
function S.next(game, kinds, since, seconds)
    local item = game.wait_attention(kinds, seconds or 120, since)
    return item, game.mark()
end

--- One load after `since`: the first told item of the load's kinds is its begin, and the next one
--- its end - not the begin told again by a new boot. Returns both items and the cursor after the end.
function S.one_load(game, since, seconds)
    local begun, after = S.next(game, S.KINDS, since, seconds)
    eq(begun.what, "loading", "the first item of the load is its begin")
    eq(begun.side, "client", "the client tells the load")
    -- A cursor text names one boot per side: a mark taken while the client's Lua is being reset
    -- may stand at the start of the new boot, as a caller that never saw the old one. A begin
    -- written at StartLoading is the old boot's (`since`'s), so the cursor is put right past it
    -- there; a begin a new boot opened again is the current boot's.
    if begun.state == "StartLoading" then after = S.past(since, begun) end
    local ended, past = S.next(game, S.KINDS, after, seconds)
    game.save("load.txt", fmt({ since = since, begun = begun, after = after, ended = ended, past = past }) .. "\n")
    eq(ended.what, "loading_ended", "the next item is the load's end, not its begin told again")
    eq(ended.side, "client", "the client tells the load's end")
    return begun, ended, past
end

--- The "press any key" page, then its key: the camera descends (`menu_descent`) and comes to
--- rest (`menu_descent_ended`, playback 0). The page is read (GK.Gui.Stuck) until it is up; it
--- also waits as the attention state window:SplashScreen (`splash`), which this file does not ask for.
function S.descent(game)
    step("the press-any-key page is up", "observation", function()
        need(game.wait("ui", "(GK.Gui.Stuck()) == 'splash'", 60), "the main menu shows its press-any-key page")
    end)
    local before = game.mark()
    step("the page's key", "fair", function() game.key("space") end)
    step("the camera descends and comes to rest", "observation", function()
        local went = S.next(game, { "menu_descent", "menu_descent_ended" }, before, 30)
        eq(went.what, "menu_descent", "the descent begins on the key")
        eq(went.timeline, S.DESCENT, "the descent is MainMenu/MM_Accept")
        eq(went.playback, 2, "its PlaybackState plays (2)")
        local rest = S.next(game, { "menu_descent", "menu_descent_ended" }, S.past(before, went), 30)
        eq(rest.what, "menu_descent_ended", "the descent ends")
        eq(rest.playback, 0, "its PlaybackState is back at rest (0)")
    end)
end

return S
