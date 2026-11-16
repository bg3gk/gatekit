-- GK.Show.Picture puts a picture of an ordinary file over the game (docs/contracts/lua-api.md,
-- "GK.Show"): a png of a mod's package read through the game's file system, or a file anywhere on
-- the disk named by its whole path, drawn by Script Extender's drawing layer over the whole frame.
-- The rule held here: the verb answers once the first frame with the picture is drawn, with where
-- it lies; a picture fitted with `contain` keeps its shape inside the rectangle asked for and one
-- fitted with `cover` fills it; it leaves by itself after its seconds, by a key named in `skip`
-- (which the picture takes and the game does not get: no window opens under it), or by
-- GK.Show.Hide, and each leaving is a row `picture_hidden` that says why; a picture is in a shot
-- taken with the extender's layer and not in the game's own frame; a missing file, a path with
-- `..`, a layer under the interface, a key that does not exist and a video are refused with the
-- reason.
-- The picture in a shot is shown by its pixels: a test picture whose red grows to the right is
-- stretched over the screen from a file of the test's output folder, and two points of the shot
-- with the layer (`game.pixel`) carry the picture's colours there, which the game's own frame at
-- the same moment does not.
-- What a person sees: a small woven tile on the left for two seconds, a larger one in the middle
-- until Escape, then a red and green gradient over the whole screen for a moment.
-- Staging (cheat): the pictures themselves. The key is fair. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k show/picture --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A picture of a file over the game: drawn where asked, gone by its time, by a key it takes from the game, or by Hide; each end a row with its reason." }

-- A picture the repository ships: the Weave mod's own tile, 64 by 64.
local FILE = "Mods/Gwynerva_Weave/ScriptExtender/Lua/ui/weave.png"

local function near(a, b, by) return math.abs(a - b) <= (by or 0.01) end

local function widgets(game)
    local open = game.ui("local names = {} for _, w in ipairs(GK.Gui.Open() or {}) do names[#names + 1] = type(w) == 'table' and (w.widget or w.name) or tostring(w) end table.sort(names) return table.concat(names, ' ')")
    return open
end

