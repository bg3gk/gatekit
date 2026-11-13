-- The in-game console, driven through its own API (GK.Console*), no screen reading:
-- runs code on each side, sorts output by side / level / mod, keeps the input line intact while
-- hundreds of lines fly by, completes names, watches expressions, and survives a Lua reload.

scenario { level = "GustavX:TUT_Avernus_C", mods = {} }

local FLOOD = [[
local n = 0
local handler
handler = Ext.Events.Tick:Subscribe(function()
    for i = 1, 8 do
        n = n + 1
        if n % 100 == 0 then _PW("[FloodMod] warning " .. n) else print("[FloodMod] line " .. n) end
    end
    if n >= 400 then Ext.Events.Tick:Unsubscribe(handler) end
end)
]]

-- What a mod would put into its own client script.
local PANEL = [[
local kit = Mods.GateKit
local UI = kit and kit.GK and kit.GK.UI
if not UI then return end
local state = { debug = false, mode = "Visible", name = "" }
kit.Scenario = state
UI.Panel("Scenario Mod", function(p)
    p:Text("A panel registered by a mod.")
    p:Button("Count party", { server = "return #GK.Party()" })
    p:SameLine():Button("Fail", function() error("on purpose") end)
    p:Toggle("Debug messages", function() return state.debug end, function(on) state.debug = on end)
    p:Select("Mode", { "Visible", "ActiveSet", "Hidden" }, function() return state.mode end,
        function(option) state.mode = option return "mode is now " .. option end)
    p:Input("Name", "", function(text) state.name = text return "hello " .. text end)
    p:Separator("State")
    p:Values(function() return state end)
    p:Watch("GK.Character().hp", "server")
end)
]]

local RESET = "GK.ConsoleSet{open=true, tab='Log', target='client', editor=false, follow=true, view='color', " ..
    "sides={client=true, server=true}, levels={Debug=true, Info=true, Warning=true, Error=true}, " ..
    "source='', text='', input=''}"

