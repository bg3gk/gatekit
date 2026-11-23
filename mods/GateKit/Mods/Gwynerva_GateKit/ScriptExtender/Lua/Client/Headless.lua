-- A headless game from its first frame (the night's W3, round 6). The interface's view model of a
-- character learns that it is a playable character of a player only from MODIFIED party and player
-- components (unplaced::UpdateCharactersPlayable, read by gui::ViewModelProvider::Update): with
-- nothing drawn from the process start the host's view model is made after the party was set up and
-- never learns it, and the dialogue window - which the server's dialogue waits on - never gets its
-- dialogue (round 5). A drawn start gives the view model those changes; a client that draws nothing
-- asks the server instead, at each Running of the client, to send the party's PartyMember and Player
-- again (the same values: a replication, not a change of the logic), and the view model learns
-- them as a drawn start would have given them.

local Headless = GK.Headless or {}
GK.Headless = Headless
Headless.Asked = Headless.Asked or 0

--- Whether the client draws its frames (Ext.StartupVideos.RenderCut's mode 0, or no render cut in
--- this build). observation.
function Headless.Drawing()
    local cut = Ext.StartupVideos and Ext.StartupVideos.RenderCut
    if not cut then return true end
    local ok, c = pcall(cut)
    if not ok or type(c) ~= "table" or not c.Installed then return true end
    return c.Mode == 0
end

local channel
do
    local ok, made = pcall(Ext.Net.CreateChannel, ModuleUUID, "GK_Headless")
    if ok then channel = made end
end

--- Asks the server to send the party's PartyMember and Player components again, so the interface's
--- view models learn who is playable; does nothing while frames are drawn unless `always`. true when
--- asked. cheat (a replication of unchanged components).
function Headless.AskParty(always)
    if not channel or (not always and Headless.Drawing()) then return false end
    local ok = pcall(function() channel:SendToServer({ what = "party" }) end)
    if ok then Headless.Asked = Headless.Asked + 1 end
    return ok
end

if not Headless.subscribed then
    Headless.subscribed = true
    Ext.Events.GameStateChanged:Subscribe(function(e)
        if tostring(e.ToState) == "Running" then pcall(Headless.AskParty) end
    end)
end
