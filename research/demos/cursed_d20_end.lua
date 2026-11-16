-- Ends demonstration 4: the curse lifted (the Roll listener off, the clock stops), and the stage
-- scope takes back the goblins, the fight, the pin and the host's protection.
local report = GKDemo4 and GKDemo4.report
if GKDemo4 and GKDemo4.stop then GKDemo4.stop() end
GKDemo4 = nil
local clean = GK.Stage.Clean()
return { report = report, notUndone = clean.notUndone, removed = clean.removed }
