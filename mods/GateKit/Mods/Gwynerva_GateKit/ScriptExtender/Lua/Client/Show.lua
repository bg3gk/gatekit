-- GK.Show (client side): a picture put over the game straight from a file - no texture resource,
-- no atlas, no XAML. docs/contracts/lua-api.md, "GK.Show".
--
-- The picture is drawn by Script Extender's own drawing layer (IMGUI), which decodes png, jpg, tga
-- and bmp files read through the game's file system: a file of a mod's package or a loose file
-- under the game's Data folder. That layer is drawn after the game's whole frame, so a picture is
-- over everything the game shows, in any client state that draws frames.

GK.Show = GK.Show or {}
local Show = GK.Show
Show.Pictures = Show.Pictures or {}     -- handle -> the picture on the screen
Show.Ended = Show.Ended or {}           -- handle -> what is known of a picture that left
Show.EndedOrder = Show.EndedOrder or {}
Show.Seq = Show.Seq or 0

local KEPT = 32                          -- how many pictures that left are remembered
local FILE_TYPES = { png = true, jpg = true, jpeg = true, tga = true, bmp = true }
local FITS = { contain = true, cover = true, stretch = true }
-- What the key field can tell apart: a character typed, and the key that ends typing.
local CHARACTER = { space = " ", enter = "\n" }

local function now() return Ext.Utils.MonotonicTime() end
local function log(kind, payload) if GK.EventLog then pcall(GK.EventLog.Append, kind, payload) end end

local function refuse(why, extra)
    local out = { ok = false, error = why }
    for k, v in pairs(extra or {}) do out[k] = v end
    return out
end

local function viewport()
    local v = Ext.IMGUI.GetViewportSize()
    return v[1], v[2]
end

-- `where` as { x, y, w, h } in parts of the screen.
local function rectOf(where)
    if where == nil or where == "screen" then return { 0, 0, 1, 1 } end
    if type(where) ~= "table" then return nil, "where is \"screen\" or { x, y, w, h } in parts of the screen (0 to 1)" end
    local r = { where.x or where[1], where.y or where[2], where.w or where[3], where.h or where[4] }
    for i = 1, 4 do
        if type(r[i]) ~= "number" then return nil, "where needs four numbers: x, y, w, h in parts of the screen (0 to 1)" end
    end
    if r[3] <= 0 or r[4] <= 0 then return nil, "where has no width or no height" end
    if r[1] < -1 or r[2] < -1 or r[3] > 4 or r[4] > 4 or r[1] > 2 or r[2] > 2 then
        return nil, "where is in parts of the screen (0 to 1), not in pixels"
    end
    return r
end

local function colourOf(value)
    if value == nil or value == false then return nil end
    if value == "black" then return { 0, 0, 0, 1 } end
    if value == "white" then return { 1, 1, 1, 1 } end
    if type(value) == "string" then
        local r, g, b, a = value:match("^#(%x%x)(%x%x)(%x%x)(%x?%x?)$")
        if not r then return nil, "a colour is \"black\", \"white\", \"#rrggbb\", \"#rrggbbaa\" or { r, g, b[, a] } from 0 to 1" end
        return { tonumber(r, 16) / 255, tonumber(g, 16) / 255, tonumber(b, 16) / 255, a ~= "" and tonumber(a, 16) / 255 or 1 }
    end
    if type(value) == "table" and type(value[1]) == "number" and type(value[2]) == "number" and type(value[3]) == "number" then
        return { value[1], value[2], value[3], type(value[4]) == "number" and value[4] or 1 }
    end
    return nil, "a colour is \"black\", \"white\", \"#rrggbb\", \"#rrggbbaa\" or { r, g, b[, a] } from 0 to 1"
end

-- fade as { in, out } in seconds.
local function fadeOf(value)
    if value == nil or value == false then return 0, 0 end
    if type(value) == "number" then return math.max(0, value), math.max(0, value) end
    if type(value) == "table" then
        local i, o = value["in"] or value[1] or 0, value.out or value[2] or 0
        if type(i) == "number" and type(o) == "number" then return math.max(0, i), math.max(0, o) end
    end
    return nil
end

