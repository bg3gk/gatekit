-- The first launch, walked as a player walks it: while the game's switch ShowFirstTimeSetup is on,
-- the interface shows Eula, then SensitiveSettings (the explicit-content choice), then
-- GammaCalibration (on a display without HDR), then the "press any key" page, then the main menu,
-- whose page turns the switch off (research/studies/startup-videos.md, "The first launch";
-- code:gui::DCMainMenu::OnCheckFirstTimeSetupValueCommand). Each page is an attention state of its
-- own, key window:<page> (`setup`, the splash screen `splash`), opened with the controls a player
-- has, and every page entered and left is a `ui_page` / `ui_page_ended` row
-- (docs/contracts/bridge-protocol.md, "Attention"). The choices made are what the game keeps: the
-- explicit-content choice is the switches ShowGenitals and ShowExplicitRomanceScenes after
-- SensitiveSettings' Next, the gamma the view model's Gamma as the slider moves and the profile's
-- graphicSettings.lsx SDRGamma after Accept.
-- Each page is awaited by its own row, never by a time. The choice and the gamma are set to
-- something other than what the copy holds, so two runs in a row each change them.
-- HDRCalibration is not walked: it shows instead of GammaCalibration only on an HDR display
-- (SensitiveSettings' Next raises OpenHDR when gui::DCFirstTimeSetup's HasHDR is true), and the
-- machine this was written on has none. The mod policy page (ModBrowserSplashScreen) shows only
-- while WasLarianModPolicyAccepted is off, which Lua cannot write.
-- Venue: a start of its own as a first launch (`first_launch`: up --first-launch sets the switch
-- at the first Lua and stops on Eula). The pages write the copy's own profile.
-- Classes: the clicks, the choice, the slider and the key (fair); reading the rows, the pages and
-- the switches (observation).
-- Run: gatekit --instance <copy> test tests -k startup/first_launch/walk

test { first_launch = true, surprises = { "setup", "splash" },
       about = "The first-launch pages each wait as an attention state with their controls, and the choices made on them are what the game keeps; the main menu turns the switch off." }

local PAGES = { "Eula", "SensitiveSettings", "GammaCalibration", "SplashScreen", "MainMenu" }

-- A cursor at the start of the client's current boot: the rows the start wrote before the test.
local function boot_start(game)
    return (string.gsub(game.mark(), "client:(%d+)%.%d+", "client:%1.0"))
end

-- The attention row that opened page `page` after `since`.
local function opened(game, since, kind, page)
    local row = game.wait_row(since, { kind }, function(p) return p.window == page end, 60)
    eq(row.key, "window:" .. page, "the page waits under window:<page>")
    return row.payload
end

local function control(controls, handle)
    for _, c in ipairs(controls or {}) do
        if c.handle == handle then return c end
    end
end

local function switches(game)
    return game.client([[
        local s = Ext.Utils.GetGlobalSwitches()
        return { first = s.ShowFirstTimeSetup, genitals = s.ShowGenitals, romance = s.ShowExplicitRomanceScenes }
    ]])
end

