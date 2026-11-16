-- GK.Show.Picture where there is no world (docs/contracts/lua-api.md, "GK.Show"): in the main
-- menu only the client runs, and the drawing layer draws there as in a level. The rule held here:
-- a picture over the whole screen is drawn in the client state Menu; with `skip = "any"` Enter
-- takes it off and is named in the row; the main menu under it stands as it stood.
-- Staging (cheat): the picture. The key is fair. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k show/menu

test { menu = true,
       about = "A picture over the main menu, where only the client runs: drawn in the state Menu, taken off by a key, the menu under it untouched." }

local FILE = "Mods/Gwynerva_Weave/ScriptExtender/Lua/ui/weave.png"

function run(game)
    step("the main menu stands", "observation", function() game.wait("ui", "GK.Gui.Stuck() == 'mainmenu'", 60) end)
    local mark = game.mark()
    local shown = step("a picture over the whole screen on black, off by any key", "cheat", function()
        local answer = game.ui(string.format(
            "return GK.Show.Picture{ file = %q, where = 'screen', back = 'black', fade = { 0.3, 0.3 }, skip = 'any' }", FILE))
        check(answer.ok, "the picture is drawn", answer)
        eq(answer.state, "Menu", "the client's state")
        return answer
    end)
    step("it is shown at full strength", "observation", function()
        game.wait("ui", string.format("(GK.Show.Shown(%q) or {}).phase == 'on'", shown.handle), 10)
    end)
    step("Enter", "fair", function() game.key("enter") end)
    step("it left as skipped by that key; the main menu stands", "observation", function()
        local gone = row(mark, "picture_hidden", function(p) return p.handle == shown.handle end, 10)
        eq(gone.payload.why, "skipped", "why it left")
        eq(gone.payload.key, "enter", "the key")
        eq(game.ui("return GK.Gui.Stuck()"), "mainmenu", "the main menu is still what is shown")
    end)
end
