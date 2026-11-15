-- esv::active_roll::StartSpellRequestOneFrameComponent: the shortest way to make it appear again.
-- Claim: A dialogue skill check at the Last Light Inn (trespass dialogue with Flaming Fist, Intimidation/Deception, DC 22) whose roll window offers Guidance (Astarion knows Target_Guidance) : choosing the offered bonus (AddModifierCommand with the VMBoostModifier of Target_Guidance from DCActiveRoll.NearbyPartyCharacters[1].BoostModifierList[1]) and pressing roll makes esv::active_roll::WaitingComponent and StartSpellRequestOneFrameComponent appear (both new_to_the_record, same capture second).
-- Backed by: sighting only, reached by cheat, in GustavX:SCL_Main_A
-- Still unresolved: Seen once new_to_the_record (one bg3_seen_new after the roll start). The repeat failed: after bg3_reset the trespass dialogue did not start again (Visit Isobel x3, teleport away and back), so the same action was NOT repeated with a fresh capture. bg3_verify (prefix esv::active_roll, record) said nothing_alive: both types were gone by the time it ran (WaitingComponent probe: nothing alive at every later poll). Not compared. Which of the two components belongs to the Add-bonus pick or to the roll press is not separated: I did the Command then RollNext in one go. First roll of the same dialogue without clicking Add bonus lit only eoc::active_roll::ModifiersComponent / InProgress, no Waiting.
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k active_roll_start_spell_guidance_dialog

test { level = "GustavX:SCL_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::active_roll::StartSpellRequestOneFrameComponent")

    -- Cheat: teach Guidance, teleport next to Isobel; the trespass dialogue starts by itself (worked at the first visit after a fresh load in the sighting; not after the reset in the repeat, see open)
    game.lua("server", [[
GK.Stage.Teach('Target_Guidance'); GK.Stage.Visit('263bfbfc-6160-46f4-a9e1-1089cdb5c211')
]])
    game.sleep(4)

    game.lua("client", [[
GK.Autoplay(false); return GK.Answer(2)
]])
    game.sleep(2)

    game.lua("client", [[
return GK.Answer(2)
]])
    game.sleep(2)

    -- the window's Add bonus button has no command; this is what it runs
    game.lua("client", [[
local vm=GK.ViewModel('gui::DCActiveRoll'); return GK.Command('AddModifierCommand', vm.NearbyPartyCharacters[1].BoostModifierList[1], 'gui::DCActiveRoll')
]])
    game.sleep(1)

    game.lua("client", [[
return GK.RollNext()
]])
    game.sleep(1)

    local seen = alive("server", "esv::active_roll::StartSpellRequestOneFrameComponent", 20)
    print("esv::active_roll::StartSpellRequestOneFrameComponent alive on server: " .. fmt(seen))
end