--- What the console gains on GateKit's Script Extender build (docs/se-fork.md). Every part runs
--- only where the running build has the feature; on the stock build all are skipped.
local function fork_parts(game, _result)
    local client = game.client
    local features = client("return GK.ConsoleState().features")
    check(features ~= nil,
        "the game runs an older bridge than this scenario (no GK.ConsoleState().features): " ..
        "rebuild the bridge pak with `gatekit [--instance <name>] build` while that game is closed")
    local skipped = {}

    local function state_when(condition, timeout)
        return game.wait("client", ("(function() local s = GK.ConsoleState() return (%s) and s end)()"):format(condition),
            timeout or 10)
    end

    -- The input line as a command line: typed text, Tab at the cursor, Up / Down, Enter. The
    -- keys are injected, so the game window stays in the background.
    if features.history and features.completion and features.cursor and features.injectText then
        client("GK.ConsoleSet{target='client', input='', focus=true}")
        state_when("s.inputActive")
        client('Ext.Input.InjectText("return GK.Clie")')
        state_when("s.input == 'return GK.Clie'")
        client('Ext.Input.InjectKeyPress("TAB")')
        local state = state_when("s.input == 'return GK.Client'")
        check(state.cursor == #"return GK.Client" and state.inputActive, "the cursor is not at the end", state)
        -- Tab in the middle of the line: the name in front of the cursor, the rest stays.
        client("GK.ConsoleSet{input='return #GK.Clie().x', cursor=15}")
        state_when("s.input == 'return #GK.Clie().x' and s.cursor == 15")
        client('Ext.Input.InjectKeyPress("TAB")')
        state = state_when("s.input == 'return #GK.Client().x'")
        check(state.cursor == #"return #GK.Client", "the cursor did not follow the completion", state)
        -- Up recalls the last command with the cursor at its end, Down returns to what was typed.
        local history = client("return GK.ConsoleHistory()")
        local last_command = history[#history]
        client("GK.ConsoleSet{input='draft'}")
        state_when("s.input == 'draft'")
        client('Ext.Input.InjectKeyPress("UP")')
        state = game.wait("client", "(function() local s = GK.ConsoleState() local h = GK.ConsoleHistory() " ..
            "return s.input == h[#h] and s end)()", 10)
        check(state.input == last_command and state.cursor == #last_command, "Up did not recall the last command", state)
        client('Ext.Input.InjectKeyPress("DOWN")')
        state_when("s.input == 'draft'")
        -- Enter runs what was typed.
        client("GK.ConsoleSet{input=''}")
        state_when("s.input == ''")
        client('Ext.Input.InjectText("return 20 + 3")')
        state_when("s.input == 'return 20 + 3'")
        local before = client("return GK.ConsoleState().last")
        client('Ext.Input.InjectKeyPress("RETURN")')
        local ran = game.wait("client", ("(function() local e = GK.ConsoleLog{since=%s, kind='result'}.entries " ..
            "return #e > 0 and e end)()"):format(before), 10)
        check(ran[1].text == "23", "Enter did not run the line", ran)
        state = client("return GK.ConsoleState()")
        check(state.input == "" and state.inputActive, "the input line was not emptied", state)
    else
        skipped[#skipped + 1] = "keys in the input line"
    end

    -- Copy all goes to the clipboard (the user's own clipboard text is put back).
    if features.clipboard then
        local copied = client([[
            local before = Ext.IMGUI.GetClipboardText()
            local answer = GK.ConsoleCopy()
            local now = Ext.IMGUI.GetClipboardText() or ""
            if before then Ext.IMGUI.SetClipboardText(before) end
            answer.found = now:find("> return 6 * 7", 1, true) ~= nil
            answer.size = #now
            answer.view = GK.ConsoleState().view
            return answer]])
        check(copied.clipboard == true and copied.found and copied.size >= copied.bytes, "nothing was copied", copied)
        check(copied.view == "color", "no detour through the Select view", copied)
    else
        skipped[#skipped + 1] = "clipboard"
    end

    -- The wheel pauses and resumes the log: scrolling up stops Follow, the end starts it again.
    if features.scroll then
        client("GK.ConsoleSet{follow=true}")
        game.sleep(0.6)
        local scroll = state_when("s.scroll and s.scroll.max > 200 and s.scroll.y >= s.scroll.max - 4").scroll
        client("GK.ConsoleSet{scroll='top'}")
        state_when("s.follow == false")
        client("print('behind the wheel')")
        check(not contains(client("return GK.ConsoleOutput()"), "behind the wheel"), "the paused view moved")
        client("GK.ConsoleSet{scroll='end'}")
        state_when("s.follow == true")
        check(contains(client("return GK.ConsoleOutput()"), "behind the wheel"), "the view did not catch up", scroll)
    else
        skipped[#skipped + 1] = "scroll position"
    end

    -- What Script Extender itself reports from C++: an error raised inside a timer callback.
    if features.consoleMessages then
        for _, side in ipairs({ "client", "server" }) do
            local marker = "gatekit-cpp-" .. side
            game.lua(side, ("Ext.Timer.WaitFor(50, function() error('%s') end) return true"):format(marker))
            local seen = game.wait("client", ("(function() local e = GK.ConsoleLog{text='%s', level='Error'}.entries " ..
                "return #e > 0 and e end)()"):format(marker), 15)
            -- one console per process: the line is there once, whichever side raised the error
            check(#seen == 1 and seen[1].count == 1 and seen[1].source == "Script Extender", "not one C++ message", seen)
        end
        -- ... while a mod's own line is still there once, not a second time as a console message.
        client("print('[OnceMod] global') Mods.GateKit.Ext.Utils.Print('[OnceMod] own')")
        game.server("print('[OnceMod] server')")
        game.sleep(1.5)
        local once = client("return GK.ConsoleLog{source='[OnceMod]'}.entries")
        local texts = {}
        for i, entry in ipairs(once) do texts[i] = entry.text end
        eq(sorted(texts), { "[OnceMod] global", "[OnceMod] own", "[OnceMod] server" }, "the mod's lines")
        for _, entry in ipairs(once) do
            check(entry.count == 1 and entry.source ~= "Script Extender", "a line was counted twice", entry)
        end
    else
        skipped[#skipped + 1] = "Script Extender's own messages"
    end

    -- Osiris names complete on the server, with the kind and the parameter types.
    if game.server("return GK.Features.osirisNames") then
        client("GK.ConsoleSet{target='server', input='return Osi.GetHitpo'}")
        local state = state_when("s.suggestions[1] == 'GetHitpoints'")
        check(startswith(state.hint, "Query GetHitpoints(") and contains(state.hint, "out "), "no hint", state)
        check(client("return GK.ConsoleTab()") == "return Osi.GetHitpoints", "Tab did not complete the Osiris name")
        client("GK.ConsoleSet{input='Osi.DB_Pla'}")
        state_when("#s.suggestions > 0 and s.suggestions[1]:sub(1, 6) == 'DB_Pla'")
        client("GK.ConsoleSet{target='client', input=''}")
    else
        skipped[#skipped + 1] = "Osiris names"
    end

    print(("console on Script Extender v%s: skipped (not in this build): %s")
        :format(client("return Ext.Utils.Version()"), join(skipped, ", ", "nothing")))
end

function run(game)
    game.wait_state("Running", "server", 60)
    local client = game.client
    local kept_watches = client("return GK.ConsoleWatches()") or {}
    client(RESET .. " GK.ConsoleUnwatch() GK.ConsoleClear()")
    check(client("return GK.ConsoleState().open") == true, "the console did not open")

    -- Code runs on the client, the server and in the UI context; each result carries its ticket.
    local t_client = client("return GK.ConsoleRun('client', 'return 6 * 7')")
    local t_server = client("return GK.ConsoleRun('server', 'return Osi.GetHostCharacter()')")
    local t_ui = client("return GK.ConsoleRun('ui', 'return #GK.Commands() > 50')")
    local t_error = client([[return GK.ConsoleRun('server', 'error("boom")')]])
    local host = game.server("return Osi.GetHostCharacter()")

    local function result(ticket)
        return game.wait("client", ("(function() local e = GK.ConsoleLog{ticket=%s, kind='result'}.entries " ..
            "return #e > 0 and e end)()"):format(ticket), 10)[1]
    end

    check(result(t_client).text == "42", "the client did not answer 42")
    local answer = result(t_server)
    check(answer.text == host and answer.side == "server", "the server's answer is wrong", answer)
    check(result(t_ui).text == "true", "the UI context did not answer")
    local failed = result(t_error)
    check(failed.level == "Error" and contains(failed.text, "boom"), "the error was not shown", failed)
    check(not contains(failed.text, "xpcall"), "the GateKit's own frames belong cut off: " .. failed.text)
    check(contains(client("return GK.ConsoleOutput()"), "> return 6 * 7"), "the command is not in the output")

    -- What mods print is caught on both sides, with the side, the level and the [tag].
    game.server("print('[TestMod] from the server') _PW('[TestMod] careful') _PE('[TestMod] broken')")
    client("print('[OtherMod] from the client') Mods.GateKit.Ext.Utils.Print('[OtherMod] old style')")
    local tagged = game.wait("client", "(function() local e = GK.ConsoleLog{source='[TestMod]'}.entries " ..
        "return #e == 3 and e end)()", 10)
    local levels = {}
    for i, entry in ipairs(tagged) do levels[i] = entry.level end
    eq(levels, { "Info", "Warning", "Error" }, "the levels")
    for _, entry in ipairs(tagged) do check(entry.side == "server", "wrong side", entry) end
    check(#client("return GK.ConsoleLog{source='[OtherMod]', side='client'}.entries") == 2, "client lines missing")
    check(#client("return GK.ConsoleLog{source='[OtherMod]', side='server'}.entries") == 0, "lines on the wrong side")
    check(#client("return GK.ConsoleLog{source='[TestMod]', level='Warning'}.entries") == 2, "level filter")
    check(#client("return GK.ConsoleLog{text='TestMod -careful'}.entries") == 2, "text filter")
    local sources = client("return GK.ConsoleState().sources") -- source -> how many lines
    check(sources["[TestMod]"] and sources["[OtherMod]"] and sources["Console"], "sources", sources)

    -- The same filters, set on the window: what it shows follows.
    local state = client("return GK.ConsoleSet{source='[TestMod]', levels={Debug=false, Info=false}}")
    local shown = client("return GK.ConsoleOutput()")
    check(state.shown == 2 and contains(shown, "careful") and contains(shown, "broken"), "the filter did not hold", shown)
    check(not contains(shown, "from the server") and not contains(shown, "OtherMod"), "too much is shown", shown)
    client("GK.ConsoleSet{source='', sides={server=false}, levels={Debug=true, Info=true}}")
    shown = client("return GK.ConsoleOutput()")
    check(contains(shown, "OtherMod") and not contains(shown, "TestMod"), "the side filter did not hold", shown)
    client(RESET)

    -- Identical lines in a row are one entry with a counter.
    client("for i = 1, 5 do print('same line') end")
    local same_line = client("return GK.ConsoleLog{text='same line'}.entries")
    check(#same_line == 1 and same_line[1].count == 5, "repeats are not counted", same_line)

    -- The input line keeps its text and the cursor while 800 lines arrive from both sides.
    local typed = "return 1 + 1"
    client(("GK.ConsoleSet{input='%s', focus=true}"):format(typed))
    game.wait("client", "GK.ConsoleState().inputActive", 5)
    local before = client("return GK.ConsoleState().last")
    game.server(FLOOD)
    client(FLOOD)
    local deadline, arrived = game.now() + 30, false
    while game.now() < deadline do
        state = client("return GK.ConsoleState()")
        check(state.input == typed and state.inputActive, "the input line was disturbed", state)
        if state.last - before >= 800 then
            arrived = true
            break
        end
    end
    check(arrived, "the flood did not arrive", state.last - before)
    check(state.shown == 250 and state.total > 800, "the window does not hold the flood", state)
    -- `last` counts every entry, the extender's own timing notices included (many on a busy
    -- machine), so the loop above may end a few flood lines early: wait for the last of them.
    local warnings = "#GK.ConsoleLog{source='[FloodMod]', level='Warning', limit=50}.entries"
    game.wait("client", warnings .. " >= 8", 10)
    game.sleep(1.5) -- longer than the wait for the server's claim: a line counted twice shows by now
    check(client("return " .. warnings) == 8, "warnings counted twice")
    game.shot("console_flood.jpg")

    -- Enter runs the line, empties it, keeps the cursor there and remembers the command.
    local ticket = client("return GK.ConsoleSubmit()")
    check(result(ticket).text == "2", "the line did not run")
    state = client("return GK.ConsoleState()")
    check(state.input == "" and state.inputActive, "the input line was not emptied", state)
    local history = client("return GK.ConsoleHistory()")
    check(history[#history] == typed, "the command was not remembered", history)

    -- With Follow off the view stands still and counts what waits.
    client("GK.ConsoleSet{follow=false} print('while paused')")
    state = client("return GK.ConsoleState()")
    check(state.waiting >= 1 and not contains(client("return GK.ConsoleOutput()"), "while paused"), "Follow off", state)
    client("GK.ConsoleSet{follow=true}")
    check(contains(client("return GK.ConsoleOutput()"), "while paused"), "the view did not catch up")

    -- Completion: on the client at once, on the server after a round trip.
    client("GK.ConsoleSet{input='return GK.Clie'}")
    check(client("return GK.ConsoleTab()") == "return GK.Client", "client completion")
    client("GK.ConsoleSet{target='server', input='return GK.Par'}")
    game.wait("client", "GK.ConsoleState().suggestions[1] == 'Party'", 5)
    check(client("return GK.ConsoleTab()") == "return GK.Party", "server completion")
    client("GK.ConsoleSet{input='Ext.Stats.Ge'}")
    game.wait("client", "#GK.ConsoleState().suggestions > 1", 5)
    check(client("return GK.ConsoleTab()") == "Ext.Stats.Get", "several matches: as far as they agree")
    client("GK.ConsoleSet{target='client', input=''}")

    fork_parts(game, result)

    -- Pinned expressions are evaluated on their side.
    client("GK.ConsoleWatch('GK.Character().hp', 'server') GK.ConsoleWatch('1 + 2', 'client')")
    local watches = game.wait("client", "(function() local w = GK.ConsoleWatches() return w[1].value and w end)()", 10)
    check(tonumber(watches[1].value) > 0 and watches[2].value == "3", "the watches did not run", watches)

    -- A multi-line chunk in the editor box.
    client("GK.ConsoleSet{editor=true, input='local sum = 0\\nfor i = 1, 4 do sum = sum + i end\\nreturn sum'}")
    check(result(client("return GK.ConsoleSubmit()")).text == "10", "the editor chunk did not run")
    client("GK.ConsoleSet{editor=false}")

    -- `!name args` runs a console command a mod registered.
    client("Ext.RegisterConsoleCommand('dk_scenario', function(_, word) Mods.GateKit.Heard = word end)")
    client("GK.ConsoleRun('client', '!dk_scenario hello')")
    check(client("return Mods.GateKit.Heard") == "hello", "the console command did not run")

    -- A mod's own panel: registered by name, driven without the mouse, read back as data.
    client(PANEL)
    check(has(client("return GK.UI.Panels()"), "Scenario Mod"), "the panel was not registered")

    local function last(label)
        return game.wait("client", ("(function() local l = GK.UI.Read('Scenario Mod').last " ..
            "return l and l.label == '%s' and l end)()"):format(label), 10)
    end

    client("GK.UI.Press('Scenario Mod', 'Count party')")        -- a button whose code runs on the server
    local counted = last("Count party")
    check(counted.ok and tonumber(counted.text) >= 1, "the server button", counted)
    client("GK.UI.Press('Scenario Mod', 'Debug messages')")     -- a toggle bound to the mod's own state
    check(client("return Mods.GateKit.Scenario.debug") == true, "the toggle did not reach the mod")
    client("GK.UI.Press('Scenario Mod', 'Mode', 'Hidden')")     -- a list
    check(last("Mode").text == "mode is now Hidden", "the list did not answer")
    client("GK.UI.Press('Scenario Mod', 'Name', 'Gwyn')")       -- a text field
    check(last("Name").text == "hello Gwyn", "the text field did not answer")
    client("GK.UI.Press('Scenario Mod', 'Fail')")               -- an action that raises
    local broke = last("Fail")
    check(broke.ok == false and contains(broke.text, "on purpose"), "the failure was not reported", broke)
    local errors = client("return GK.ConsoleLog{source='Scenario Mod', level='Error'}.entries")
    check(#errors == 1, "the failure is not in the log once", errors)
    local panel = {}
    for _, element in ipairs(client("return GK.UI.Read('Scenario Mod')").elements) do
        panel[element.label or element.kind] = element.value
    end
    check(panel["Debug messages"] == true and panel.Mode == "Hidden" and panel.Name == "Gwyn", "the panel's state", panel)
    check(contains(panel.values, "mode:  Hidden"), "the values block", panel)
    game.wait("client", "(function() for _, e in ipairs(GK.UI.Read('Scenario Mod').elements) do " ..
        "if e.kind == 'watch' and tonumber(e.value) then return true end end end)()", 10)
    client("GK.ConsoleSet{tab='Scenario Mod'}")
    game.sleep(0.5)
    game.shot("panel.jpg")
    check(client("return GK.UI.Remove('Scenario Mod')") == true, "the panel was not removed")

    -- The Events tab: the journal of both sides, merged.
    game.server("GK.Note('ScenarioMark', { from = 'server' })")
    client("GK.Note('ScenarioMark', { from = 'client' })")
    local marks = game.wait("client", "(function() local e = GK.ConsoleEvents{text='ScenarioMark'} return #e == 2 and e end)()", 10)
    local sides = {}
    for _, mark in ipairs(marks) do sides[mark.side] = true end
    check(sides.client and sides.server, "both sides belong in the journal", marks)
    client("GK.ConsoleSet{tab='Console'}")

    -- The Select view holds the same lines as plain text (what Ctrl+A, Ctrl+C would take).
    client("GK.ConsoleSet{view='select'}")
    check(client("return GK.ConsoleState().view") == "select", "the Select view")
    client("GK.ConsoleSet{view='color', follow=true}")
    game.shot("console.jpg")

    -- A Lua reload (hot reload) brings the window back with its log and its watches.
    -- (The console's own "Reload Lua" is used: it needs no linked mod folder. A command sent
    -- while Lua restarts would be lost, hence the pause.)
    client("GK.ConsoleReload()")
    game.sleep(4)
    game.wait("server", "GK.Complete ~= nil", 15)
    game.wait("client", "GK.ConsoleState ~= nil and GK.ConsoleState().open", 15)
    local kept = client("return GK.ConsoleLog{text='while paused'}.entries")
    check(#kept == 1, "the log did not survive the reload", kept)
    check(client("return GK.ConsoleState().watches") == 2, "the watches did not survive the reload")
    history = client("return GK.ConsoleHistory()")
    check(startswith(history[#history], "local sum"), "the history did not survive the reload", history)
    -- ... and output is caught again after it.
    game.server("print('[TestMod] after the reload')")
    game.wait("client", "#GK.ConsoleLog{text='after the reload'}.entries == 1", 10)

    -- Leave the console as it was found.
    client("GK.ConsoleUnwatch()")
    for _, watch in ipairs(kept_watches) do
        client(("GK.ConsoleWatch(%s, '%s')"):format(lua_string(watch.code), watch.side))
    end
    client(RESET .. " GK.ConsoleSet{open=false}")
    check(client("return GK.ToggleConsole()") == true, "the key did not open the console")
    check(client("return GK.ToggleConsole()") == false, "the key did not close the console")
end
