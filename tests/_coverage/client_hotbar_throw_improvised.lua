-- esv::improvised_weapon::PickUpDataComponent: the shortest way to make it appear again.
-- Claim: A throw started the player's own way - the hotbar's Throw action, an improvised weapon (a broom) picked from the throw list, a point on the ground aimed at and clicked - lights the improvised-weapon pick-up chain (esv PickUpRequest/PickUpData/PreparingToBeReleased, eoc Wielding/Wielded) and the thrown chain (ecl thrown PrepareAnimationRequest/DummyAttachment/ThrownSound, eoc thrown Attach/IsThrown, esv thrown EntityThrownRequest/Landed). Osiris/GK.Act throws had not lit them.
-- Backed by: repeated, reached by play, in GustavX:Basic_Level_A (first recorded 2026-11-14 in GustavX:SCL_Main_A with a mallet)
-- Still unresolved: ["ThrownAnimationRequest, ThrowerSoundEvent, SpellPrepareAnimationRequest, esv thrown Attachment/DetachmentRequest, Carried prepare still unseen."]
-- Rewritten 2026-11-14: no screen point, no slot number. The slot is named by its action and the
-- item by its uuid (GK.Gui.UseSlot), the aim is a world point (GK.Gui.Aim) and the click is
-- GK.Gui.Confirm - the game's own targeting, preview and cast request, as a click gives them.
-- The throw is done twice, at two spots, each from a fresh targeting.
-- Replay: gatekit --instance <copy> test tests/_coverage -k client_hotbar_throw_improvised

test { level = "GustavX:Basic_Level_A" }

local BROOM = "cbb98d74-68e9-4d0f-a4ff-6fa92cfcea13"  -- root template TOOL_GEN_Broom_A (an improvised weapon)

-- Staging (a cheat, not part of the claim): a broom in the host's bag, the host at `spot`.
local function stage(spot)
    return game.server(string.format([[
        local host = Osi.GetHostCharacter()
        local x, y, z = Osi.FindValidPosition(%f, 0, %f, 4, host, 1)
        Osi.TeleportToPosition(host, x, y, z, "", 0, 0, 0, 0, 1)
        Osi.TemplateAddTo(%q, host, 1, 0)
        return { x, y, z }
    ]], spot[1], spot[2], BROOM))
end

local function carriedBroom()
    return game.wait("server", string.format([[(function()
        local found = GK.Find(%q, Osi.GetHostCharacter()) return found and found[1] end)()]], BROOM), 10)
end

local function throw(at, label)
    local used = game.ui([[return GK.Gui.UseSlot("Throw_Throw")]])
    check(used.ok, label .. ": the Throw action did not begin its targeting", used)
    local broom = carriedBroom()
    local picked = game.ui(string.format([[return GK.Gui.UseSlot(%q)]], broom))
    check(picked.ok, label .. ": the broom was not taken from the throw list", picked)
    local aimed = game.ui(string.format([[return GK.Gui.Aim({ %f, %f, %f })]], at[1], at[2], at[3]))
    check(aimed.ok, label .. ": the game's targeting did not take the aim", aimed)
    local clicked = game.ui([[return GK.Gui.Confirm()]])
    check(clicked.ok, label .. ": the click was not taken", clicked)
    -- the broom leaves the bag
    game.wait("server", string.format([[Osi.GetInventoryOwner(%q) == nil]], broom), 10)
end

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::improvised_weapon::PickUpDataComponent")

    local a = stage({ 3.75, 2.25 })
    throw({ a[1] + 4, a[2], a[3] + 1 }, "spot A")
    local b = stage({ -6, -4 })
    throw({ b[1] - 2, b[2], b[3] + 4 }, "spot B")

    local seen = alive("server", "esv::improvised_weapon::PickUpDataComponent", 10)
    print("esv::improvised_weapon::PickUpDataComponent alive on server: " .. fmt(seen))
end
