-- Weave (mods/Weave/README.md), driven through GK.Weave.* and GK.Overlay*, no screen reading: a
-- window mounted from a component (template, script and style in one file), reactivity (state,
-- {{ }}, #if, #for, #model, :class, computed), nested components with slots and scoped styles,
-- utility classes with variants, themes, text sizes, hot reload without a Lua restart, settings
-- that survive one, the HUD button, windows that come back when pushed out of the picture; then
-- gradients, shadows, outlines, a canvas, alignment, flex and grid, the widgets' own options,
-- blocks in files of their own, translations and storage. Needs GateKit's build of Script
-- Extender (GATEKIT_SE) and the Weave mod (Gwynerva_Weave; GK.Weave is its alias here).

scenario { level = "GustavX:TUT_Avernus_C", mods = {} }

-- Weave's own picture, in its mod folder (it moved out of Gwynerva_GateKit with the mod).
local WEAVE_PNG = "Mods/Gwynerva_Weave/ScriptExtender/Lua/ui/weave.png"

local BADGE = [[
<template>
  <group class="badge">
    <text class="caption">{{ caption }}: {{ amount * 2 }}</text>
    <slot><text class="empty">nothing inside</text></slot>
    <button class="more" @click="emit('more', amount + 1)">More</button>
  </group>
</template>
<style scoped>
text { color: #00ff00; }
</style>
]]

local PROBE = [[
<template>
  <window id="probe" title="Probe: {{ count }}" width="420" height="340" x="80" y="140">
    <text id="hello" class="title" :class="{ big = count >= 2 }">Hello, {{ name }}! {{ count }} clicks, {{ double }} doubled.</text>
    <row>
      <button id="plus" class="primary" @click="count++">Plus</button>
      <button id="add" @click="add()">Add</button>
      <checkbox id="loud" #model="loud" label="Loud"/>
    </row>
    <text id="shout" #if="loud &amp;&amp; count > 0">LOUD</text>
    <text id="calm" #else>calm</text>
    <combo id="level" options="Debug|Info|Error" #model="level"/>
    <input id="name" #model="name"/>
    <child id="list" height="60" border="true">
      <bullet #for="item, i in items" :key="item.id" class="item">{{ i }}: {{ item.label }}</bullet>
    </child>
    <probe-badge id="badge" caption="Badge" :amount="count" @more="count = value">
      <text id="inside">inside {{ name }}</text>
    </probe-badge>
    <text id="outside">outside</text>
    <text id="util" class="px-4 py-2 text-red-500/50 md:hidden">utility</text>
    <text id="applied" class="chip">applied</text>
  </window>
</template>
<script>
name = "Tav"
count = 0
loud = false
level = "Info"
items = {}
computed("double", function() return count * 2 end)
function add() items[#items + 1] = { id = #items + 1, label = "item " .. (#items + 1) } end
watch("level", function(new, old) lastLevel = old .. ">" .. new end)
</script>
<style>
@theme gate  { --probe: #ff0000; }
@theme slate { --probe: #0000ff; }
.title         { color: var(--probe); font: +1; }
.title.big     { font: +2; }
#plus          { background: #808080; rounding: 9; padding: 6 14; }
#plus:hover    { background: #ffffff; }
#list          { background: #000000; }
.chip          { @apply px-2 rounded-lg; color: #123456; }
</style>
]]

local GFX = ([[
<template>
  <window id="gfx" title="Weave graphics" width="460" height="560" x="520" y="140">
    <group id="card" class="fancy">
      <text id="label">behind a group</text>
    </group>
    <button id="glow" class="glow" :class="{ tinted = tinted }">Gradient</button>
    <button id="tw" class="bg-linear-to-b from-white to-black shadow-lg outline-2 outline-white rounded-lg">Utilities</button>
    <button id="locked" disabled="true">Locked</button>
    <text id="right" class="text-right">right</text>
    <canvas id="c" width="300" height="80" :draw="drawing"/>
    <image id="pic" src="%s" width="32"/>
    <text id="tiled" class="tiled">tiled</text>
    <text id="big" class="big">Big</text>
    <button id="fade" class="fade" :class="{ lit = tinted }">Fade</button>
  </window>
</template>
<script>
tinted = false
drawing = { { "fill", 0, 0, 300, 80, "#101010", 4 }, { "line", 0, 40, 300, 40, "var(--accent)", 2 },
    { "text", 6, 6, "#ffffff", "canvas" }, { "nonsense" } }
</script>
<style>
window  { min-width: 400; min-height: 250; }
.fancy  { background: linear-gradient(to right, #804000, #000000); rounding: 8; background-padding: 6;
          outline: 2 #ffcc00; box-shadow: 3 3 10 alpha(#000000, 0.5); }
.glow   { background: linear-gradient(#ffffff, #000000); }
#label  { background: #202020; }
.tinted { color: #00ff00; padding: 4 20; font: Big; width: 200; }
.tiled  { background-image: url(%s); background-mode: tile; background-tint: #ffffff80; }
@font-face { font-family: Probe; src: url(Public/Game/GUI/Assets/Fonts/QuadraatOffcPro/QuadraatOffcPro.ttf); }
.big    { font-family: Probe; font-size: 40; }
.fade   { background: #000000; transition: 0.3; }
.fade.lit { background: #ffffff; }
</style>
]]):format(WEAVE_PNG, WEAVE_PNG)

local LAYOUT = [[
<template>
  <window id="lay" title="Weave layout" width="500" height="360" x="520" y="140">
    <group id="bar" class="flex justify-between items-center">
      <text id="brand">Brand</text>
      <group id="nav" class="flex gap-2">
        <button #for="name in pages" :key="name">{{ name }}</button>
      </group>
    </group>
    <group id="find" class="flex items-center">
      <text id="find-label">Find</text>
      <input id="q" class="grow"/>
      <button id="go">Go</button>
    </group>
    <group id="cards" class="cards">
      <child #for="n in cards" :key="n" class="cell" height="40"/>
    </group>
    <text id="narrow">narrow</text>
  </window>
</template>
<script>
pages = { "One", "Two" }
cards = 3
</script>
<style>
.cards  { display: grid; grid-template-columns: 1fr; gap: 10 10; }
#narrow { display: none; }
@media (min-width: 700) { .cards { grid-template-columns: repeat(3, 1fr); } }
@media (max-width: 699) { #narrow { display: block; color: #ff0000; } }
</style>
]]

local CSS4 = [[
<template>
  <window id="css4" title="Weave CSS" height="620" x="60" y="100">
    <group id="list" class="list">
      <text #for="n in rows" :key="n" class="row even:opacity-50">row {{ n }}</text>
    </group>
    <group id="none" class="list"/>
    <group id="one"><text id="only" class="lone">only</text></group>
    <button id="on" kind="primary-big" :mood="mood">On</button>
    <button id="off" kind="ghost" class="disabled:opacity-25" disabled="true">Off</button>
    <group id="card" class="card wide">
      <text id="title" class="title">title</text>
      <group><text id="deep" class="title">deep</text></group>
    </group>
    <button id="calc">calc</button>
    <text id="bad">bad</text>
    <group id="box" class="box">
      <button id="a">A</button>
      <button id="e">E</button>
      <button id="b">B</button>
      <child id="abs"/>
    </group>
    <group id="col" class="col">
      <button id="c">C</button>
      <child id="video"/>
      <button id="d">D</button>
    </group>
    <group id="tight" class="flex gap-x-2">
      <button id="s1" class="basis-[150]">S1</button>
      <button id="s2" class="basis-[150] shrink-0 -ml-1 mr-[3]">S2</button>
    </group>
    <text id="after">after</text>
  </window>
</template>
<script>
rows = 3
mood = "calm"
</script>
<style>
window  { width: 30%; }
.row:first-child              { color: #ff0000; }
.row:last-child               { color: #0000ff; }
.row:not(:first-child):not(:last-child) { color: #00ff00; }
.row:nth-child(2n+1)          { font: +1; }
.list:empty                   { color: #123456; }
.lone:only-child              { color: #654321; }
button[kind]                  { rounding: 3; }
button[kind=ghost]            { rounding: 5; }
[kind^=primary][kind$="big"]  { border: 2; }
[kind*=mary-b]                { color: #ffffff; }
[mood=angry]                  { color: #ff0000; }
button:disabled               { color: #808080; }
button:not(:disabled):not([kind]) { rounding: 1; }
.card {
  color: #010203;
  &.wide { rounding: 4; }
  .title { color: #ff00ff; }
  & > text { font: +2; }
  @media (min-width: 100) { opacity: 0.5; }
  @media (min-width: 9000) { opacity: 0.25; }
}
#calc   { width: calc(100 + 2 * (30 - 5)); height: clamp(10, 10vh + 5000, 40); indent: min(1vw, 0.5vw); }
#bad    { width: 50%; }
.box    { display: flex; width: 400; padding: 10 20 30 40; background: #202020; }
#a      { width: 25%; }
#e      { margin-left: -8; }
#b      { margin: 0 10 0 auto; }
#abs    { position: absolute; right: 5; bottom: 5; width: 20; height: 20; }
.col    { display: flex; flex-direction: column; width: 300; row-gap: 20; }
#c      { margin: 0 auto; width: 100; }
#video  { width: 50%; aspect-ratio: 2 / 1; }
#d      { margin-top: 12; order: -1; }
#tight  { width: 200; }
</style>
]]

local WIDGETS = [[
<template>
  <window id="wid" title="Weave widgets" width="460" height="520" x="520" y="140">
    <header id="head" label="Header" :open="opened" @expand="expanded = expanded + 1" @collapse="collapsed = collapsed + 1">
      <text id="inside">inside</text>
    </header>
    <tree id="branch" label="Tree" open><text>leaf</text></tree>
    <selectable id="sel" @dblclick="doubles = doubles + 1">pick</selectable>
    <drag id="dr" speed="0.25" min="0" max="10"/>
    <number id="num" int step="5" value="1"/>
    <table id="tbl" columns="2" inner-width="600" flags="ScrollX"><tr><td><text>a</text></td><td><text>b</text></td></tr></table>
    <text id="medium">Medium</text>
    <text id="large" style="font: Large">Large</text>
    <text id="arb" class="text-[40]">text-[40]</text>
    <text id="rem" class="text-[1.25rem]">1.25rem</text>
    <group id="sized" style="font-size: 40">
      <text id="forty">forty</text>
      <text id="half" style="font-size: 0.5em">half</text>
    </group>
  </window>
</template>
<script>
opened = false
expanded = 0
collapsed = 0
doubles = 0
</script>
]]

local FILES = [[
<template>
  <window id="extra" title="Weave files" width="360" height="200" x="900" y="140">
    <text id="tr">{{ t(handle) }}</text>
    <text id="raw">{{ t("no such handle") }}</text>
    <text id="kept">{{ settings.volume }}</text>
  </window>
</template>
<script src="probe-extra.lua"/>
<style src="probe-extra.css"/>
]]

--- Gradients, shadows, outlines, backgrounds behind text, a canvas, alignment, lasting size
--- constraints, styles taken back in place, pictures, fonts of one's own, transitions.
local function graphics(game, features)
    local client = game.client
    client(("GK.Weave.RegisterComponent('gfx', %s) KitGfx = GK.Weave.Mount('gfx') KitGfx:Open()"):format(lua_string(GFX)))
    local tree = client("return GK.Weave.Dump('gfx')")
    check(#tree.children == 10, "not every element was built", tree)
    local tiled = client("return GK.Weave.Dump('gfx', '#tiled').style")
    check(endswith(tiled["deco:image"], "weave.png") and tiled["deco:imageMode"] == "Tile", "the tiled picture", tiled)
    check(near(tiled["deco:imageTint"], { 1, 1, 1, 0.5 }), "the tint", tiled)
    check(client("return GK.Weave.Dump('gfx', '#fade').style['prop:transition']") == 0.3, "the transition")
    local card = client("return GK.Weave.Dump('gfx', '#card').style")
    check(card["deco:gradient"][3] == 90 and card["deco:border"][2] == 2 and card["deco:rounding"] == 8, "the card", card)
    check(near(card["deco:shadow"][1], { 0, 0, 0, 0.5 }), "the shadow colour", card)
    eq({ card["deco:shadow"][2], card["deco:shadow"][3], card["deco:shadow"][4] }, { 10, 3, 3 }, "the shadow")
    local glow = client("return GK.Weave.Dump('gfx', '#glow').style")
    eq(glow["color:Button"], { 0, 0, 0, 0 }, "the flat fill gives way to the gradient")
    local tw = client("return GK.Weave.Dump('gfx', '#tw').style") -- the same from utility classes
    check(tw["deco:gradient"][3] == 180 and tw["deco:border"][2] == 2, "the utility decorations", tw)
    eq({ tw["deco:shadow"][2], tw["deco:shadow"][3], tw["deco:shadow"][4] }, { 14, 0, 6 }, "the utility shadow")
    local locked = client("return { GK.Weave.Dump('gfx', '#locked'), KitGfx:Get('#locked').widget.Disabled }")
    check(locked[1].disabled and contains(locked[1].class, "disabled") and locked[2] == true, "the disabled button", locked)
    check(client("return GK.Weave.Dump('gfx', '#c').commands") == 4, "the canvas commands")

    game.wait("client", "#GK.ConsoleLog{text='no such command: nonsense'}.entries > 0", 10)
    check(client("return KitGfx:Get('#c').widget:GetCommandCount()") == 3, "the bad command is skipped")
    client("KitGfx.state.drawing = { { 'disc', 20, 20, 8, '#ff0000' } } GK.Weave.flush()")
    check(client("return KitGfx:Get('#c').widget:GetCommandCount()") == 1, "a drawing follows state")

    -- text-right - the element ends where the window's contents end, and follows a resize.
    local function right_edge()
        return game.wait("client", [[(function()
            local r, w = KitGfx:Get('#right').widget.LastRect, KitGfx.window.LastRect
            local pad = KitGfx:Get('#glow').widget.LastRect[1] - w[1]
            return r[3] > r[1] and math.abs(r[3] - (w[3] - pad)) <= 1.5 and { r, w } end)()]], 10)
    end
    right_edge()
    client("KitGfx.window:SetSize({ 600, 560 }, 'Always')")
    game.wait("client", "KitGfx.window.LastSize[1] == 600", 10)
    right_edge()
    -- min-width / min-height last.
    client("KitGfx.window:SetSize({ 100, 100 }, 'Always')")
    game.sleep(0.6)
    eq(client("return KitGfx.window.LastSize"), { 400, 250 }, "the minimum size")
    -- A rule that stops applying is taken back in place - colours, style variables, the text
    -- size, a width: the same element, no rebuild.
    local widget = "KitGfx:Get('#glow').widget"
    local width = ("(function() local r = %s.LastRect return r[3] - r[1] end)()"):format(widget)
    local plain_width = client("return " .. width)
    client("KitGfx:Get('#glow').marker = 7 KitGfx:Set('tinted', true)")
    eq(client(("return { %s:GetColor('Text') ~= nil, %s:GetStyle('FramePadding') ~= nil, %s.FontSize }")
        :format(widget, widget, widget)), { true, true, 44 }, "the tinted style")
    game.wait("client", ("%s == 200"):format(width), 10)
    client("KitGfx:Set('tinted', false)")
    game.wait("client", ("%s == %s"):format(width, plain_width), 10)
    game.sleep(1.0) -- a rebuild would have happened by now
    eq(client(("return { KitGfx:Get('#glow').marker, %s:GetColor('Text') == nil, %s:GetStyle('FramePadding') == nil, " ..
        "%s.FontSize == nil, KitGfx.pendingRebuild }"):format(widget, widget, widget)),
        { 7, true, true, true, false }, "the style was not taken back in place")
    -- Pictures from files: the DX11 renderer draws them; under Vulkan they are left out, said once.
    if features.files then
        local size = client(("return { Ext.IMGUI.GetImageSize('%s'), KitGfx:Get('#pic').widget.LastRect }"):format(WEAVE_PNG))
        eq(size[1], { 64, 64 }, "the picture's size")
        check(math.abs((size[2][3] - size[2][1]) - 32) <= 1 and math.abs((size[2][4] - size[2][2]) - 32) <= 1, "the drawn size", size)
    else
        game.wait("client", "#GK.ConsoleLog{text='pictures from files need the DX11 renderer'}.entries > 0", 10)
    end
    -- A font of one's own at a size of its own: loaded once, sized like any other text.
    local big = client("local w = KitGfx:Get('#big').widget local r = w.LastRect return { r[4] - r[2], w.Font, w.FontSize }")
    check(big[1] > 22 and big[2] == "Weave/Probe" and big[3] == 40, "the font of one's own", big)
    client("KitGfx:Set('tinted', true)")
    check(client("return KitGfx:Get('#fade').widget:IsAnimating()") == true, "the transition did not start")
    game.wait("client", "not KitGfx:Get('#fade').widget:IsAnimating()", 10)
    client("KitGfx:Set('tinted', false)")
    game.shot("weave_graphics.jpg")
    client("KitGfx:Destroy()")
end

--- Flex, grid and @media: positions are computed by the kit and stand still; a breakpoint
--- restyles the window; what #for builds takes part in its parent's layout.
local function layout(game)
    local client = game.client
    client(("GK.Weave.RegisterComponent('lay', %s) KitLay = GK.Weave.Mount('lay') KitLay:Open()"):format(lua_string(LAYOUT)))
    check(client("return GK.Weave.Dump('lay', '#cards').style['prop:layout:display']") == "grid", "the grid")
    -- @media works from the window's size alone - on any build.
    game.wait("client", "GK.Weave.Dump('lay', '#narrow').visible == nil", 10)
    client("KitLay.window:SetSize({ 760, 360 }, 'Always')")
    game.wait("client", "GK.Weave.Dump('lay', '#narrow').visible == false", 10)
    check(client("return GK.Weave.Dump('lay', '#cards').style['prop:layout:columns']") == "repeat(3, 1fr)", "the columns")
    local rects = [[(function()
        local o, cells = {}, KitLay:Query('.cell')
        for _, id in ipairs({ 'brand', 'nav', 'find-label', 'q', 'go' }) do o[id] = KitLay:Get('#' .. id).widget.LastRect end
        local buttons = KitLay:Query('#nav button')
        o.n1, o.n2 = buttons[1].widget.LastRect, buttons[#buttons].widget.LastRect
        for i, cell in ipairs(cells) do o['c' .. i] = cell.widget.LastRect end
        o.window = KitLay.window.LastRect
        o.left = KitLay:Get('#bar').anchor.LastRect[1]
        return o
    end)()]]

    local r = settled(rects)
    local left = r.left
    local right = r.window[3] - (left - r.window[1]) -- the same padding on both sides
    check(math.abs(r.brand[1] - left) <= 1 and math.abs(r.n2[3] - right) <= 1.5, "space-between", r)
    check(r.n1[3] < r.n2[1] and math.abs(r.n1[2] - r.n2[2]) <= 1, "the nested row", r)
    check(math.abs(r.go[3] - right) <= 1.5 and math.abs(r.q[3] - r.go[1]) < 20, "the input grew", r)
    check(r.q[3] - r.q[1] > 300, "the input is too narrow", r)
    local third = (right - left - 20) / 3 -- three columns
    check(math.abs((r.c1[3] - r.c1[1]) - third) <= 2 and math.abs(r.c2[1] - (r.c1[3] + 10)) <= 2, "the columns", r)
    check(math.abs(r.c1[2] - r.c3[2]) <= 1 and r.c3[1] > r.c2[3], "one row of three", r)
    -- What #for adds joins the layout: a fourth cell starts the second row, a third page button
    -- pushes the others left.
    client("KitLay:Set({ cards = 4, pages = { 'One', 'Two', 'Three' } })")
    game.wait("client", "#KitLay:Query('.cell') == 4", 10)
    r = settled(rects)
    check(r.c4[2] > r.c1[4] and math.abs(r.c4[1] - r.c1[1]) <= 1, "the fourth cell", r)
    check(math.abs(r.n2[3] - right) <= 1.5, "the nav moved", r)
    -- Below the breakpoint the grid is one column again, without a rebuild.
    client("KitLay:Get('#cards').marker = 5 KitLay.window:SetSize({ 500, 360 }, 'Always')")
    game.wait("client", "GK.Weave.Dump('lay', '#narrow').visible == nil", 10)
    r = settled(rects)
    check(r.c2[2] > r.c1[4] and math.abs(r.c2[1] - r.c1[1]) <= 1, "one column again", r)
    check(math.abs((r.c1[3] - r.c1[1]) - (r.window[3] - r.window[1] - 2 * (r.left - r.window[1]))) <= 2, "the full width", r)
    check(client("return KitLay:Get('#cards').marker") == 5, "the grid was rebuilt")
    game.shot("weave_layout.jpg")
    client("KitLay:Destroy()")
end

--- Selectors that look at siblings, attributes and state; rules inside rules; calc() and
--- viewport units; and - on GateKit's build of Script Extender - the box model in a flex
--- container: margins, padding, percentages, aspect-ratio, absolute positioning, shrinking.
local function css4(game)
    local client = game.client
    local said = client("return #GK.ConsoleLog{text='css4'}.entries")
    client(("GK.Weave.RegisterComponent('css4', %s) KitCss = GK.Weave.Mount('css4') KitCss:Open()"):format(lua_string(CSS4)))

    local function style(selector)
        return client(("return GK.Weave.Dump('css4', '%s').style"):format(selector)) or {}
    end

    local function rows()
        return client([[local o = {} for i, el in ipairs(KitCss:Query('.row')) do
            local s = el.applied o[i] = { { table.unpack(s['color:Text'].value) }, s['prop:font-size'] and s['prop:font-size'].value or false,
                s['var:Alpha'] and s['var:Alpha'].value[1] or false } end return o]])
    end

    local red, blue = { 1, 0, 0, 1 }, { 0, 0, 1, 1 }
    local green = client("return GK.Weave.ParseColor('#00ff00')")
    -- :first-child, :last-child, :not(), :nth-child(an+b), and even: on a utility class.
    local r = rows()
    check(near(r[1][1], red) and near(r[2][1], green) and near(r[3][1], blue), "the row colours", r)
    check(r[1][2] and not r[2][2] and r[3][2] and r[1][2] == r[3][2], "nth-child(2n+1)", r)
    eq({ r[1][3], r[2][3], r[3][3] }, { false, 0.5, false }, "even:opacity-50")
    -- A list that changes: the rows that were there are styled for their new places.
    client("KitCss:Query('.row')[1].marker = 4 KitCss:Set('rows', 4)")
    r = rows()
    check(near(r[1][1], red) and near(r[3][1], green) and near(r[4][1], blue), "the new places", r)
    eq({ r[1][3], r[2][3], r[3][3], r[4][3] }, { false, 0.5, false, 0.5 }, "even, four rows")
    client("KitCss:Set('rows', 2)")
    r = rows()
    check(#r == 2 and near(r[1][1], red) and near(r[2][1], blue) and not r[2][2], "two rows", r)
    check(client("return KitCss:Query('.row')[1].marker") == 4, "the rows that stay are not built anew")
    -- :empty and :only-child.
    check(near(style("#none")["color:Text"], client("return GK.Weave.ParseColor('#123456')")), ":empty")
    check(style("#list")["color:Text"] == nil, ":empty must not match a full list")
    check(near(style("#only")["color:Text"], client("return GK.Weave.ParseColor('#654321')")), ":only-child")
    -- Attributes: there at all, equal, starting, ending, containing - and one bound to state.
    local on, off = style("#on"), style("#off")
    eq(on["var:FrameRounding"], { 3, 3 }, "button[kind]")
    eq(off["var:FrameRounding"], { 5, 5 }, "button[kind=ghost]")
    check(same(on["var:FrameBorderSize"], { 2, 2 }) and off["var:FrameBorderSize"] == nil, "[kind^=][kind$=]", on, off)
    check(near(on["color:Text"], { 1, 1, 1, 1 }), "[kind*=mary-b]", on)
    client("KitCss:Set('mood', 'angry')")
    check(near(style("#on")["color:Text"], red), "an attribute bound to state")
    -- :disabled (and disabled: on a utility class) follow the element.
    check(near(off["color:Text"], client("return GK.Weave.ParseColor('#808080')")) and same(off["var:Alpha"], { 0.25, 0.25 }),
        ":disabled", off)
    eq(style("#calc")["var:FrameRounding"], { 1, 1 }, ":not(:disabled):not([kind])")
    client("KitCss:Get('#off'):Enable()") -- taken back in place
    off = style("#off")
    check(off["color:Text"] == nil and off["var:Alpha"] == nil, "the disabled style was not taken back", off)
    -- Rules inside a rule: &.x, a descendant, & > child, @media.
    local card, title, deep = style("#card"), style("#title"), style("#deep")
    check(near(card["color:Text"], client("return GK.Weave.ParseColor('#010203')")) and same(card["var:Alpha"], { 0.5, 0.5 }),
        "the nested rules", card)
    eq(card["var:FrameRounding"], { 4, 4 }, "&.wide")
    check(near(title["color:Text"], { 1, 0, 1, 1 }) and near(deep["color:Text"], { 1, 0, 1, 1 }), "a descendant rule", title, deep)
    check(title["prop:font-size"] and not deep["prop:font-size"], "& > text", title, deep)
    -- calc(), clamp(), min() and viewport units; a window's percentage is of the picture.
    local viewport = client("return Ext.IMGUI.GetViewportSize()")
    local calc = style("#calc")
    check(calc["prop:width"] == 150 and calc["prop:height"] == 40, "calc() and clamp()", calc)
    check(math.abs(calc["var:IndentSpacing"][1] - viewport[1] * 0.005) < 0.01, "min() with viewport units", calc, viewport)
    check(client("return KitCss.viewportUnits") == true, "the viewport units were not noticed")
    check(math.abs(client("return KitCss.root.size[1]") - viewport[1] * 0.3) < 0.01, "the window's percentage")
    game.wait("client", ("math.abs(KitCss.window.LastSize[1] - %s) <= 1"):format(viewport[1] * 0.3), 10)
    -- When the picture's size changes (here: the kit is told it was another), what was worked
    -- out from it is worked out again - shown by a style put in behind the kit's back.
    client("KitCss:Get('#calc').inline = GK.Weave.ParseInline('width: 20vw') KitCss.viewportSeen = { 1, 1 }")
    game.wait("client", ("math.abs(GK.Weave.Dump('css4', '#calc').style['prop:width'] - %s) < 0.01"):format(viewport[1] * 0.2), 10)
    -- A percentage with nothing to be a percentage of is said so, once, and left out.
    check(style("#bad")["prop:width"] == nil, "a percentage with no container")
    game.wait("client", "#GK.ConsoleLog{text='% needs a flex or grid container'}.entries > 0", 10)
    check(client("return #GK.ConsoleLog{text='css4'}.entries") == said + 1, "nothing else in the component is a problem")

    local rects = [[(function()
        local o = {}
        for _, id in ipairs({ 'box', 'a', 'e', 'b', 'abs', 'col', 'c', 'video', 'd', 'tight', 's1', 's2', 'after' }) do
            o[id] = KitCss:Get('#' .. id).widget.LastRect
        end
        return o
    end)()]]

    local function close(a, b, tolerance) return math.abs(a - b) <= (tolerance or 1.5) end
    local function width(rect) return rect[3] - rect[1] end
    local function height(rect) return rect[4] - rect[2] end

    local r2 = settled(rects)
    local box, a, e, b, spot = r2.box, r2.a, r2.e, r2.b, r2.abs
    -- padding: the items are inset, the container - and so its background - is the whole box
    check(close(width(box), 400) and close(height(box), height(a) + 40), "the padded box", r2)
    check(close(a[1], box[1] + 40) and close(a[2], box[2] + 10), "the padding", r2)
    check(close(width(a), 0.25 * 340), "25% of the room inside", r2)
    check(close(e[1], a[3] + 8 - 8), "a negative margin eats the gap", r2)
    check(close(b[3], box[1] + 400 - 20 - 10), "margin-left: auto, margin-right: 10", r2)
    check(close(spot[3], box[3] - 5) and close(spot[4], box[4] - 5) and close(width(spot), 20) and close(height(spot), 20),
        "position: absolute", r2)
    local col, c, video, d = r2.col, r2.c, r2.video, r2.d
    check(close(d[2], col[2] + 12) and c[2] > d[4] and video[2] > c[4], "order: -1, margin-top", r2)
    check(close(c[2], d[4] + 20) and close(video[2], c[4] + 20), "row-gap", r2)
    check(close(width(c), 100) and close((c[1] + c[3]) / 2, col[1] + 150), "margin: 0 auto", r2)
    check(close(width(video), 150) and close(height(video), 75), "50%, aspect-ratio 2 / 1", r2)
    local tight, s1, s2 = r2.tight, r2.s1, r2.s2
    check(close(width(s2), 150) and close(width(s1), 200 - 150 - 8 + 4 - 3), "one shrinks, shrink-0 does not", r2)
    check(close(s2[1], s1[3] + 8 - 4) and close(s2[3], tight[1] + 200 - 3), "the margins", r2)
    check(r2.after[2] >= tight[4] and tight[2] >= col[4] and col[2] >= box[4], "the flow goes on below each box", r2)
    game.shot("weave_css4.jpg")
    client("KitCss:Destroy()")
end

--- What the widgets of the v34 base offer: a header opened from state, expand / collapse /
--- dblclick events, a drag's speed, a number's step, a table's inner width; text sizes of any
--- number, em and rem.
local function widgets(game)
    local client = game.client
    local said = client("return #GK.ConsoleLog{text='cannot raise'}.entries")
    client(("GK.Weave.RegisterComponent('wid', %s) KitWid = GK.Weave.Mount('wid') KitWid:Open()"):format(lua_string(WIDGETS)))
    local function w(id) return ("KitWid:Get('#%s').widget"):format(id) end
    eq(client(("return { %s.Speed, %s.Step, %s.InnerWidth }"):format(w("dr"), w("num"), w("tbl"))),
        { 0.25, 5, 600 }, "the widgets' own options")
    -- :open follows state; the header's own Open is what IMGUI drew last
    game.wait("client", w("head") .. ".Open == false", 10)
    client("KitWid:Set('opened', true)")
    game.wait("client", w("head") .. ".Open == true", 10)
    client("KitWid:Set('opened', false)")
    game.wait("client", w("head") .. ".Open == false", 10)
    client("GK.Weave.Fire('wid', '#head', 'expand') GK.Weave.Fire('wid', '#head', 'collapse') GK.Weave.Fire('wid', '#sel', 'dblclick')")
    eq(client("return { KitWid.state.expanded, KitWid.state.collapsed, KitWid.state.doubles }"), { 1, 1, 1 }, "the events")
    check(client("return #GK.ConsoleLog{text='cannot raise'}.entries") == said, "every event found its widget member")
    -- Text sizes: a number, rem (of the base, Medium 32), em (of the parent) - drawn as the
    -- built-in size of the same number is.
    local function size(id) return ("GK.Weave.Dump('wid', '#%s').style['prop:font-size']"):format(id) end
    eq(client(("return { %s, %s, %s, %s }"):format(size("large"), size("arb"), size("rem"), size("half"))),
        { 40, 40, 40, 20 }, "the text sizes")
    local heights = game.wait("client", [[(function() local o = {}
        for _, id in ipairs({ 'medium', 'large', 'arb', 'rem', 'forty', 'half' }) do
            local r = KitWid:Get('#' .. id).widget.LastRect o[id] = r[4] - r[2] end
        return o.medium > 0 and o end)()]], 10)
    check(heights.large > heights.medium and heights.large == heights.arb and heights.arb == heights.rem
        and heights.rem == heights.forty, "the drawn heights", heights)
    check(math.abs(heights.half - heights.large / 2) <= 1.5, "half of the parent's size", heights)
    client("KitWid:Destroy()")
end

--- Blocks in files of their own (`src=""`), the game's translations (`t()`) and a table kept in
--- a file (`useStorage`), which a window mounted again finds as it was left.
local function files(game)
    local client = game.client
    local stored = game.weave .. "/storage/extra.probe.json"
    game.remove(stored)
    local handle = client("return Ext.Stats.Get('Projectile_FireBolt').DisplayName")
    client("GK.Weave.Define('probe-extra.lua', 'settings = useStorage(\"probe\", { volume = 5 })') " ..
        "GK.Weave.Define('probe-extra.css', '#kept { color: #00ff00; }') " ..
        ("GK.Weave.RegisterComponent('extra', %s)"):format(lua_string(FILES)))
    local mount = ("KitExtra = GK.Weave.Mount('extra', { env = { handle = '%s' } }) KitExtra:Open()"):format(handle)
    client(mount)
    local translated = client(("return Ext.Loca.GetTranslatedString('%s')"):format(handle))
    check(client("return GK.Weave.Dump('extra', '#tr').label") == translated and translated ~= "", "t()", translated)
    check(client("return GK.Weave.Dump('extra', '#raw').label") == "no such handle", "an unknown key stays as written")
    check(client("return GK.Weave.Dump('extra', '#kept').label") == "5", "the stored table's default")
    check(near(client("return GK.Weave.Dump('extra', '#kept').style['color:Text']"),
        client("return GK.Weave.ParseColor('#00ff00')")), "the style from a file")
    -- A changed block file is a changed component: the style follows without a rebuild.
    client("KitExtra:Get('#kept').marker = 2 GK.Weave.Define('probe-extra.css', '#kept { color: #ff0000; }')")
    game.wait("client", "GK.Weave.Dump('extra', '#kept').style['color:Text'][2] < 0.01", 10)
    check(client("return KitExtra:Get('#kept').marker") == 2, "the window was rebuilt")
    -- What is written to the stored table is saved, and found again by a window mounted anew.
    client("KitExtra.state.settings.volume = 9 GK.Weave.flush()")
    check(client("return GK.Weave.Dump('extra', '#kept').label") == "9", "the stored table did not follow")
    local deadline = game.now() + 5
    while true do
        local text = game.read(stored)
        if text and contains(text:gsub(" ", ""), '"volume":9') then break end
        check(game.now() < deadline, stored .. " was not written")
        game.sleep(0.2)
    end
    client(("KitExtra:Destroy() %s"):format(mount))
    check(client("return GK.Weave.Dump('extra', '#kept').label") == "9", "the saved value was not found again")
    client("KitExtra:Destroy()")
    game.remove(stored)
end

function run(game)
    game.wait_state("Running", "server", 60)
    local client = game.client
    local user_ui = game.weave .. "/ui"
    game.mkdir(user_ui)
    for _, file in ipairs(game.glob(user_ui, "probe*")) do game.remove(file) end
    local before = client("return GK.OverlayState()")
    client("GK.OverlaySet{defaults=true, settings=false} GK.ConsoleSet{open=false}")
    local features = client("return GK.Weave.Has()")
    eq(keys(features), { "files" }, "what the build offers")

    -- The small languages.
    local node = client([[return GK.Weave.ParseXml('<a x="1" :y="z" @click="go()" #if="ok"><b class="c d">t &amp; t</b><e/></a>')]])
    check(same(node.attrs, { x = "1", [":y"] = "z", ["@click"] = "go()", ["#if"] = "ok" })
        and node.children[1].text == "t & t", "the XML reader", node)
    local problem = client([[local n, e = GK.Weave.ParseXml('<a>\n<b>\n</a>') return e]])
    check(contains(problem, "line 3") and contains(problem, "<b>"), "the XML error", problem)
    local sheet = client("local s = GK.Weave.ParseCss('@theme x { --a: #fff; } a.b > c:hover, #d { color: var(--a); } " ..
        "@media (min-width: 600) { e { color: red; } } broken') " ..
        "return { s.themes.x['--a'], #s.rules[1].selectors, s.rules[2].conditions[1]['min-width'], #s.rules, #s.problems }")
    eq(sheet, { "#fff", 2, 600, 2, 1 }, "what cannot be read is reported, the rest is kept")
    -- Expressions are Lua with a little sugar.
    local lua = client([[return GK.Weave.transpile('a != null && !b ? items.length : user?.name ?? `no ${x}`')]])
    for _, piece in ipairs({ "~=", "nil", "and not", "__if", "# (", "or __nil", '"no "' }) do
        check(contains(lua, piece), "missing from the transpiled code", piece, lua)
    end
    check(client("return GK.Weave.transpile('count++; total += n * 2')") == "count = count + 1 ; total = total + ( n * 2 )",
        "the assignment sugar")
    -- Colours mean what they mean in a browser and go to IMGUI as written (the build converts
    -- them for an HDR picture itself).
    local grey = client("return GK.Weave.ParseColor('#808080')")
    check(near(grey, { 0.502, 0.502, 0.502, 1 }, 0.005), "the grey", grey)
    local gradient = client([[return { GK.Weave.ParseGradient('linear-gradient(to right, #ff0000, rgba(0, 0, 255, 0.5))') }]])
    check(#gradient == 1 and gradient[1][3] == 90 and near(gradient[1][2], { 0, 0, 1, 0.5 }), "the gradient", gradient)
    -- Text sizes are Script Extender's font units: names, steps of 4 from the base, numbers, em / rem.
    local sizes = client("local F = GK.Weave.FontSizeFor return { F('Large'), F('+1'), F('-2', 'Big'), F('22'), " ..
        "F('1.25rem'), F('1.5em').em, F('+9', 'Big'), F('nonsense') == nil }")
    eq(sizes, { 40, 36, 36, 22, 40, 1.5, 80, true }, "the text sizes")
    -- Reactivity by itself: an effect runs again for exactly what it read.
    eq(client([[local W = GK.Weave
        local s = W.reactive({ a = 1, b = 1, list = {} })
        local runs, seen = 0, nil
        local e = W.effect(function() runs = runs + 1 seen = s.a + #s.list end)
        s.b = 5 W.flush()                       -- not read: no run
        s.a = 2 s.a = 3 W.flush()               -- two writes: one run
        table.insert(s.list, 'x') W.flush()     -- lists are reactive
        W.stop(e) s.a = 9 W.flush()
        return { runs, seen }]]), { 3, 4 }, "the effect")

    -- A window from a component: template, script and style in one text.
    local documents = client(("GK.Weave.RegisterComponent('probe-badge', %s)\n" ..
        "GK.Weave.RegisterComponent('probe', %s)\n" ..
        "KitProbe = GK.Weave.Mount('probe')\nKitProbe:Open()\nreturn GK.Weave.Documents()")
        :format(lua_string(BADGE), lua_string(PROBE)))
    check(has(documents, "probe"), "the window did not mount", documents)
    local function label(id) return client(("return KitProbe:Get('#%s').label"):format(id)) end
    check(label("hello") == "Hello, Tav! 0 clicks, 0 doubled.", "the first line", label("hello"))
    local red = client("return GK.Weave.ParseColor('#ff0000')")
    local hello = client("return GK.Weave.Dump('probe', '#hello')")
    check(near(hello.style["color:Text"], red) and hello.style["prop:font-size"] == 36, "font: +1", hello)
    local plus = client("return GK.Weave.Dump('probe', '#plus')")
    check(near(plus.style["color:Button"], grey) and same(plus.style["var:FrameRounding"], { 9, 9 }), "the button's style", plus)
    check(near(plus.style["color:ButtonHovered"], { 1, 1, 1, 1 }) and same(plus.style["var:FramePadding"], { 14, 6 }), "hover", plus)

    -- Events and state, as the user would cause them.
    client("KitProbe:Get('#hello').marker = 1")
    client("GK.Weave.Fire('probe', '#plus', 'click') GK.Weave.Fire('probe', '#plus', 'click')")
    check(label("hello") == "Hello, Tav! 2 clicks, 4 doubled.", "the clicks did not count", label("hello"))
    eq(client("return { KitProbe.root.label, KitProbe.state.count, KitProbe:Value('double') }"),
        { "Probe: 2", 2, 4 }, "the title, the state and the computed value")
    check(client("return GK.Weave.Dump('probe', '#hello')").class == "big title", ":class")
    check(client("return GK.Weave.Dump('probe', '#hello').style['prop:font-size']") == 40, ".title.big")
    -- #if / #else swap elements; #model goes both ways.
    eq(client("return { KitProbe:Get('#shout') == nil, KitProbe:Get('#calm') ~= nil }"), { true, true }, "#else")
    client("GK.Weave.Fire('probe', '#loud', 'change', true)")
    eq(client("return { KitProbe.state.loud, KitProbe:Get('#shout') ~= nil, KitProbe:Get('#calm') == nil }"),
        { true, true, true }, "#if")
    client("GK.Weave.Fire('probe', '#level', 'change', 'Error') GK.Weave.Fire('probe', '#name', 'change', 'Karlach')")
    eq(client("return { KitProbe.state.level, KitProbe:Value('lastLevel') }"), { "Error", "Info>Error" }, "watch()")
    check(label("hello") == "Hello, Karlach! 2 clicks, 4 doubled.", "#model did not reach the state")
    client("KitProbe:Set('name', 'Gale')") -- the other way: state moves the widgets
    check(client("return KitProbe:Get('#name'):Value()") == "Gale", "the state did not reach the widget")
    -- #for keeps what it can: adding at the end builds one row, the others stay.
    client("KitProbe:Call('add') KitProbe:Call('add')")
    client("KitProbe:Query('.item')[1].marker = 7 KitProbe:Call('add')")
    local rows = client("local o = {} for i, e in ipairs(KitProbe:Query('.item')) do o[i] = e.label end return o")
    eq(rows, { "1: item 1", "2: item 2", "3: item 3" }, "the rows")
    check(client("return KitProbe:Query('.item')[1].marker") == 7, "the first row was built anew")
    client("KitProbe.state.items[2].label = 'changed' GK.Weave.flush()")
    check(client("return KitProbe:Query('.item')[2].label") == "2: changed", "a changed row")
    client("table.remove(KitProbe.state.items, 1) GK.Weave.flush()") -- in the middle: built anew
    eq(client("local o = {} for i, e in ipairs(KitProbe:Query('.item')) do o[i] = e.label end return o"),
        { "1: changed", "2: item 3" }, "the rows after a removal")
    -- A component: properties follow the parent's state, events come back, the slot is the parent's.
    check(client("return KitProbe:Query('.caption')[1].label") == "Badge: 4", "the component's property")
    check(label("inside") == "inside Gale", "the slot")
    client("KitProbe:Query('.more')[1]:Fire('click', true)")
    eq(client("return { KitProbe.state.count, KitProbe:Query('.caption')[1].label }"), { 3, "Badge: 6" }, "the event came back")
    local green = client("return GK.Weave.ParseColor('#00ff00')") -- <style scoped>
    check(near(client("return GK.Weave.Dump('probe', '.caption').style['color:Text']"), green), "the scoped style")
    check((client("return GK.Weave.Dump('probe', '#outside').style") or {})["color:Text"] == nil, "the scoped style escaped")
    check(client("return KitProbe:Get('#hello').marker") == 1, "nothing above may have rebuilt the window")

    -- Utility classes are worked out from their names; variants follow the window's width.
    local util = client("return GK.Weave.Dump('probe', '#util')")
    check(same(util.style["var:FramePadding"], { 16, 8 }) and near(util.style["color:Text"], { 0.937, 0.267, 0.267, 0.5 }),
        "the utility classes", util)
    check(util.visible == nil, "420 wide: below md", util)
    local chip = client("return GK.Weave.Dump('probe', '#applied').style") -- @apply inside a rule
    check(chip["var:FramePadding"][1] == 8 and same(chip["var:FrameRounding"], { 8, 8 }), "@apply", chip)
    client("KitProbe.window:SetSize({ 800, 340 }, 'Always')")
    game.wait("client", "GK.Weave.Dump('probe', '#util').visible == false", 10)
    client("KitProbe.window:SetSize({ 420, 340 }, 'Always')")
    game.wait("client", "GK.Weave.Dump('probe', '#util').visible == nil", 10)

    -- A theme is a set of variables: switching it recolours every window in place.
    local state = client("return GK.OverlaySet{theme='slate'}")
    check(state.theme == "slate" and has(state.themes, "gate") and has(state.themes, "slate") and has(state.themes, "parchment"),
        "the themes", state)
    local blue = client("return GK.Weave.ParseColor('#0000ff')")
    check(near(client("return GK.Weave.Dump('probe', '#hello').style['color:Text']"), blue), "the theme did not recolour")
    game.shot("weave_slate.jpg")
    -- The text size is the overlay's: every window has it, `font: +2` counts from it (Large 40 + 8),
    -- and it is taken back in place - no window is built anew for it.
    client("GK.OverlaySet{font='Large'}")
    check(client("return GK.Weave.Dump('probe', '#hello').style['prop:font-size']") == 48, "font: +2 from Large")
    eq(client("return { GK.Weave.Dump('probe').style['prop:font-size'], KitProbe.window.FontSize }"), { 40, 40 }, "the window's size")
    check(client("return GK.ConsoleState().font") == "Large", "the console follows")
    client("GK.ConsoleSet{font='Small'}") -- the old call still works
    check(client("return GK.OverlayState().font") == "Small", "the old call")
    client("GK.OverlaySet{font=''}")
    eq(client("return { KitProbe.window.FontSize == nil, KitProbe:Get('#hello').marker, KitProbe.pendingRebuild }"),
        { true, 1, false }, "the text size was taken back in place")

    -- Hot reload, no Lua restart: a saved <style> restyles, a saved <template> rebuilds - and
    -- the state stays in both cases.
    client("KitProbe:Get('#plus').marker = 3")
    game.save(user_ui .. "/probe.weave", (PROBE:gsub("#808080", "#00ff00")))
    game.wait("client", ("(function() local c = GK.Weave.Dump('probe', '#plus').style['color:Button'] " ..
        "return math.abs(c[2] - %s) < 0.01 and c[1] < 0.01 end)()"):format(green[2]), 10)
    eq(client("return { KitProbe:Get('#plus').marker, KitProbe.state.count }"), { 3, 3 }, "a restyle must not rebuild")
    game.save(user_ui .. "/probe.weave",
        (PROBE:gsub('<text id="outside">', '<button id="added">Added</button><text id="outside">', 1)))
    game.wait("client", "KitProbe:Get('#added') ~= nil", 10)
    eq(client("return { KitProbe:IsOpen(), KitProbe.state.name, KitProbe.state.count, KitProbe:Get('#hello').label }"),
        { true, "Gale", 3, "Hello, Gale! 3 clicks, 6 doubled." }, "a rebuild keeps the state and stays open")
    -- A file that cannot be read leaves the window as it was and says why in the console log.
    game.save(user_ui .. "/probe.weave", "<template><window><text>oops</window></template>")
    game.wait("client", "#GK.ConsoleLog{text='probe.weave'}.entries > 0", 10)
    check(client("return KitProbe:Get('#added') ~= nil") == true, "a broken file took the window down")
    game.remove(user_ui .. "/probe.weave")
    game.wait("client", "KitProbe:Get('#added') == nil", 10) -- back to the registered component

    -- Windows stay inside the picture: pushed out, one comes back by itself.
    client("KitProbe.window:SetPos({9000, 7000}, 'Always')")
    game.sleep(0.3)
    local inside = game.wait("client", "(function() local p, s, v = KitProbe.window.LastPosition, KitProbe.window.LastSize, " ..
        "Ext.IMGUI.GetViewportSize() return p[1] + s[1] <= v[1] + 1 and p[2] + s[2] <= v[2] + 1 " ..
        "and p[1] >= 0 and p[2] >= 0 and { p, s, v } end)()", 10)
    check(inside, "the window did not come back", inside)
    client("GK.ConsoleSet{open=true} GK.OverlayRescue()")
    game.sleep(1.5)
    eq(client("return GK.ConsoleState().pos"), { 40, 40 }, "the rescue")
    client("GK.ConsoleSet{open=false}")

    -- The button under the minimap: there while the game shows its HUD, gone when switched off.
    local hud = game.wait("client", "(function() local h = GK.OverlayState().hud return h.visible and h end)()", 15)
    local viewport = client("return Ext.IMGUI.GetViewportSize()")
    check(viewport[1] * 0.6 < hud.x and hud.x < viewport[1] and hud.y > 0 and hud.y < viewport[2] * 0.6,
        "the HUD button is in the wrong corner", hud, viewport)
    check(client("return GK.Weave.Dump('hud', '#toggle').label") == "GK", "the HUD button's label")
    client("GK.Weave.Fire('hud', '#toggle', 'click')") -- a click opens the console
    check(client("return GK.ConsoleState().open") == true, "the HUD button did not open the console")
    client("GK.Weave.Fire('hud', '#toggle', 'click')")
    check(client("return GK.ConsoleState().open") == false, "the HUD button did not close the console")
    client("GK.OverlaySet{hudX=-40, hudY=25}")
    local moved = game.wait("client", ("(function() local h = GK.OverlayState().hud return h.x == %s and h end)()")
        :format(hud.x - 40), 10)
    check(moved.y == hud.y + 25, "the HUD button did not move", hud, moved)
    game.shot("weave_hud.jpg")
    client("GK.OverlaySet{hudButton=false}")
    game.wait("client", "not GK.OverlayState().hud.visible", 10)

    -- The settings window is a component itself; what it changes is what GK.OverlaySet changes.
    client("GK.OverlaySettings(true)")
    check(client("return GK.Weave.Dump('overlay', '#theme').value") == "slate", "the settings window")
    check(client("return GK.Weave.Document('overlay'):Get('#hud-x') == nil"), '#if="hudButton"')
    client("GK.Weave.Fire('overlay', '#theme', 'change', 'parchment') GK.Weave.Fire('overlay', '#block-mouse', 'change', false)")
    state = client("return GK.OverlayState()")
    check(state.theme == "parchment" and state.blockMouse == false and state.settingsOpen, "the settings window", state)
    game.shot("weave_settings.jpg")

    -- Settings survive a restart of Lua (and of the game: they are in overlay.json).
    game.sleep(2.5)
    client("KitProbe:Destroy() GK.ConsoleReload()")
    game.sleep(4)
    game.wait("client", "GK.OverlayState ~= nil and GK.OverlayState().theme == 'parchment'", 20)
    state = client("return GK.OverlayState()")
    check(state.blockMouse == false and state.hudButton == false and state.hudX == -40, "the settings did not survive", state)
    game.wait("server", "GK.Complete ~= nil", 15)

    -- The demo window of the documentation.
    client("GK.OverlaySet{theme='gate'} GK.Weave.Demo(true) GK.Weave.Fire('demo', '#volume', 'change', 90)")
    check(client("return GK.Weave.Dump('demo', '#greeting').label") == "Hello, Tav! Volume 90.", "the demo window")
    client("GK.Weave.Fire('demo', '#add', 'click')")
    local title = client("return GK.Weave.Document('demo').root.label")
    check(endswith(title, "(1 items)"), "the demo's title", title)
    check(client("return #GK.Weave.Document('demo'):Query('#cards child')") == 3, "components from #for")
    game.shot("weave_demo.jpg")
    client("GK.Weave.Demo(false)")

    graphics(game, features)
    layout(game)
    css4(game)
    widgets(game)
    files(game)

    -- Leave the overlay as it was found.
    local parts = {}
    for _, key in ipairs({ "theme", "font", "key", "blockKeys", "blockMouse", "hudButton", "hudX", "hudY" }) do
        local value = before[key]
        parts[#parts + 1] = key .. "=" .. (type(value) == "string" and ("%q"):format(value) or tostring(value))
    end
    client("GK.OverlaySet{" .. table.concat(parts, ", ") .. ", settings=false}")
    check(client("return GK.OverlayState().theme") == before.theme, "the overlay was not put back")
end
