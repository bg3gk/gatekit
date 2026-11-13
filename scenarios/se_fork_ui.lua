-- The graphics and layout additions of GateKit's Script Extender build (docs/se-fork.md,
-- group 8): geometry read-back, removable styles, lasting size constraints, decorations behind
-- elements (fill, gradient, border, shadow, image), the canvas element, fonts from files and
-- animated styles. On stock Script Extender every part is skipped and the scenario passes.
--
--     set GATEKIT_SE=<se-fork>\builds\v34
--     gatekit test -k se_fork_ui
-- gatekit:allow-non-english  a merged font is only worth testing with glyphs the first font
-- does not have; that is the whole point of merging one.

scenario { level = "GustavX:TUT_Avernus_C", mods = {} }

local BUILD = [[
local kit = Mods.GateKit
local w = Ext.IMGUI.NewWindow("SE fork UI")
w:SetPos({ 60, 60 })
w:SetSize({ 520, 460 })
local s = { window = w, has = {} }
kit.SeForkUi = s

-- Reading a property or method the build does not have raises an error.
for name, member in pairs({ decoration = "SetBackground", canvas = "AddCanvas", geometry = "LastRect",
                            clear = "ClearColor" }) do
    s.has[name] = pcall(function() return w[member] end)
end

s.title = w:AddText("Decorated elements")
s.button = w:AddButton("A button")
s.right = w:AddButton("Same line")
s.right.SameLine = true

local group = w:AddGroup("card")
s.group = group
group:AddText("A card: gradient, rounded border, shadow")
s.inner = group:AddButton("Inner, decorated too")
group:AddText("third line of the card")

s.locked = w:AddButton("Disabled button")
s.locked.OnClick = function() s.lockedClicks = (s.lockedClicks or 0) + 1 end

if s.has.decoration then
    w:SetBackgroundGradient({ 0.10, 0.02, 0.02, 0.9 }, { 0.02, 0.02, 0.12, 0.9 }, 135)
    group:SetBackgroundGradient({ 0.30, 0.10, 0.05, 1 }, { 0.05, 0.10, 0.30, 1 }, 90)
    group:SetBackgroundRounding(10)
    group:SetBackgroundPadding(8)
    group:SetBorder({ 0.9, 0.7, 0.3, 1 }, 2)
    group:SetShadow({ 0, 0, 0, 0.8 }, 12, 4, 4)
    s.inner:SetBackground({ 0.2, 0.3, 0.1, 1 })
    s.inner:SetBorder({ 0.5, 0.9, 0.3, 1 }, 1)
    s.inner:SetBackgroundPadding(3)
    s.title:SetBackground({ 0.25, 0.05, 0.25, 0.8 })
end

-- Stock already has this: `Disabled` is one of the ItemFlags (no greying; add SetStyle("Alpha", x)).
s.locked.Disabled = true
s.locked:SetStyle("Alpha", 0.4)

if s.has.canvas then
    local c = w:AddCanvas(480, 120)
    s.canvas = c
    c:AddRectGradient({ 0, 0 }, { 480, 120 }, { 0.05, 0.05, 0.05, 1 }, { 0.15, 0.15, 0.2, 1 }, 180, 6)
    c:AddLine({ 10, 110 }, { 470, 110 }, { 0.6, 0.6, 0.6, 1 }, 1)
    c:AddPolyline({ { 10, 100 }, { 80, 60 }, { 150, 80 }, { 220, 30 }, { 290, 50 }, { 360, 20 }, { 470, 40 } },
        { 0.3, 0.9, 0.4, 1 }, false, 2)
    c:AddCircleFilled({ 220, 30 }, 5, { 1, 0.8, 0.2, 1 })
    c:AddCircle({ 360, 20 }, 8, { 1, 0.3, 0.3, 1 }, 2)
    c:AddRect({ 20, 10 }, { 120, 40 }, { 0.8, 0.8, 1, 1 }, 4, 1)
    c:AddRectFilled({ 130, 10 }, { 180, 40 }, { 0.3, 0.3, 0.8, 0.6 }, 4)
    c:AddTriangleFilled({ 400, 100 }, { 430, 60 }, { 460, 100 }, { 0.8, 0.4, 0.1, 1 })
    c:AddTriangle({ 300, 100 }, { 320, 70 }, { 340, 100 }, { 1, 1, 1, 1 }, 1)
    c:AddBezier({ 10, 20 }, { 60, 120 }, { 120, -20 }, { 200, 100 }, { 0.9, 0.5, 0.9, 1 }, 2)
    c:AddText({ 26, 16 }, { 1, 1, 1, 1 }, "canvas text")
end
return s.has
]]

