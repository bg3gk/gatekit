-- ls::anubis::ScriptCmdRestoreAnubisStateOneFrameComponent: the shortest way to make it appear again.
-- Claim: Osi.SaveAnubisState(npc) then Osi.RestoreAnubisState(npc) on a scripted NPC light ls::anubis ScriptCmdSaveAnubisState and ScriptCmdRestoreAnubisState one-frames (hint from the names session confirmed).
-- Backed by: repeated, reached by play, in GustavX:SCL_Main_A
-- Still unresolved: ["savegame::RestoringStateComponent / WaitingForConfigChange did not show (the restore found no saved state to wait on, or it finished in a frame); ScriptCmdChangeAnubisConfig/Interrupt not lit by CreateAt (returned nil - bad template id use), CharacterMoveTo, Play/StopAnimation, or a dialogue (persuasion roll failed at the inn\u0027s trespass dialogue)."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k osiris_restore_anubis_state

test { level = "GustavX:SCL_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "ls::anubis::ScriptCmdRestoreAnubisStateOneFrameComponent")

    game.lua("server", [[
local id for _,e in ipairs(Ext.Entity.GetAllEntitiesWithComponent('ServerCharacter')) do local t=e.ServerCharacter.Template and e.ServerCharacter.Template.Name or '' if t=='S_HAV_HarperGuard_001' then id=e.Uuid.EntityUuid end end Osi.SaveAnubisState(id) Osi.RestoreAnubisState(id) return id
]])

    game.sleep(3)

    local seen = alive("server", "ls::anubis::ScriptCmdRestoreAnubisStateOneFrameComponent", 10)
    print("ls::anubis::ScriptCmdRestoreAnubisStateOneFrameComponent alive on server: " .. fmt(seen))
end
