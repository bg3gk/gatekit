-- Several senders talking to one side of the game at once lose nothing.
--
-- Eight senders x 25 commands each, on both sides at once, every answer must come back to the
-- sender that asked and carry its own number. Before the command queue (2026-11-13) commands and
-- answers overwrote each other and callers waited out their timeouts.
--
-- game.burst runs the senders on the host, each with a bridge client of its own; the queue file,
-- its lock and the answer files are the ones several processes share (the Python scenario
-- started eight processes for the same check).

scenario { level = "GustavX:TUT_Avernus_C" }

function run(game)
    local times = game.burst(8, 25)
    check(#times == 200, "answers missing", #times)
    print(("  %d answers; median %.2f s, slowest %.2f s")
        :format(#times, times[math.floor(#times / 2)], times[#times]))
    local leftovers = game.glob(game.channel, "res_*_*.json")
    check(#leftovers == 0, "answers left behind", leftovers)
end
