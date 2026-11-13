-- The frame cap, which is the most fragile thing GateKit knows about the engine and the thing
-- most worth catching early.
--
-- `GK.SetFrameCap` writes fields of the engine's own `BaseApp` at offsets this build knows:
-- the cap asked for, the cap in force, the cap used while the window is not in front, and the
-- microseconds a frame must last. A patch to the game can move any of them. When that happens the
-- extender refuses to write (it checks the engine's arithmetic first), everything keeps working,
-- and the only thing anyone notices is that several copies are slow again - which is exactly the
-- kind of failure nobody traces back. So it is asserted here, and `gatekit test` finds it.
--
-- It needs GateKit's Script Extender build. On the stock one GK.FrameCap() is nil and the scenario
-- says so rather than failing, the same way the other fork scenarios do.
--
--     gatekit --instance <yours> test -k framecap

scenario { level = "GustavX:TUT_Avernus_C", mods = {} }

local SAMPLE = 6          -- seconds to count frames over
local SETTLE = 1.5        -- seconds to let a new cap take effect before counting

-- Frames a second, counted by the renderer's own present counter and the game's own clock, so
-- neither the host's scheduling nor the bridge's round trip is in the number.
local function rate(game)
    local function sample()
        return game.client([[local i = Ext.ClientRender.MirrorInfo()
            return { presents = i and i.Presents or -1, time = Ext.Utils.MonotonicTime() }]])
    end
    local first = sample()
    game.sleep(SAMPLE)
    local last = sample()
    check(first.presents >= 0 and last.presents >= first.presents,
        "the renderer will not say how many frames it has shown", first.presents)
    return (last.presents - first.presents) / ((last.time - first.time) / 1000)
end

function run(game)
    local cap = game.client("return GK.FrameCap()")
    if not cap then
        print("the frame cap is not reachable in this build; nothing to check")
        return
    end

    print(("the engine: cap %d, in force %d, in the background %d, %d us a frame, focus %s, throttle %s")
        :format(cap.frameCap, cap.effectiveFrameCap, cap.lostFocusFrameCap, cap.microsecondsPerFrame,
            tostring(cap.hasFocus), tostring(cap.yieldOnLostFocus)))

    -- 1. The engine's own arithmetic, which is also what the extender checks before it writes
    --    anything: the clock's microseconds are what the cap in force gives.
    local function clockAgrees(state)
        local want = state.effectiveFrameCap > 0 and math.floor(1000000 / state.effectiveFrameCap) or 0
        return math.abs(state.microsecondsPerFrame - want) <= 1
    end
    check(clockAgrees(cap), "the clock does not follow the cap in force: the engine's layout has moved",
        cap.microsecondsPerFrame)

    local wasBackground = cap.lostFocusFrameCap

    -- 2. A cap is taken, and it reaches every field it has to reach. Both caps, because the
    --    smaller of the two wins while the window is not in front - and in a deck it never is.
    check(game.client("return GK.SetFrameCap(10)"), "the game would not take a frame cap")
    local ten = game.client("return GK.FrameCap()")
    check(ten.frameCap == 10, "the cap asked for was not written", ten.frameCap)
    check(ten.effectiveFrameCap == 10, "the cap in force did not follow", ten.effectiveFrameCap)
    check(ten.lostFocusFrameCap == 10, "the background cap did not follow, so a tile behind another would be throttled",
        ten.lostFocusFrameCap)
    check(clockAgrees(ten), "the clock did not follow the new cap", ten.microsecondsPerFrame)

    -- 3. The fields are the right fields: the game actually draws at the rate it was given.
    --    Writing a plausible-looking number somewhere harmless would pass everything above.
    game.sleep(SETTLE)
    local capped = rate(game)
    print(("capped at 10: %.1f frames a second"):format(capped))
    check(capped > 6 and capped < 14, "the game was capped at 10 but does not draw at 10", capped)

    -- 4. And lifting it lifts the engine's own background throttle with it, which is the whole
    --    point: without this every copy but the one in front sits at thirty a second.
    check(game.client("return GK.SetFrameCap(0)"), "the game would not take a lifted cap")
    local free = game.client("return GK.FrameCap()")
    check(free.effectiveFrameCap == 0 and free.microsecondsPerFrame == 0,
        "lifting the cap left the clock capped", free.microsecondsPerFrame)
    check(free.lostFocusFrameCap == 0, "the background throttle is still in force", free.lostFocusFrameCap)
    game.sleep(SETTLE)
    local lifted = rate(game)
    print(("uncapped: %.1f frames a second (the window is %s)")
        :format(lifted, free.hasFocus and "in front" or "not in front"))
    check(lifted > 15, "the cap was lifted but the game still draws slowly; on a machine that cannot "
        .. "manage more than 15 frames a second this is not a fault", lifted)

    -- 5. Leave the copy as the engine would have it, not as this scenario left it.
    game.client(("return Ext.ClientRender.SetLostFocusFrameCap(%d)"):format(wasBackground))
    local back = game.client("return GK.FrameCap()")
    check(back.lostFocusFrameCap == wasBackground, "the engine's own background throttle was not put back",
        back.lostFocusFrameCap)
end
