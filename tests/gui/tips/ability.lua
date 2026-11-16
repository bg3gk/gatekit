-- The tooltip of a hotbar action, called by its handle and read (docs/contracts/lua-api.md, GK.Gui,
-- "Tooltips"). The rule held here: the tooltip the game opens for a hotbar slot is about the action
-- in that slot - its title is the name the game's own stats give that action, and the first
-- sentence of the stats' description stands among its texts. The tooltip is opened as a hover
-- opens it (the tooltip's own IsOpen), with no pointer and no place on the screen.
-- Staging: none (the host's own hotbar). Calling the tooltip and closing it: fair. Reading it and
-- the stats: observation.
-- Run: gatekit --instance <copy> test tests -k gui/tips/ability

test { level = "GustavX:Basic_Level_A",
       about = "A hotbar action's tooltip, called by handle, names the action and carries its description." }

local SLOT = "HotbarItemsList#1"

function run(game)
    game.wait_state("Running", "server", 120)
    step("the hotbar stands and no tooltip is open", "fair", function()
        game.wait("ui", "GK.Gui.Widget('HotBar') ~= nil and GK.Gui.Handle('HotBar', 'HotbarItemsList') ~= nil", 60)
        check(game.ui("return GK.Gui.CloseTips()").ok, "nothing is left open")
    end)

    local tip = step("the slot's tooltip, by its handle", "fair", function()
        local opened = game.ui(string.format("return GK.Gui.Tip('HotBar', %q)", SLOT), 30)
        check(opened.ok, "the tooltip stands open", opened)
        return opened.tip
    end)

    step("it names the action and carries its description", "observation", function()
        need(tip.about and tip.about.spell, "the first slot of the hotbar holds an action of the game's stats")
        local stats = game.ui(string.format([[
            local stat = Ext.Stats.Get(%q)
            return { name = Ext.Loca.GetTranslatedString(stat.DisplayName), description = Ext.Loca.GetTranslatedString(stat.Description) } ]],
            tip.about.spell))
        eq(tip.title, stats.name, "the title is the action's own name")
        eq(tip.pinned, false, "a tooltip called up is not pinned")
        check(tip.owner and tip.owner.widget == "HotBar", "it says which window it was called from", tip.owner)
        -- the description as the stats write it, up to its first tag (a keyword's link or a parameter)
        local opening = string.match(stats.description, "^[^<%[]+")
        need(opening and #opening >= 8, "the action's description opens with plain text")
        local found = false
        for _, row in ipairs(tip.texts) do
            if string.sub(row.text, 1, #opening) == opening then found = true end
        end
        check(found, "one of the tooltip's texts opens as the stats' description does", opening, tip.texts)
        local listed = game.ui("return GK.Gui.Tips()")
        eq(#listed.tips, 1, "one tooltip stands open")
        eq(listed.tips[1].title, tip.title, "the chain reads the same tooltip")
    end)

    step("closed, none is left", "fair", function()
        local closed = game.ui("return GK.Gui.CloseTips()", 30)
        check(closed.ok and #closed.tips == 0, "no tooltip stands", closed)
    end)
end
