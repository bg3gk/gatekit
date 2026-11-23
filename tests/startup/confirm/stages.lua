-- The start, step by step: each stage of research/studies/startup-videos.md, "The start, step by
-- step", is marked by a row of Script Extender's fork - the hook of the function that runs it, or the
-- fork's read of the game's own fields - and the rows come in the table's order:
--   - the engine's start: code:ls::PackedFileManager::LoadPackFiles, then
--     code:ls::TranslatedStringRepository::LoadFromContextFolder, then
--     code:App::LoadAndResaveGraphicsSettings, all before code:App::InitGame;
--   - the game's start: code:App::InitGame holds code:App::InitializeResourceManager and the
--     server's first state, and the client's first state, InitMenu, comes only after it returns
--     (code:ecl::EocClient::Load, its last call, queues it through
--     code:ecl::GameStateMachine::SetTargetState); the hint list it loaded
--     (code:ecl::HintManager::Visit) already holds the game's own Hints.lsx when StartLoading is
--     entered;
--   - the client's states in Load's order, InitMenu, StartLoading, Init, LoadModule, LoadMenu,
--     StopLoading, Menu, and nothing else - but on the Vulkan executable (bg3.exe) LoadPsoCache
--     comes between Init and LoadModule;
--   - StartLoading: the game's playlist is its one entry Splash_Logo_Larian
--     (code:ecl::GameStateStartLoading::MakeSimple, code:EoCFS::strSplashLogoLarian), the loading
--     screen is shown as the state is entered (code:ecl::GameStateStartLoading::Enter), and its
--     active load state while the clip plays (code:ecl::LoadStateMovie::LoadStateMovie,
--     code:ecl::LoadStateMovie::Start) is another than the one after it;
--   - the loading segments (code:ls::LoadingScreenManager): the first start carries two (three on
--     Vulkan, LoadPsoCache counting as one), and the
--     segments done grow by one each time the client leaves Init, LoadModule and LoadMenu
--     (code:ecl::GameStateMachine::Update), never on leaving InitMenu or StartLoading;
--   - Menu: the load states' queue is empty (code:ecl::GameStateStopLoading::Exit) and the loading
--     screen hidden in the tick the client enters it; the menu's own timelines start in LoadMenu,
--     under the loading screen (code:eoc::TimelineDataComponent.PlaybackState 0 -> 2); the
--     interface reaches the "press any key" page after Menu (code:ecl::GameStateMenu::Enter);
--   - the hints drawn (code:gui::DCLoadingScreen::OnAdvanceHintCommand) are entries of that list.
-- The startup clip's own end, the loading screen hidden at Menu and the descent are
-- test:startup/videos/game_clip and test:startup/loads/start; the list's layout is
-- test:startup/hints/two_mods.
-- Venue: a start of its own with the startup videos on (`startup_videos`), run when the bridge
-- first answers (`until_lua`); the test waits for the main menu's page and reads the rows after the
-- fact (Ext.StartupVideos.Timeline(): nothing of Lua exists before LoadModule).
-- Class: observation.
-- Run: gatekit --instance <copy> test tests -k startup/confirm/stages

test { until_lua = true, startup_videos = true,
       about = "Each stage of the start is marked by its function's hook or the fork's read of the game's fields, in the order of the study's table." }

local GAME = "Splash_Logo_Larian"
local MENU_TIMELINES = {   -- MainMenu/MM_Open and MainMenu/MM_Open_Boats, the menu's timelines playing before its key
    "02c1cb82-0d8e-9233-6ac5-79feaf9a43ff", "20cb00dc-154f-4bbf-6611-5e8c62e6321b" }
local SEGMENTS = { Init = true, LoadModule = true, LoadMenu = true }

