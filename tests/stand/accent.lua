-- GK.Stand.Accent (stand/README.md): pointing at something without a line. The rule held here: on a
-- point the style's point effect plays and the camera settles there; on a creature 35 m off (out
-- of the camera's view) the camera glides to it without a cut, the style `danger` adds GK.Focus's
-- outline; each writes a row `stand_accent`; the camera comes back to the host by itself.
-- Staging (cheat): a hostile human 35 m from the host. The accent: cheat. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k stand/accent --speed working

test { level = "GustavX:Basic_Level_A",
       about = "GK.Stand.Accent glows on a point or a creature, takes the camera there and back, and writes a row." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local far = step("a hostile human 35 m off", "cheat", function()
        return uuid(game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            return GK.Stage.Spawn{ template = "human", at = { x, y, z + 35 }, faction = "hostile" }[1] ]]))
    end)
    local mark = game.mark()
    local onPoint = step("the accent on a point 6 m off", "cheat", function()
        return game.server("local x, y, z = Osi.GetPosition(Osi.GetHostCharacter()); return GK.Stand.Accent({ x - 4, y, z + 4.5 })", 30)
    end)
    step("the point's glow and the camera there", "observation", function()
        check(onPoint.ok and onPoint.glow == "spectral", "the point effect played", onPoint)
        check(onPoint.camera and onPoint.camera.ok, "the camera settled on the point", onPoint.camera)
    end)
    local onFar = step("the danger accent on the far human", "cheat", function()
        return game.server(string.format("return GK.Stand.Accent(%q, 'danger')", far), 30)
    end)
    step("a glide, the outline, two rows, and the camera back", "observation", function()
        check(onFar.ok and onFar.camera and onFar.camera.ok and not onFar.camera.snapped, "the camera glided to it", onFar)
        eq(onFar.marker, "outline", "the danger style's outline")
        local r = row(mark, "stand_accent", function(p) return p.style == "danger" end, 5)
        eq(uuid(r.payload.at), far, "the row names the human")
        local p = game.server("local x, y, z = Osi.GetPosition(Osi.GetHostCharacter()); return { x, y, z }")
        game.wait("client", string.format(
            "(function() local s = GK.Camera.State(); return s.moving == false and math.abs(s.target[1] - %f) < 0.6 and math.abs(s.target[3] - %f) < 0.6 end)()",
            p[1], p[3]), 10)
    end)
end
