using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Leads;

namespace GateKit.Sweep;

/// <summary>
/// The `leads` plan: the open leads (research/coverage/leads.jsonl) whose instrument a sweep can
/// play - sweep, osiris, stage - and whose `how` is concrete enough to run without a model: give a
/// character a status or a passive, add a boost no data carries (its common argument shapes, one
/// call each), or an Osiris call matched by name, with arguments known to be safe. Every step is a
/// cheat and says so. When the run ends each lead is reported back: lit, empty or blocked.
/// </summary>
public static partial class SweepLeads
{
    public static readonly IReadOnlyList<string> Instruments = ["sweep", "osiris", "stage"];

    [GeneratedRegex(@"^(?:Give a character|Use) (\S+) \(a (status|passive)")]
    private static partial Regex Carried();

    [GeneratedRegex(@"carries the boost (\w+)\(\)")]
    private static partial Regex Uncarried();

    [GeneratedRegex(@"^(?:Try|Call) Osi\.(\w+)")]
    private static partial Regex OsirisCall();

    /// <summary>The argument shapes tried for a boost nothing in the data carries: each its own
    /// AddBoosts, so a shape the parser refuses does not take the others with it.</summary>
    public static readonly IReadOnlyList<string> BoostShapes =
    [
        "()", "(1)", "(10)", "(true)", "(Strength)", "(Strength,1)", "(Attack,10)", "(Attack,1)", "(All,10)", "(Fire)", "(Tiny)",
        "(Huge)", "(1,1)", "(Attack,Success)", "(SavingThrow,Critical)", "(MeleeWeaponAttack)", "(Strength,Attack)", "(Piercing)",
    ];

    /// <summary>
    /// The Osiris calls the leads name by matching alone, with the arguments a sweep can supply
    /// without breaking the copy: a character (the dummy, or the host where the story passes the
    /// player) wherever the signature allows one. SetTeleportTargetOverride from item to item
    /// crashed a copy once (2026-11-13): its source is an item the step creates, its target the
    /// dummy, and the watchdog stands by. Arguments the story uses (a material preset, a movie)
    /// are taken from it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string How, string? Undo, double Settle)> OsirisCalls =
        new Dictionary<string, (string, string?, double)>(StringComparer.Ordinal)
        {
            ["SetStoryShortRestDisabled"] = ("Osi.SetStoryShortRestDisabled(H, \"GateKitSweep\")", null, 2),
            ["RequestLongRest"] = ("Osi.RequestLongRest(H, 0)", null, 6),
            ["RequestAutoLevel"] = ("Osi.RequestAutoLevel(D)", null, 3),
            ["EndRepose"] = ("Osi.EndRepose(D)", null, 2),
            ["SendToCampChest"] = ("Osi.TemplateAddTo(\"" + Potion + "\", D, 1, 0)\n"
                + "Ext.Timer.WaitFor(600, function() local f = GK.Find(\"" + Potion + "\", D) if f and f[1] then Osi.SendToCampChest(f[1], H) end end)", null, 3),
            ["TriggerMovieEndFadeIn"] = ("Osi.TriggerMovieEndFadeIn(H, \"\")", null, 3),
            ["SetCharacterOnPortraitPainting"] = (CreateItem + "\nExt.Timer.WaitFor(600, function() if SweepItem then Osi.SetCharacterOnPortraitPainting(D, SweepItem) end end)",
                DeleteItem, 3),
            ["AddCustomMaterialOverride"] = ("Osi.AddCustomMaterialOverride(D, \"dc46662a-d909-4df5-ad12-50a524150aff\")",
                "pcall(Osi.RemoveCustomMaterialOverride, D, \"dc46662a-d909-4df5-ad12-50a524150aff\")", 2),
            ["SetTeleportTargetOverride"] = (CreateItem + "\nExt.Timer.WaitFor(600, function() if SweepItem then Osi.SetTeleportTargetOverride(SweepItem, D) end end)",
                DeleteItem, 3),
        };

    /// <summary>A potion of healing, the most ordinary item there is.</summary>
    const string Potion = "d47006e9-8a51-453d-b200-9e0d42e9bbab";

    const string CreateItem = "SweepItem = GK.Stage.Spawn{ template = \"potion\", near = D, distance = 1 }[1]";

    const string DeleteItem = "if SweepItem then pcall(Osi.RequestDelete, SweepItem); SweepItem = nil end";

