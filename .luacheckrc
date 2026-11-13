-- What luacheck should know about Lua that runs inside Baldur's Gate 3.
--
-- Script Extender gives each mod its own global environment and puts things in it that no
-- standalone Lua has: `Ext` (the extender's own API), `Osi` (the game's Osiris calls), `Mods`
-- (every loaded mod's table). Without this, every one of them is reported as an undefined
-- variable, and the warnings that matter are buried under a thousand that are not.
--
-- The mods' own API tables are globals too, because that is how another mod reaches them: a
-- global declared here lands in `Mods.GateKit`, which is what `Mods.GateKit.GK` reads.

std = "lua54"

globals = {
    -- Script Extender
    "Ext", "Osi", "Mods",
    -- the savegame table the extender restores into a mod (deprecated upstream, still read)
    "PersistentVars",
    -- GateKit's own API, and Weave's
    "GK", "Weave",
    -- the mod's own UUID, which the extender puts in every mod's environment
    "ModuleUUID",
    -- the probe's state, kept in globals on purpose: it has to outlive a Lua reload
    "Probe", "ProbeCaptureState", "ProbeSeenState", "_PW",
    -- a scenario declares what it needs and a `run` function; the runner supplies both, along
    -- with the small vocabulary a scenario is written in
    "scenario", "run", "check", "join", "keys", "fmt", "contains",
    "bytes", "count", "endswith", "eq", "has", "lua_list", "lua_string",
    "near", "same", "settled", "sorted", "startswith",
}

-- Line length is not policed. 374 lines here are longer than 120 characters, and rewrapping all
-- of them to satisfy a linter would be a large untested change to working code for no reader's
-- benefit. Length is a matter for review, where a person can tell a long table from a long thought.
max_line_length = false

files["lua-types"] = {
    -- Type stubs: a declaration whose body is empty and whose arguments exist to be read, by a
    -- person and by an editor. "This argument is never used" is the point of the file.
    unused_args = false,
    unused = false,
}

-- Generated from the game's own UI, not written by hand.
exclude_files = {
    "mods/GateKit/Mods/Gwynerva_GateKit/ScriptExtender/Lua/Client/UICatalog.lua",
}
