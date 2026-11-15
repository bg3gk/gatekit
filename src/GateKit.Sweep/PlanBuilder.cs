using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Sweep;

/// <summary>
/// Builds sweep plans from the game's own data: for every thing of a kind the stats name, the
/// cheapest entry that carries it into the game, and a step that applies that entry to a dummy.
/// Deterministic: the same index and the same component list give the same plan, byte for byte.
/// </summary>
public static partial class PlanBuilder
{
    public static readonly IReadOnlyList<string> Kinds =
        ["boosts", "statuses", "functors", "spellflags", "leads", "statustypes", "surfaces", "contexts", "spelltypes", "interrupts", "items", "summons"];

    /// <summary>The cause name the sweep's direct boosts carry, so they can be told apart and removed.</summary>
    public const string Cause = "GateKitSweep";

    public static SweepPlan Build(string kind, StatsData stats, IReadOnlyCollection<string> componentTypes, string? data = null, PlanInputs? inputs = null) => kind switch
    {
        "boosts" => Boosts(stats, componentTypes, data),
        "statuses" => Statuses(stats, data),
        "functors" => Functors(stats, data),
        "spellflags" => SpellFlags(stats, data),
        "leads" => SweepLeads.Build(inputs?.Leads ?? [], stats, data),
        "statustypes" => MorePlans.StatusTypes(stats, inputs, data),
        "surfaces" => MorePlans.Surfaces(inputs, data),
        "contexts" => MorePlans.Contexts(stats, data),
        "spelltypes" => MorePlans.SpellTypes(stats, data),
        "interrupts" => MorePlans.Interrupts(stats, data),
        "items" => MorePlans.Items(stats, componentTypes, data),
        "summons" => MorePlans.Summons(stats, data),
        _ => throw new SweepException($"no plan kind `{kind}`; the kinds are {string.Join(", ", Kinds)}"),
    };

    // -------------------------------------------------------------------------------------
    // boosts

    [GeneratedRegex(@"^eoc::([A-Za-z0-9]+)BoostComponent$")]
    private static partial Regex BoostComponent();

    /// <summary>
    /// The component a boost of this name is stored in, read off the registered type names:
    /// <c>eoc::&lt;Name&gt;BoostComponent</c> when there is one; else the one component whose stem
    /// begins or ends with the name (or that the name begins with) on a word boundary and which
    /// is not itself another boost's name - <c>ActionResource</c> lands on
    /// <c>ActionResourceValueBoostComponent</c>, <c>IgnoreDamageThreshold</c> on
    /// <c>IgnoreDamageThresholdMinBoostComponent</c>. Null when nothing or more than one fits.
    /// </summary>
    public static string? BoostComponentOf(string boost, IReadOnlyDictionary<string, string> stems, IReadOnlySet<string> boostNames)
    {
        if (stems.TryGetValue(boost, out var exact)) return exact;
        bool Boundary(string text, int at) => at <= 0 || at >= text.Length || char.IsAsciiLetterUpper(text[at]);
        var fits = stems.Where(pair => !boostNames.Contains(pair.Key) && (
                (pair.Key.StartsWith(boost, StringComparison.Ordinal) && Boundary(pair.Key, boost.Length))
                || (pair.Key.EndsWith(boost, StringComparison.Ordinal) && Boundary(pair.Key, pair.Key.Length - boost.Length))
                || (boost.StartsWith(pair.Key, StringComparison.Ordinal) && Boundary(boost, pair.Key.Length))))
            .Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToList();
        return fits.Count == 1 ? fits[0] : null;
    }

