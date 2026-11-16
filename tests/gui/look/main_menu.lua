-- GK.Gui.Look reads any open widget by walking it (docs/contracts/lua-api.md, GK.Gui): the main
-- menu, which has no entry of its own anywhere in the toolkit, comes out as its buttons, each with
-- a handle, the text a player reads on it and whether it can be pressed. The rule held here: the
-- walk finds the buttons by what they are; every handle is unique and leads back to its control;
-- a handle nothing has is refused by name.
-- Staging: none. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k gui/look/main_menu

test { menu = true,
       about = "The main menu read as a window like any other: its buttons with handles and texts, found by walking the widget." }

function run(game)
    local look = step("the main menu's controls", "observation", function()
        game.wait("ui", "GK.Gui.Stuck() == 'mainmenu'", 60)
        local seen = game.ui("return GK.Gui.Look('MainMenu')")
        check(seen.ok, "the main menu is read", seen)
        return seen
    end)
    step("buttons with texts and unique handles", "observation", function()
        local handles, buttons, texts = {}, 0, 0
        for _, control in ipairs(look.controls) do
            check(not handles[control.handle], "a handle names one control", control.handle)
            handles[control.handle] = true
            if control.kind == "button" then
                buttons = buttons + 1
                if control.text then texts = texts + 1 end
            end
        end
        check(buttons >= 4, "the menu's buttons are found", buttons)
        check(texts >= 4, "the buttons carry the texts a player reads", texts)
        print("main menu: " .. #look.controls .. " controls, " .. buttons .. " buttons, " .. texts .. " with a text")
    end)
    step("a handle leads back to its control, an unknown one is refused", "observation", function()
        local first = look.controls[1].handle
        local back = game.ui(string.format("local row, why = GK.Gui.Handle('MainMenu', %q) return { found = row ~= nil, kind = row and row.kind, why = why }", first))
        check(back.found, "the first handle resolves", first, back)
        local none = game.ui("local row, why = GK.Gui.Handle('MainMenu', 'NoSuchControl/Button[9]') return { found = row ~= nil, why = why }")
        check(not none.found and contains(none.why, "no control"), "an unknown handle is refused by name", none)
    end)
end
