-- Scene "skill_issue" (the video stand): Command: Drop at Commander Zhalk always fails; he mocks,
-- waits for the player's reply, and Shadowheart turns him into a turd.
-- Arm:    ./gatekit --instance <copy> exec server "$(cat stand/scenes/skill_issue.lua)"
-- Reply:  ./gatekit --instance <copy> exec server "return GKScene.skill_issue.reply()"
-- Disarm: ./gatekit --instance <copy> exec server "$(cat stand/scenes/skill_issue_off.lua)"
-- State:  GKScene.skill_issue = { armed, fired, awaiting, done, log = { { tick, ms, text } } }

GKScene = GKScene or {}
local NAME = "skill_issue"
local ZHALK = "ed103005-fd71-457d-ae6c-39654bbd8f2e"
local SHADOWHEART = "3ed74f06-3c60-42dc-83f6-f034cb47c679"
local SPELL = "Target_Command_Drop"           -- matched as a prefix (an upcast variant too)
local LINES = {
    zhalk = "Skill issue, сосунок.",
    shadowheart = "Как невежливо.",
    after = "Вот теперь ты пахнешь честно.",
}
local DUNG = "ee8625e8-1bb4-4bc3-985d-b37ce87ee7f2"   -- LOOT_GEN_Poop_A
local TURD_STATUS = "GK_SCENE_TURD"                   -- a copy of POLYMORPH_CHEESE named "Какашка"
local TURD_SPELL = "GK_Scene_TurdPolymorph"           -- a copy of Target_Polymorph: no save, no concentration, no cost
local TURD_HANDLE = "h6b1d5c0dg7e57g4b1dg9e0ag900000000001"

local old = GKScene[NAME]
if old and old.armed then return { ok = true, already = true, fired = old.fired, awaiting = old.awaiting } end

local S = { armed = true, fired = false, awaiting = nil, done = false, log = {}, made = {}, listeners = {} }
GKScene[NAME] = S

