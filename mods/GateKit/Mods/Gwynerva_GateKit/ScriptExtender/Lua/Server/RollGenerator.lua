-- The game's generator for a roll stream, written once: the standard 64-bit Mersenne Twister seeded
-- with the stream's seed (code:eoc::roll::stream::RollManager::InternalRollDice). GK.Rolls.Next and
-- SetNext compute a stream's coming dice with it. The file uses the 64-bit integers and the bit
-- operators of the game's Lua.

GK.Rolls = GK.Rolls or {}
local Generator = {}
GK.Rolls.Generator = Generator

--- The outputs 0..upto of a generator seeded with `seed`.
function Generator.Outputs(seed, upto)
    local mt = { [0] = seed }
    for i = 1, 311 do local p = mt[i - 1]; mt[i] = (p ~ (p >> 62)) * 6364136223846793005 + i end
    local at, out = 312, {}
    for k = 0, upto do
        if at >= 312 then
            for i = 0, 311 do
                local x = (mt[i] & ~0x7FFFFFFF) | (mt[(i + 1) % 312] & 0x7FFFFFFF)
                local v = mt[(i + 156) % 312] ~ (x >> 1)
                if x & 1 ~= 0 then v = v ~ 0xB5026F5AA96619E9 end
                mt[i] = v
            end
            at = 0
        end
        local x = mt[at]; at = at + 1
        x = x ~ ((x >> 29) & 0x5555555555555555)
        x = x ~ ((x << 17) & 0x71D67FFFEDA60000)
        x = x ~ ((x << 37) & 0xFFF7EEE000000000)
        out[k] = x ~ (x >> 43)
    end
    return out
end

--- One die of `faces` from an output, the output taken as unsigned.
function Generator.Face(x, faces) return 1 + (((x >> 1) % faces) * 2 + (x & 1)) % faces end

--- The output a call's die number k (from 0) is drawn at, the stream's count being `count` when
--- the call begins: the generator is seeded once for the call and each die discards the count again.
function Generator.DrawnAt(count, k) return (k + 1) * count + (k * (k + 3)) // 2 end
