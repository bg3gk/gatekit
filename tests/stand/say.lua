-- GK.Stand.Say (stand/README.md): the character's intent shown - a line over its head in the stand's
-- style and, with `at`, the accent on the thing: the glow, the camera's focus and its return, the
-- sound. The rule held here: the line is on the speaker when the verb answers (GK.Say.Shown), cut at
-- a word to GK.Stand.MaxChars when longer; `at` given by a name the party knows resolves to the
-- nearest creature of that name; the camera went to it (ok) and comes back to the speaker after
-- the style's hold; one row `stand_said` keeps the line, `think` and `do`. A name nobody has is
-- refused with nothing shown. What a person sees: the screenshot `say.jpg` (the line in violet
-- between pale guillemets, the human tinted violet over a violet-white burst).
-- Staging (cheat): a neutral human 5 m from the host. The line and accent: cheat (GK.Say).
-- Reading: observation.
-- Run: gatekit --instance <copy> test tests -k stand/say --speed working

test { level = "GustavX:Basic_Level_A",
       about = "GK.Stand.Say shows a line over the speaker, points the camera at `at` and back, and writes one row." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local human = step("a human beside the host", "cheat", function()
        local g = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            return GK.Stage.Spawn{ template = "human", at = { x + 2.5, y, z + 1.5 }, faction = "neutral" }[1] ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        return uuid(g)
    end)
    local name = game.server(string.format("return GK.See.Name(%q)", human))
    local mark = game.mark()
    local said = step("the intent, about the human by its name", "cheat", function()
        return game.server(string.format(
            "return GK.Stand.Say{ say = 'I will ask this one about the ship', think = 'he looks lost', ['do'] = 'a human nearby', at = %q }",
            name), 30)
    end)
    step("a picture of the line and the glow", "observation", function() game.shot("say.jpg", 0) end)
    step("the line on the speaker, the camera there and back", "observation", function()
        check(said.ok and said.line.ok, "the line came on the speaker", said)
        eq(uuid(said.accent.at), human, "the name resolved to the human")
        check(said.accent.camera and said.accent.camera.ok, "the camera settled on the human", said.accent)
        local shown = game.server(string.format("return GK.Say.Shown(%q)", said.who))
        eq(shown and shown.text, "I will ask this one about the ship", "the line the speaker shows")
        local r = row(mark, "stand_said", function(p) return uuid(p.at) == human end, 5)
        eq(r.payload.think, "he looks lost", "the row keeps the thought")
        eq(r.payload["do"], "a human nearby", "the row keeps the reason")
        local p = game.server(string.format("local x, y, z = Osi.GetPosition(%q); return { x, y, z }", said.who))
        game.wait("client", string.format(
            "(function() local s = GK.Camera.State(); return s.moving == false and math.abs(s.target[1] - %f) < 0.6 and math.abs(s.target[3] - %f) < 0.6 end)()",
            p[1], p[3]), 10)
    end)
    step("a long line cut at a word", "cheat", function()
        local long = game.server("return GK.Stand.Say{ say = string.rep('very ', 20) .. 'long', style = 'aside' }", 20)
        local max = game.server("return GK.Stand.MaxChars")
        check(long.ok and long.cut and #long.say <= max and long.say:sub(-3) == "...", "cut to the limit with ...", long.say, max)
    end)
    step("a name nobody has", "cheat", function()
        local none = game.server("return GK.Stand.Say{ say = 'over there', at = 'Nobody Of That Name' }", 20)
        check(none.accent and none.accent.ok == false, "the accent refused, the line still shown", none)
    end)
end
