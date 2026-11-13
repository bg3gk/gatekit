-- GK.Conditions: the game's spell condition texts, evaluated before a cast - what the game checks
-- when the player picks a target (Sneak Attack: "HasAdvantage(...) or HasAllyWithinRange(...)").
-- Server side.
--
--   GK.Conditions.Check(who, spell, target)  { ok = true | false | nil, failed = "the part that is
--                                            false", unknown = { functions not modelled } }
--   GK.Conditions.Eval(text, source, target) the same for any condition text
--
-- Three-valued: a function this file does not know is "unknown" (nil), and so is anything built
-- from it that its known parts do not decide. `ok = false` is certain; `ok = nil` means "the game
-- decides". The language: `and`, `or`, `not`, parentheses, calls with arguments (strings, numbers,
-- names such as AttackType.MeleeWeaponAttack, context.Source / context.Target).
-- Spell stats carry three: RequirementConditions (about the caster), TargetConditions (about the
-- target) and CycleConditions (which targets the cursor offers); Check uses the first two.

GK.Conditions = GK.Conditions or {}
local C = GK.Conditions
local See = GK.See

local function try(fn, ...)
    local ok, a, b = pcall(fn, ...)
    if ok then return a, b end
    return nil
end
local function bool(v) return v == 1 or v == true end

-- ---------------------------------------------------------------------------------------
-- Tokens and parsing