-- The keys that take a picture off, as the key field can hear them.
local function skipOf(value)
    if value == nil or value == false then return nil end
    if value == true or value == "any" then return { any = true, chars = {}, names = { "any" } } end
    if type(value) == "string" then value = { value } end
    if type(value) ~= "table" then return nil, "skip is a list of key names" end
    local skip = { chars = {}, names = {} }
    for _, name in ipairs(value) do
        local key = tostring(name):lower()
        if key == "any" then
            skip.any = true
        elseif key == "escape" or key == "esc" then
            skip.escape = true
            key = "escape"
        elseif CHARACTER[key] then
            skip.chars[CHARACTER[key]] = key
        elseif #key == 1 then
            skip.chars[key] = key
        else
            return nil, "skip cannot hear the key '" .. tostring(name) .. "': it hears escape, enter, space, a letter, a digit, "
                .. "or \"any\" (a key that types nothing - an arrow, a function key - reaches neither the picture nor the game)"
        end
        skip.names[#skip.names + 1] = key
    end
    if #skip.names == 0 then return nil end
    return skip
end

-- Where the picture lies in a rectangle of the screen (pixels) by its fit, and which part of it is drawn.
local function place(pic, vw, vh)
    local r = pic.rect
    local x, y, w, h = r[1] * vw, r[2] * vh, r[3] * vw, r[4] * vh
    local pw, ph = pic.size[1], pic.size[2]
    if pic.fit == "stretch" or pw <= 0 or ph <= 0 then return x, y, x + w, y + h, { 0, 0 }, { 1, 1 } end
    local scale = pic.fit == "cover" and math.max(w / pw, h / ph) or math.min(w / pw, h / ph)
    local dw, dh = pw * scale, ph * scale
    if pic.fit == "cover" then
        local u, v = (dw - w) / 2 / dw, (dh - h) / 2 / dh
        return x, y, x + w, y + h, { u, v }, { 1 - u, 1 - v }
    end
    local ox, oy = x + (w - dw) / 2, y + (h - dh) / 2
    return ox, oy, ox + dw, oy + dh, { 0, 0 }, { 1, 1 }
end

local function draw(pic)
    local vw, vh = viewport()
    if vw ~= pic.vw or vh ~= pic.vh then
        pic.vw, pic.vh = vw, vh
        pic.window:SetSize({ vw, vh }, "Always")
        pic.canvas.Size = { vw, vh }
    elseif pic.alpha == pic.drawnAlpha then
        return
    end
    local a = pic.alpha
    pic.drawnAlpha = a
    local c = pic.canvas
    c:Clear()
    if pic.back then
        c:AddRectFilled({ 0, 0 }, { vw, vh }, { pic.back[1], pic.back[2], pic.back[3], pic.back[4] * a })
    end
    local x1, y1, x2, y2, uv0, uv1 = place(pic, vw, vh)
    pic.pixels = { math.floor(x1 + 0.5), math.floor(y1 + 0.5), math.floor(x2 - x1 + 0.5), math.floor(y2 - y1 + 0.5) }
    pic.covers = { x1 / vw, y1 / vh, (x2 - x1) / vw, (y2 - y1) / vh }
    c:AddImage(pic.source, { x1, y1 }, { x2, y2 }, { 1, 1, 1, pic.opacity * a }, uv0, uv1)
end

local function copy(list)
    if not list then return nil end
    local out = {}
    for i, v in ipairs(list) do out[i] = v end
    return out
end

local function describe(pic)
    return { handle = pic.handle, file = pic.file, resource = pic.resource, size = copy(pic.size), where = copy(pic.rect), fit = pic.fit,
             covers = copy(pic.covers), pixels = copy(pic.pixels), screen = pic.vw and { pic.vw, pic.vh } or nil, over = "everything",
             phase = pic.phase, alpha = math.floor(pic.alpha * 1000 + 0.5) / 1000, seconds = pic.seconds, fade = { pic.fadeIn, pic.fadeOut },
             skip = pic.skip and copy(pic.skip.names) or nil, state = pic.state,
             shownMs = pic.drawnAt and math.floor(now() - pic.drawnAt) or nil }
end

local tick

local function forget(pic, why, key)
    Show.Pictures[pic.handle] = nil
    pcall(function() pic.window:Destroy() end)
    local ended = describe(pic)
    ended.phase, ended.alpha, ended.left, ended.why, ended.key = nil, nil, true, why, key
    Show.Ended[pic.handle] = ended
    Show.EndedOrder[#Show.EndedOrder + 1] = pic.handle
    while #Show.EndedOrder > KEPT do Show.Ended[table.remove(Show.EndedOrder, 1)] = nil end
    if pic.drawnAt then
        log("picture_hidden", { handle = pic.handle, why = why, key = key, shownMs = ended.shownMs, file = pic.file, resource = pic.resource })
    end
    if next(Show.Pictures) == nil and Show.TickSubscription then
        Ext.Events.Tick:Unsubscribe(Show.TickSubscription)
        Show.TickSubscription = nil
    end
end

-- Begins a picture's leaving: it fades out and is then forgotten.
local function leave(pic, why, key, fade)
    if pic.phase == "out" then return end
    pic.phase, pic.why, pic.key = "out", why, key
    pic.outFrom, pic.outAt = pic.alpha, now()
    if fade ~= nil then pic.fadeOut = fade end
end

-- One frame of every picture shown. Subscribed only while there is one.
tick = function()
    local t = now()
    for _, pic in pairs(Show.Pictures) do
        if not pic.drawnAt then
            local size = pic.window.LastSize
            if size and size[1] > 0 then
                pic.drawnAt, pic.inAt = t, t
                log("picture_shown", { handle = pic.handle, file = pic.file, resource = pic.resource, where = pic.rect, fit = pic.fit,
                    over = "everything", seconds = pic.seconds, skip = pic.skip and pic.skip.names or nil, state = pic.state })
            end
        end
        if pic.drawnAt then
            if pic.phase == "in" then
                pic.alpha = pic.fadeIn > 0 and math.min(1, (t - pic.inAt) / (pic.fadeIn * 1000)) or 1
                if pic.alpha >= 1 then pic.phase, pic.onAt = "on", t end
            elseif pic.phase == "on" then
                if pic.seconds and t - pic.onAt >= pic.seconds * 1000 then leave(pic, "seconds") end
            end
            if pic.phase == "out" then
                pic.alpha = pic.fadeOut > 0 and math.max(0, pic.outFrom * (1 - (t - pic.outAt) / (pic.fadeOut * 1000))) or 0
            end
            if pic.phase == "out" and pic.alpha <= 0 then
                forget(pic, pic.why, pic.key)
            else
                local ok, err = pcall(draw, pic)
                if not ok then forget(pic, "failed: " .. tostring(err)) end
            end
        end
    end
end

-- The key field: a text field of the drawing layer that holds the keyboard while the picture is
-- shown. A key pressed goes to it and not to the game; it tells a typed character and the end of
-- typing (Escape, or a click of the mouse) and nothing else.
local function listen(pic)
    local field = pic.window:AddInputText("##gk_show_keys_" .. pic.handle, "")
    field.Multiline = true
    field.SizeHint = { 4, 4 }
    field.AbsolutePosition = { 0, 0 }
    pcall(function() field:SetStyle("Alpha", 0) end)
    field.OnChange = function()
        local text = field.Text or ""
        if text == "" or not Show.Pictures[pic.handle] then return end
        local last = text:sub(-1)
        field.Text = ""
        local name = pic.skip.chars[last:lower()]
        if name or pic.skip.any then leave(pic, "skipped", name or (last == " " and "space" or last == "\n" and "enter" or last)) end
    end
    field.OnDeactivate = function()
        if not Show.Pictures[pic.handle] or pic.phase == "out" then return end
        if pic.skip.escape or pic.skip.any then
            leave(pic, "skipped", "escape")
        else
            field:Activate()
        end
    end
    pic.window:SetFocus()
    field:Activate()
    pic.field = field
end

--- Puts a picture over the game. `options`: `file` (png, jpg, tga or bmp, a path under the game's
--- Data folder as a mod ships it) or `resource` (a texture resource's uuid or an icon's name);
--- `where` ("screen", or { x, y, w, h } in parts of the screen); `fit` ("contain", "cover",
--- "stretch"); `back` (a colour behind it over the whole screen); `opacity`; `fade` (seconds, or
--- { in, out }); `seconds` (how long it stays at full strength; none: until hidden); `skip` (key
--- names that take it off); `over` ("everything"). Answers once the first frame with it is drawn.
function Show.Picture(options)
    if type(options) == "string" then options = { file = options } end
    if type(options) ~= "table" then return refuse("GK.Show.Picture takes a table: { file = ... } or { resource = ... }") end
    local over = options.over or "everything"
    if over ~= "everything" then
        return refuse("a picture is drawn by Script Extender's drawing layer, after the game's whole frame: over = \"everything\" is the only "
            .. "layer; under the game's interface needs the interface itself to load the picture, and its texture loader reads the game's "
            .. "own texture resources only", { over = over })
    end
    local source, file, resource = nil, options.file, options.resource
    if file ~= nil and resource ~= nil then return refuse("give file or resource, not both") end
    if type(file) == "string" then
        file = file:gsub("\\", "/")
        if file:match("^%a:/") or file:sub(1, 1) == "/" or file:find("%.%./") then
            return refuse("the game reads a picture through its own file system: give a path under its Data folder, as a mod ships it "
                .. "(Mods/<Mod>/..., Public/<Mod>/...), in a package or loose; a file elsewhere on the disk is not reachable from inside the game",
                { file = file, hint = "put the file (or a link to its folder) under <game>/Data and name it from there" })
        end
        local ext = file:match("%.(%w+)$")
        if not ext or not FILE_TYPES[ext:lower()] then
            return refuse("a picture file is png, jpg, tga or bmp; a dds or a video is not decoded here", { file = file })
        end
        if not file:find("/") then
            return refuse("a picture file is named with its folder under Data; a bare name is read as a texture resource or an icon", { file = file })
        end
        local info = Ext.IMGUI.GetBackendInfo()
        if not (type(info) == "table" and info.Renderer == "DX11") then
            return refuse("pictures from files are decoded under the DX11 renderer only", { file = file, renderer = type(info) == "table" and info.Renderer or nil })
        end
        source = file
    elseif type(resource) == "string" and resource ~= "" then
        source = resource
    else
        return refuse("GK.Show.Picture needs file (a path under the game's Data folder) or resource (a texture's uuid or an icon's name)")
    end
    local rect, whyRect = rectOf(options.where)
    if not rect then return refuse(whyRect) end
    local fit = options.fit or "contain"
    if not FITS[fit] then return refuse("fit is contain, cover or stretch", { fit = fit }) end
    local back, whyBack = colourOf(options.back)
    if whyBack then return refuse(whyBack) end
    local fadeIn, fadeOut = fadeOf(options.fade)
    if not fadeIn then return refuse("fade is seconds, or { in = seconds, out = seconds }") end
    local seconds = options.seconds
    if seconds ~= nil and (type(seconds) ~= "number" or seconds < 0) then return refuse("seconds is a number of seconds") end
    local skip, whySkip = skipOf(options.skip)
    if whySkip then return refuse(whySkip) end

    local okSize, size = pcall(Ext.IMGUI.GetImageSize, source)
    if not okSize or type(size) ~= "table" or not size[1] or size[1] <= 0 then
        return refuse(file and "the game's file system has no such picture, or it is not a png, jpg, tga or bmp inside"
            or "no texture resource or icon of that name", { file = file, resource = resource })
    end

    Show.Seq = Show.Seq + 1
    local handle = "picture-" .. Show.Seq
    local vw, vh = viewport()
    local window = Ext.IMGUI.NewWindow("###gk_show_" .. Show.Seq)
    for _, flag in ipairs({ "NoTitleBar", "NoResize", "NoMove", "NoScrollbar", "NoCollapse", "NoSavedSettings", "NoBackground",
        "NoScrollWithMouse", "NoMouseInputs", "NoBringToFrontOnFocus" }) do
        pcall(function() window[flag] = true end)
    end
    if not skip then
        for _, flag in ipairs({ "NoInputs", "NoNav", "NoFocusOnAppearing" }) do pcall(function() window[flag] = true end) end
    end
    window.Closeable = false
    window:SetPos({ 0, 0 }, "Always")
    window:SetSize({ vw, vh }, "Always")
    window:SetStyle("WindowPadding", 0, 0)
    window:SetStyle("WindowBorderSize", 0)
    window:SetStyle("WindowRounding", 0)
    local pic = { handle = handle, source = source, file = file, resource = resource, size = { size[1], size[2] }, rect = rect, fit = fit,
        back = back, opacity = type(options.opacity) == "number" and math.max(0, math.min(1, options.opacity)) or 1,
        fadeIn = fadeIn, fadeOut = fadeOut, seconds = seconds, skip = skip, window = window, phase = "in",
        alpha = fadeIn > 0 and 0 or 1, state = tostring(Ext.Utils.GetGameState and Ext.Utils.GetGameState() or nil) }
    if skip then listen(pic) end
    local okCanvas, canvas = pcall(function() return window:AddCanvas(vw, vh) end)
    if not okCanvas or not canvas then
        pcall(function() window:Destroy() end)
        return refuse("this Script Extender build has no canvas to draw a picture on (GateKit's build has)")
    end
    canvas.AbsolutePosition = { 0, 0 }
    pic.canvas, pic.vw, pic.vh = canvas, vw, vh
    local okDraw, whyDraw = pcall(function() pic.vw = nil; draw(pic) end)
    if not okDraw then
        pcall(function() window:Destroy() end)
        return refuse("the picture could not be drawn: " .. tostring(whyDraw), { file = file, resource = resource })
    end
    window.Open = true
    Show.Pictures[handle] = pic
    if not Show.TickSubscription then Show.TickSubscription = Ext.Events.Tick:Subscribe(function() tick() end) end

    return GK.Observe({ ok = true, handle = handle, waiting = true }, function(ticks, final)
        local shown = Show.Pictures[handle]
        if not shown then
            local ended = Show.Ended[handle] or {}
            return refuse("the picture left before it was drawn: " .. tostring(ended.why), { handle = handle })
        end
        if shown.drawnAt then
            local out = describe(shown)
            out.ok, out.ticks = true, ticks
            return out
        end
        if final then
            forget(shown, "never drawn")
            return refuse("the picture was never drawn: the drawing layer showed no frame of its window in this state of the client",
                { handle = handle, state = shown.state })
        end
    end, 3000)
end

--- A video over the game. Refused: what plays one is not reachable from Lua; the answer names it.
function Show.Video(options)
    if type(options) == "string" then options = { file = options } end
    local file = type(options) == "table" and options.file or nil
    return refuse("no video can be played from Lua: the game's player takes Bink 2 files (.bk2) only and is asked by the game's own code; "
        .. "the interface's video element (ls.LSVideoPanel, with Video and AutoPlay) plays one inside a widget a mod ships as XAML and "
        .. "nothing creates one from Lua; Script Extender's drawing layer decodes still pictures", { file = file,
        hint = "a mod's widget with <ls:LSVideoPanel Video=\"<name>\" AutoPlay=\"True\"/> and the file <name>.bk2 where the game's own videos lie (Video/, "
            .. "or Mods/<Mod>/Localization/<language>/Video/); "
            .. "a still picture is shown by GK.Show.Picture" })
end

--- Takes a picture off (`handle`), or every picture (no handle). `options.fade`: seconds of the
--- fade out (default: the picture's own). Answers once it is gone.
function Show.Hide(handle, options)
    if type(handle) == "table" and options == nil then handle, options = nil, handle end
    local fade = type(options) == "table" and options.fade or nil
    if fade ~= nil and type(fade) ~= "number" then return refuse("fade is seconds") end
    local handles = {}
    if handle ~= nil then
        if not Show.Pictures[handle] then
            local ended = Show.Ended[handle]
            if ended then return refuse("that picture has left already: " .. tostring(ended.why), { handle = handle, why = ended.why }) end
            return refuse("no picture with the handle " .. tostring(handle), { handle = handle })
        end
        handles[1] = handle
    else
        for h in pairs(Show.Pictures) do handles[#handles + 1] = h end
        table.sort(handles)
        if #handles == 0 then return { ok = true, hidden = {}, did = "nothing was shown" } end
    end
    for _, h in ipairs(handles) do
        local pic = Show.Pictures[h]
        if not pic.drawnAt then forget(pic, "hidden") else leave(pic, "hidden", nil, fade) end
    end
    return GK.Observe({ ok = true, hidden = handles, waiting = true }, function(ticks, final)
        local left = {}
        for _, h in ipairs(handles) do
            if Show.Pictures[h] then
                if not final then return nil end
                return refuse("the picture did not leave", { handle = h })
            end
            left[#left + 1] = Show.Ended[h]
        end
        return { ok = true, hidden = handles, left = left, ticks = ticks }
    end, 15000)
end

--- What is shown: a list of the pictures on the screen, each as GK.Show.Picture answered it with
--- its `phase` ("in", "on", "out") and `alpha`. With a handle: that picture, shown or left (`left`,
--- `why`, `key`, `shownMs`), or nil.
function Show.Shown(handle)
    if handle ~= nil then
        local pic = Show.Pictures[handle]
        if pic then return describe(pic) end
        return Show.Ended[handle]
    end
    local list = {}
    for _, pic in pairs(Show.Pictures) do list[#list + 1] = describe(pic) end
    table.sort(list, function(a, b) return a.handle < b.handle end)
    return list
end
