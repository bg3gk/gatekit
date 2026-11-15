using System.Globalization;
using System.Text.Json.Nodes;

namespace GateKit.Sweep;

/// <summary>What a plan kind needs besides the stats: the extender's enums as the running game
/// lists them (statustypes, surfaces) and the leads (leads).</summary>
public sealed record PlanInputs
{
    /// <summary>Enum name -> its labels (Ext.Enums.&lt;name&gt;), read from a running copy.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Enums { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyList<JsonObject> Leads { get; init; } = [];
}

/// <summary>
/// The plan kinds of the second sweep (docs/contracts/cli.md, 8.13): status types only the
/// extender's enum names, every surface type stepped into, passive functor contexts staged, spell
/// types and animation kinds cast by a party member through GK.Act (and checked), interrupt
/// contexts provoked, item boosts and passives equipped, and summon/spawn functors.
/// </summary>
public static class MorePlans
{
    /// <summary>The kinds whose plan needs the running game's enums.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> EnumsOf = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["statustypes"] = ["StatusType"],
        ["surfaces"] = ["SurfaceType"],
    };

    static string L(string text) => PlanBuilder.Lua(text);

    static IReadOnlyList<string> Enum(PlanInputs? inputs, string name) =>
        inputs?.Enums.GetValueOrDefault(name) is { Count: > 0 } labels ? labels
            : throw new SweepException($"this plan needs the extender's enum {name}: give a running copy with --instance");

    // -------------------------------------------------------------------------------------
    // status types the data never uses

    public static SweepPlan StatusTypes(StatsData stats, PlanInputs? inputs, string? data)
    {
        var used = stats.Entries.Where(e => e.Type == "StatusData").Select(e => e.Field("StatusType")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var labels = Enum(inputs, "StatusType");
        var steps = labels.Where(l => !used.Contains(l)).Order(StringComparer.Ordinal).Select(type => new SweepStep
        {
            Id = $"status-enum:{type}",
            What = $"status type {type}: in the extender's enum, no StatusData entry has it; applied by its name",
            Carrier = new Carrier("status", type, "StatusType", Shipping: false),
            ReachedBy = "cheat",
            How = $"Osi.ApplyStatus(D, {L(type)}, 6.0, 1, D)",
            Undo = $"pcall(Osi.RemoveStatus, D, {L(type)})",
            Check = $"return Osi.HasActiveStatus(D, {L(type)}) == 1 or \"the game did not apply it\"",
            Settle = 1.5,
            Fresh = true,
        }).ToList();
        return new SweepPlan
        {
            Kind = "statustypes",
            Data = data,
            Counts = new JsonObject { ["status_types_in_enum"] = labels.Count, ["used_by_data"] = used.Count, ["steps"] = steps.Count },
            Steps = steps,
        };
    }

    // -------------------------------------------------------------------------------------
    // surfaces

    public static SweepPlan Surfaces(PlanInputs? inputs, string? data)
    {
        var labels = Enum(inputs, "SurfaceType").Where(l => l != "None").Order(StringComparer.Ordinal).ToList();
        // Each step on its own spot of a ring around the host: a dummy that dies in one surface
        // leaves blood where the next would stand, and the surface found under it would be that.
        var steps = labels.Select((surface, index) => new SweepStep
        {
            Id = $"surface:{surface}",
            What = $"surface {surface} created under the dummy, which stands in it; the statuses it applies are noted",
            Carrier = new Carrier("surface", "Surface" + surface, Shipping: true),
            ReachedBy = "cheat",
            How = "SweepStatusesBefore = {}\n"
                + "for _, s in ipairs(GK.Stage.Statuses(D) or {}) do SweepStatusesBefore[type(s) == 'table' and (s.id or s.name) or s] = true end\n"
                + $"local h, a = GK.Stage.Position(H), {(index % 16).ToString(CultureInfo.InvariantCulture)} * math.pi / 8\n"
                + $"local r = {(7 + 3 * (index / 16 % 3)).ToString(CultureInfo.InvariantCulture)}\n"
                + "local x, y, z = h[1] + r * math.cos(a), h[2], h[3] + r * math.sin(a)\n"
                + "local ok, vx, vy, vz = pcall(Osi.FindValidPosition, x, y, z, 4.0, D, 1)\n"
                + "if ok and vx then x, y, z = vx, vy, vz end\n"
                + "Osi.TeleportToPosition(D, x, y, z, \"\", 0, 0, 0, 0, 1)\n"
                + "SweepGroundBefore = tostring(Osi.GetSurfaceGroundAt and Osi.GetSurfaceGroundAt(D) or nil)\n"
                + $"Osi.CreateSurfaceAtPosition(x, y, z, {L("Surface" + surface)}, 2.0, 6.0)",
            Check = "local ground = tostring(Osi.GetSurfaceGroundAt and Osi.GetSurfaceGroundAt(D) or nil)\n"
                + "local cloud = tostring(Osi.GetSurfaceCloudAt and Osi.GetSurfaceCloudAt(D) or nil)\n"
                + "local applied = {}\n"
                + "for _, s in ipairs(GK.Stage.Statuses(D) or {}) do local n = type(s) == 'table' and (s.id or s.name) or s\n"
                + "  if n and not SweepStatusesBefore[n] then applied[#applied + 1] = tostring(n) end end\n"
                + "local function some(v) return v ~= 'nil' and v ~= '' and v ~= 'SurfaceNone' and v ~= 'None' end\n"
                + $"local wanted = {L("Surface" + surface)}\n"
                + "return { ok = ground == wanted or cloud == wanted, note = 'under the dummy: ' .. ground .. '/' .. cloud .. ' (ground before: ' .. tostring(SweepGroundBefore) .. ')'"
                + " .. ((some(ground) or some(cloud)) and '' or '; nothing') .. '; applied: ' .. table.concat(applied, ',') }",
            Settle = 3.0,
            Fresh = true,
        }).ToList();
        return new SweepPlan
        {
            Kind = "surfaces",
            Data = data,
            Counts = new JsonObject { ["surface_types_in_enum"] = labels.Count, ["steps"] = steps.Count },
            Steps = steps,
        };
    }

    // -------------------------------------------------------------------------------------
    // passive functor contexts

    const string Attack = "Target_MainHandAttack";

    static string Teach(string spell, string who) => $"GK.Stage.Teach({L(spell)}, {who})";

    static string CastLater(string spell, string target, string who, int ms = 1200) =>
        $"Ext.Timer.WaitFor({ms}, function() pcall(GK.Stage.Cast, {L(spell)}, {target}, {who}) end)";

    /// <summary>How each StatsFunctorContext event is made to happen between the dummy D (which has
    /// the passive) and a second dummy T; null where no staging is known (the try is blocked).</summary>
    public static string? Staging(string context) => context switch
    {
        "OnAttack" or "OnDamage" or "OnCast" or "OnCastResolved" =>
            $"{Teach(Attack, "D")}\n{CastLater(Attack, "T", "D")}",
        "OnAttacked" or "OnDamaged" or "OnDamagedPrevented" =>
            $"{Teach(Attack, "T")}\n{CastLater(Attack, "D", "T")}",
        "OnHeal" => $"{Teach("Target_CureWounds", "D")}\nOsi.ApplyDamage(T, 3, \"Bludgeoning\", \"\")\n{CastLater("Target_CureWounds", "T", "D")}",
        "OnHealed" => $"{Teach("Target_CureWounds", "T")}\nOsi.ApplyDamage(D, 3, \"Bludgeoning\", \"\")\n{CastLater("Target_CureWounds", "D", "T")}",
        "OnStatusApplied" or "OnStatusRemoved" =>
            "Osi.ApplyStatus(D, \"BLESS\", 6.0, 1, T)\nExt.Timer.WaitFor(1500, function() pcall(Osi.RemoveStatus, D, \"BLESS\") end)",
        "OnStatusApply" or "OnStatusRemove" =>
            "Osi.ApplyStatus(T, \"BLESS\", 6.0, 1, D)\nExt.Timer.WaitFor(1500, function() pcall(Osi.RemoveStatus, T, \"BLESS\") end)",
        "OnTurn" or "OnRound" or "OnCombatStarted" or "OnCombatEnded" =>
            $"GK.Stage.Faction(\"enemy\", T)\n{Teach(Attack, "T")}\n{CastLater(Attack, "D", "T")}",
        "OnCreate" => "-- adding the passive is the event",
        "OnEquip" => "Osi.TemplateAddTo(GK.Stage.Templates.dagger, D, 1, 0)\n"
            + "Ext.Timer.WaitFor(700, function() local f = GK.Find(GK.Stage.Templates.dagger, D) if f and f[1] then Osi.Equip(D, f[1], 1, 0, 0) end end)",
        "OnMovedDistance" => "local p = GK.Stage.Position(D)\nOsi.CharacterMoveToPosition(D, p[1] + 4, p[2], p[3], \"Run\", \"\", 0)",
        "OnObscurityChanged" => "local p = GK.Stage.Position(D)\nOsi.CreateSurfaceAtPosition(p[1], p[2], p[3], \"SurfaceDarknessCloud\", 2.0, 6.0)",
        "OnActionResourcesChanged" => $"{Teach("Shout_Dash", "D")}\n{CastLater("Shout_Dash", "D", "D")}",
        "OnConsumed" => "Osi.TemplateAddTo(GK.Stage.Templates.potion, D, 1, 0)\n"
            + "Ext.Timer.WaitFor(700, function() local f = GK.Find(GK.Stage.Templates.potion, D) if f and f[1] then Osi.Use(D, f[1], \"\") end end)",
        "OnAbilityCheck" => "Osi.RequestPassiveRoll(D, T, \"SkillCheck\", \"Athletics\", \"0ebd6b2b-b9a6-4a0d-9a3b-3f9b1ad66cb5\", 0, \"\")",
        _ => null,
    };

    public static SweepPlan Contexts(StatsData stats, string? data)
    {
        var byContext = new Dictionary<string, List<StatEntry>>(StringComparer.Ordinal);
        foreach (var passive in stats.Entries.Where(e => e.Type == "PassiveData" && e.Field("StatsFunctors") is not null))
            foreach (var context in StatsText.Flags(passive.Field("StatsFunctorContext")))
            {
                if (context is "None" or "") continue;
                if (!byContext.TryGetValue(context, out var list)) byContext[context] = list = [];
                list.Add(passive);
            }
        var steps = new List<SweepStep>();
        foreach (var (context, list) in byContext.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var best = list.OrderBy(e => e.Shipping ? 0 : 1)
                .ThenBy(e => e.Field("Conditions") is null ? 0 : 1)
                .ThenBy(e => StatsText.Flags(e.Field("StatsFunctorContext")).Count)
                .ThenBy(e => StatsText.Calls(e.Field("StatsFunctors")).Count)
                .ThenBy(e => e.Name, StringComparer.Ordinal).First();
            var staging = Staging(context);
            steps.Add(new SweepStep
            {
                Id = $"context:{context}",
                What = $"passive functor context {context} through {best.Name} ({PlanBuilder.CutText(best.Field("StatsFunctors")!, 70)}), the event staged between two dummies",
                Carrier = new Carrier("passive", best.Name, "StatsFunctorContext", best.Shipping),
                ReachedBy = "cheat",
                How = $"Osi.AddPassive(D, {L(best.Name)})\n" + (staging ?? ""),
                Undo = $"pcall(Osi.RemovePassive, D, {L(best.Name)})\npcall(GK.Stage.EndCombat, D)",
                Check = $"return Osi.HasPassive(D, {L(best.Name)}) == 1 or \"the passive is not on the dummy\"",
                Blocked = staging is null ? $"no staging is known for {context} (a rest or a level change: the tour's)" : null,
                Settle = context is "OnTurn" or "OnRound" or "OnCombatStarted" or "OnCombatEnded" ? 8.0 : 4.0,
                Fresh = true,
            });
        }
        return new SweepPlan
        {
            Kind = "contexts",
            Data = data,
            Counts = new JsonObject { ["functor_contexts_in_data"] = byContext.Count, ["steps"] = steps.Count, ["blocked"] = steps.Count(s => s.Blocked is not null) },
            Steps = steps,
        };
    }

    // -------------------------------------------------------------------------------------
    // spell types and animation kinds, cast by a party member

    static readonly string[] SpellGroups = ["SpellType", "SpellAnimationType", "SpellJumpType", "HitAnimationType", "SpellAnimationIntentType"];

    static int Level(StatEntry e) => int.TryParse(e.Field("Level"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? level : 0;

    /// <summary>What GK.Cheat.Cast is given as the target for a spell of this type.</summary>
    static string TargetOf(string? spellType) => spellType switch
    {
        "Shout" => "H",
        "Zone" or "Wall" or "ProjectileStrike" or "Cone" or "Storm" => "GK.Stage.Position(T)",
        "Teleportation" => "(function() local p = GK.Stage.Position(H) return { p[1] + 3, p[2], p[3] } end)()",
        _ => "T",
    };

    public static SweepPlan SpellTypes(StatsData stats, string? data)
    {
        var spells = stats.Entries.Where(e => e.Type == "SpellData" && e.Shipping && !e.Name.StartsWith("Interrupt_", StringComparison.Ordinal)).ToList();
        var steps = new List<SweepStep>();
        var seenSpells = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in SpellGroups)
        {
            foreach (var value in spells.Select(e => e.Field(group)?.Trim()).OfType<string>().Where(v => v.Length > 0 && v != "None")
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var best = spells.Where(e => e.Field(group)?.Trim() == value)
                    .OrderBy(e => Level(e))
                    .ThenBy(e => e.Field("Cooldown") is null or "None" ? 0 : 1)
                    .ThenBy(e => e.Field("SpellType") == "Throw" ? 1 : 0)
                    .ThenBy(e => e.Field("RequirementConditions") is null ? 0 : 1)
                    .ThenBy(e => e.Field("TargetConditions") is null ? 0 : 1)
                    .ThenBy(e => e.Name.Length).ThenBy(e => e.Name, StringComparer.Ordinal).First();
                var name = L(best.Name);
                var target = TargetOf(best.Field("SpellType"));
                var second = best.Field("SpellType") == "Throw" ? ", target2 = SweepItem" : "";
                steps.Add(new SweepStep
                {
                    Id = $"cast:{group}:{value}",
                    What = $"{group} {value}: the host casts {best.Name} ({best.Field("SpellType")}, level {Level(best)}) through GK.Cheat.Cast, checked",
                    Carrier = new Carrier("spell", best.Name, group, best.Shipping),
                    ReachedBy = "play_with_cheat_setup",
                    How = (second.Length > 0 ? "SweepItem = GK.Stage.Spawn{ template = \"potion\", near = H, distance = 1 }[1]\n" : "")
                        + $"GK.Stage.Teach({name}, H)\nGK.Stage.Restore(H)\nSweepJob = nil\n"
                        + $"Ext.Timer.WaitFor(1500, function() SweepJob = GK.Cheat.Cast(H, {name}, {target}, {{ walk = true{second} }}) end)",
                    Check = "local j = SweepJob\nif not j then return 'no cast was asked' end\n"
                        + "if j.ok == false then return 'refused: ' .. tostring(j.error) end\n"
                        + "local v = GK.Act.Job(j.job)\nif type(v) ~= 'table' then return 'no job' end\n"
                        + "if v.status == 'done' then return true end\n"
                        + "return { ok = false, note = 'job ' .. tostring(v.status) .. ': ' .. tostring(v.error) .. ' (step ' .. tostring(v.step) .. ')' }",
                    Undo = "pcall(GK.Cheat.Stop, H)\npcall(GK.Stage.EndCombat, H)\n"
                        + $"pcall(Osi.RemoveSpell, H, {name}, 0)\nif SweepItem then pcall(Osi.RequestDelete, SweepItem); SweepItem = nil end",
                    Settle = 8.0,
                    Fresh = true,
                });
                seenSpells.Add(best.Name);
            }
        }
        return new SweepPlan
        {
            Kind = "spelltypes",
            Data = data,
            Counts = new JsonObject { ["groups"] = SpellGroups.Length, ["distinct_spells"] = seenSpells.Count, ["steps"] = steps.Count },
            Steps = steps,
        };
    }

    // -------------------------------------------------------------------------------------
    // interrupts

    /// <summary>How each InterruptContext is provoked against a dummy D that has the interrupt, by a
    /// second dummy T made hostile; both directions where the interrupt may be the attacker's.</summary>
    public static string? Provocation(string context) => context switch
    {
        "OnCastHit" or "OnPreDamage" or "OnPostRoll" =>
            $"GK.Stage.Faction(\"enemy\", T)\n{Teach(Attack, "T")}\n{Teach(Attack, "D")}\n{CastLater(Attack, "T", "D")}\n{CastLater(Attack, "D", "T", 4000)}",
        "OnSpellCast" => $"GK.Stage.Faction(\"enemy\", T)\n{Teach("Projectile_FireBolt", "T")}\n{CastLater("Projectile_FireBolt", "D", "T")}",
        "OnStatusApplied" => "Osi.ApplyStatus(D, \"BLESS\", 6.0, 1, T)\nOsi.ApplyStatus(T, \"BANE\", 6.0, 1, D)",
        "OnDeath" => "Ext.Timer.WaitFor(800, function() pcall(Osi.Die, T, 0, \"NULL_00000000-0000-0000-0000-000000000000\", 0, 1, 0) end)",
        "OnLeaveAttackRange" => "GK.Stage.Faction(\"enemy\", T)\nlocal p = GK.Stage.Position(D)\nOsi.TeleportToPosition(T, p[1] + 1, p[2], p[3], \"\", 0, 0, 0, 0, 1)\n"
            + "Ext.Timer.WaitFor(1500, function() Osi.CharacterMoveToPosition(T, p[1] + 9, p[2], p[3], \"Run\", \"\", 0) end)",
        "OnEnterAttackRange" => "GK.Stage.Faction(\"enemy\", T)\nExt.Timer.WaitFor(800, function() local p = GK.Stage.Position(D) Osi.CharacterMoveToPosition(T, p[1] + 1, p[2], p[3], \"Run\", \"\", 0) end)",
        _ => null,
    };

    public static SweepPlan Interrupts(StatsData stats, string? data)
    {
        var byContext = new Dictionary<string, List<StatEntry>>(StringComparer.Ordinal);
        foreach (var interrupt in stats.Entries.Where(e => e.Type == "InterruptData"))
            foreach (var context in StatsText.Flags(interrupt.Field("InterruptContext")))
            {
                if (!byContext.TryGetValue(context, out var list)) byContext[context] = list = [];
                list.Add(interrupt);
            }
        var steps = new List<SweepStep>();
        foreach (var (context, list) in byContext.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var best = list.OrderBy(e => e.Shipping ? 0 : 1)
                .ThenBy(e => e.Field("Cost") is null ? 0 : 1)
                .ThenBy(e => (e.Field("Conditions") ?? "").Length)
                .ThenBy(e => e.Name, StringComparer.Ordinal).First();
            var unlock = $"UnlockInterrupt({best.Name})";
            var provoke = Provocation(context);
            steps.Add(new SweepStep
            {
                Id = $"interrupt:{context}",
                What = $"interrupt context {context} through {best.Name}: unlocked on a dummy (AddBoosts {unlock}), then provoked by a hostile dummy",
                Carrier = new Carrier("direct", unlock, "InterruptContext", best.Shipping),
                ReachedBy = "cheat",
                How = $"Osi.AddBoosts(D, {L(unlock)}, {L(PlanBuilder.Cause)}, D)\n" + (provoke ?? ""),
                Undo = $"pcall(Osi.RemoveBoosts, D, {L(unlock)}, 0, {L(PlanBuilder.Cause)}, D)\npcall(GK.Stage.EndCombat, D)",
                Blocked = provoke is null ? $"no provocation is known for {context}" : null,
                Settle = 8.0,
                Fresh = true,
            });
        }
        return new SweepPlan
        {
            Kind = "interrupts",
            Data = data,
            Counts = new JsonObject { ["interrupts_in_data"] = stats.Entries.Count(e => e.Type == "InterruptData"), ["contexts"] = byContext.Count, ["steps"] = steps.Count },
            Steps = steps,
        };
    }

    // -------------------------------------------------------------------------------------
    // items: every boost and passive context an item carries, equipped once

    public static SweepPlan Items(StatsData stats, IReadOnlyCollection<string> componentTypes, string? data)
    {
        var found = new Dictionary<string, List<(StatEntry Item, int Weight)>>(StringComparer.Ordinal);
        void Add(string key, StatEntry item, int weight)
        {
            if (!found.TryGetValue(key, out var list)) found[key] = list = [];
            list.Add((item, weight));
        }
        foreach (var item in stats.Entries.Where(e => PlanBuilder.CarrierKind(e) == "item"))
        {
            var weight = 0;
            foreach (var field in StatsText.BoostFields) weight += StatsText.Calls(item.Field(field)).Count;
            var passives = StatsText.Flags(item.Field("PassivesOnEquip"));
            weight += passives.Count;
            foreach (var field in StatsText.BoostFields)
                foreach (var call in StatsText.Calls(item.Field(field)))
                    if (!call.Conditional) Add("boost:" + call.Name, item, weight);
            foreach (var name in passives)
            {
                if (stats.Get(name) is not { } passive) continue;
                foreach (var call in StatsText.Calls(passive.Field("Boosts")))
                    if (!call.Conditional) Add("boost:" + call.Name, item, weight);
                foreach (var context in StatsText.Flags(passive.Field("StatsFunctorContext")))
                    if (context is not ("" or "None")) Add("context:" + context, item, weight);
            }
            if (item.Field("StatusOnEquip") is not null) Add("status-on-equip", item, weight);
        }
        var stems = PlanBuilder.BoostStems(componentTypes);
        var names = found.Keys.Where(k => k.StartsWith("boost:", StringComparison.Ordinal)).Select(k => k[6..]).ToHashSet(StringComparer.Ordinal);
        var steps = new List<SweepStep>();
        foreach (var key in found.Keys.Order(StringComparer.Ordinal))
        {
            var best = found[key].OrderBy(c => c.Item.Shipping ? 0 : 1).ThenBy(c => c.Weight)
                .ThenBy(c => c.Item.Name, StringComparer.Ordinal).First().Item;
            var component = key.StartsWith("boost:", StringComparison.Ordinal) ? PlanBuilder.BoostComponentOf(key[6..], stems, names) : null;
            steps.Add(PlanBuilder.CarrierStepOf($"item:{key}", $"{key.Replace(':', ' ')} carried by item {best.Name}, created and equipped on a dummy",
                best, key.StartsWith("context:", StringComparison.Ordinal) ? "PassivesOnEquip" : "DefaultBoosts", component is null ? null : [component])
                with { Check = "return (SweepItem ~= nil and Osi.IsEquipped(SweepItem) == 1) or 'the item is not equipped'", Fresh = true });
        }
        return new SweepPlan
        {
            Kind = "items",
            Data = data,
            Counts = new JsonObject
            {
                ["items_with_boosts_or_passives"] = found.Values.SelectMany(l => l.Select(c => c.Item.Name)).Distinct(StringComparer.Ordinal).Count(),
                ["distinct_boosts_and_contexts"] = found.Count,
                ["steps"] = steps.Count,
            },
            Steps = steps,
        };
    }

    // -------------------------------------------------------------------------------------
    // summon and spawn functors

    static readonly string[] SummonFunctors = ["Summon", "Spawn", "SummonInInventory", "SpawnInInventory"];

    /// <summary>What tells one way of summoning from another, not one creature from another: the
    /// functor, its arity, the lifetime (Permanent, UntilLongRest, turns), whether an AI-helper
    /// projectile places it, whether it stacks, and whether statuses come with it.</summary>
    static string Shape(StatsCall call)
    {
        var args = StatsText.SplitTop(call.Arguments, ',').Select(a => a.Trim()).ToList();
        var rest = args.Skip(1).ToList();
        var lifetime = rest.FirstOrDefault(a => a is "Permanent" or "UntilLongRest") ?? (rest.Any(a => double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) ? "turns" : "-");
        var helper = rest.Any(a => a.StartsWith("Projectile_AiHelper", StringComparison.Ordinal)) ? "+helper" : "";
        var stack = rest.Any(a => a.EndsWith("Stack", StringComparison.Ordinal)) ? "+stack" : "";
        var statuses = rest.Any(a => a.Length > 0 && a.All(c => char.IsAsciiLetterUpper(c) || c == '_' || char.IsAsciiDigit(c)) && a.Any(char.IsAsciiLetter)) ? "+statuses" : "";
        var flags = rest.Any(a => a is "true" or "false") ? "+flags" : "";
        return $"{call.Name}/{args.Count}/{lifetime}{helper}{stack}{statuses}{flags}";
    }

    public static SweepPlan Summons(StatsData stats, string? data)
    {
        var found = new Dictionary<string, List<(StatEntry Entry, string Field, StatsCall Call)>>(StringComparer.Ordinal);
        foreach (var entry in stats.Entries)
        {
            if (PlanBuilder.CarrierKind(entry) is not ("status" or "spell")) continue;
            foreach (var field in StatsText.FunctorFields)
                foreach (var call in StatsText.Calls(entry.Field(field)))
                {
                    if (!SummonFunctors.Contains(call.Name)) continue;
                    var key = Shape(call);
                    if (!found.TryGetValue(key, out var list)) found[key] = list = [];
                    list.Add((entry, field, call));
                }
        }
        var steps = new List<SweepStep>();
        foreach (var key in found.Keys.Order(StringComparer.Ordinal))
        {
            var best = found[key].OrderBy(c => c.Call.Conditional ? 1 : 0).ThenBy(c => c.Entry.Shipping ? 0 : 1)
                .ThenBy(c => c.Entry.Type == "StatusData" ? 0 : 1)
                .ThenBy(c => c.Entry.Field("SpellType") switch { "Shout" => 0, "Target" => 1, _ => 2 })
                .ThenBy(c => c.Entry.Name, StringComparer.Ordinal).First();
            steps.Add(PlanBuilder.CarrierStepOf($"summon:{key}", $"{best.Call.Name} ({PlanBuilder.CutText(best.Call.Text, 80)}) through {best.Entry.Name}.{best.Field}",
                best.Entry, best.Field, null) with { Settle = 6.0, Fresh = true });
        }
        return new SweepPlan
        {
            Kind = "summons",
            Data = data,
            Counts = new JsonObject { ["summon_calls_in_data"] = found.Values.Sum(l => l.Count), ["distinct_shapes"] = found.Count, ["steps"] = steps.Count },
            Steps = steps,
        };
    }
}
