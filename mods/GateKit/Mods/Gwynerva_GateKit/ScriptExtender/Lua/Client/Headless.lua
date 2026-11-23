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

--- Whether the interface's local player knows its characters: the CurrentPlayer of the game's view
--- model has assigned characters and its selected character is a playable one. nil when there is no
--- interface to read yet. Called in the UI's context. observation.
function Headless.Mended()
    local ok, known = pcall(function()
        local data = GK.Gui.Data("PlayerPortraits")
        if not data then return nil end
        local player = data.CurrentPlayer
        if not player then return nil end
        local selected = player.SelectedCharacter
        return #player.AssignedCharacters > 0 and selected ~= nil and selected.IsPlayableCharacter == true
    end)
    if not ok then return nil end
    return known
end

-- The mend is checked, not assumed: a replication that reaches the client before the interface has
-- made its local player's view model is lost like the first one (seen: asked at the client's
-- Running, the local player still had no characters, and a reaction prompt never opened - the
-- reaction popup is opened for a local player's characters only). After each Running of a client
-- that draws nothing, every tick asks the interface whether the local player knows its characters,
-- and asks the server again (at most once a second of the game, at most AskLimit times) until it does.
Headless.AskLimit = 20
Headless.AskEveryTicks = 30
Headless.Pending = Headless.Pending or nil   -- { asked, sinceTick, checking } while the mend is unconfirmed
Headless.Tick = Headless.Tick or 0
Headless.Log = Headless.Log or {}

local function note(text)
    Headless.Log[#Headless.Log + 1] = text
    if #Headless.Log > 20 then table.remove(Headless.Log, 1) end
end

function Headless.Follow()
    local pending = Headless.Pending
    if not pending or pending.checking or not (Ext.UI and Ext.UI.Defer) then return end
    pending.checking = true
    Ext.UI.Defer(function()
        pending.checking = false
        if Headless.Pending ~= pending then return end
        local mended = Headless.Mended()
        if mended then
            Headless.Pending = nil
            Headless.MendedAt = { asked = pending.asked, ticks = Headless.Tick - pending.began }
            note(("mended after %d ask(s), %d tick(s)"):format(pending.asked, Headless.Tick - pending.began))
            return
        end
        if Headless.Tick - pending.sinceTick < Headless.AskEveryTicks then return end
        if pending.asked >= Headless.AskLimit then
            Headless.Pending = nil
            Headless.MendedAt = { asked = pending.asked, failed = true }
            note(("not mended after %d asks: the local player has no characters in the interface"):format(pending.asked))
            Ext.Utils.PrintWarning("[GateKit] headless: the interface's local player still has no characters after "
                .. pending.asked .. " replications")
            return
        end
        if Headless.AskParty() then
            pending.asked = pending.asked + 1
            pending.sinceTick = Headless.Tick
        end
    end)
end

if not Headless.subscribed then
    Headless.subscribed = true
    Ext.Events.GameStateChanged:Subscribe(function(e)
        if tostring(e.ToState) ~= "Running" then return end
        local ok, asked = pcall(Headless.AskParty)
        if ok and asked then
            Headless.Pending = { asked = 1, sinceTick = Headless.Tick, began = Headless.Tick }
        end
    end)
    Ext.Events.Tick:Subscribe(function()
        Headless.Tick = Headless.Tick + 1
        if Headless.Pending then pcall(Headless.Follow) end
    end)
end
