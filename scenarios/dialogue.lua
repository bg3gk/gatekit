-- A dialogue driven from code: start it, read the line and the answers, choose, follow it to the end.

scenario { level = "GustavX:TUT_Avernus_C", mods = {} }

local POOL = "S_TUT_Start_Brinepool_5efb6f66-b2ce-403e-b7c0-b68f607ef13a"

--- Chooses the last answer ("leave") again and again until no dialogue is on screen.
local function walk_out(game)
    local steps = 0
    while true do
        local now = game.dialog()
        if not now then return steps end
        steps = steps + 1
        check(steps < 20, "the dialogue does not end", now)
        if #now.answers > 0 then
            game.ui(("return GK.Answer(%d)"):format(now.answers[#now.answers].n))
        end
        game.wait("ui", ("(function() local d = GK.Dialog(); return d == nil or d.text ~= %s " ..
            "or #d.answers ~= %d end)()"):format(lua_string(now.text), #now.answers), 30)
    end
end

function run(game)
    game.wait_state("Running", "server", 60)
    local known = game.server('return GK.Dialogs("Brinepool")')
    check(known and #known > 0, "the brine pool dialogue is not registered")
    local dialog = known[1].dialog

    game.fast_scenes(20)
    walk_out(game) -- anything left over from an earlier scenario in the same session
    local mark = game.server("return GK.Events().last")
    game.wait("server", ('GK.StartDialog("%s", "%s")'):format(dialog, POOL), 15, 1.0)
    local shown = game.wait("ui", "(GK.Dialog() or {}).showAnswers and GK.Dialog()", 30)
    check(shown.speaker and shown.text, "no line on screen", shown)
    check(#shown.answers >= 2, "too few answers", shown)

    local steps = walk_out(game)
    local kinds = {}
    for _, event in ipairs(game.server(("return GK.Events(%s)"):format(mark)).events) do kinds[event.kind] = true end
    check(kinds.DialogStarted and kinds.DialogEnded, "events missing", keys(kinds))
    game.fast_scenes(1)
    print(("dialogue walked in %d step(s); first line by %s; answers offered: %d")
        :format(steps, shown.speaker, #shown.answers))
end