function run(game)
    game.wait_state("Running", "server", 120)
    local mark = game.mark()
    local first = step("a picture for two seconds, fitted into a rectangle", "cheat", function()
        local shown = game.ui(string.format(
            "return GK.Show.Picture{ file = %q, where = { 0.1, 0.2, 0.3, 0.2 }, fit = 'contain', fade = 0.2, seconds = 2 }", FILE))
        check(shown.ok, "the picture is drawn", shown)
        return shown
    end)
    step("it lies inside the rectangle and keeps its shape", "observation", function()
        eq(first.size[1], 64, "the file's own width")
        eq(first.size[2], 64, "the file's own height")
        check(near(first.pixels[3], first.pixels[4], 1), "a square picture is drawn square", first.pixels)
        local c = first.covers
        check(c[1] >= 0.1 - 0.001 and c[1] + c[3] <= 0.4 + 0.001 and near(c[2], 0.2) and near(c[4], 0.2),
            "contain: as high as the rectangle, inside its width", c)
        local r = row(mark, "picture_shown", function(p) return p.handle == first.handle end, 5)
        eq(r.payload.file, FILE, "the row names the file")
        local list = game.ui("return GK.Show.Shown()")
        check(#list == 1 and list[1].handle == first.handle, "GK.Show.Shown lists it", list)
    end)
    step("it leaves by itself when its seconds are up", "observation", function()
        local gone = row(mark, "picture_hidden", function(p) return p.handle == first.handle end, 15)
        eq(gone.payload.why, "seconds", "why it left")
        check(gone.payload.shownMs >= 2000 and gone.payload.shownMs < 6000, "shown for its two seconds and its fades", gone.payload.shownMs)
        local after = game.ui(string.format("return GK.Show.Shown(%q)", first.handle))
        check(after and after.left and after.why == "seconds", "the handle still answers what became of it", after)
    end)

    local before = step("the windows open before the key", "observation", function() return widgets(game) end)
    local second = step("a picture that Escape takes off, filling its rectangle", "cheat", function()
        local shown = game.ui(string.format(
            "return GK.Show.Picture{ file = %q, where = { 0.35, 0.3, 0.3, 0.4 }, fit = 'cover', back = '#000000aa', skip = { 'escape' } }", FILE))
        check(shown.ok, "the picture is drawn", shown)
        local c = shown.covers
        check(near(c[1], 0.35) and near(c[2], 0.3) and near(c[3], 0.3) and near(c[4], 0.4), "cover: the whole rectangle", c)
        return shown
    end)
    step("Escape", "fair", function() game.key("escape") end)
    step("the picture took the key: it left as skipped and no window opened under it", "observation", function()
        local gone = row(mark, "picture_hidden", function(p) return p.handle == second.handle end, 10)
        eq(gone.payload.why, "skipped", "why it left")
        eq(gone.payload.key, "escape", "the key")
        eq(widgets(game), before, "the game's windows are as they were: Escape did not reach the game")
    end)

    step("an icon of the game's as a resource, then everything hidden", "cheat", function()
        local icon = game.ui("return GK.Show.Picture{ resource = 'Action_Dash', where = { 0.8, 0.1, 0.1, 0.1 } }")
        check(icon.ok and icon.resource == "Action_Dash", "an icon of the game's atlas is drawn", icon)
        local hidden = game.ui("return GK.Show.Hide()", 30)
        check(hidden.ok and hidden.hidden[1] == icon.handle, "Hide answers once it is gone", hidden)
        eq(#game.ui("return GK.Show.Shown()"), 0, "nothing is shown")
        local gone = row(mark, "picture_hidden", function(p) return p.handle == icon.handle end, 5)
        eq(gone.payload.why, "hidden", "why it left")
    end)

    local disk = step("a picture of a file on the disk, over the whole screen", "cheat", function()
        local path = game.testimage("gradient.png", 64, 64):gsub("\\", "/")
        local shown = game.ui(string.format("return GK.Show.Picture{ file = %q, fit = 'stretch' }", path))
        check(shown.ok, "a file named by its whole path is drawn", shown)
        eq(shown.file, path, "the answer names the file")
        return shown
    end)
    step("it is in a shot taken with the extender's layer, and not in the game's own frame", "observation", function()
        local with, without = game.shot("with-layer.png", 0, true), game.shot("game-frame.png", 0)
        -- the test picture at a quarter and at three quarters of the width, half way down: red 64 and 191, green 128, blue 64
        local function carries(file)
            local left, right = game.pixel(file, 0.25, 0.5), game.pixel(file, 0.75, 0.5)
            local function is(p, r) return math.abs(p.r - r) <= 16 and math.abs(p.g - 128) <= 16 and math.abs(p.b - 64) <= 16 end
            return is(left, 64) and is(right, 191), { left, right }
        end
        local inLayer, seen = carries(with)
        check(inLayer, "the shot with the layer carries the picture's colours at two points", seen)
        local inFrame, plain = carries(without)
        check(not inFrame, "the game's own frame does not", plain)
    end)
    step("the picture of the disk hidden", "cheat", function()
        local hidden = game.ui(string.format("return GK.Show.Hide(%q)", disk.handle), 30)
        check(hidden.ok, "Hide answers once it is gone", hidden)
    end)

    step("what cannot be shown is refused with its reason", "cheat", function()
        local no = game.ui(string.format([[return {
            outside = GK.Show.Picture{ file = "C:/no-such-folder-of-pictures/logo.png" },
            dots = GK.Show.Picture{ file = "Mods/../../logo.png" },
            missing = GK.Show.Picture{ file = "Mods/Gwynerva_Weave/no-such-picture.png" },
            dds = GK.Show.Picture{ file = "Public/Game/GUI/Assets/logo.dds" },
            under = GK.Show.Picture{ file = %q, over = "under the UI" },
            key = GK.Show.Picture{ file = %q, skip = { "no-such-key" } },
            video = GK.Show.Video{ file = "Video/Splash_Logo_Larian.bk2" } }]], FILE, FILE))
        check(no.outside.ok == false and contains(no.outside.error, "disk has no such picture"), "a file the disk does not have", no.outside)
        check(no.dots.ok == false and contains(no.dots.error, ".."), "a path with ..", no.dots)
        check(no.missing.ok == false and contains(no.missing.error, "no such picture"), "a missing file", no.missing)
        check(no.dds.ok == false and contains(no.dds.error, "png, jpg, tga or bmp"), "a format that is not decoded", no.dds)
        check(no.under.ok == false and contains(no.under.error, "everything"), "a layer under the interface", no.under)
        check(no.key.ok == false and contains(no.key.error, "does not know the key"), "a key that does not exist", no.key)
        check(no.video.ok == false and contains(no.video.error, "video"), "a video", no.video)
        eq(#game.ui("return GK.Show.Shown()"), 0, "a refusal leaves nothing on the screen")
    end)
end
