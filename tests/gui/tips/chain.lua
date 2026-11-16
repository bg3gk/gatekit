-- Tooltips nest: a pinned tooltip's link opens a second tooltip (docs/contracts/lua-api.md, GK.Gui,
-- "Tooltips"). The rules held here:
--   * the game's pin (its input event UIPinTooltip, the player's key) moves the open tooltip into
--     the game's list of pinned ones, and the pinned one keeps what it showed;
--   * a keyword of a pinned tooltip's text is a link with a tooltip of its own; called up, that
--     tooltip is about what the link names, and its parent in the chain is the pinned tooltip;
--   * pinned in turn, the two stand together, the second still under the first;
--   * the game's clearing of pinned tooltips leaves none.
-- The test needs a hotbar action whose description holds a keyword link (Jump names Strength); it
-- takes the first slot that has one. Nothing rests on a place of the screen.
-- Staging: none. Calling, pinning and closing: fair. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k gui/tips/chain

test { level = "GustavX:Basic_Level_A",
       about = "A pinned tooltip's link opens a second tooltip whose parent is the first; clearing leaves none." }

local function ui(game, code) return game.ui(code, 30) end

function run(game)
    game.wait_state("Running", "server", 120)
    step("the hotbar stands and no tooltip is open", "fair", function()
        game.wait("ui", "GK.Gui.Widget('HotBar') ~= nil and GK.Gui.Handle('HotBar', 'HotbarItemsList') ~= nil", 60)
        check(ui(game, "return GK.Gui.CloseTips()").ok, "nothing is left open")
    end)

    local first, link = step("a slot whose tooltip holds a keyword link", "fair", function()
        for slot = 1, 8 do
            local opened = ui(game, string.format("return GK.Gui.Tip('HotBar', 'HotbarItemsList#%d')", slot))
            if opened.ok then
                for _, candidate in ipairs(opened.tip.links) do
                    if candidate.tag then return opened.tip, candidate end
                end
            end
        end
        need(false, "one of the first hotbar slots has a keyword link in its tooltip")
    end)

    local pinned = step("the tooltip pinned by the game's own key", "fair", function()
        local answer = ui(game, "return GK.Gui.PinTip()")
        check(answer.ok, "the game pinned it", answer)
        return answer.pinned
    end)

    step("the pinned one keeps what it showed", "observation", function()
        eq(pinned.pinned, true, "it is pinned")
        eq(pinned.title, first.title, "the same title")
        eq(#pinned.texts, #first.texts, "the same texts")
        local listed = ui(game, "return GK.Gui.Tips()")
        eq(#listed.tips, 1, "one tooltip stands, the pinned one")
        eq(listed.tips[1].handle, pinned.handle, "the chain reads it by the same handle")
    end)

    local second = step("the link's own tooltip", "fair", function()
        local linkHandle
        for _, candidate in ipairs(pinned.links) do
            if candidate.tag == link.tag then linkHandle = candidate.handle end
        end
        check(linkHandle, "the pinned tooltip holds the same link", pinned.links)
        local opened = ui(game, string.format("return GK.Gui.Tip(%q)", linkHandle))
        check(opened.ok, "the link's tooltip stands open", opened)
        return opened.tip
    end)

    step("the second is about the link and its parent is the first", "observation", function()
        eq(second.parent, pinned.handle, "the parent is the pinned tooltip")
        eq(second.pinned, false, "it is not pinned yet")
        check(second.about and second.about.of == link.of, "it is about what the link names", second.about, link)
        check(second.title and second.title ~= pinned.title, "it has a title of its own", second.title)
    end)

    step("pinned in turn, the two stand together", "fair", function()
        local answer = ui(game, "return GK.Gui.PinTip()")
        check(answer.ok, "the game pinned the second", answer)
        eq(answer.pinned.parent, pinned.handle, "the second pinned tooltip is still under the first")
        local listed = ui(game, "return GK.Gui.Tips()")
        eq(#listed.tips, 2, "two tooltips stand")
        check(listed.tips[1].pinned and listed.tips[2].pinned, "both are pinned", listed.tips)
        local apart = ui(game, string.format("return GK.Gui.CloseTips(%q)", listed.tips[1].handle))
        check(not apart.ok, "one pinned tooltip among several is not closed apart", apart)
    end)

    step("cleared, none is left", "fair", function()
        local closed = ui(game, "return GK.Gui.CloseTips()")
        check(closed.ok and #closed.tips == 0, "no tooltip stands", closed)
        eq(#ui(game, "return GK.Gui.Tips()").tips, 0, "the chain is empty")
        check(not ui(game, "return GK.Gui.IsOpen('PinnedTooltips')"), "the widget of pinned tooltips is gone")
    end)
end
