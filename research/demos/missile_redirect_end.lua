-- Ends demonstration 1: the damage handlers off, the goblins, the spell, the slots and the fight
-- taken back by the stage scope.
local report = GKDemo1 and GKDemo1.report
if GKDemo1 and GKDemo1.stop then GKDemo1.stop() end
GKDemo1 = nil
local clean = GK.Stage.Clean()
return { report = report, notUndone = clean.notUndone, removed = clean.removed }
