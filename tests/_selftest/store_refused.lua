-- A value the save would lose is refused before it is written: GK.Store.Set answers
-- { ok = false, error, at } with the path of the offending part, and the store keeps what it held
-- (docs/contracts/lua-api.md, section 20, "What can be kept"). The Script Extender saves a variable
-- that holds a function or a userdata as nothing, brings a number key of a table that is no
-- plain list back as a text, and cannot write a table that holds itself
-- (research/studies/save-kept-data.md); the store also refuses nesting deeper than 64 and a
-- number that is not finite.


-- Each case is one offender, built in the game (a function, a userdata and a cycle cannot cross
-- to the host), put at the key `bad` of a store that holds `kept`. GK.Store.Check gives the same
-- path from the value's own root. No save is made: the refusal comes before the write.
-- Staging (cheat): the writes. Reading: observation.
-- Run: gatekit --instance <copy> test tests/_selftest -k store_refused

test { level = "GustavX:Basic_Level_A",
       about = "A value the save would lose is refused before it is written." }

local NAME = "gk_refused"

-- name, the Lua that builds the offender, the path of the offending part inside it, and inside
-- the store where that differs: the depth is counted from the store's root, one level above `bad`
local CASES = {
    { "a function", "return { list = { 1, 2 }, f = print }", "f" },
    { "a userdata", "return { who = Ext.Entity.Get(Osi.GetHostCharacter()) }", "who" },
    { "a number key", "return { name = 'x', [3] = 'y' }", "3" },
    { "itself", "local t = { a = {} } t.a.back = t return t", "a.back" },
    { "deeper than 64", "local t = {} for _ = 1, 70 do t = { x = t } end return t",
      ("x."):rep(63) .. "x", "bad." .. ("x."):rep(62) .. "x" },
    { "infinite", "return { 1, math.huge }", "2" },
    { "not a number", "return { n = 0 / 0 }", "n" },
}

local function offender(code) return "(function() " .. code .. " end)()" end

for _, c in ipairs(CASES) do
    local name, code, at, inStore = c[1], c[2], c[3], c[4] or ("bad." .. c[3])
    case(name, function(game)
        game.wait_state("Running", "server", 120)
        step("the store holds a value", "cheat", function()
            check(game.server("return GK.Store.Set('" .. NAME .. "', nil, { kept = 'Кодекс' })").ok, "written")
        end)
        step("the value checked", "observation", function()
            local r = game.server("return GK.Store.Check(" .. offender(code) .. ")")
            eq(r.ok, false, "refused by Check")
            eq(r.at, at, "the path of the offender")
        end)
        step("the offender written", "cheat", function()
            local r = game.server("return GK.Store.Set('" .. NAME .. "', 'bad', " .. offender(code) .. ")")
            eq(r.ok, false, "refused by Set")
            eq(r.at, inStore, "the path of the offender in the store")
            check(type(r.error) == "string" and r.error ~= "", "the reason is said", r)
        end)
        step("the store is unchanged", "observation", function()
            eq(game.server("return GK.Store.Get('" .. NAME .. "')"), { kept = "Кодекс" }, "what the store held")
        end)
        step("the store taken out", "cheat", function()
            check(game.server("return GK.Store.Set('" .. NAME .. "', nil, {})").ok, "emptied")
        end)
    end)
end
