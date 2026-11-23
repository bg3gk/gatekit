-- A mod changes the game's startup videos with a file, not with Lua: Script Extender's fork reads
-- every enabled mod's `Mods/<Folder>/ScriptExtender/StartupVideos.json` at the game's first
-- StartLoading state, composes one list over the game's own entry (`Splash_Logo_Larian`, the only
-- one) and writes it into the game's playlist as one entry whose names the player plays in turn
-- (the fork's Docs/API.md, "Startup Videos"; research/studies/startup-videos.md). The rules held
-- here, each a case:
--   - the start: the bridge's declaration, put loose beside the bridge's folder, hides the game's
--     entry, adds one after it and one before it (the hidden entry still anchors) and moves one of
--     its own before another; the start played exactly that, and the game went on only when the
--     last video stopped (_start.lua);
--   - Hide: "*" hides the game's entry and those of the mods loaded before, never the mod's own; a
--     reference hides exactly that one entry;
--   - Replace plays another video in an entry's place; a second mod's Replace over it wins and warns,
--     naming both mods;
--   - Add without Before/After follows the game's entries in load order, then in file order;
--   - what cannot hold is dropped with a warning: a reference to no entry, the latest-loaded
--     constraint of a cycle (the warning names the ids), a ModTable two mods share (the warning names
--     both UUIDs), and `*` as an Id;
--   - an owner named by UUID and by ModTable is one owner.
-- The composer's rules are read through `Ext.StartupVideos.Compose`, the same composer on
-- declarations given in Lua, with no start of their own. Before and After another mod's whole group
-- is test:startup/videos/two_mods; the player's switch off is test:startup/videos/switch_off.
-- Venue: a start of its own with the startup videos on (`startup_videos`), the declaration and three
-- of our clips (a red word on black, 1 s, Bink 2) put into the copy before it (`files`), ending when
-- the bridge first answers (`until_lua`). The cases change nothing a later case reads, so no reset
-- rung is declared. Classes: reading the start and composing are observations.
-- Run: gatekit --instance <copy> test tests -k startup/videos/own_clips

test { until_lua = true, startup_videos = true,
       files = {
           ["Data/Video/GK_ONE.bk2"] = "ONE.bk2",
           ["Data/Video/GK_TWO.bk2"] = "TWO.bk2",
           ["Data/Video/GK_THREE.bk2"] = "THREE.bk2",
           ["Data/Mods/Gwynerva_GateKit/ScriptExtender/StartupVideos.json"] = "StartupVideos.json",
       },
       about = "The startup videos the game plays are the game's entry as the enabled mods' StartupVideos.json hide, replace, add and order it; what cannot hold is dropped with a warning." }

local S = require("_start")
local GAME, entry = S.GAME, S.entry
local A = "00000000-0000-0000-0000-00000000a001"        -- two mods given to Compose: TA, loaded first,
local B = "00000000-0000-0000-0000-00000000b001"        -- and TB
local GAME_ENTRY = GAME .. "=" .. GAME

-- Composes the declarations (Lua source of the list Compose takes; A and B stand for the UUIDs)
-- and returns the list as "Id=Video" and the warnings.
local function compose(game, declarations)
    return game.client(string.format([[
        local A, B = %q, %q
        local c = Ext.StartupVideos.Compose(%s)
        local list = {}
        for _, e in ipairs(c.Entries) do list[#list + 1] = e.Id .. "=" .. e.Video end
        return { list = list, warnings = c.Warnings or {} }
    ]], A, B, declarations))
end

-- Every warning holds every one of `parts`; there are `n` of them.
local function warned(c, n, parts)
    eq(#c.warnings, n, "the number of warnings " .. fmt(c.warnings))
    for _, w in ipairs(c.warnings) do
        for _, part in ipairs(parts) do check(contains(w, part), "the warning names " .. part, w) end
    end
end

case("the start", function(game)
    step("the list the start was given and played", "observation", function()
        local r = S.read(game)
        game.save("start.txt", fmt(r) .. "\n")
        check(r.enabled, "the player's switch is on (startup_videos)", r.enabled)
        eq(r.game, { GAME }, "the game's own entries")
        local bridge = r.mods.Gwynerva_GateKit
        check(bridge and bridge.declared and bridge.uuid == S.BRIDGE, "the bridge's declaration was found, under its UUID", r.mods)
        -- the file: Hide the game's; One After it, Three Before it, Two Before GateKit:Three
        eq(r.list, { entry(S.BRIDGE, "Two", "GK_TWO"), entry(S.BRIDGE, "Three", "GK_THREE"), entry(S.BRIDGE, "One", "GK_ONE") },
            "the composition")
        eq(r.warnings, {}, "no warnings")
        S.played(r, { "GK_TWO", "GK_THREE", "GK_ONE" })
    end)
end)

case("hide all", function(game)
    step("a later mod's * hides the game's and earlier mods' entries, not its own", "observation", function()
        local c = compose(game, [[{
            { Uuid = A, ModTable = "TA", Add = { { Id = "P", Video = "GK_ONE" } } },
            { Uuid = B, ModTable = "TB", Hide = { "*" }, Add = { { Id = "Q", Video = "GK_TWO" } } } }]])
        eq(c.list, { entry(B, "Q", "GK_TWO") }, "what is left")
        warned(c, 0, {})
    end)
end)

case("hide one", function(game)
    step("a reference hides exactly that entry", "observation", function()
        local c = compose(game, [[{
            { Uuid = A, ModTable = "TA", Add = { { Id = "P", Video = "GK_ONE" }, { Id = "Q", Video = "GK_TWO" } } },
            { Uuid = B, ModTable = "TB", Hide = { "TA:P" } } }]])
        eq(c.list, { GAME_ENTRY, entry(A, "Q", "GK_TWO") }, "what is left")
        warned(c, 0, {})
    end)
end)

case("replace one", function(game)
    step("the game's entry plays another video", "observation", function()
        local c = compose(game, [[{ { Uuid = A, ModTable = "TA", Replace = { { Id = "Splash_Logo_Larian", Video = "GK_TWO" } } } }]])
        eq(c.list, { GAME .. "=GK_TWO" }, "the game's entry, replaced")
        warned(c, 0, {})
    end)
    step("a second mod's Replace over it wins and warns, naming both", "observation", function()
        local c = compose(game, [[{
            { Uuid = A, ModTable = "TA", Replace = { { Id = "Splash_Logo_Larian", Video = "GK_TWO" } } },
            { Uuid = B, ModTable = "TB", Replace = { { Id = "Splash_Logo_Larian", Video = "GK_THREE" } } } }]])
        eq(c.list, { GAME .. "=GK_THREE" }, "the last loaded wins")
        warned(c, 1, { A, B, GAME })
    end)
end)

case("add own", function(game)
    step("added entries follow the game's, in load order and file order", "observation", function()
        local c = compose(game, [[{
            { Uuid = A, ModTable = "TA", Add = { { Id = "P", Video = "GK_ONE" }, { Id = "Q", Video = "GK_TWO" } } },
            { Uuid = B, ModTable = "TB", Add = { { Id = "R", Video = "GK_THREE" } } } }]])
        eq(c.list, { GAME_ENTRY, entry(A, "P", "GK_ONE"), entry(A, "Q", "GK_TWO"), entry(B, "R", "GK_THREE") }, "the list")
        warned(c, 0, {})
    end)
end)

case("unknown id", function(game)
    step("a constraint naming no entry is dropped with a warning", "observation", function()
        -- R would go before P if its constraint held; it names an entry TA does not add
        local c = compose(game, [[{ { Uuid = A, ModTable = "TA", Add = {
            { Id = "P", Video = "GK_ONE" }, { Id = "R", Video = "GK_TWO", Before = "TA:Nope" } } } }]])
        eq(c.list, { GAME_ENTRY, entry(A, "P", "GK_ONE"), entry(A, "R", "GK_TWO") }, "the list in load order")
        warned(c, 1, { "TA:Nope", "dropped" })
    end)
end)

case("cycle", function(game)
    step("the latest-loaded constraint of a cycle is dropped, naming the ids", "observation", function()
        local c = compose(game, [[{
            { Uuid = A, ModTable = "TA", Add = { { Id = "P", Video = "GK_ONE", After = "TB:Q" } } },
            { Uuid = B, ModTable = "TB", Add = { { Id = "Q", Video = "GK_TWO", After = "TA:P" } } } }]])
        -- TA's P After TB:Q holds; TB's Q After TA:P, loaded last, closes the cycle
        eq(c.list, { GAME_ENTRY, entry(B, "Q", "GK_TWO"), entry(A, "P", "GK_ONE") }, "the list")
        warned(c, 1, { "cycle", A .. ":P", B .. ":Q" })
    end)
end)

case("shared modtable", function(game)
    step("a ModTable two mods share names neither", "observation", function()
        local c = compose(game, [[{
            { Uuid = A, ModTable = "T", Add = { { Id = "P", Video = "GK_ONE" } } },
            { Uuid = B, ModTable = "T", Add = { { Id = "Q", Video = "GK_TWO", Before = "T:P" } } } }]])
        eq(c.list, { GAME_ENTRY, entry(A, "P", "GK_ONE"), entry(B, "Q", "GK_TWO") }, "Q not moved before P")
        warned(c, 1, { A, B, "dropped" })
    end)
end)

case("star as an id", function(game)
    step("an Add whose Id is * is left out", "observation", function()
        local c = compose(game, [[{ { Uuid = A, ModTable = "TA", Add = { { Id = "*", Video = "GK_ONE" } } } }]])
        eq(c.list, { GAME_ENTRY }, "nothing added")
        warned(c, 1, { "\"*\"", A })
    end)
end)

case("owner by uuid or modtable", function(game)
    step("TA:P and <TA's UUID>:P are one entry", "observation", function()
        local function by(owner)
            return compose(game, string.format([[{
                { Uuid = A, ModTable = "TA", Add = { { Id = "P", Video = "GK_ONE" } } },
                { Uuid = B, ModTable = "TB", Add = { { Id = "Q", Video = "GK_TWO", Before = %q } } } }]], owner .. ":P"))
        end
        local byTable, byUuid = by("TA"), by(A)
        eq(byTable.list, { GAME_ENTRY, entry(B, "Q", "GK_TWO"), entry(A, "P", "GK_ONE") }, "by ModTable")
        eq(byUuid.list, byTable.list, "by UUID, the same")
        warned(byTable, 0, {})
        warned(byUuid, 0, {})
    end)
end)
