-- AI.Stand (both sides): a stand's scenes as data. The mod knows no scene by name: the session
-- points at the stand's manifest (ai_session `scenes_file`), a JSON object
--
--   { "scenes": { "<name>": { "side": "server" | "client" | "ui", "arm": "<file>", "off": "<file>",
--                             "options": { ... }, "checkpoint": "<flag>", "reset": "<flag>",
--                             "reset_hold": <seconds> } } }
--
-- with file paths relative to the manifest. `name` is the scene's table under GKScene. The host
-- reads a file the agent names (ai_scene `file`); the mod keeps the code in the copy at arming, so
-- a scene is armed again by name, and armed again by itself after a load, which restarts Lua.
--
--   AI.Stand.Take(text, from)   the manifest, as the host read it, kept with the session
--   AI.Stand.Current()          { from, scenes } or nil
--   AI.Stand.Entry(name)        one scene of the manifest, or nil
--   AI.Stand.Path(name, which)  the full path of its "arm" or "off" file
--   AI.Stand.Code(name, which)  the code kept for it, or nil; AI.Stand.Keep(name, which, code)
--   AI.Stand.Armed()            name -> { level }: what the session armed and has not disarmed
--   AI.Stand.Mark(name, level)  armed on `level` (false: disarmed)
--   AI.Stand.Chunk(code, options)   the code with the manifest's options set before it
--   AI.Stand.Run(code)          the code run in GateKit's environment: { ok, error }
--   AI.Stand.Hint(name, which)  the call that arms (or disarms) it with its file

AI.Stand = AI.Stand or {}
local St = AI.Stand
St.Sides = { server = true, client = true, ui = true }

local function kept(name, which) return "Gwynerva_GateKitAI/scene_" .. name .. "_" .. which .. ".lua" end

function St.Take(text, from)
    local ok, value = pcall(Ext.Json.Parse, tostring(text or ""))
    if not ok or type(value) ~= "table" or type(value.scenes) ~= "table" then
        return nil, "the manifest is not a JSON object with an object `scenes`"
    end
    for name, e in pairs(value.scenes) do
        if type(name) ~= "string" or not name:match("^[%w_]+$") or type(e) ~= "table" then
            return nil, "each scene of the manifest is an object under a name of letters, digits and underscores"
        end
        if not St.Sides[e.side or ""] then return nil, "the scene " .. name .. " has no side (server, client or ui)" end
    end
    local stand = { from = from, scenes = value.scenes }
    AI.SessionFile.Write({ stand = stand })
    return stand
end

function St.Current()
    local stand = AI.SessionFile.Read().stand
    return type(stand) == "table" and type(stand.scenes) == "table" and stand or nil
end

function St.Entry(name)
    local stand = St.Current()
    return stand and type(name) == "string" and stand.scenes[name] or nil
end

function St.Path(name, which)
    local stand, e = St.Current(), St.Entry(name)
    local file = e and e[which]
    if type(file) ~= "string" then return nil end
    if file:match("^%a:[/\\]") or file:match("^/") then return file end
    local dir = type(stand.from) == "string" and stand.from:match("^(.*)[/\\][^/\\]*$")
    return dir and (dir .. "/" .. file) or file
end

function St.Code(name, which)
    local ok, text = pcall(Ext.IO.LoadFile, kept(name, which))
    return ok and type(text) == "string" and text ~= "" and text or nil
end

function St.Keep(name, which, code) pcall(Ext.IO.SaveFile, kept(name, which), code) end

function St.Armed()
    local armed = AI.SessionFile.Read().armed
    return type(armed) == "table" and armed or {}
end

function St.Mark(name, level)
    local armed = St.Armed()
    armed[name] = level ~= false and { level = level or "" } or nil
    AI.SessionFile.Write({ armed = next(armed) and armed or false })
end

function St.Chunk(code, options)
    if type(options) ~= "table" or next(options) == nil then return code end
    local json = Ext.Json.Stringify(options)
    local level = 1
    while json:find("]" .. string.rep("=", level) .. "]", 1, true) do level = level + 1 end
    local eq = string.rep("=", level)
    return "GKSceneOptions = GKSceneOptions or {} for k, v in pairs(Ext.Json.Parse([" .. eq .. "[" .. json .. "]" .. eq .. "])) do GKSceneOptions[k] = v end\n" .. code
end

--- Runs a scene's chunk in GateKit's environment, where `gatekit exec` runs it (GK, GKScene,
--- GKSceneOptions): a chunk loaded from this mod would see this mod's own. { ok, error }; a chunk
--- that answers { ok = false, error } has failed too.
function St.Run(code)
    -- Script Extender's load takes the environment as its second argument
    local chunk, problem = load(code, Mods.GateKit)
    if not chunk then return { ok = false, error = "syntax: " .. tostring(problem) } end
    local ok, value = xpcall(chunk, function(e) return debug and debug.traceback and debug.traceback(tostring(e), 2) or tostring(e) end)
    if not ok then return { ok = false, error = tostring(value) } end
    if type(value) == "table" and value.ok == false then return { ok = false, error = tostring(value.error or "the scene said no") } end
    return { ok = true }
end

function St.Hint(name, which)
    local path = St.Path(name, which)
    if not path then return "the stand's manifest names no " .. which .. " file for " .. tostring(name) end
    return string.format('ai_scene {"name":"%s","call":"%s","file":"%s"}', name, which, path:gsub("\\", "/"))
end
