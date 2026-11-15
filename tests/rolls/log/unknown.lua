-- A roll the roll log cannot place is written all the same, and says so: the field it cannot fill
-- is `unknown` and the raw return address it was asked from (`caller`) is kept, so that a reader
-- can name the code later (docs/contracts/lua-api.md, section 17, GK.RollLog: "the row is still
-- written"). The roll: the healing dice of a potion of healing, a status's heal; the log knows the
-- asker (the dice of a damage expression) but no system it names as the cause stands above it.
-- Every asker the scenes of the roll log reach is named (test:rolls/log/origins); an asker of no
-- name is not staged here, so the unknown side shown is the cause, through the same rule.
-- Staging (cheat): the stage scope's status. Reading the row: observation.
-- When the heal's cause is named, this test fails and wants another roll the log cannot place.
-- Run: gatekit --instance <copy> test tests/rolls -k rolls/log/unknown

test { level = "GustavX:Basic_Level_A",
           about = "A roll asked from a place the log does not know is written and marked unknown, not dropped." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the roll log on", "observation", function()
        check(game.server("return GK.RollLog.Available()"), "this Script Extender build throws the roll event")
        game.server("return GK.EventLog.On('dice', { seconds = 600 })")
    end)
    local mark = game.mark()
    step("a potion's heal", "cheat", function()
        game.server(string.format("return GK.Stage.Status('POTION_OF_HEALING', %q, 0)", host))
    end)
    step("the row", "observation", function()
        local p = row(mark, "dice_thrown", function(p) return uuid(p.who) == host end, 30).payload
        eq(p.cause, "unknown", "the cause no named system holds")
        eq(p.causeId, nil, "no registry id for the cause")
        check(type(p.caller) == "string" and string.match(p.caller, "^bg3_dx11%.exe%+0x%x+$"), "the asking address is kept", p.caller)
        check(p.origin ~= nil and p.origin ~= "", "the asker is said, named or unknown", p.origin)
        check(#p.dice >= 1 and p.total ~= nil, "the dice and the total are written", p)
        -- the asker the row names is the one the log's table holds for that address
        local rva = tonumber(string.match(p.caller, "0x(%x+)$"), 16)
        local named = game.server(string.format("local o = GK.RollLog.Origin('bg3_dx11.exe', %d) return o and o.id or 'unknown'", rva))
        eq(p.origin, named, "the asker read from the log's table")
    end)
end