-- The rows, in a form the host checks: the engine's steps until the client's first state, the
-- client's states, and the rows each stage is marked by.
local function read(game)
    return game.client([[
        local r = { steps = {}, states = {}, loads = {}, progress = {}, playing = {}, hints = {}, own = 0 }
        local text = Ext.IO.LoadFile("Public/Game/Hints/Hints.lsx", "data") or ""
        for _ in text:gmatch('id="HintKey"') do r.own = r.own + 1 end
        r.game = Ext.StartupVideos.GetPlaylist().Game
        for _, e in ipairs(Ext.StartupVideos.Timeline()) do
            if e.What == "Step" and #r.states == 0 and e.Phase then
                r.steps[#r.steps + 1] = { name = e.Name, phase = e.Phase, ms = e.Ms }
            elseif e.What == "State" and e.Side == "Server" and not r.server then
                r.server = { from = e.From, to = e.To, ms = e.Ms }
            elseif e.What == "State" and e.Side == "Client" then
                r.states[#r.states + 1] = { from = e.From, to = e.To, ms = e.Ms }
            elseif e.What == "HintsComposed" and not r.composed then
                r.composed = { game = e.GameCount, count = e.Count, ms = e.Ms }
            elseif e.What == "LoadingScreenShown" and not r.shown then
                r.shown = e.Ms
            elseif e.What == "LoadingScreenHidden" and not r.hidden then
                r.hidden = e.Ms
            elseif e.What == "LoadState" then
                r.loads[#r.loads + 1] = { name = e.Name, ms = e.Ms }
            elseif e.What == "VideoStarted" and not r.started then
                r.started = e.Ms
            elseif e.What == "VideoStopped" and not r.stopped then
                r.stopped = e.Ms
            elseif e.What == "LoadingProgress" then
                r.progress[#r.progress + 1] = { done = e.Progress, total = e.ProgressTotal, ms = e.Ms }
            elseif e.What == "TimelinePlayback" and e.From == 0 and e.To == 2 then
                r.playing[#r.playing + 1] = { name = e.Name, ms = e.Ms }
            elseif e.What == "HintShown" then
                r.hints[#r.hints + 1] = { index = e.Index, count = e.Count, key = e.Name }
            elseif e.What == "UIState" and e.Name == "SplashScreen" and e.Change == "Entered" and not r.splash then
                r.splash = e.Ms
            end
        end
        return r
    ]], 60)
end

-- The time of the first step row of `name` in `phase` ("Begin", "End"), or nil.
local function at(steps, name, phase)
    for _, s in ipairs(steps) do
        if s.name == name and s.phase == phase then return s.ms end
    end
end

-- The time the client entered `state`, or nil.
local function entered(r, state)
    for _, s in ipairs(r.states) do
        if s.to == state then return s.ms end
    end
end

-- The rows of `list` from `from` (inclusive) to `to` (exclusive), by time.
local function between(list, from, to)
    local out = {}
    for _, e in ipairs(list) do
        if e.ms >= from and (not to or e.ms < to) then out[#out + 1] = e end
    end
    return out
end

function run(game)
    step("the main menu's page is up", "observation", function()
        need(game.wait("ui", "(GK.Gui.Stuck()) == 'splash'", 120), "the start reaches the press-any-key page")
    end)
    local r = step("the start's rows, read after the fact", "observation", function()
        local r = read(game)
        game.save("rows.txt", fmt(r) .. "\n")
        check(#r.states > 0 and #r.steps > 0, "the timeline holds the engine's steps and the client's states", r)
        return r
    end)

    step("the engine's start: the paks, the localization, the graphic settings, then the game's start", "observation", function()
        local pakB, pakE = at(r.steps, "ls::PackedFileManager::LoadPackFiles", "Begin"), at(r.steps, "ls::PackedFileManager::LoadPackFiles", "End")
        local locB, locE = at(r.steps, "ls::TranslatedStringRepository::LoadFromContextFolder", "Begin"),
                           at(r.steps, "ls::TranslatedStringRepository::LoadFromContextFolder", "End")
        local graphics = at(r.steps, "App::LoadAndResaveGraphicsSettings", "End")
        local gameB = at(r.steps, "App::InitGame", "Begin")
        check(pakB and pakE and locB and locE and graphics and gameB, "every hooked step of the engine's start ran before the client's first state", r.steps)
        check(pakB <= pakE and pakE <= locB and locB <= locE, "the game's paks are mounted before the localization is loaded", r.steps)
        check(locE <= graphics and graphics <= gameB, "the graphic settings come after the localization and before the game's start", r.steps)
    end)

    step("the game's start: the resources and the server inside InitGame, then the client's first state", "observation", function()
        local gameB, gameE = at(r.steps, "App::InitGame", "Begin"), at(r.steps, "App::InitGame", "End")
        local resB, resE = at(r.steps, "App::InitializeResourceManager", "Begin"), at(r.steps, "App::InitializeResourceManager", "End")
        check(gameB and gameE and resB and resE, "App::InitGame and App::InitializeResourceManager ran", r.steps)
        check(gameB <= resB and resB <= resE and resE <= gameE, "the resources are loaded inside the game's start", r.steps)
        check(r.server and r.server.from == "Unknown" and r.server.to == "Uninitialized", "the server's machine started", r.server)
        check(gameB <= r.server.ms and r.server.ms <= gameE, "the server's machine starts inside the game's start", r.server, gameB, gameE)
        local first = r.states[1]
        eq({ first.from, first.to }, { "Unknown", "InitMenu" }, "the client's first state is InitMenu")
        check(first.ms >= gameE, "the client's first state comes after the game's start returned", first, gameE)
        check(r.composed, "the hint list was read at StartLoading", r)
        check(r.own > 0, "the game's own Hints.lsx was read")
        eq(r.composed.game, r.own, "at StartLoading the list holds the entries of the game's own Hints.lsx")
    end)

    step("the client's states, in the order the game's start queues them", "observation", function()
        local vulkan = false
        for _, s in ipairs(r.states) do if s.to == "LoadPsoCache" then vulkan = true end end
        local want = vulkan
            and { "Unknown -> InitMenu", "InitMenu -> StartLoading", "StartLoading -> Init", "Init -> LoadPsoCache",
                  "LoadPsoCache -> LoadModule", "LoadModule -> LoadMenu", "LoadMenu -> StopLoading", "StopLoading -> Menu" }
            or { "Unknown -> InitMenu", "InitMenu -> StartLoading", "StartLoading -> Init", "Init -> LoadModule",
                 "LoadModule -> LoadMenu", "LoadMenu -> StopLoading", "StopLoading -> Menu" }
        local path = {}
        for i = 1, #want do
            local s = r.states[i]
            path[#path + 1] = s and (s.from .. " -> " .. s.to) or "-"
        end
        eq(path, want, "the start's client states")
    end)

    step("StartLoading: the game's playlist, the loading screen, the movie's load state", "observation", function()
        eq(r.game, { GAME }, "the game's own playlist is its one startup entry")
        local start, left = entered(r, "StartLoading"), entered(r, "Init")
        check(r.shown and r.shown >= start and r.shown - start < 100, "the loading screen is shown as StartLoading is entered", r.shown, start)
        local during = between(r.loads, start, left)
        check(#during >= 1 and during[1].name ~= "none", "a load state is active while the clip plays", during)
        check(r.started and r.started >= start and r.started < left, "the player started the clip in StartLoading", r.started)
        local after = between(r.loads, left, entered(r, "LoadModule"))
        check(#after >= 1 and after[1].name ~= "none" and after[1].name ~= during[1].name,
            "after the clip another load state is active than the movie's", during, after)
    end)

    step("the loading segments: two at the first start, one done on leaving each loading state", "observation", function()
        -- the Vulkan executable's LoadPsoCache is one segment more, counted like the others
        local vulkan = entered(r, "LoadPsoCache") ~= nil
        local extra = vulkan and 1 or 0
        local want = { StartLoading = 0, Init = 0, LoadPsoCache = 1, LoadModule = 1 + extra, LoadMenu = 2 + extra, StopLoading = 3 + extra }
        for i, s in ipairs(r.states) do
            local d = want[s.to]
            if d then
                local following = r.states[i + 1]
                local rows = between(r.progress, s.ms, following and following.ms)
                check(#rows > 0, "the loading screen's progress was read in " .. s.to, r.progress)
                for _, p in ipairs(rows) do
                    eq(p.total, 2 + extra, "the first start carries two segments, three on Vulkan (" .. s.to .. ")")
                    eq(p.done, d, "the segments done in " .. s.to)
                end
            end
        end
        local counted = 0
        for _, s in ipairs(r.states) do
            if SEGMENTS[s.from] or s.from == "LoadPsoCache" then counted = counted + 1 end
        end
        eq(counted, 3 + extra, "the start leaves one loading state more than its total")
    end)

    step("Menu: the queue emptied, the loading screen hidden, the menu's timelines started under it", "observation", function()
        local menu = entered(r, "Menu")
        local last = r.loads[#r.loads]
        eq(last.name, "none", "no load state is active in the main menu")
        check(last.ms >= menu and last.ms - menu < 50, "the queue is emptied in the tick the client enters Menu", last, menu)
        check(r.hidden and r.hidden >= menu and r.hidden - menu < 50, "the loading screen is hidden in the tick the client enters Menu", r.hidden, menu)
        local loadMenu = entered(r, "LoadMenu")
        local stopLoading = entered(r, "StopLoading")
        for _, id in ipairs(MENU_TIMELINES) do
            local went = nil
            for _, row in ipairs(r.playing) do
                if row.name == id and went == nil then went = row.ms end
            end
            check(went ~= nil, "the menu's timeline " .. id .. " starts playing", r.playing)
            check(went >= loadMenu and went < stopLoading, "the menu's timeline " .. id .. " starts playing in LoadMenu", went, loadMenu, stopLoading)
        end
        check(r.splash and r.splash >= menu, "the press-any-key page comes after Menu", r.splash, menu)
    end)

    step("the hints drawn are entries of the list", "observation", function()
        check(#r.hints > 0, "the loading screen drew a hint during the start", r.hints)
        for _, h in ipairs(r.hints) do
            eq(h.count, r.composed.count, "a hint is drawn from the whole list")
            check(h.index >= 0 and h.index < h.count and h.key and h.key ~= "", "the drawn index is in the list and names a key", h)
        end
    end)
end
