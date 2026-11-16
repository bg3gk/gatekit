-- The character creation window through the general verbs (docs/contracts/lua-api.md, GK.Gui): no
-- code of the toolkit knows this window. The rules held here:
--   * a choice made through a list of the window reaches the server's definition: the race chosen in
--     `raceListBox` by its Guid is the definition's Race; a head chosen in the appearance panel
--     changes the definition's Visuals (the head items carry no Guid: the list's own selection is
--     what the game takes);
--   * a slider set inside its range is taken, a value outside it is refused;
--   * the point buy refuses a point it does not have: with every point spent the button that adds
--     one is disabled and the click is refused; a point given back enables it;
--   * the way on is refused while something is unfinished: with a point unspent the button to the
--     name page is disabled and the click is refused.
-- Staging: the level of character creation started fresh. Acting: fair (the window's own lists,
-- buttons and sliders). Reading the server's definition: observation.
-- Run: gatekit --instance <copy> test tests -k gui/look/creation

test { level = "GustavX:SYS_CC_I",
       about = "Character creation driven by the general window verbs: choices reach the server's definition, the point buy and the way on refuse what the game refuses." }

local W = "CharacterCreation"
local HUMAN = "0eb594cb-8820-4be6-a58d-8be7a1a98fba"
local DEFINITION = [[
    local e = Ext.Entity.GetAllEntitiesWithComponent("CCCharacterDefinition")[1]
    local d = e.CCCharacterDefinition.Definition
    return { race = d.Definition.Race, visuals = Ext.Types.Serialize(d.Visual.Visuals),
             choices = Ext.Types.Serialize(d.Visual.AdditionalChoices) } ]]

local function ui(game, code) return game.ui(code, 30) end
local function control(game, handle, options)
    local seen = ui(game, string.format("return GK.Gui.Look(%q, %s)", W, options or "nil"))
    for _, c in ipairs(seen.controls or {}) do if c.handle == handle then return c end end
end

function run(game)
    game.wait_state("Running", "server", 120)
    step("the window stands and nothing covers it", "fair", function()
        game.wait("ui", "GK.Gui.Widget('CharacterCreation') ~= nil", 60)
        ui(game, "return GK.Gui.Resolve()")
        game.wait("ui", "#GK.Gui.Look('CharacterCreation').controls > 5", 30)
    end)

    step("the race, by its Guid in the window's list", "fair", function()
        local tab = ui(game, string.format("return GK.Gui.Select(%q, 'gameplayTabs', 'raceTab')", W))
        check(tab.ok, "the race tab is chosen", tab)
        game.wait("ui", "GK.Gui.Handle('CharacterCreation', 'raceListBox') ~= nil", 10)
        local chosen = ui(game, string.format("return GK.Gui.Select(%q, 'raceListBox', %q)", W, HUMAN))
        check(chosen.ok and chosen.reads == chosen.selected, "the list reads the race as selected", chosen)
        game.wait("server", string.format(
            "Ext.Entity.GetAllEntitiesWithComponent('CCCharacterDefinition')[1].CCCharacterDefinition.Definition.Definition.Race == %q", HUMAN), 10)
    end)

    local before = step("the definition before the look is touched", "observation", function() return game.server(DEFINITION) end)

    step("the appearance panel, a head and a slider", "fair", function()
        local opened = ui(game, string.format("return GK.Gui.Click(%q, 'EditAppearanceButton')", W))
        check(opened.ok, "the appearance panel opens", opened)
        check(ui(game, string.format("return GK.Gui.Select(%q, 'appearanceTabs', 'generalTab')", W)).ok, "the general tab")
        game.wait("ui", "GK.Gui.Handle('CharacterCreation', 'SelectableHeadsListBox') ~= nil", 10)
        local heads = control(game, "SelectableHeadsListBox")
        check(heads and heads.count >= 2, "the heads are listed", heads)
        local other = heads.selected == 2 and 1 or 2
        local head = ui(game, string.format("return GK.Gui.Select(%q, 'SelectableHeadsListBox', %d)", W, other))
        check(head.ok and head.reads == other, "the list reads the head as selected", head)
        local age = ui(game, string.format("return GK.Gui.Set(%q, 'Age/Slider', 60)", W))
        check(age.ok and age.after == 60, "the slider takes a value inside its range", age)
        local far = ui(game, string.format("return GK.Gui.Set(%q, 'Age/Slider', 400)", W))
        check(not far.ok and far.max == 100, "a value outside the range is refused", far)
    end)

    step("the server's definition took the head and the slider", "observation", function()
        game.wait("server", string.format([[(function()
            local d = Ext.Entity.GetAllEntitiesWithComponent('CCCharacterDefinition')[1].CCCharacterDefinition.Definition
            return table.concat(Ext.Types.Serialize(d.Visual.Visuals), ',') ~= %q end)()]], table.concat(before.visuals, ",")), 10)
        local after = game.server(DEFINITION)
        check(table.concat(after.visuals, ",") ~= table.concat(before.visuals, ","), "the Visuals changed", after.visuals)
        local aged = false
        for _, value in ipairs(after.choices) do if math.abs(value - 0.6) < 0.01 then aged = true end end
        check(aged, "one of AdditionalChoices is the slider's 60 of 100", after.choices)
        eq(after.race, HUMAN, "the race stayed")
    end)

    step("the abilities page", "fair", function()
        check(ui(game, string.format("return GK.Gui.Click(%q, 'backToGameplayPage')", W)).ok, "back to the choices")
        game.wait("ui", "GK.Gui.Handle('CharacterCreation', 'gameplayTabs') ~= nil", 10)
        check(ui(game, string.format("return GK.Gui.Select(%q, 'gameplayTabs', 'abilityTab')", W)).ok, "the abilities tab")
        game.wait("ui", "GK.Gui.Handle('CharacterCreation', 'AddAbility') ~= nil", 10)
    end)

    step("the point buy refuses a point it does not have", "fair", function()
        local data = ui(game, "local d = GK.Gui.Data('CharacterCreation') return { unused = d.UnusedAbilityPoints, used = d.UsedAbilityPoints }")
        eq(data.unused, 0, "every point is spent as the page opens")
        local add = control(game, "AddAbility")
        check(add and add.enabled == false, "the button that adds a point is disabled", add)
        local refused = ui(game, string.format("return GK.Gui.Click(%q, 'AddAbility')", W))
        check(not refused.ok and contains(refused.error, "disabled"), "the click is refused", refused)
        eq(ui(game, "return GK.Gui.Data('CharacterCreation').UsedAbilityPoints"), data.used, "no point was spent")
    end)

    step("with a point unspent the way on is refused", "fair", function()
        local down = control(game, "ReduceAbility")
        local which = down and down.enabled ~= false and "ReduceAbility" or "ReduceAbility[2]"
        local reduced = ui(game, string.format("return GK.Gui.Click(%q, %q)", W, which))
        check(reduced.ok, "a point is given back", reduced)
        local unused = ui(game, "return GK.Gui.Data('CharacterCreation').UnusedAbilityPoints")
        check(unused >= 1, "what the lowered ability cost is unspent", unused)
        local proceed = control(game, "GameplayProceedButton")
        check(proceed and proceed.enabled == false, "the button to the name page is disabled", proceed)
        local refused = ui(game, string.format("return GK.Gui.Click(%q, 'GameplayProceedButton')", W))
        check(not refused.ok and contains(refused.error, "disabled"), "the click is refused", refused)
        local again = control(game, "AddAbility")
        check(again and again.enabled ~= false, "the point can be spent again", again)
    end)
end