-- An atlas icon every game has: the icon of a base-game spell.
local EXTRA = [[
local s = Mods.GateKit.SeForkUi
local icon = Ext.Stats.Get("Projectile_FireBolt").Icon
local out = { icon = icon }
s.group:SetBackgroundImage(icon, "Tile", { 1, 1, 1, 0.35 })
s.inner:SetBackgroundImage(icon, "Slice", { 1, 1, 1, 1 }, 16)
s.title:SetBackgroundImage(icon)
s.canvas:Clear()
s.canvas:AddRectFilled({ 0, 0 }, { 480, 120 }, { 0.1, 0.1, 0.1, 1 })
out.canvasImage = s.canvas:AddImage(icon, { 10, 10 }, { 110, 110 })
s.canvas:AddImage(icon, { 130, 10 }, { 330, 110 }, { 1, 0.5, 0.5, 1 }, { 0.25, 0.25 }, { 0.75, 0.75 })
out.badImage = s.canvas:AddImage("no_such_icon_or_texture", { 0, 0 }, { 10, 10 })
local file = "Public/Game/GUI/Assets/Fonts/QuadraatOffcPro/QuadraatOffcPro.ttf"
out.font = Ext.IMGUI.LoadFont("SeForkBig", file, 30)
-- The same file again as the "second font": its Cyrillic block merged into a Latin font.
out.merged = Ext.IMGUI.LoadFont("SeForkMerged", file, 22, file, 0x0400, 0x04FF)
out.fontListed = false
for _, name in ipairs(Ext.IMGUI.GetFontNames()) do
    if name == "SeForkBig" then out.fontListed = true end
end
s.title.Font = "SeForkBig"
s.bigText = s.window:AddText("Merged font: Привет 22px")
s.bigText.Font = "SeForkMerged"
s.title:AnimateStyle("Alpha", 0.1, 1.0, 0.4)
s.button:AnimateColor("Button", { 0.9, 0.1, 0.1, 1 }, { 0.1, 0.5, 0.9, 1 }, 0.4)
out.animating = s.title:IsAnimating() and s.button:IsAnimating()
return out
]]

--- Waits out `ms` milliseconds of the game's own clock (a frame or two must pass).
local function frames(game, ms)
    game.wait("client", ("Ext.Timer.MonotonicTime() > %d")
        :format(game.client("return Ext.Timer.MonotonicTime()") + ms), 20)
end

