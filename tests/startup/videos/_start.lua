-- The start of the game as Script Extender's fork recorded it, and the checks every test under
-- tests/startup/videos makes of it. Each such test is one start of its own (`until_lua`): it ends
-- when the bridge first answers, in the client's LoadModule state, right after the videos.
-- `Ext.StartupVideos.GetPlaylist()` is what the start was given (the fork's Docs/API.md, "Startup
-- Videos"); `Ext.StartupVideos.Timeline()` is every stage of the start from the process's first
-- moment (research/studies/startup-videos.md, "The start, step by step").

local S = {}

S.GAME = "Splash_Logo_Larian"                        -- the game's one startup entry and its video
S.BRIDGE = "b1d5c0de-7e57-4b1d-9e0a-6d6576646b69"   -- the GateKit bridge, ModTable GateKit
S.WEAVE = "8ee3d1c7-0362-41bc-b7c9-b10d85c62f4b"    -- Weave, ModTable GateKitWeave, loaded before the bridge

--- An entry of the list as GetPlaylist and Compose give it: "<owner>:<Id>=<Video>".
function S.entry(owner, id, video) return owner .. ":" .. id .. "=" .. video end

--- The start: the playlist (the switch, the list as "Id=Video", the warnings, the enabled mods by
--- folder) and the client's rows of the timeline read in order - StartLoading entered and left, the
--- list written, the videos started, the first stop after them, and any state change between.
function S.read(game)
    return game.client([[
        local p = Ext.StartupVideos.GetPlaylist()
        local r = { enabled = p.Enabled, seen = p.Seen, written = p.Written, game = p.Game, list = {},
                    warnings = p.Warnings or {}, mods = {}, started = {}, between = {} }
        for _, e in ipairs(p.Entries) do r.list[#r.list + 1] = e.Id .. "=" .. e.Video end
        for _, m in ipairs(p.Mods) do r.mods[m.Folder] = { uuid = m.Uuid, declared = m.DeclarationFound } end
        for _, e in ipairs(Ext.StartupVideos.Timeline()) do
            if e.Side == "Client" then
                if e.What == "State" and e.To == "StartLoading" and not r.entered then
                    r.entered = e.Ms
                elseif e.What == "State" and r.entered and not r.left then
                    if e.From == "StartLoading" then r.left = e.Ms
                    else r.between[#r.between + 1] = e.From .. " -> " .. e.To end
                elseif e.What == "PlaylistWritten" and not r.playlist then
                    r.playlist = e.Name
                elseif e.What == "VideoStarted" and not r.left then
                    r.started[#r.started + 1] = string.match(e.Name or "", "^(%S+)")
                    r.lastStart = e.Ms
                elseif e.What == "VideoStopped" and #r.started > 0 and not r.stopped then
                    r.stopped = e.Ms
                end
            end
        end
        return r
    ]])
end

--- What every start checks: the list written into the game's playlist (one entry of all its names,
--- named after the first, or none), the videos the player started in that order, and the client
--- leaving StartLoading only when the last of them stopped, with no state change while they played.
function S.played(r, videos)
    check(r.seen and r.written, "the fork met the startup playlist and wrote it", r)
    eq(r.between, {}, "no state change of the client while it was in StartLoading")
    check(r.entered and r.left, "the client entered StartLoading and left it", r)
    eq(r.started, videos, "the videos the player started, in turn")
    if #videos == 0 then
        eq(r.playlist, "no entry", "nothing written into the game's playlist")
        -- nothing holds the state: its exit waits for no player (about 35 ms in R5's starts)
        check(r.left - r.entered < 1000, "the client left StartLoading at once", r)
        return
    end
    eq(r.playlist, string.format("%s (%d names)", videos[1], #videos), "the list written as one entry of all its names")
    check(r.stopped, "the player stopped", r)
    check(r.left >= r.lastStart, "the client left StartLoading after the last video started", r)
    -- the state's exit and the player's stop are seen in one tick, the state first
    check(math.abs(r.left - r.stopped) < 100, "the client left StartLoading as the last video stopped (within a frame)", r)
end

return S
