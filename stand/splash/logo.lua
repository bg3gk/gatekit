-- The stand's own splash: the channel's logo on black over the game's "press any key" screen, in
-- with a fade, a few seconds, out with a fade - or off at once by Escape, Enter or the space bar,
-- which the picture takes so that the screen under it does not hear them. stand/splash/README.md.
-- UI context, on a start that stops on the splash screen (gatekit up --splash).
GKScene = GKScene or {}
local O = (GKSceneOptions and GKSceneOptions.splash_logo) or {}
local S = { armed = true, fired = false, log = {} }
GKScene.splash_logo = S

local GK = Mods.GateKit.GK
-- The logo is the owner's own file and is not in the repository: a path under the game's Data folder.
local LOGO = O.logo or "Stand/channel-logo-chip.png"
local HIGH = O.height or 0.56          -- the logo's height in parts of the screen
local SECONDS = O.seconds or 3
local FADE_IN, FADE_OUT = O.fadeIn or 1.2, O.fadeOut or 0.9
local SKIP = O.skip or { "escape", "enter", "space" }

local function note(text) S.log[#S.log + 1] = text end

S.under = GK.Gui.Stuck()
local shown = GK.Show.Picture{ file = LOGO, where = { 0, (1 - HIGH) / 2, 1, HIGH }, fit = "contain", back = "black",
    fade = { FADE_IN, FADE_OUT }, seconds = SECONDS, skip = SKIP }
if not shown.ok then
    S.armed, S.error = false, shown.error
    return { armed = false, error = shown.error, hint = shown.hint, logo = LOGO }
end
S.handle, S.fired = shown.handle, true
note("the logo is up over " .. tostring(S.under))

-- The row that says the logo left ends the scene: what is under it is on the screen again.
GK.EventLog.Listeners.splash_logo = function(row)
    if row.kind ~= "picture_hidden" or not row.payload or row.payload.handle ~= S.handle then return end
    GK.EventLog.Listeners.splash_logo = nil
    S.armed = false
    S.left = { why = row.payload.why, key = row.payload.key, shownMs = row.payload.shownMs }
    S.after = GK.Gui.Stuck()
    note("the logo left (" .. tostring(row.payload.why) .. "); under it: " .. tostring(S.after))
end

S.off = function()
    GK.EventLog.Listeners.splash_logo = nil
    if S.handle and GK.Show.Shown(S.handle) and not GK.Show.Shown(S.handle).left then GK.Show.Hide(S.handle, { fade = 0 }) end
    S.armed = false
end

return { armed = true, handle = S.handle, logo = LOGO, under = S.under, seconds = SECONDS, skip = SKIP }