function run(game)
    local client = game.client
    local has = client(BUILD)
    local skipped = {}
    for _, name in ipairs(keys(has)) do
        if not has[name] then skipped[#skipped + 1] = name end
    end
    local ok, problem = pcall(function()
        game.wait("client", "Mods.GateKit.SeForkUi.window.LastSize[1] > 0", 10)

        if has.geometry then
            local geo = game.wait("client", [[(function()
                local s = Mods.GateKit.SeForkUi
                local b, r, g, w = s.button.LastRect, s.right.LastRect, s.group.LastRect, s.window.LastRect
                if b[3] <= b[1] then return nil end
                return { button = b, right = r, group = g, window = w, inner = s.inner.LastRect,
                         avail = s.button.LastAvail, size = s.window.LastSize, pos = s.window.LastPosition }
            end)()]], 10)
            local b, r, g, w = geo.button, geo.right, geo.group, geo.window
            check(b[3] < b[1] + 400 and b[4] > b[2], "not a sane rectangle", geo)
            check(r[1] >= b[3] and math.abs(r[2] - b[2]) < 2, '"Same line" does not sit to the right', geo)
            check(g[2] >= b[4], "the group is not below the buttons", geo)
            local inner = geo.inner
            check(g[1] <= inner[1] and inner[3] <= g[3] and g[2] <= inner[2] and inner[4] <= g[4], "the inner rect", geo)
            check(math.abs(w[1] - geo.pos[1]) < 1 and math.abs((w[3] - w[1]) - geo.size[1]) < 1, "position and size", geo)
            check(geo.avail[1] > 0 and geo.avail[1] <= geo.size[1], "the room left", geo)
        end

        if has.clear then
            local cleared = client([[
                local b = Mods.GateKit.SeForkUi.button
                b:SetColor("Button", { 1, 0, 0, 1 })
                b:SetStyle("FrameRounding", 6)
                local before = { b:GetColor("Button") ~= nil, b:GetStyle("FrameRounding") ~= nil }
                b:ClearColor("Button")
                b:ClearStyle("FrameRounding")
                b:ClearColor("Button")  -- clearing what is not set is not an error
                return { before = before, color = b:GetColor("Button"), style = b:GetStyle("FrameRounding") }]])
            eq(cleared.before, { true, true }, "the style was not set in the first place")
            check(cleared.color == nil and cleared.style == nil, "the style was not cleared", cleared)
        end

        -- Size constraints with a minimum only: the window must keep its size and honour the
        -- minimum for good (stock: collapses for a frame, and the constraint is forgotten).
        if has.geometry then
            client([[local w = Mods.GateKit.SeForkUi.window
                w:SetSizeConstraints({ 500, 300 })
                w:SetSize({ 200, 100 })]])
            frames(game, 500)
            local size = client("return Mods.GateKit.SeForkUi.window.LastSize")
            check(size[1] >= 500 and size[2] >= 300, "the minimum size was forgotten", size)
            client([[local w = Mods.GateKit.SeForkUi.window
                w:SetSizeConstraints()
                w:SetSize({ 520, 460 })]])
            game.wait("client", "Mods.GateKit.SeForkUi.window.LastSize[1] == 520", 10)
        end

        if has.canvas then
            local canvas = game.wait("client", [[(function()
                local c = Mods.GateKit.SeForkUi.canvas
                local r = c.LastRect
                return r[3] > r[1] and { rect = r, count = c:GetCommandCount() } or nil
            end)()]], 10)
            check(canvas.count == 11, "not every canvas command was kept", canvas)
            local rect = canvas.rect
            check(math.abs((rect[3] - rect[1]) - 480) < 2 and math.abs((rect[4] - rect[2]) - 120) < 2, "the canvas size", canvas)
            client("Mods.GateKit.SeForkUi.canvas:Clear()")
            check(client("return Mods.GateKit.SeForkUi.canvas:GetCommandCount()") == 0, "the canvas was not cleared")
            client([[local c = Mods.GateKit.SeForkUi.canvas
                c:AddRectFilled({ 0, 0 }, { 480, 120 }, { 0.1, 0.1, 0.1, 1 })
                c:AddText({ 10, 10 }, { 1, 1, 1, 1 }, "redrawn")]])
        end

        -- A disabled button does not answer a click (needs injected mouse input: same build).
        if has.geometry and client("return Ext.Input.InjectMouseButton ~= nil") then
            local click = [[
                local s = Mods.GateKit.SeForkUi
                local r = s.%s.LastRect
                local x, y = math.floor((r[1] + r[3]) / 2), math.floor((r[2] + r[4]) / 2)
                Ext.Input.InjectMouseMove(x, y)
                Ext.Input.InjectMouseButton(1, true, x, y)
                Ext.Input.InjectMouseButton(1, false, x, y)
                return { x, y }]]
            client([[local s = Mods.GateKit.SeForkUi
                s.button.OnClick = function() s.clicks = (s.clicks or 0) + 1 end]])
            client(click:format("button"))
            game.wait("client", "Mods.GateKit.SeForkUi.clicks == 1", 10)
            client(click:format("locked"))
            frames(game, 500)
            check(client("return Mods.GateKit.SeForkUi.lockedClicks") == nil, "a disabled button answered a click")
            client("Mods.GateKit.SeForkUi.locked.Disabled = false")
            client(click:format("locked"))
            game.wait("client", "Mods.GateKit.SeForkUi.lockedClicks == 1", 10)
            client("Mods.GateKit.SeForkUi.locked.Disabled = true")
        end

        if has.decoration then
            -- Removing and re-adding decorations while the window is drawn must not disturb it.
            client([[local s = Mods.GateKit.SeForkUi
                s.inner:ClearDecoration()
                s.inner:SetBackground({ 0.2, 0.3, 0.1, 1 })
                s.inner:SetBorder({ 0.5, 0.9, 0.3, 1 }, 1)
                s.inner:SetBackgroundPadding(3)]])
            frames(game, 300)
            check(client("return Mods.GateKit.SeForkUi.window.LastSize[1]") == 520, "the window was disturbed")
            game.shot("se_fork_ui.jpg")
        end

        -- Images behind elements and on the canvas, fonts from files, animated styles.
        if has.decoration and client("return pcall(function() return Mods.GateKit.SeForkUi.window.SetBackgroundImage end)") then
            local extra = client(EXTRA)
            check(extra.canvasImage == true and extra.badImage == false, "the canvas images", extra)
            check(extra.font == true and extra.merged == true and extra.fontListed == true, "the fonts", extra)
            check(extra.animating == true, "nothing is animating", extra)
            local done = game.wait("client", [[(function()
                local s = Mods.GateKit.SeForkUi
                if s.title:IsAnimating() or s.button:IsAnimating() then return nil end
                return { alpha = s.title:GetStyle("Alpha"), color = s.button:GetColor("Button") }
            end)()]], 10)
            check(math.abs(done.alpha - 1.0) < 0.001, "the animation did not end where it was told", done)
            check(near(done.color, { 0.1, 0.5, 0.9, 1.0 }, 0.001), "the animated colour", done)
            frames(game, 500)
            check(client("return Mods.GateKit.SeForkUi.window.LastSize[1]") == 520, "the window was disturbed")
            game.shot("se_fork_ui-images.jpg")

            -- A font per size, changed while the window is on screen: every new size rebuilds
            -- the font atlas, which is where a build without upstream's fixes could crash.
            local heights = {}
            for size = 14, 30, 2 do
                local loaded = client(([[
                    local s = Mods.GateKit.SeForkUi
                    local name = "SeForkSize%d"
                    local ok = Ext.IMGUI.LoadFont(name, "Public/Game/GUI/Assets/Fonts/QuadraatOffcPro/QuadraatOffcPro.ttf", %d)
                    s.bigText.Font = name
                    s.bigText.Label = "Font size %d: Привет"
                    return ok]]):format(size, size, size))
                check(loaded == true, "a font did not load", size)
                frames(game, 150)
                local rect = client("return Mods.GateKit.SeForkUi.bigText.LastRect")
                heights[size] = rect[4] - rect[2]
            end
            -- The text really is drawn at the size asked for: 30 px is about twice as tall as 14 px
            -- (screen pixels are the font's pixels times the UI scale, so only the ratio is checked).
            check(heights[30] > heights[14] * 1.7, "the font size did not reach the screen", heights)

            -- A size per element, no font to load (needs the ImGui 1.92+ base of the fork).
            if client("return pcall(function() return Mods.GateKit.SeForkUi.bigText.FontSize end)") then
                local sized = {}
                for _, size in ipairs({ 16, 48 }) do
                    client(([[local t = Mods.GateKit.SeForkUi.bigText
                        t.Font = "Medium"
                        t.FontSize = %d
                        t.Label = "FontSize %d: Привет"
                    ]]):format(size, size))
                    frames(game, 200)
                    local rect = client("return Mods.GateKit.SeForkUi.bigText.LastRect")
                    sized[size] = rect[4] - rect[2]
                end
                check(sized[48] > sized[16] * 2.5, "the per-element font size", sized)
                client("Mods.GateKit.SeForkUi.bigText.FontSize = nil")
            else
                skipped[#skipped + 1] = "per-element font size"
            end
            check(client("return Mods.GateKit.SeForkUi.window.LastSize[1]") == 520, "the window was disturbed")
        elseif has.decoration then
            skipped[#skipped + 1] = "images, fonts, animation"
        end

        -- An image file of the mod's own, straight from the game's data folder.
        if client("return Ext.IMGUI.GetImageSize ~= nil") then
            local folder = game.folder .. "/Data/Mods/GateKitScenario"
            game.testimage(folder .. "/se_fork_test.png", 64, 32)
            local shown, trouble = pcall(function()
                local out = client([[
                    local s = Mods.GateKit.SeForkUi
                    local path = "Mods/GateKitScenario/se_fork_test.png"
                    local out = { size = Ext.IMGUI.GetImageSize(path),
                                  missing = Ext.IMGUI.GetImageSize("Mods/GateKitScenario/none.png") == nil,
                                  icon = Ext.IMGUI.GetImageSize(Ext.Stats.Get("Projectile_FireBolt").Icon) ~= nil }
                    s.fileImage = s.window:AddImage(path, { 128, 64 })
                    s.window:SetBackgroundImage(path, "Tile", { 1, 1, 1, 0.25 })
                    out.onCanvas = s.canvas:AddImage(path, { 350, 10 }, { 470, 70 })
                    return out]])
                eq(out.size, { 64, 32 }, "the picture's size")
                check(out.missing == true and out.icon == true and out.onCanvas == true, "the picture", out)
                local rect = game.wait("client", [[(function()
                    local r = Mods.GateKit.SeForkUi.fileImage.LastRect
                    return r[3] > r[1] and r or nil
                end)()]], 10)
                check(math.abs((rect[3] - rect[1]) - 128) < 2 and math.abs((rect[4] - rect[2]) - 64) < 2, "the drawn size", rect)
                game.shot("se_fork_ui-file.jpg")
            end)
            game.remove(folder)
            check(shown, tostring(trouble))
        else
            skipped[#skipped + 1] = "image files"
        end
    end)
    client([[local s = Mods.GateKit.SeForkUi
        if s then s.window:Destroy() Mods.GateKit.SeForkUi = nil end]])
    check(ok, tostring(problem))

    print("skipped (not in this build): " .. join(skipped, ", ", "nothing"))
end
