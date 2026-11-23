-- Shared by the loading tests: a chunk for the interface's own context (`game.ui`) that reads the
-- live Noesis tree and answers what draws a waiting indicator now - every d20 of ours (the mod's
-- BusyElement, found by the names its template gives: GK_D20, GK_D20_Face) and every spinner image
-- of the game's (ico_spinner) - each with the widget it lies in and that widget's data.
-- Answer: { dice = { { widget, data, face, progress, visible, width, angle, scale } }, spinners =
-- { { widget, data } }, pages = { { name, data, visible } }, walked }.

local D = {}

D.READ = [[
local function tn(o) local ok, n = pcall(function() return tostring(o.Type) end) return ok and n or "?" end
local function get(o, k) if not o then return nil end local ok, v = pcall(function() return o[k] end) if ok then return v end end
local out = { dice = {}, spinners = {}, pages = {}, widgets = {}, walked = 0 }
local root = Ext.UI.GetRoot()
if not root then return out end
local stack = { { root, 0, nil, nil } }
while #stack > 0 do
    local top = table.remove(stack)
    local e, depth, widget, die = top[1], top[2], top[3], top[4]
    out.walked = out.walked + 1
    if out.walked > 60000 then out.cut = true break end
    local t = tn(e)
    local name = get(e, "Name")
    if t:find("UIWidget") then
        local dc = get(e, "DataContext")
        widget = { name = name or "", data = dc and tn(dc) or "" }
        out.widgets[#out.widgets + 1] = { name = widget.name, data = widget.data, order = out.walked,
                                          depth = depth, visible = get(e, "IsVisible") }
    end
    if name and name:find("^GK_OWN") then
        out.pages[#out.pages + 1] = { name = name, widget = widget and widget.name, data = widget and widget.data,
            visible = get(e, "IsVisible"), width = get(e, "ActualWidth"), height = get(e, "ActualHeight"),
            opacity = get(e, "Opacity") }
    end
    if name == "LoadingProgress" then die = { game = true } end
    if die and die.game and t:find("TextBlock") and not out.percent then out.percent = get(e, "Text") end
    if name == "GK_D20" then
        die = { widget = widget and widget.name, data = widget and widget.data, visible = get(e, "IsVisible"),
                width = get(e, "ActualWidth"), angle = get(get(e, "RenderTransform"), "Angle") }
        out.dice[#out.dice + 1] = die
    elseif die and name == "GK_D20_Pulse" then
        die.scale = get(get(e, "RenderTransform"), "ScaleX")
    elseif die and name == "GK_D20_Face" then
        local dc = get(e, "DataContext")
        die.face = get(e, "Text")
        die.progress = get(dc, "Progress")
    end
    if t:find("Image") then
        local src = get(e, "Source")
        local uri = src and get(src, "UriSource")
        if uri and tostring(uri):find("ico_spinner") then
            out.spinners[#out.spinners + 1] = { widget = widget and widget.name, data = widget and widget.data,
                                                visible = get(e, "IsVisible") }
        end
    end
    if depth < 80 then
        local n = 0
        pcall(function() n = e.VisualChildrenCount end)
        for i = n, 1, -1 do
            local ok, child = pcall(function() return e:VisualChild(i) end)
            if ok and child then stack[#stack + 1] = { child, depth + 1, widget, die } end
        end
    end
end
return out
]]

-- The game's loading screen as the tree shows it: its widget's x:Name and the type its data
-- reads as (`code:gui::DCLoadingScreen`, handed out by Script Extender as ls.LoadingScreen).
D.SCREEN = { widget = "root", data = "ls.LoadingScreen" }

--- The dice of a reading drawn in the widget named `widget`, shown.
function D.dice(reading, widget)
    local found = {}
    for _, d in ipairs(reading.dice or {}) do
        if d.widget == widget and d.visible then found[#found + 1] = d end
    end
    return found
end

--- The game's own spinners of a reading in the widget named `widget`, shown.
function D.spinners(reading, widget)
    local found = {}
    for _, s in ipairs(reading.spinners or {}) do
        if s.widget == widget and s.visible ~= false then found[#found + 1] = s end
    end
    return found
end

--- Whether a reading holds the game's loading screen.
function D.screen(reading)
    for _, w in ipairs(reading.widgets or {}) do
        if w.name == D.SCREEN.widget and w.data == D.SCREEN.data then return w end
    end
end

--- An element of ours (GK_OWN_*) of a reading, by name.
function D.page(reading, name)
    for _, p in ipairs(reading.pages or {}) do
        if p.name == name then return p end
    end
end

--- Reads the interface one reading after another - each is a chunk the interface answers in a
--- frame of its own, so no pause is put between them - until `done(reading)` or a row of one of
--- `ends` (default: `loading_ended`) after `since` (when `since` is given): the game's own end of
--- what is watched. `seconds` (default 120) only guards against a hang. The first reading
--- `photo(reading)` accepts is photographed into the test's output as `shot`. Answers the
--- readings; a reading the interface did not answer (it can be busy inside a load) is left out.
function D.watch(game, opts)
    local readings, photographed = {}, nil
    local ends = opts.ends or { "loading_ended" }
    local deadline = game.now() + (opts.seconds or 120)
    while game.now() < deadline do
        local ok, r = pcall(game.ui, D.READ, 10)
        if ok and type(r) == "table" then
            r.at = game.now()
            readings[#readings + 1] = r
            if opts.photo and not photographed and opts.photo(r) then
                photographed = game.shot(opts.shot or "load.jpg", 0)
            end
            if opts.done and opts.done(r, readings) then break end
        end
        if opts.since and #game.events(opts.since, ends).rows > 0 then break end
    end
    return readings, photographed
end

return D
