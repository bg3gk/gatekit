-- Ends demonstration 3: the clock and the Roll listener stop, the family `fight` goes off, and
-- the stage scope takes back the goblins, the fight, the pin and the host's protection.
local report = GKDemo3 and GKDemo3.report
if GKDemo3 and GKDemo3.stop then GKDemo3.stop() end
GKDemo3 = nil
local clean = GK.Stage.Clean()
return { report = report, notUndone = clean.notUndone, removed = clean.removed }
