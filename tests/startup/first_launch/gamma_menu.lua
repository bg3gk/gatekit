-- The gamma page over the main menu: GammaCalibration opened under MainMenu waits as the attention
-- state window:GammaCalibration (`setup`, with the switch ShowFirstTimeSetup off), its controls are
-- the menu's set - Cancel, Reset, Accept - and its AcceptButton raises CloseWidget, which closes it
-- and leaves the main menu standing (research/studies/startup-videos.md, "The first launch").
-- The game opens it by itself from the main menu while graphicSettings.lsx holds
-- ShowGammaCalibration 1 (code:gui::DCMainMenu::OnTryOpenGammaCalibrationCommand). A test cannot
-- set that flag: the file is the profile's (a test's files go into the game folder) and Script
-- Extender has no setter for the graphic settings. The page is opened by its own input event,
-- OpenGammaCalibration, raised from the main menu as a key raises it.
-- Venue: the main menu (`menu`). Classes: the event and the click (fair); reading the rows and
-- the pages (observation).
-- Run: gatekit --instance <copy> test tests -k startup/first_launch/gamma_menu

test { menu = true, surprises = { "setup" },
       about = "The gamma page over the main menu waits with its menu controls, and its Accept closes it back to the menu." }

local function control(controls, handle)
    for _, c in ipairs(controls or {}) do
        if c.handle == handle then return c end
    end
end

function run(game)
    local before = game.mark()
    step("the gamma page opened from the main menu", "fair", function()
        check(game.ui("return GK.Gui.Event('OpenGammaCalibration', 'MainMenu')").ok, "OpenGammaCalibration raised")
    end)
    step("it waits with the menu's controls", "observation", function()
        local row = game.wait_row(before, { "setup" }, function(p) return p.window == "GammaCalibration" end, 30)
        eq(row.key, "window:GammaCalibration", "the page waits under window:GammaCalibration")
        local page = row.payload
        eq(page.firstLaunch, false, "not a first launch")
        for _, handle in ipairs({ "GammaCalibration/Slider", "CancelButton", "ResetButton", "AcceptButton" }) do
            check(control(page.controls, handle), "the row names " .. handle, page.controls)
        end
        check(not control(page.controls, "ResetButtonFirstTimeSetup"), "the first launch's reset button is not shown", page.controls)
        local accept = control(page.controls, "AcceptButton")
        eq(accept.command, "CustomEvent", "AcceptButton runs the widget's CustomEvent")
        eq(accept.parameter, "CloseWidget", "with CloseWidget")
        check(game.ui("return GK.Gui.IsOpen('MainMenu')"), "the main menu stands under it")
    end)
    before = game.mark()
    step("Accept", "fair", function()
        check(game.ui("return GK.Gui.Click('GammaCalibration', 'AcceptButton')").ok, "AcceptButton clicked")
    end)
    step("the page closed, the main menu stands", "observation", function()
        game.wait_row(before, { "setup_ended" }, function(p) return p.window == "GammaCalibration" end, 30)
        game.wait_row(before, { "ui_page_ended" }, function(p) return p.page == "GammaCalibration" end, 30)
        check(not game.ui("return GK.Gui.IsOpen('GammaCalibration')"), "GammaCalibration is not open")
        check(game.ui("return GK.Gui.IsOpen('MainMenu')"), "the main menu stands")
    end)
end
