-- Scene "finale", its part on the UI side: a block of the stand's own at the head of the game's
-- credits - a picture on the black screen before them, and lines as the credits' first entry.
-- Armed before GKScene.finale.go(); with no options it does nothing and the credits are the game's.
-- Arm:    ./gatekit --instance <copy> exec ui "GKSceneOptions = { finale = { credits = { logo = 'Stand/gatekit-logo.png',
--             lines = { { text = 'GateKit', bold = true, size = 84 }, { text = 'MAP · EXTEND · AUTOMATE' },
--             { text = 'From Gwynerva, to the game I love most' } } } } } $(cat stand/scenes/finale_credits.lua)"
-- Disarm: ./gatekit --instance <copy> exec ui "return GKScene.finale_credits.off()"
-- UI context.
--
-- The picture is the stand's, not the credits': the credits page has no picture entry (its view
-- model holds text lines and sections only), so `logo` - a file under the copy's Data folder - is
-- drawn by GK.Show.Picture when the HUD's screen fade stands at full black, for `seconds`, on
-- black. The server's part holds the credits back meanwhile: its option `black`.
--
-- The lines are the credits' own first entry. The credits are the file
-- Public/Game/Credits/credits.txt, read into the page's view model (gui::DCCredits.Credits:
-- ls.VMTextline and ls.VMCreditsSection, each with a Line of the game's markup). A Line is
-- read-only for the extender, so the text is written into the page: the first entry is an empty
-- line, and its TextBlock (named creditLine) takes Text, FontWeight and FontSize. Text is plain: the
-- markup is read from Line only. A brush cannot be made, and a brush's Color written from Lua stays
-- what it was, so a colour is one of the page's own brushes: `color = "gold"` is the page's list
-- brush, anything else is left the credits' white. All the lines are one block with one look:
-- `bold`, `size` and `color` of the first, the texts joined by line breaks. `gap` empty lines
-- (of the block's own size) end the block and part it from the game's own credits.
-- Options: GKSceneOptions = { finale = { credits = { logo = <file under Data>, height = 0.5,
-- seconds = 4, fade = { 0.8, 0.8 }, gap = 4, lines = { { text, bold, size, color } } } } }.
-- State: GKScene.finale_credits = { armed, picture, wrote, waiting, error, log }.
GKScene = GKScene or {}
local old = GKScene.finale_credits
if old and old.off then pcall(old.off) end

local S = { armed = true, log = {} }
GKScene.finale_credits = S
local GK = GK or Mods.GateKit.GK
local O = (GKSceneOptions and GKSceneOptions.finale and GKSceneOptions.finale.credits) or {}
local LINES = O.lines or {}
local GAP = O.gap or 4 -- empty lines between the stand's block and the game's own credits

local function note(text) S.log[#S.log + 1] = { ms = Ext.Utils.MonotonicTime(), text = text } end
local function try(f) local ok, v = pcall(f) if ok then return v end end

-- The first element named `name` under `e`, in the page's own order.
local function find(e, name, depth)
    if not e or depth > 40 then return end
    if try(function() return tostring(e.Name) end) == name then return e end
    local n = try(function() return e.VisualChildrenCount end) or 0
    for i = 1, n do
        local got = find(try(function() return e:VisualChild(i) end), name, depth + 1)
        if got then return got end
    end
end

-- The HUD's screen fade: whether it shows at full black.
local function black()
    local fade = find(try(function() return Ext.UI.GetRoot() end), "ScreenFade", 0)
    return fade and try(function()
        local data = fade.DataContext.CurrentPlayer.UIData
        return (data.ScreenFadeOpacity or 0) >= 1 and tostring(data.ScreenFadeVisibility) ~= "Collapsed"
    end) or false
end

local function picture()
    local high = O.height or 0.5
    local fade = O.fade or { 0.8, 0.8 }
    local shown = GK.Show.Picture{ file = O.logo, where = { 0, (1 - high) / 2, 1, high }, fit = "contain", back = "black",
        fade = fade, seconds = O.seconds or 4 }
    S.picture = shown.ok and { handle = shown.handle, pixels = shown.pixels, covers = shown.covers } or { error = shown.error }
    note(shown.ok and "the picture is up on the black screen" or ("the picture was refused: " .. tostring(shown.error)))
end

local function write(page)
    local line = find(page, "creditLine", 0)
    if not line then S.waiting = "the page's first line" return end
    if try(function() return line.DataContext.Line end) ~= "" then
        S.error = "the page's first entry is not the empty line: nothing is written"
        return
    end
    local first, texts = LINES[1], {}
    for _, entry in ipairs(LINES) do texts[#texts + 1] = tostring(entry.text or "") end
    -- the gap: empty lines at the end of the block, so the game's own credits begin well below it
    line.Text = table.concat(texts, "\n") .. string.rep("\n", GAP)
    if first.bold then line.FontWeight = "Bold" end
    if first.size then line.FontSize = first.size end
    if first.color == "gold" then
        local parent, brush = line, nil
        for _ = 1, 12 do
            parent = try(function() return parent.VisualParent end)
            if not parent then break end
            if try(function() return tostring(parent.Type) end) == "ItemsControl" then brush = try(function() return parent.Foreground end) break end
        end
        if brush then line.Foreground = brush else note("the page's list brush was not found: the lines stay white") end
    elseif first.color then
        note("the colour " .. tostring(first.color) .. " is not one of the page's brushes: the lines stay white")
    end
    S.wrote, S.waiting = line.Text, nil
    note("the lines are the credits' first entry")
end

local function tick()
    if not S.armed or S.error then return end
    if O.logo and not S.picture then
        if black() then picture() else S.waiting = "the black screen" end
    end
    if #LINES > 0 and not S.wrote then
        local page = find(try(function() return Ext.UI.GetRoot():Find("ContentRoot") end), "CreditsMenu", 0)
        if page then write(page) elseif S.picture or not O.logo then S.waiting = "the credits page" end
    end
end

S.subscription = Ext.Events.Tick:Subscribe(function()
    local ok, problem = pcall(tick)
    if not ok then S.error = tostring(problem) end
end)

function S.off()
    S.armed = false
    if S.subscription then pcall(function() Ext.Events.Tick:Unsubscribe(S.subscription) end) S.subscription = nil end
    if S.picture and S.picture.handle then pcall(GK.Show.Hide, S.picture.handle, { fade = 0 }) end
    return true
end

note((O.logo or #LINES > 0) and "armed" or "armed with nothing to show: the credits stay the game's own")
return { armed = true, logo = O.logo, lines = #LINES }
