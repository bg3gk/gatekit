-- A colour of character creation chosen by handle (docs/contracts/lua-api.md, GK.Gui, "Look"). The
-- skin colour's swatch is a toggle button that stands for the colour chosen now and does one thing
-- when pressed: it shows the panel of colours, whose list is in the window's tree whether the panel
-- is shown or not. The rules held here:
--   * the swatch reads as a control whose text is the name of the colour chosen now, and the list
--     of its options is the list of colours whose selected item carries that name;
--   * a colour selected in that list by its handle reaches the server's definition (its
--     SkinColor changes), and the swatch then reads the name of the colour chosen.
-- Nothing rests on a place of the screen, and the panel is never opened.
-- Staging: the level of character creation started fresh. Acting: fair (the window's own button,
-- tab and list). Reading the window and the server's definition: observation.
-- Run: gatekit --instance <copy> test tests -k gui/look/swatch

test { level = "GustavX:SYS_CC_I",
       about = "A skin colour selected by handle in the swatch's list reaches the server's definition, and the swatch reads it." }

local W = "CharacterCreation"
local SKIN = "Ext.Entity.GetAllEntitiesWithComponent('CCCharacterDefinition')[1].CCCharacterDefinition.Definition.Visual.SkinColor"

local function ui(game, code) return game.ui(code, 30) end
local function controls(game, options)
    return ui(game, string.format("return GK.Gui.Look(%q, %s)", W, options or "nil")).controls or {}
end
local function swatchOf(game)
    for _, c in ipairs(controls(game)) do if c.handle == "swatchSkinColourButton" then return c end end
end

function run(game)
    game.wait_state("Running", "server", 120)
    step("the appearance panel, its general tab", "fair", function()
        game.wait("ui", "GK.Gui.Widget('CharacterCreation') ~= nil", 60)
        ui(game, "return GK.Gui.Resolve()")
        -- the window may stand on any of its pages: the appearance page is entered only when it is not shown
        game.wait("ui", "GK.Gui.Handle('CharacterCreation', 'EditAppearanceButton') ~= nil or GK.Gui.Handle('CharacterCreation', 'appearanceTabs') ~= nil", 30)
        if not ui(game, "return GK.Gui.Handle('CharacterCreation', 'appearanceTabs') ~= nil") then
            check(ui(game, string.format("return GK.Gui.Click(%q, 'EditAppearanceButton')", W)).ok, "the appearance panel opens")
        end
        check(ui(game, string.format("return GK.Gui.Select(%q, 'appearanceTabs', 'generalTab')", W)).ok, "the general tab")
        game.wait("ui", "GK.Gui.Handle('CharacterCreation', 'swatchSkinColourButton') ~= nil", 10)
    end)

    local swatch, colours, before = step("the swatch, its colours and the definition", "observation", function()
        local button = swatchOf(game)
        check(button and button.kind == "toggle" and button.text, "the swatch is a toggle named by the colour chosen now", button)
        eq(button.data, "ls.VMSelectableColour", "it stands for a colour")
        local list
        for _, c in ipairs(controls(game, "{ all = true, items = 400 }")) do
            if c.kind == "list" and c.items[1] and c.items[1].type == "ls.VMSelectableColour" and c.value == button.text then list = c end
        end
        need(list, "a list of colours whose selected item is the swatch's colour is in the window's tree")
        check(list.count >= 2, "it offers more than one colour", list.count)
        return button, list, game.server("return " .. SKIN)
    end)

    local other = colours.selected == 2 and 1 or 2
    local chosen = step("another colour, by the list's handle", "fair", function()
        local answer = ui(game, string.format("return GK.Gui.Select(%q, %q, %d)", W, colours.handle, other))
        check(answer.ok and answer.reads == other, "the list reads the colour as selected", answer)
        return answer
    end)

    step("the definition took it and the swatch reads it", "observation", function()
        game.wait("server", string.format("%s ~= %q", SKIN, before), 10)
        check(game.server("return " .. SKIN) ~= before, "the definition's SkinColor changed", before)
        game.wait("ui", string.format([[(function()
            for _, c in ipairs(GK.Gui.Look('CharacterCreation').controls) do
                if c.handle == 'swatchSkinColourButton' then return c.text == %q end
            end end)()]], chosen.text), 10)
        local button = swatchOf(game)
        eq(button.text, chosen.text, "the swatch is named by the colour chosen")
        check(button.text ~= swatch.text, "it is another colour than before", swatch.text)
    end)
end