local function log(text, data)
    local c = GK.Speed.Clock()
    S.log[#S.log + 1] = { tick = c.ticks, ms = Ext.Utils.MonotonicTime(), text = text, data = data }
end
local function try(what, fn, ...)
    local ok, v = pcall(fn, ...)
    if not ok then log("failed: " .. what .. ": " .. tostring(v)) end
    return ok and v or nil
end
local function later(ms, fn)
    Ext.Timer.WaitForRealtime(ms, function()
        if S.armed then try("a step", fn) end
    end)
end
local function focus(at, hold)
    -- the camera's focus; the game may hold the camera (a dialogue): then nothing, no failure
    try("focus", GK.Camera.Focus, at, { glide = true, hold = hold })
end
local function say(who, text, style) try("line", GK.Say.Line, who, text, { style = style }) end
local function bare(id) return id and tostring(id):match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

GK.Say.Styles.zhalk = GK.Say.Styles.zhalk or { color = "#FF5030", bold = true, prefix = "♦ ", suffix = " ♦", accent = "#FFD040" }
GK.Say.Styles.shadowheart = GK.Say.Styles.shadowheart or { color = "#C8A8FF", bold = true, italic = true, prefix = "~ ", suffix = " ~", accent = "#E0C8FF", effect = "orb" }

-- the stage scope a rig needs: the stand's when one is open, else one of the scene's own
if not (GK.Stage.Ledger and GK.Stage.Ledger.baseline) then
    -- a scope lost with an earlier Lua state (a load) makes a plain Begin take no baseline
    local b = GK.Stage.Begin()
    if b and b.lost then GK.Stage.Begin{ fresh = true } end
    S.ownScope = true
end

-- his save against Command: Drop is decided at the throw: a natural 20, every time while armed
local rig = GK.Rolls.Rig{ who = ZHALK, kind = "SavingThrow", spell = SPELL, natural = 20, times = 0 }
if not rig.ok then
    -- a build whose roll event does not name the spell: every saving throw of his is rigged instead
    rig = GK.Rolls.Rig{ who = ZHALK, kind = "SavingThrow", natural = 20, times = 0 }
    S.rigBySpell = false
else
    S.rigBySpell = true
end
S.rig = rig.id
log("armed: rig " .. tostring(rig.id) .. " on Zhalk's saving throws against " .. SPELL)

local function rigDecided()
    for _, r in ipairs(GK.Rolls.Rigs()) do if r.id == S.rig then return r.decided end end
    return S.lastDecided or 0
end
S.lastDecided = rigDecided()

-- the turd: a polymorph status and a spell of the scene's own, made once per game process
local function makeTurd()
    -- plain text: the combat log shows a status's name with its markup as written
    Ext.Loca.UpdateTranslatedString(TURD_HANDLE, "Какашка")
    local st = Ext.Stats.Get(TURD_STATUS) or Ext.Stats.Create(TURD_STATUS, "StatusData", "POLYMORPH_CHEESE")
    st.DisplayName = TURD_HANDLE
    st:Sync()
    local sp = Ext.Stats.Get(TURD_SPELL) or Ext.Stats.Create(TURD_SPELL, "SpellData", "Target_Polymorph")
    sp.SpellRoll = { Default = "" }
    sp.SpellFlags = { "HasVerbalComponent", "HasSomaticComponent", "IsSpell" }
    sp.UseCosts = ""
    sp:Sync()
end

local function turn()
    -- the copy's functor list is shared with Target_Polymorph: pointed at the turd for this cast
    -- only, and put back when the cast is over (and by the disarm)
    -- (read anew each time: a functor held across ticks has expired)
    local functor = {}
    setmetatable(functor, { __newindex = function(_, k, v) Ext.Stats.Get(TURD_SPELL).SpellSuccess[1].Functors[1][k] = v end })
    functor.StatusId = TURD_STATUS
    S.functorSwapped = true
    GK.Stage.Teach(TURD_SPELL, SHADOWHEART)
    later(300, function()
        GK.Stage.Cast(TURD_SPELL, ZHALK, SHADOWHEART)
        log("Shadowheart casts the polymorph at Zhalk")
    end)
    local tries = 0
    local function landed()
        tries = tries + 1
        if Osi.HasActiveStatus(ZHALK, TURD_STATUS) == 1 then
            functor.StatusId = "POLYMORPH_SHEEP"; S.functorSwapped = false
            -- 5 rounds (the polymorph's own); the cheese form's one hit point does not end it
            -- early: immortal while it lasts (a StatusRemoved listener takes that off)
            Osi.SetImmortal(ZHALK, 1)
            S.immortal = true
            local x, y, z = Osi.GetPosition(ZHALK)
            for i, d in ipairs({ { 0.35, 0.2 }, { -0.3, 0.3 }, { 0.05, -0.4 } }) do
                local item = Osi.CreateAt(DUNG, x + d[1], y, z + d[2], 0, 1, "")
                if item then S.made[#S.made + 1] = item end
            end
            GK.Say.Accent(ZHALK, { effect = "spectral" })
            -- the game's own camera follows the caster to the end of the cast: the focus a moment later
            later(1100, function() focus(ZHALK, 4.5) end)
            log("Zhalk is a turd (" .. TURD_STATUS .. "), immortal while it lasts; dung at his feet")
            later(3600, function()
                say(SHADOWHEART, LINES.after, "shadowheart")
                S.done = true
                log("done: the fight goes on without him")
            end)
        elseif tries < 40 then
            later(150, landed)
        else
            functor.StatusId = "POLYMORPH_SHEEP"; S.functorSwapped = false
            log("the polymorph did not land in 6 s: the cheese status is put on by hand")
            GK.Stage.Status("POLYMORPH_CHEESE", ZHALK, 15)
            S.done = true
        end
    end
    later(800, landed)
end

--- The driver calls this once the player has answered Zhalk (the agent says its own line).
function S.reply()
    if S.awaiting ~= "reply" then return { ok = false, error = "not waiting for a reply", fired = S.fired } end
    S.awaiting = nil
    log("the reply came")
    focus(SHADOWHEART)   -- no hold: its return would pull the camera off Zhalk's polymorph
    later(500, function() say(SHADOWHEART, LINES.shadowheart, "shadowheart") end)
    later(3300, turn)
    return { ok = true }
end

local function fire(caster)
    if S.fired then return end
    S.fired = true
    log("fired: " .. tostring(caster) .. " cast " .. SPELL .. " at Zhalk and he saved (rig decided " .. tostring(rigDecided()) .. ")")
    later(1200, function()
        focus(ZHALK, 4)
        GK.Say.Accent(ZHALK, { effect = "faerie" })
    end)
    later(1700, function()
        say(ZHALK, LINES.zhalk, "zhalk")
        S.awaiting = "reply"
        log("awaiting the player's reply (GKScene.skill_issue.reply())")
    end)
end

-- the trigger: the cast of Command: Drop at Zhalk by a party member, then its save
makeTurd()
local pending = nil
local function isParty(id) for _, m in ipairs(GK.Stage.Party()) do if m == id then return true end end return false end
S.listeners[#S.listeners + 1] = Ext.Osiris.RegisterListener("UsingSpellOnTarget", 6, "after", function(caster, target, spell)
    if not S.armed or S.fired then return end
    if bare(target) == ZHALK and tostring(spell):sub(1, #SPELL) == SPELL and isParty(bare(caster)) then
        -- the save is thrown before this event: compare with the count before the cast
        pending = { caster = bare(caster), decided = S.lastDecided }
        log("seen: " .. tostring(spell) .. " at Zhalk by " .. tostring(caster))
    end
end)
S.listeners[#S.listeners + 1] = Ext.Osiris.RegisterListener("CastedSpell", 5, "after", function(caster, spell)
    if not S.armed or S.fired or not pending then return end
    if tostring(spell):sub(1, #SPELL) ~= SPELL or bare(caster) ~= pending.caster then return end
    -- the hit (and its save) lands a moment after the cast ends: look a few times
    local p, tries = pending, 0
    pending = nil
    local function look()
        tries = tries + 1
        local now = rigDecided()
        local saved = now > p.decided
        if saved then S.lastDecided = now end
        local disarmed = Osi.HasActiveStatus(ZHALK, "DISARM") == 1 or Osi.HasActiveStatus(ZHALK, "COMMAND_DROP") == 1
        if saved and not disarmed then
            fire(p.caster)
        elseif tries < 20 then
            later(150, look)
        else
            log("the cast ended without a rigged save (saved " .. tostring(saved) .. ", disarmed " .. tostring(disarmed) .. "): not fired")
        end
    end
    later(150, look)
end)

S.listeners[#S.listeners + 1] = Ext.Osiris.RegisterListener("StatusRemoved", 4, "after", function(object, status)
    if status == TURD_STATUS and bare(object) == ZHALK and S.immortal then
        pcall(Osi.SetImmortal, ZHALK, 0)
        S.immortal = false
        log("the turd wore off: Zhalk is himself again, mortal")
    end
end)

return { ok = true, armed = true, rig = S.rig, rigBySpell = S.rigBySpell, ownScope = S.ownScope == true }