local function tokenize(text)
    local tokens, i = {}, 1
    while i <= #text do
        local c = text:sub(i, i)
        if c:match("%s") then
            i = i + 1
        elseif c == "(" or c == ")" or c == "," then
            tokens[#tokens + 1] = { kind = c }
            i = i + 1
        elseif c == "'" or c == '"' then
            local j = text:find(c, i + 1, true) or #text
            tokens[#tokens + 1] = { kind = "string", value = text:sub(i + 1, j - 1) }
            i = j + 1
        elseif c:match("[%d%-]") and text:sub(i):match("^%-?%d") then
            local num = text:sub(i):match("^%-?[%d%.]+")
            tokens[#tokens + 1] = { kind = "number", value = tonumber(num) }
            i = i + #num
        else
            local word = text:sub(i):match("^[%w_%.]+")
            if not word then
                i = i + 1   -- an operator this parser does not know: skipped
            else
                local lower = word:lower()
                if lower == "and" or lower == "or" or lower == "not" then
                    tokens[#tokens + 1] = { kind = lower }
                elseif lower == "true" or lower == "false" then
                    tokens[#tokens + 1] = { kind = "bool", value = lower == "true" }
                else
                    tokens[#tokens + 1] = { kind = "name", value = word }
                end
                i = i + #word
            end
        end
    end
    return tokens
end

-- expr := term { "or" term } ; term := factor { "and" factor } ; factor := "not" factor | "(" expr ")" | call | value
local function parse(tokens)
    local pos = 1
    local function peek() return tokens[pos] end
    local function take() pos = pos + 1; return tokens[pos - 1] end
    local expr
    local function factor()
        local t = peek()
        if not t then return { kind = "unknown" } end
        if t.kind == "not" then take(); return { kind = "not", a = factor() } end
        if t.kind == "(" then
            take()
            local e = expr()
            if peek() and peek().kind == ")" then take() end
            return e
        end
        if t.kind == "name" then
            take()
            if peek() and peek().kind == "(" then
                take()
                local args = {}
                while peek() and peek().kind ~= ")" do
                    if peek().kind == "," then take() else args[#args + 1] = expr() end
                end
                if peek() then take() end
                return { kind = "call", name = t.value, args = args }
            end
            return { kind = "value", value = t.value }
        end
        take()
        return { kind = "value", value = t.value }
    end
    local function term()
        local a = factor()
        while peek() and peek().kind == "and" do take(); a = { kind = "and", a = a, b = factor() } end
        return a
    end
    expr = function()
        local a = term()
        while peek() and peek().kind == "or" do take(); a = { kind = "or", a = a, b = term() } end
        return a
    end
    return expr()
end

local cache = {}
local function compile(text)
    if cache[text] == nil then cache[text] = parse(tokenize(text)) end
    return cache[text]
end

-- ---------------------------------------------------------------------------------------
-- The functions the game's conditions use most

local function hasComponent(id, name)
    local e = id and try(Ext.Entity.Get, id)
    return e ~= nil and try(function() return e[name] end) ~= nil
end

local function relation(a, b)
    if not (a and b) then return nil end
    if See.IsMember(a) and See.IsMember(b) then return "ally" end
    if bool(try(Osi.IsEnemy, a, b)) then return "enemy" end
    if bool(try(Osi.IsAlly, a, b)) then return "ally" end
    return "neutral"
end

local function arg(node, ctx)
    if not node then return nil end
    if node.kind == "value" then
        if node.value == "context.Source" then return ctx.source end
        if node.value == "context.Target" then return ctx.target end
        return node.value
    end
    return nil
end

-- Each function: (ctx, args nodes) -> true | false | nil. ctx: { subject, source, target }.
C.Functions = {
    Character = function(ctx) return hasComponent(ctx.subject, "ServerCharacter") end,
    Item = function(ctx) return hasComponent(ctx.subject, "ServerItem") end,
    Self = function(ctx) return ctx.subject == ctx.source end,
    Dead = function(ctx) return bool(try(Osi.IsDead, ctx.subject)) end,
    Enemy = function(ctx) return relation(ctx.source, ctx.subject) == "enemy" end,
    Ally = function(ctx) return relation(ctx.source, ctx.subject) == "ally" end,
    Player = function(ctx, args)
        local who = arg(args[1], ctx) or ctx.subject
        return See.IsMember(who) or bool(try(Osi.IsPlayer, who))
    end,
    Combat = function(ctx, args) return bool(try(Osi.IsInCombat, arg(args[1], ctx) or ctx.subject)) end,
    HasStatus = function(ctx, args)
        local status = arg(args[1], ctx)
        local who = arg(args[2], ctx) or ctx.subject
        if not status then return nil end
        if status:sub(1, 3) == "SG_" then return nil end   -- status groups: not modelled
        return bool(try(Osi.HasActiveStatus, who, status))
    end,
    HasActionResource = function(ctx, args)
        local name, amount = arg(args[1], ctx), arg(args[2], ctx) or 1
        local who = arg(args[6], ctx) or ctx.subject
        local res = See.Resources(who)
        local map = { ActionPoint = res.action, BonusActionPoint = res.bonus, ReactionActionPoint = res.reaction,
                      Movement = res.movement }
        local pair = map[name] or (res.other or {})[name]
        if not pair then return nil end
        return pair[1] >= (tonumber(amount) or 1)
    end,
    HasAdvantage = function(ctx, args)
        local attackType = tostring(arg(args[1], ctx) or ""):match("AttackType%.(%a+)")
        local m = See.RollMods(ctx.source, "attack", attackType, ctx.target)
        return m.advantage > 0
    end,
    HasDisadvantage = function(ctx, args)
        local attackType = tostring(arg(args[1], ctx) or ""):match("AttackType%.(%a+)")
        local m = See.RollMods(ctx.source, "attack", attackType, ctx.target)
        return m.advantage < 0
    end,
    -- an ally of the source (not the source itself, not incapacitated) within range of the subject
    HasAllyWithinRange = function(ctx, args)
        local range = tonumber(arg(args[2], ctx)) or 1.5
        local subject = ctx.subject
        local sp = See.Position(subject)
        if not sp then return nil end
        for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter")) do
            local id = try(function() return e.Uuid.EntityUuid end)
            if id and id ~= ctx.source and id ~= subject and relation(ctx.source, id) == "ally"
                and not bool(try(Osi.IsDead, id)) then
                local p = See.Position(id)
                local hp = try(function() return e.Health.Hp end)
                if p and (hp or 1) > 0 then
                    local dx, dz = p[1] - sp[1], p[3] - sp[3]
                    -- range is measured edge to edge: allow the two bodies' radii
                    if math.sqrt(dx * dx + dz * dz) <= range + 0.6 and math.abs(p[2] - sp[2]) < 2 then return true end
                end
            end
        end
        return false
    end,
    WieldingFinesseWeaponInSpecificHand = function(ctx, args)
        local who = arg(args[1], ctx) or ctx.source
        local item = try(Osi.GetEquippedItem, who, "Melee Main Weapon")
        local e = item and try(Ext.Entity.Get, item)
        local stat = e and try(function() return Ext.Stats.Get(e.Data.StatsId) end)
        if not stat then return false end
        for _, p in ipairs(try(function() return stat["Weapon Properties"] end) or {}) do
            if p == "Finesse" then return true end
        end
        return false
    end,
    CanUseWeaponActions = function() return true end,
    IsProficientWithEquippedWeapon = function() return true end,
}

-- ---------------------------------------------------------------------------------------
-- Evaluation

-- A node back as text, to name the part of a condition that fails.
local function deparse(node)
    if not node then return "?" end
    local k = node.kind
    if k == "and" then return deparse(node.a) .. " and " .. deparse(node.b) end
    if k == "or" then return "(" .. deparse(node.a) .. " or " .. deparse(node.b) .. ")" end
    if k == "not" then return "not " .. deparse(node.a) end
    if k == "call" then
        local args = {}
        for _, a in ipairs(node.args) do
            args[#args + 1] = a.kind == "value" and (type(a.value) == "string" and not a.value:find("%.") and ("'" .. a.value .. "'") or tostring(a.value)) or deparse(a)
        end
        return node.name .. "(" .. table.concat(args, ",") .. ")"
    end
    return tostring(node.value)
end

-- The operands of a chain of "and"s.
local function conjuncts(node, out)
    out = out or {}
    if node and node.kind == "and" then
        conjuncts(node.a, out)
        conjuncts(node.b, out)
    else
        out[#out + 1] = node
    end
    return out
end

local function eval(node, ctx, trail)
    if not node then return nil end
    local k = node.kind
    if k == "and" then
        local a = eval(node.a, ctx, trail)
        if a == false then return false end
        local b = eval(node.b, ctx, trail)
        if b == false then return false end
        if a == true and b == true then return true end
        return nil
    elseif k == "or" then
        local a = eval(node.a, ctx, trail)
        if a == true then return true end
        local b = eval(node.b, ctx, trail)
        if b == true then return true end
        if a == false and b == false then return false end
        return nil
    elseif k == "not" then
        local a = eval(node.a, ctx, trail)
        if a == nil then return nil end
        return not a
    elseif k == "bool" or (k == "value" and type(node.value) == "boolean") then
        return node.value
    elseif k == "call" then
        local fn = C.Functions[node.name]
        if not fn then
            trail.unknown[node.name] = true
            return nil
        end
        -- a call whose first argument is context.Source / context.Target asks about that one
        local sub = ctx
        local first = node.args[1] and node.args[1].kind == "value" and node.args[1].value
        if first == "context.Source" and node.name ~= "HasActionResource" and node.name ~= "WieldingFinesseWeaponInSpecificHand" then
            sub = { subject = ctx.source, source = ctx.source, target = ctx.target }
        end
        local ok, value = pcall(fn, sub, node.args)
        if not ok then return nil end
        return value
    end
    return nil
end

function C.Eval(text, source, target, subject)
    if not text or text == "" then return { ok = true } end
    local trail = { failed = {}, unknown = {} }
    local ctx = { source = source, target = target, subject = subject or target or source }
    local root = compile(text)
    local ok = eval(root, ctx, trail)
    if ok == false then
        -- name the parts of the top-level "and" chain that are false
        local parts = {}
        for _, node in ipairs(conjuncts(root)) do
            if eval(node, ctx, { failed = {}, unknown = {} }) == false then parts[#parts + 1] = deparse(node) end
        end
        trail.failed = parts
    end
    local unknown = {}
    for name in pairs(trail.unknown) do unknown[#unknown + 1] = name end
    return { ok = ok, failed = ok == false and table.concat(trail.failed, ", ") or nil,
             unknown = #unknown > 0 and unknown or nil }
end

function C.Check(who, spell, target)
    local stat = try(Ext.Stats.Get, spell)
    if not stat then return { ok = nil } end
    local req = try(function() return stat.RequirementConditions end) or ""
    local r = C.Eval(req, who, target, who)
    if r.ok == false then
        return { ok = false, failed = "requirement: " .. (r.failed or "?"), text = req }
    end
    if target and type(target) == "string" then
        local tc = try(function() return stat.TargetConditions end) or ""
        local t = C.Eval(tc, who, target, target)
        if t.ok == false then
            return { ok = false, failed = "target: " .. (t.failed or "?"), text = tc }
        end
        if t.ok == nil or r.ok == nil then return { ok = nil, unknown = t.unknown or r.unknown } end
    end
    return { ok = r.ok }
end

return C
