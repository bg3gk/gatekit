-- Two enabled mods ship loading-screen hints in their own folders, and both replace one hint of
-- the game's: each mod's new key is added to the game's list after the game's own, in the mods'
-- load order, and the replaced key keeps its place in the list with the later mod's entry; the
-- composition warns of each replacement, naming both sides (the fork's Docs/API.md, "Loading
-- Hints"). The two mods are the bridge's own pair, both enabled in every copy: Weave, loaded first,
-- adds GK_HINT_WEAVE and replaces HINT_CAM_ZOOM; the bridge adds GK_HINT_GATEKIT and replaces
-- HINT_CAM_ZOOM again (internal, with a wiki page long enough to lie outside the entry). The game's
-- own order is read from its own file, Public/Game/Hints/Hints.lsx.
-- The code: each mod's file is loaded by code:ls::FileFormatIO::LoadFile into a manager of its own
-- (code:ecl::HintManager::Visit); GK.Hints.List reads the game's list at code:ecl::HintManager::m_ptr
-- in the layout of code:ecl::HintManager (code:ecl::HintManager.Hints, code:ecl::HintManager.HintsCount)
-- and code:ecl::Hint (code:ecl::Hint.HintKey, code:ecl::Hint.Controller, code:ecl::Hint.Keyboard,
-- code:ecl::Hint.WikiPage, code:ecl::Hint.Internal).
-- Venue: a start of its own with each mod's Public/<Folder>/Hints/Hints.lsx put loose under the
-- copy's Data/Public before it (`files`: the copy's linked Data/Public is made a real folder for
-- them, so nothing lands in the installed game), ending when the bridge first answers (`until_lua`):
-- the list is composed once, at the client's first StartLoading, before any Lua. With the player's
-- switch off ("ModHints": false in ScriptExtenderSettings.json) the list is the game's alone and the
-- test fails. Class: observation (GK.Hints.List, Ext.Hints.GetComposition).
-- Run: gatekit --instance <copy> test tests -k startup/hints/two_mods

test { until_lua = true,
       files = {
           ["Data/Public/Gwynerva_Weave/Hints/Hints.lsx"] = "weave.lsx",
           ["Data/Public/Gwynerva_GateKit/Hints/Hints.lsx"] = "gatekit.lsx",
       },
       about = "Two mods' hints are added after the game's in load order; a key both replace keeps its place with the later mod's entry, and each replacement is warned of naming both." }

local BRIDGE = "b1d5c0de-7e57-4b1d-9e0a-6d6576646b69"   -- the GateKit bridge, loaded after Weave
local WEAVE = "8ee3d1c7-0362-41bc-b7c9-b10d85c62f4b"    -- Weave
local REPLACED = "HINT_CAM_ZOOM"

function run(game)
    step("the hint list the start composed", "observation", function()
        local r = game.client([[
            local list, why = GK.Hints.List()
            local c = Ext.Hints.GetComposition()
            local own = {}
            local text = Ext.IO.LoadFile("Public/Game/Hints/Hints.lsx", "data") or ""
            for key in text:gmatch('id="HintKey"[^>]-value="([^"]*)"') do own[#own + 1] = key end
            local files = {}
            for _, f in ipairs(c.Files or {}) do
                files[f.Folder] = { source = f.Source, found = f.Found, loaded = f.Loaded, count = f.Count, added = f.Added, replaced = f.Replaced }
            end
            return { list = list, why = why, enabled = c.Enabled, seen = c.Seen, written = c.Written, gameCount = c.GameCount,
                     count = c.Count, warnings = c.Warnings or {}, files = files, own = own }
        ]], 60)
        local lines = {}
        for _, h in ipairs(r.list or {}) do
            lines[#lines + 1] = string.format("%d %s %s %s", h.index, tostring(h.key), tostring(h.source), tostring(h.wiki))
        end
        game.save("hints.txt", fmt(r.warnings) .. "\n" .. join(lines, "\n") .. "\n")
        check(r.list, "GK.Hints.List read the list", r.why)
        check(#r.own > 0, "the game's own Hints.lsx was read")

        local at = {}
        for _, h in ipairs(r.list) do at[h.key] = h end
        local weave, bridge, zoom = at.GK_HINT_WEAVE, at.GK_HINT_GATEKIT, at[REPLACED]
        check(weave and bridge, "both mods' new keys are in the game's list", r.enabled, r.files)
        eq(weave.source, WEAVE, "GK_HINT_WEAVE is Weave's")
        eq(bridge.source, BRIDGE, "GK_HINT_GATEKIT is the bridge's")
        eq(#r.list, #r.own + 2, "the list is the game's own and the two new keys")
        eq({ weave.index, bridge.index }, { #r.own + 1, #r.own + 2 }, "the new keys follow the game's, in the mods' load order")

        local place
        for i, key in ipairs(r.own) do if key == REPLACED then place = i end end
        check(place and zoom, "the game's own file and the list have " .. REPLACED)
        eq(zoom.index, place, REPLACED .. " keeps the place it has in the game's file")
        eq(zoom.source, BRIDGE, REPLACED .. " is the later mod's entry")
        eq({ zoom.internal, zoom.keyboard, zoom.controller, zoom.wiki }, { true, true, false, "GateKit Bridge test hints" },
            REPLACED .. " holds the bridge's fields")
        eq(zoom.replaced and zoom.replaced.source, WEAVE, REPLACED .. " replaced Weave's entry")

        local named = { weave = false, both = false }
        for _, w in ipairs(r.warnings) do
            if contains(w, REPLACED) and contains(w, WEAVE) and not contains(w, BRIDGE) then named.weave = true end
            if contains(w, REPLACED) and contains(w, WEAVE) and contains(w, BRIDGE) then named.both = true end
        end
        check(named.weave, "a warning names Weave replacing the game's " .. REPLACED, r.warnings)
        check(named.both, "a warning names the bridge replacing Weave's " .. REPLACED, r.warnings)
    end)
end
