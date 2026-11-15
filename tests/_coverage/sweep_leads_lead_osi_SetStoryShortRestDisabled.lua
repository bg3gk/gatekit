-- eoc::StoryShortRestDisabledComponent: made to appear by a sweep step, no model in the loop.
-- Claim: lead eoc::StoryShortRestDisabledComponent: Osi.SetStoryShortRestDisabled with sane arguments (matched by name only) makes it appear on a fresh dummy.
-- Reached by cheat (osiris SetStoryShortRestDisabled), in GustavX:Basic_Level_A; plan `leads`, step lead-osi:SetStoryShortRestDisabled.
-- Written 2026-11-14 by `gatekit sweep run`. Replay: gatekit --instance <copy> test tests/_coverage -k sweep_leads_lead_osi_SetStoryShortRestDisabled

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "eoc::StoryShortRestDisabledComponent")

    -- A fresh dummy (a temporary neutral human) for the step to work on.
    game.lua("server", [[
SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1]
return { SweepDummy, SweepTarget }
]])

    game.wait("server", [[
GK.Stage.Ready({ SweepDummy, SweepTarget })
]], 20)

    -- The sweep's step lead-osi:SetStoryShortRestDisabled: osiris SetStoryShortRestDisabled (a cheat: the cause is staged, no player acts).
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()
Osi.SetStoryShortRestDisabled(H, "GateKitSweep")
]], 20)
    game.sleep(2.5)

    local seen = alive("server", "eoc::StoryShortRestDisabledComponent", 10)
    print("eoc::StoryShortRestDisabledComponent alive on server: " .. fmt(seen))

    -- Leave nothing behind for the next replay.
    game.lua("server", [[
local D, T = SweepDummy, SweepTarget
local H = Osi.GetHostCharacter()

for _, id in ipairs({ D, T }) do pcall(GK.Stage.Remove, id) end
SweepDummy, SweepTarget = nil, nil
return true
]])
end