    /// <summary>Stem -> component, for every <c>eoc::*BoostComponent</c> of the registered types.</summary>
    public static Dictionary<string, string> BoostStems(IEnumerable<string> componentTypes)
    {
        var stems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in componentTypes)
            if (BoostComponent().Match(name) is { Success: true } match) stems[match.Groups[1].Value] = name;
        return stems;
    }

    sealed record BoostCandidate(StatEntry Entry, string Field, StatsCall Call, int Parts);

    static SweepPlan Boosts(StatsData stats, IReadOnlyCollection<string> componentTypes, string? data)
    {
        var found = new Dictionary<string, List<BoostCandidate>>(StringComparer.Ordinal);
        foreach (var entry in stats.Entries)
        {
            if (CarrierKind(entry) is null) continue;
            foreach (var field in StatsText.BoostFields)
            {
                if (entry.Field(field) is not { } text) continue;
                var calls = StatsText.Calls(text);
                foreach (var call in calls)
                {
                    if (!found.TryGetValue(call.Name, out var list)) found[call.Name] = list = [];
                    list.Add(new BoostCandidate(entry, field, call, calls.Count));
                }
            }
        }
        var stems = BoostStems(componentTypes);
        var names = found.Keys.ToHashSet(StringComparer.Ordinal);
        var steps = new List<SweepStep>();
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in found.Keys.Order(StringComparer.Ordinal))
        {
            var best = found[name].OrderBy(c => c.Call.Conditional ? 1 : 0)
                .ThenBy(c => KindRank(CarrierKind(c.Entry)!))
                .ThenBy(c => c.Entry.Shipping ? 0 : 1)
                .ThenBy(c => SideEffects(c.Entry))
                .ThenBy(c => c.Parts)
                .ThenBy(c => c.Entry.Name, StringComparer.Ordinal)
                .First();
            var component = BoostComponentOf(name, stems, names);
            if (component is not null) mapped.Add(component);
            var plain = found[name].Where(c => !c.Call.Conditional).OrderBy(c => c.Entry.Shipping ? 0 : 1)
                .ThenBy(c => c.Call.Text.Length).ThenBy(c => c.Call.Text, StringComparer.Ordinal).FirstOrDefault() ?? best;
            var step = CarrierStep($"boost:{name}", $"boost {name} ({best.Call.Text}) through {CarrierKind(best.Entry)} {best.Entry.Name}",
                best.Entry, best.Field, component is null ? null : [component]);
            steps.Add(step with { Fallback = new Fallback(new Carrier("direct", plain.Call.Text), DirectHow(plain.Call.Text), DirectUndo(plain.Call.Text), "cheat") });
        }
        // The boost components no shipping entry names: only a direct AddBoosts can reach them,
        // and the arguments are not known, so the common shapes are tried in one step.
        var unnamed = stems.Where(pair => !mapped.Contains(pair.Value)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        foreach (var (stem, component) in unnamed)
        {
            var forms = new[] { $"{stem}()", $"{stem}(1)", $"{stem}(true)", $"{stem}(Strength)", $"{stem}(1,1)" };
            var text = string.Join(";", forms);
            steps.Add(new SweepStep
            {
                Id = $"boost-direct:{stem}",
                What = $"{component}: no shipping entry names a boost for it; AddBoosts tries {string.Join(", ", forms)}",
                Carrier = new Carrier("direct", text, Shipping: false),
                How = string.Join("\n", forms.Select(DirectHow)),
                Undo = string.Join("\n", forms.Select(DirectUndo)),
                ReachedBy = "cheat",
                Expects = [component],
            });
        }
        return new SweepPlan
        {
            Kind = "boosts",
            Data = data,
            Counts = new JsonObject
            {
                ["boost_names_in_data"] = found.Count,
                ["boost_components_registered"] = stems.Count,
                ["boost_components_named_by_data"] = mapped.Count,
                ["boost_components_direct_only"] = unnamed.Count,
                ["steps"] = steps.Count,
            },
            Steps = steps,
        };
    }

    static string DirectHow(string text) =>
        $"Osi.AddBoosts(D, {Lua(text)}, {Lua(Cause)}, D)";

    static string DirectUndo(string text) =>
        $"pcall(Osi.RemoveBoosts, D, {Lua(text)}, 0, {Lua(Cause)}, D)";

    // -------------------------------------------------------------------------------------
    // statuses

    static SweepPlan Statuses(StatsData stats, string? data)
    {
        var statuses = stats.Entries.Where(e => e.Type == "StatusData").ToList();
        var byType = statuses.Where(e => e.Field("StatusType") is not null).GroupBy(e => e.Field("StatusType")!, StringComparer.Ordinal);
        var steps = new List<SweepStep>();
        foreach (var group in byType.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var best = CheapestStatus(group);
            steps.Add(CarrierStep($"status-type:{group.Key}", $"status type {group.Key} through {best.Name}", best, "StatusType", null));
        }
        var byFlag = new Dictionary<string, List<StatEntry>>(StringComparer.Ordinal);
        foreach (var status in statuses)
            foreach (var flag in StatsText.Flags(status.Field("StatusPropertyFlags")))
            {
                if (!byFlag.TryGetValue(flag, out var list)) byFlag[flag] = list = [];
                list.Add(status);
            }
        foreach (var (flag, list) in byFlag.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var best = CheapestStatus(list);
            steps.Add(CarrierStep($"status-flag:{flag}", $"status property flag {flag} through {best.Name} ({best.Field("StatusType")})",
                best, "StatusPropertyFlags", null));
        }
        return new SweepPlan
        {
            Kind = "statuses",
            Data = data,
            Counts = new JsonObject { ["status_types"] = byType.Count(), ["status_property_flags"] = byFlag.Count, ["steps"] = steps.Count },
            Steps = steps,
        };
    }

    static StatEntry CheapestStatus(IEnumerable<StatEntry> statuses) => statuses
        .OrderBy(e => e.Shipping ? 0 : 1)
        .ThenBy(e => e.Field("StatusType") == "BOOST" ? 0 : 1)
        .ThenBy(SideEffects)
        .ThenBy(e => StatsText.Calls(e.Field("Boosts")).Count)
        .ThenBy(e => e.Fields.Count)
        .ThenBy(e => e.Name, StringComparer.Ordinal)
        .First();

    // -------------------------------------------------------------------------------------
    // functors

    sealed record FunctorCandidate(StatEntry Entry, string Field, StatsCall Call, int Parts);

    /// <summary>The fields whose functors run when the entry is applied or cast by the sweep.</summary>
    static int FunctorFieldRank(StatEntry entry, string field) => (entry.Type, field) switch
    {
        ("StatusData", "OnApplyFunctors") => 0,
        ("SpellData", "SpellSuccess") => 1,
        ("SpellData", "SpellProperties") => 1,
        ("StatusData", "TickFunctors") => 2,
        ("StatusData", "OnRemoveFunctors") => 2,
        ("SpellData", _) => 3,
        ("StatusData", _) => 3,
        _ => 4,
    };

    static SweepPlan Functors(StatsData stats, string? data)
    {
        var found = new Dictionary<string, List<FunctorCandidate>>(StringComparer.Ordinal);
        foreach (var entry in stats.Entries)
        {
            if (CarrierKind(entry) is not ("status" or "spell" or "passive")) continue;
            foreach (var field in StatsText.FunctorFields)
            {
                if (entry.Field(field) is not { } text) continue;
                var calls = StatsText.Calls(text);
                foreach (var call in calls)
                {
                    if (!found.TryGetValue(call.Name, out var list)) found[call.Name] = list = [];
                    list.Add(new FunctorCandidate(entry, field, call, calls.Count));
                }
            }
        }
        var steps = new List<SweepStep>();
        foreach (var name in found.Keys.Order(StringComparer.Ordinal))
        {
            var best = found[name]
                .OrderBy(c => c.Call.Conditional || c.Call.Prefixes.Contains("AI_ONLY") ? 1 : 0)
                .ThenBy(c => FunctorFieldRank(c.Entry, c.Field))
                .ThenBy(c => c.Entry.Shipping ? 0 : 1)
                .ThenBy(c => SpellTypeRank(c.Entry))
                .ThenBy(c => c.Parts)
                .ThenBy(c => c.Entry.Name, StringComparer.Ordinal)
                .First();
            steps.Add(CarrierStep($"functor:{name}", $"functor {name} ({Cut(best.Call.Text, 80)}) through {CarrierKind(best.Entry)} {best.Entry.Name}.{best.Field}",
                best.Entry, best.Field, null));
        }
        return new SweepPlan
        {
            Kind = "functors",
            Data = data,
            Counts = new JsonObject { ["functor_names_in_data"] = found.Count, ["steps"] = steps.Count },
            Steps = steps,
        };
    }

    // -------------------------------------------------------------------------------------
    // spell flags

    static SweepPlan SpellFlags(StatsData stats, string? data)
    {
        var byFlag = new Dictionary<string, List<StatEntry>>(StringComparer.Ordinal);
        foreach (var spell in stats.Entries.Where(e => e.Type == "SpellData"))
            foreach (var flag in StatsText.Flags(spell.Field("SpellFlags")))
            {
                if (!byFlag.TryGetValue(flag, out var list)) byFlag[flag] = list = [];
                list.Add(spell);
            }
        var steps = new List<SweepStep>();
        foreach (var (flag, list) in byFlag.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var best = list.OrderBy(e => e.Shipping ? 0 : 1).ThenBy(SpellTypeRank)
                .ThenBy(e => StatsText.FunctorFields.Sum(f => StatsText.Calls(e.Field(f)).Count))
                .ThenBy(e => e.Name, StringComparer.Ordinal).First();
            steps.Add(CarrierStep($"spell-flag:{flag}", $"spell flag {flag} through {best.Name} ({best.Field("SpellType")})", best, "SpellFlags", null));
        }
        return new SweepPlan
        {
            Kind = "spellflags",
            Data = data,
            Counts = new JsonObject { ["spell_flags"] = byFlag.Count, ["steps"] = steps.Count },
            Steps = steps,
        };
    }

    // -------------------------------------------------------------------------------------
    // carriers

    /// <summary>How an entry is brought into the game, or null when the sweep cannot carry it.</summary>
    public static string? CarrierKind(StatEntry entry) => entry.Type switch
    {
        "StatusData" => "status",
        "PassiveData" => "passive",
        "SpellData" => "spell",
        "Armor" or "Weapon" when entry.Field("RootTemplate") is { } root && root.Length >= 36 => "item",
        _ => null,
    };

    static int KindRank(string kind) => kind switch { "passive" => 0, "status" => 1, "item" => 2, "spell" => 3, _ => 4 };

    /// <summary>What applying an entry sets off besides the thing wanted: functors that run, a
    /// status that is not a plain boost.</summary>
    static int SideEffects(StatEntry entry)
    {
        var effects = 0;
        foreach (var field in new[] { "OnApplyFunctors", "TickFunctors", "OnRemoveFunctors", "StatsFunctors" })
            if (entry.Field(field) is not null) effects++;
        if (entry.Type == "StatusData" && entry.Field("StatusType") is { } type && type != "BOOST") effects++;
        return effects;
    }

    /// <summary>Spells cast at a character are cheapest; those that move the caster or need a point last.</summary>
    static int SpellTypeRank(StatEntry entry) => entry.Type != "SpellData" ? 0 : entry.Field("SpellType") switch
    {
        "Target" => 0,
        "Shout" => 1,
        "Projectile" => 2,
        "Zone" or "Cone" => 3,
        "Throw" or "MultiStrike" or "ProjectileStrike" => 4,
        _ => 5,
    };

    internal static SweepStep CarrierStepOf(string id, string what, StatEntry entry, string field, IReadOnlyList<string>? expects) =>
        CarrierStep(id, what, entry, field, expects);

    internal static string CutText(string text, int length) => Cut(text, length);

    static SweepStep CarrierStep(string id, string what, StatEntry entry, string field, IReadOnlyList<string>? expects)
    {
        var kind = CarrierKind(entry) ?? throw new SweepException($"{entry.Name} ({entry.Type}) cannot be carried");
        var name = Lua(entry.Name);
        var carrier = new Carrier(kind, entry.Name, field, entry.Shipping);
        switch (kind)
        {
            case "status":
                return new SweepStep
                {
                    Id = id, What = what, Carrier = carrier, Expects = expects, ReachedBy = "cheat",
                    How = $"Osi.ApplyStatus(D, {name}, -1.0, 1, D)",
                    Undo = $"pcall(Osi.RemoveStatus, D, {name})",
                    Settle = 1.0,
                    Fresh = SideEffects(entry) > 0,
                };
            case "passive":
                return new SweepStep
                {
                    Id = id, What = what, Carrier = carrier, Expects = expects, ReachedBy = "cheat",
                    How = $"Osi.AddPassive(D, {name})",
                    Undo = $"pcall(Osi.RemovePassive, D, {name})",
                    Settle = 1.0,
                    Fresh = entry.Field("StatsFunctors") is not null,
                };
            case "item":
                var root = Lua(entry.Field("RootTemplate")!);
                return new SweepStep
                {
                    Id = id, What = what, Carrier = carrier, Expects = expects, ReachedBy = "cheat",
                    How = $"Osi.TemplateAddTo({root}, D, 1, 0)\n"
                        + $"Ext.Timer.WaitFor(600, function()\n"
                        + $"    local found = GK.Find({root}, D)\n"
                        + $"    if found and found[1] then SweepItem = found[1]; Osi.Equip(D, found[1], 1, 0, 0) end\n"
                        + "end)",
                    Undo = "if SweepItem then pcall(Osi.Unequip, D, SweepItem); pcall(Osi.RequestDelete, SweepItem); SweepItem = nil end",
                    Settle = 2.0,
                };
            default:
                var self = entry.Field("SpellType") == "Shout";
                return new SweepStep
                {
                    Id = id, What = what, Carrier = carrier, Expects = expects, ReachedBy = "cheat",
                    How = $"GK.Stage.Teach({name}, D)\nreturn GK.Stage.Cast({name}, {(self ? "D" : "T")}, D)",
                    Undo = $"pcall(Osi.RemoveSpell, D, {name}, 0)",
                    Settle = 5.0,
                    Fresh = true,
                };
        }
    }

    /// <summary>A Lua string literal.</summary>
    internal static string Lua(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal) + "\"";

    static string Cut(string text, int length) => text.Length <= length ? text : text[..length] + "...";

    internal static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