    static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static SweepPlan Build(IReadOnlyList<JsonObject> leads, StatsData stats, string? data)
    {
        var open = leads.Where(r => S(r["state"]) == "open" && Instruments.Contains(S(r["instrument"])))
            .OrderBy(r => S(r["type"]), StringComparer.Ordinal).ToList();
        // one step per distinct way: three teleport leads share one Osiris call
        var byWay = new Dictionary<string, (SweepStep Step, List<string> Types)>(StringComparer.Ordinal);
        var order = new List<string>();
        var vague = 0;
        foreach (var row in open)
        {
            var type = S(row["type"]);
            var how = S(row["how"]);
            var instrument = S(row["instrument"]);
            SweepStep? step = null;
            string key;
            if (Carried().Match(how) is { Success: true } carried && stats.Get(carried.Groups[1].Value) is { } entry
                && PlanBuilder.CarrierKind(entry) is "status" or "passive")
            {
                key = "entry:" + entry.Name;
                var tick = how.Contains("TickFunctors", StringComparison.Ordinal);
                step = PlanBuilder.CarrierStepOf($"lead:{entry.Name}", $"lead {type}: {how}", entry, tick ? "TickFunctors" : "Boosts", [type])
                    with { Settle = tick ? 7.0 : 1.5 };
            }
            else if (Uncarried().Match(how) is { Success: true } boost)
            {
                var name = boost.Groups[1].Value;
                key = "boost:" + name;
                var forms = BoostShapes.Select(shape => name + shape).ToList();
                step = new SweepStep
                {
                    Id = $"lead-boost:{name}", What = $"lead {type}: AddBoosts {name} in {forms.Count} argument shapes, one call each",
                    Carrier = new Carrier("direct", name, Shipping: false), ReachedBy = "cheat", Expects = [type],
                    How = string.Join("\n", forms.Select(f => $"pcall(Osi.AddBoosts, D, {PlanBuilder.Lua(f)}, {PlanBuilder.Lua(PlanBuilder.Cause)}, D)")),
                    Undo = string.Join("\n", forms.Select(f => $"pcall(Osi.RemoveBoosts, D, {PlanBuilder.Lua(f)}, 0, {PlanBuilder.Lua(PlanBuilder.Cause)}, D)")),
                    Fresh = true,
                };
            }
            else if (OsirisCall().Match(how) is { Success: true } call)
            {
                var name = call.Groups[1].Value;
                key = "osi:" + name;
                step = OsirisCalls.TryGetValue(name, out var known)
                    ? new SweepStep
                    {
                        Id = $"lead-osi:{name}", What = $"lead {type}: Osi.{name} with sane arguments (matched by name only)",
                        Carrier = new Carrier("osiris", name, Shipping: false), ReachedBy = "cheat", How = known.How, Undo = known.Undo,
                        Settle = known.Settle, Fresh = true,
                    }
                    : new SweepStep
                    {
                        Id = $"lead-osi:{name}", What = $"lead {type}: Osi.{name}", Carrier = new Carrier("osiris", name, Shipping: false),
                        ReachedBy = "cheat", How = "", Blocked = $"no safe arguments are known for Osi.{name}",
                    };
            }
            else
            {
                vague++;
                continue;
            }
            if (byWay.TryGetValue(key, out var had)) had.Types.Add(type);
            else
            {
                byWay[key] = (step with { Instrument = instrument }, [type]);
                order.Add(key);
            }
        }
        var steps = order.Select(k => byWay[k].Step with { Leads = byWay[k].Types, Expects = byWay[k].Step.Expects is null ? null : byWay[k].Types }).ToList();
        return new SweepPlan
        {
            Kind = "leads",
            Data = data,
            Counts = new JsonObject
            {
                ["open_leads_of_sweep_osiris_stage"] = open.Count,
                ["leads_with_a_concrete_how"] = open.Count - vague,
                ["leads_too_vague_to_run"] = vague,
                ["steps"] = steps.Count,
                ["steps_blocked"] = steps.Count(s => s.Blocked is not null),
            },
            Steps = steps,
        };
    }

    /// <summary>Every lead of the plan's steps that ran, reported back once: lit when the type has
    /// been seen alive since, blocked when its step could not run, empty when it ran and nothing came.</summary>
    public static void Report(string coverageRoot, SweepPlan plan, SweepState state, string? instance, TextWriter output)
    {
        var seen = Coverage.Seen(coverageRoot);
        foreach (var step in plan.Steps)
        {
            if (step.Leads is not { Count: > 0 } types) continue;
            var outcome = state.Steps.FirstOrDefault(s => s.Id == step.Id);
            if (outcome is null) continue;
            foreach (var type in types)
            {
                if (state.Reported.Contains(type)) continue;
                var (verdict, note) = seen.Contains(type)
                    ? ("lit", $"sweep step {step.Id} ({step.What}) made it appear; a cheat.")
                    : outcome.Blocked || state.Skipped.Contains(step.Id) || (!outcome.Ok && outcome.Error is not null && outcome.New.Count == 0 && outcome.Hang)
                        ? ("blocked", $"sweep step {step.Id} could not be tried: {Cut(outcome.Error ?? "hung")}")
                        : ("empty", $"sweep step {step.Id} ({step.What}) ran{(outcome.Ok ? "" : " with an error: " + Cut(outcome.Error ?? ""))}; the type did not appear.");
                try
                {
                    Lead.Report(coverageRoot, type, verdict, note, instance, step.Instrument, state.Run);
                    state.Reported.Add(type);
                    output.WriteLine($"lead {type}: {verdict}");
                }
                catch (LeadsException e) { output.WriteLine($"lead {type}: not reported ({e.Message})"); }
            }
        }
    }

    static string Cut(string text) => text.Length <= 160 ? text.Split('\n')[0] : text[..160].Split('\n')[0] + "...";
}
