-- Ends demonstration 2: the listeners off; the dialogue, the guard, the companion (out of the
-- party, deleted) and her boost taken back by the stage scope.
local report = GKDemo2 and GKDemo2.report
if GKDemo2 and GKDemo2.stop then GKDemo2.stop() end
GKDemo2 = nil
local clean = GK.Stage.Clean()
return { report = report, notUndone = clean.notUndone, removed = clean.removed }
