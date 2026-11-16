-- A store carries what a mod knows across a save and its load when nothing else does: three
-- legendary books of the third act are placed at random spots near the host, where each stands
-- is written through GK.Store, the books are taken out of the world, and only then is the game
-- saved - so the save holds no book, only the store. After that save is loaded the books are
-- placed again from the store alone, and each must stand where the first placing put it.
-- The second record, which does not pass through the store, is a marker: an ordinary item (a
-- healing potion) put where each book stood and left in the world through the save. Its place is
-- kept by the game's own save of the level, not by the Script Extender's variables that hold the
-- store, so the two records share nothing but the spot. The host also keeps the first placing in
-- the test's own memory, which a load of the game does not touch.
-- The books (bg3.wiki: each a legendary book of the Sorcerous Vault, or its pair): the Tharchiate
-- Codex (UN_BOOK_LOW_TharchiateCypher; its parent BOOK_Wizards_Tome_Cursed_TharchiateCypher_A is
-- a plain book), Caution Before the Seelie (UNI_BOOK_LOW_SightOfTheSeelie) and the Necromancy of
-- Thay, which the Codex unlocks (UNI_FOR_ThayanTome_Unsocketed). The Annals of Karsus cannot be
-- had in the sandbox: it is an item placed in the city's level (S_LOW_KarsusNotes), whose root
-- is a generic cursed tome. The spots are random in every run: one book in each third of a
-- circle around the host, at a random angle and distance, so no constant can pass.
-- A load ends the stage scope with the Lua state; the scope is opened again over the loaded world
-- (GK.Stage.Begin carries the markers into it as objects staged and not removed, so the clean
-- takes them away). Staging (cheat): the books, the markers, the writes, the save, the load, the
-- scope, the second placing. Reading: observation. A save and a load take about a quarter of a
-- minute each. Stands on test:store/kept/round_trip (a store survives a save and its load).
-- Run: gatekit --instance <copy> test research/demos -k three_books

test { level = "GustavX:Basic_Level_A", surprises = { "loading" },
       about = "Books placed from a store after a load stand where they stood before the save." }

local NAME = "gk_three_books"
local BOOKS = {
    "486a1a14-2d67-4b9c-a2dc-7858c2a6a473",   -- UN_BOOK_LOW_TharchiateCypher, the Tharchiate Codex
    "7b290606-909c-437b-991a-f2e125d271bb",   -- UNI_BOOK_LOW_SightOfTheSeelie, Caution Before the Seelie
    "feecea9b-c091-46fd-9795-738e9c0e3019",   -- UNI_FOR_ThayanTome_Unsocketed, the Necromancy of Thay
}

local F = string.format

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local function at(game, id) return game.server(F("return { Osi.GetPosition(%q) }", id)) end

local function present(game, ids)
    return game.server("local n = 0 for _, id in ipairs(" .. lua_list(ids) .. ") do n = n + Osi.Exists(id) end return n")
end

local function spots(list)
    local t = {}
    for _, p in ipairs(list) do t[#t + 1] = F("%.2f, %.2f", p[1], p[3]) end
    return join(t, "; ")
end

function run(game)
    game.wait_state("Running", "server", 120)
    local first = step("three books at random spots", "cheat", function()
        local placed = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            local out = {}
            for i, template in ipairs(]] .. lua_list(BOOKS) .. [[) do
                local angle = math.rad((i - 1) * 120 + math.random() * 90)
                local r = 2 + math.random() * 4
                local id = GK.Stage.Spawn{ template = template, at = { x + r * math.cos(angle), y, z + r * math.sin(angle) } }[1]
                out[i] = { id = id, template = Osi.GetTemplate(id), name = Osi.ResolveTranslatedString(Osi.GetDisplayName(id)), at = { Osi.GetPosition(id) } }
            end
            return out
        ]])
        for i, b in ipairs(placed) do eq(uuid(b.template), BOOKS[i], "book " .. i .. ": its template") end
        local names, where = {}, {}
        for i, b in ipairs(placed) do names[i], where[i] = b.name, b.at end
        print("placed " .. join(names, ", ") .. " at " .. spots(where))
        return placed
    end)
    local ids, markers = {}, {}
    for i, b in ipairs(first) do ids[i] = b.id end
    step("a marker where each stands", "cheat", function()
        for i, b in ipairs(first) do
            markers[i] = game.server(F("return GK.Stage.Spawn{ template = 'potion', at = { %.17g, %.17g, %.17g }, exact = true }[1]", b.at[1], b.at[2], b.at[3]))
        end
    end)
    step("the spots written through the store", "cheat", function()
        local r = game.server([[
            local books = {}
            for i, id in ipairs(]] .. lua_list(ids) .. [[) do
                books[i] = { template = Osi.GetTemplate(id):match("(%x+%-%x+%-%x+%-%x+%-%x+)$"), at = { Osi.GetPosition(id) } }
            end
            return GK.Store.Set("]] .. NAME .. [[", nil, { books = books })
        ]])
        check(r.ok, "the store is written", r)
    end)
    step("the books taken out of the world", "cheat", function()
        game.server("for _, id in ipairs(" .. lua_list(ids) .. ") do GK.Stage.Remove(id) end return true")
        game.wait("server", "(function() local n = 0 for _, id in ipairs(" .. lua_list(ids) .. ") do n = n + Osi.Exists(id) end return n == 0 end)()", 10)
    end)
    step("the game saved", "cheat", function() game.checkpoint() end)
    step("the save loaded", "cheat", function() game.reset() end)
    step("a stage scope over the loaded world", "cheat", function()
        check(game.server("return GK.Stage.Begin{ fresh = true }").level ~= nil, "the scope is open")
    end)
    step("the save holds the markers and no book", "observation", function()
        eq(present(game, ids), 0, "none of the first books is in the loaded world")
        for i, m in ipairs(markers) do eq(at(game, m), first[i].at, "marker " .. i .. " stands where book " .. i .. " stood") end
    end)
    step("the store read", "observation", function()
        local s = game.server(F("return GK.Store.Get(%q, 'books')", NAME))
        for i, b in ipairs(first) do
            eq(s[i].template, BOOKS[i], "book " .. i .. ": its template")
            eq(s[i].at, b.at, "book " .. i .. ": its spot")
        end
    end)
    local again = step("the books placed from the store alone", "cheat", function()
        return game.server(F([[
            local out = {}
            for i, b in ipairs(GK.Store.Get(%q, "books")) do
                out[i] = GK.Stage.Spawn{ template = b.template, at = b.at, exact = true }[1]
            end
            return out
        ]], NAME))
    end)
    step("each book where the first placing put it", "observation", function()
        local where = {}
        for i, id in ipairs(again) do
            eq(uuid(game.server(F("return Osi.GetTemplate(%q)", id))), BOOKS[i], "book " .. i .. ": the same book")
            where[i] = at(game, id)
            check(near(where[i], at(game, markers[i]), 0.01), "book " .. i .. " stands on its marker", where[i])
            eq(where[i], first[i].at, "book " .. i .. " stands where it stood")
        end
        print("again at " .. spots(where))
    end)
    step("the store taken out", "cheat", function()
        check(game.server(F("return GK.Store.Set(%q, nil, {})", NAME)).ok, "emptied")
    end)
end