function run(game)
    local start = boot_start(game)
    step("Eula waits with its controls, the switch on", "observation", function()
        eq(switches(game).first, true, "ShowFirstTimeSetup is on")
        local eula = opened(game, start, "setup", "Eula")
        eq(eula.firstLaunch, true, "the row says the switch was on")
        local accept = control(eula.controls, "AcceptBtn")
        check(accept, "the row names Eula's accept button", eula.controls)
        eq(accept.command, "CustomEvent", "AcceptBtn runs the widget's CustomEvent")
        eq(accept.parameter, "OpenSensitiveSettings", "with the event of the next page")
        eq(control(eula.controls, "ToSButton").command, "OpenURLInBrowserCommand", "the terms open in the browser")
    end)

    local before = game.mark()
    step("Eula accepted", "fair", function()
        check(game.ui("return GK.Gui.Click('Eula', 'AcceptBtn')").ok, "AcceptBtn clicked")
    end)
    local wantYes
    step("SensitiveSettings: one choice, set to the other answer", "fair", function()
        local page = opened(game, before, "setup", "SensitiveSettings")
        local combo = control(page.controls, "comboBox")
        check(combo and combo.kind == "list", "the explicit-content choice is one list", page.controls)
        eq(#combo.items, 2, "two answers: no and yes")
        check(not control(page.controls, "ComboBoxButton") and not control(page.controls, "Options"),
            "the combo box's opener and the settings list are not controls of their own", page.controls)
        local look = game.ui("return GK.Gui.Look('SensitiveSettings')")
        local now = control(look.controls, "comboBox").selected
        local wanted = now == 1 and 2 or 1
        wantYes = wanted == 2
        local chose = game.ui(string.format("return GK.Gui.Select('SensitiveSettings', 'comboBox', %d)", wanted))
        check(chose.ok and chose.reads == wanted, "the list reads the answer chosen", chose)
        game.save("choice.txt", fmt({ was = now, chose = wanted, yes = wantYes }) .. "\n")
    end)

    before = game.mark()
    step("SensitiveSettings: Next", "fair", function()
        check(game.ui("return GK.Gui.Click('SensitiveSettings', 'NextButton')").ok, "NextButton clicked")
    end)
    local target
    step("GammaCalibration: the slider moved and its view model holds it", "fair", function()
        local page = opened(game, before, "setup", "GammaCalibration")
        local slider = control(page.controls, "GammaCalibration/Slider")
        check(slider and slider.kind == "slider", "the row names the slider", page.controls)
        target = math.abs((slider.value or 1) - 1.1) < 0.01 and 1.2 or 1.1
        local set = game.ui(string.format("return GK.Gui.Set('GammaCalibration', 'GammaCalibration/Slider', %.2f)", target))
        check(set.ok and set.held and set.held.Gamma, "Set answers once the view model's Gamma holds the value", set)
        check(math.abs(set.held.Gamma - target) < 1e-4, "Gamma holds the slider's value", set.held, target)
        local back = game.ui("return GK.Gui.Data('GammaCalibration').Gamma")
        check(math.abs(back - target) < 1e-4, "the view model reads it back", back, target)
    end)
    step("the explicit-content choice is the game's switches", "observation", function()
        local s = switches(game)
        eq(s.genitals, wantYes, "ShowGenitals is the answer chosen")
        eq(s.romance, wantYes, "ShowExplicitRomanceScenes is the answer chosen")
    end)

    before = game.mark()
    step("GammaCalibration accepted", "fair", function()
        check(game.ui("return GK.Gui.Click('GammaCalibration', 'AcceptButton')").ok, "AcceptButton clicked")
    end)
    step("the splash screen waits for a key; the gamma is in the profile", "observation", function()
        local splash = opened(game, before, "splash", "SplashScreen")
        check(splash.keys, "the row says which keys pass it", splash)
        eq(#(splash.controls or {}), 0, "the page has no control")
        local text = game.read(game.profile .. "/graphicSettings.lsx")
        check(text, "the copy's profile has graphicSettings.lsx", game.profile)
        local saved = tonumber(string.match(text, 'value="SDRGamma".-id="Value"[^>]-value="([%d%.]+)"'))
        check(saved, "graphicSettings.lsx holds SDRGamma")
        check(math.abs(saved - target) < 1e-3, "the profile keeps the gamma accepted", saved, target)
    end)

    before = game.mark()
    step("the splash screen's key", "fair", function() game.key("space") end)
    step("the main menu, and the switch off", "observation", function()
        game.wait_row(before, { "splash_ended" }, nil, 30)
        game.wait_row(before, { "ui_page" }, function(p) return p.page == "MainMenu" end, 30)
        -- the switch has no event of its own: the main menu's page turns it off as it loads
        check(game.wait("client", "Ext.Utils.GetGlobalSwitches().ShowFirstTimeSetup == false", 10), "ShowFirstTimeSetup is off")
        local s = switches(game)
        eq(s.genitals, wantYes, "the choice outlives the pages")
    end)
    step("every page was entered and left in order", "observation", function()
        local rows = game.events(start, { "ui_page", "ui_page_ended" }).rows
        local entered, left = {}, {}
        for _, r in ipairs(rows) do
            if r.kind == "ui_page" then entered[#entered + 1] = r.payload.page else left[r.payload.page] = true end
        end
        local at = 1
        for _, page in ipairs(entered) do
            if page == PAGES[at] then at = at + 1 end
        end
        eq(at, #PAGES + 1, "ui_page names each page in order", entered)
        for i = 1, #PAGES - 1 do check(left[PAGES[i]], "ui_page_ended for " .. PAGES[i], rows) end
    end)
end
