-- With no mod declaring anything, the game's start plays its own startup video and nothing else:
-- the fork composes the game's one entry (`Splash_Logo_Larian`) unchanged, writes it as the
-- playlist's one entry, and the client leaves StartLoading when the clip stops (_start.lua).
-- The code: the entry is code:EoCFS::strSplashLogoLarian, given by code:ecl::EocClient::Load to
-- code:ecl::GameStateStartLoading::MakeSimple and read at code:ecl::GameStateStartLoading.Playlists;
-- code:ecl::GameStateStartLoading::Enter makes the movie's load state, whose
-- code:ecl::LoadStateMovie::Start plays it through code:ecl::GameControl::ShowMovie and
-- code:ecl::VideoManager::NextVideo (the player's code:ecl::VideoManager.CurrentVideo); the state
-- ends by code:ecl::GameStateStartLoading::CanExit once code:ecl::LoadStateMovie::IsReady.
-- Venue: a start of its own with the startup videos on (`startup_videos`), ending when the bridge
-- first answers (`until_lua`), right after the video; the test only reads the fork's record.
-- Class: observation.
-- Run: gatekit --instance <copy> test tests -k startup/videos/game_clip

test { until_lua = true, startup_videos = true,
       about = "With no declaration the start plays the game's own clip alone, and the game goes on when it stops." }

local S = require("_start")

function run(game)
    step("the game's clip, as the start played it", "observation", function()
        local r = S.read(game)
        game.save("start.txt", fmt(r) .. "\n")
        check(r.enabled, "the player's switch is on (startup_videos)", r.enabled)
        eq(r.game, { S.GAME }, "the game's own entries")
        check(r.mods.Gwynerva_GateKit and not r.mods.Gwynerva_GateKit.declared, "the bridge declares nothing", r.mods)
        eq(r.list, { S.GAME .. "=" .. S.GAME }, "the composition: the game's entry as it is")
        eq(r.warnings, {}, "no warnings")
        S.played(r, { S.GAME })
    end)
end
