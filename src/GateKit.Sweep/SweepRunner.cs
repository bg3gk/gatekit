using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Copies;

namespace GateKit.Sweep;

/// <summary>How a run is driven (docs/contracts/cli.md, 8.13).</summary>
public sealed record SweepOptions
{
    /// <summary>The first step (0-based index into the plan); null resumes where the state file stopped.</summary>
    public int? From { get; init; }
    public int? Limit { get; init; }
    /// <summary>The game's time scale while the sweep runs (GK.Speed.Set); settle times shrink with it.</summary>
    public double Speed { get; init; } = 1.0;
    /// <summary>The run's id in the record (sightings carry it).</summary>
    public string Run { get; init; } = "sweep";
    /// <summary>Drain the capture after this many steps (the drain is what verifies layouts).</summary>
    public int DrainEvery { get; init; } = 5;
    /// <summary>Fresh dummies after this many steps even when no step asked for them.</summary>
    public int FreshEvery { get; init; } = 10;
    /// <summary>Return the world to its checkpoint after this many steps (0: only after a failure that needs it).</summary>
    public int ResetEvery { get; init; }
    /// <summary>Seconds the game may spend on one step's calls before the watchdog calls it a hang;
    /// the runner's own waits (settling, a dummy's arrival) do not count.</summary>
    public double StepTimeout { get; init; } = 45;
    /// <summary>Write and replay recipes for the never-seen types the steps lit.</summary>
    public bool Recipes { get; init; } = true;
    /// <summary>Stop after this much wall-clock time (the step in progress finishes).</summary>
    public TimeSpan? Budget { get; init; }
    /// <summary>Report each step's leads back (`leads report`) when the run ends.</summary>
    public bool ReportLeads { get; init; } = true;
    /// <summary>The game copy, as the leads' reports name it.</summary>
    public string? Instance { get; init; }
}

/// <summary>
/// Runs a plan step after step with the standing capture armed, no model in the loop: for each
/// step a label, a dummy in a clean state, the step's Lua, a few ticks, and the question what came
/// alive. Each step's outcome goes into the state file the moment it is known, so a run that is
/// cut short resumes from the next step. A step the game does not survive (or does not answer)
/// is retried once after the copy is brought back; a second hang skips it for good.
/// </summary>
public sealed class SweepRunner(ISweepGame game, TextWriter output, SweepTime? time = null)
{
    readonly SweepTime _time = time ?? SweepTime.Real;

    public const string Role = "sweep";

    /// <summary>The dummies a step works on. They are temporary characters (GK.Stage.Spawn), so
    /// removing them deletes them; a dead or vanished dummy is replaced.</summary>
    internal const string DummiesLua = """
        local fresh, needTarget = ...
        local function usable(id) return id ~= nil and Ext.Entity.Get(id) ~= nil and Osi.IsDead(id) ~= 1 end
        if fresh then
            for _, id in ipairs({ SweepDummy, SweepTarget }) do if id then pcall(GK.Stage.Remove, id) end end
            SweepDummy, SweepTarget = nil, nil
        end
        if not usable(SweepDummy) then SweepDummy = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 5 }[1] end
        if needTarget and not usable(SweepTarget) then SweepTarget = GK.Stage.Spawn{ template = "human", faction = "neutral", distance = 8 }[1] end
        return { D = SweepDummy, T = SweepTarget, ready = GK.Stage.Ready({ SweepDummy, SweepTarget }) }
        """;

    static string Call(string chunk, params string[] arguments) =>
        $"return (function(...)\n{chunk}\nend)({string.Join(", ", arguments)})";

    public async Task<SweepState> RunAsync(SweepPlan plan, string planPath, string statePath, SweepOptions options, CancellationToken cancel)
    {
        var state = SweepState.Load(statePath, plan, planPath, options.Run);
        var from = options.From ?? state.Next;
        var end = options.Limit is { } limit ? Math.Min(plan.Steps.Count, from + limit) : plan.Steps.Count;
        output.WriteLine($"sweep {plan.Kind}: steps {from}..{end - 1} of {plan.Steps.Count}, speed {options.Speed.ToString(CultureInfo.InvariantCulture)}, state {statePath}");
        var started = _time.Clock.GetTimestamp();
        await game.EnsureUpAsync(cancel);
        await ArmAsync(plan, options, state, checkpoint: true, cancel);
        var sinceDrain = 0;
        var sinceFresh = 0;
        var sinceReset = 0;
        var fresh = true;
        for (var index = from; index < end; index++)
        {
            cancel.ThrowIfCancellationRequested();
            if (options.Budget is { } budget && _time.Clock.GetElapsedTime(started) > budget)
            {
                output.WriteLine($"budget of {budget.TotalMinutes:0} min spent; stopping before step {index}");
                break;
            }
            var step = plan.Steps[index];
            if (state.Skipped.Contains(step.Id))
            {
                output.WriteLine($"[{index}] {step.Id}: skipped (it hung the game twice before)");
                state.Next = index + 1;
                continue;
            }
            if (step.Blocked is { } blocked)
            {
                state.Record(new StepOutcome { Index = index, Id = step.Id, Ok = false, Blocked = true, Error = "blocked: " + blocked });
                state.Next = index + 1;
                output.WriteLine($"[{index}] {step.Id}: blocked ({blocked})");
                continue;
            }
            fresh |= sinceFresh >= options.FreshEvery;
            var outcome = await StepWithWatchdogAsync(plan, step, index, fresh, options, cancel);
            sinceFresh = fresh ? 0 : sinceFresh + 1;
            fresh = step.Fresh || !outcome.Ok;
            if (outcome.Hang)
            {
                state.Hangs[step.Id] = state.Hangs.GetValueOrDefault(step.Id) + 1;
                await RecoverAsync(plan, options, state, cancel);
                fresh = true;
                if (state.Hangs[step.Id] >= 2)
                {
                    state.Skipped.Add(step.Id);
                    output.WriteLine($"[{index}] {step.Id}: hung the game twice; skipped and listed");
                }
                else
                {
                    output.WriteLine($"[{index}] {step.Id}: hung the game ({outcome.Error}); the copy is back, trying it once more");
                    outcome = await StepWithWatchdogAsync(plan, step, index, true, options, cancel);
                    if (outcome.Hang)
                    {
                        state.Hangs[step.Id]++;
                        state.Skipped.Add(step.Id);
                        await RecoverAsync(plan, options, state, cancel);
                        output.WriteLine($"[{index}] {step.Id}: hung the game twice; skipped and listed");
                    }
                }
            }
            state.Record(outcome);
            foreach (var lit in outcome.New) state.Lit.Add(lit);
            state.Next = index + 1;
            Say(index, outcome);
            sinceDrain++;
            sinceReset++;
            if (sinceDrain >= options.DrainEvery)
            {
                await DrainIntoAsync(state, outcome, cancel);
                sinceDrain = 0;
            }
            if (options.ResetEvery > 0 && sinceReset >= options.ResetEvery)
            {
                await DrainIntoAsync(state, outcome, cancel);
                await ResetAsync(plan, options, state, cancel);
                sinceReset = 0;
                fresh = true;
            }
            state.Save(statePath);
        }
        if (state.Steps.LastOrDefault() is { } last) await DrainIntoAsync(state, last, cancel);
        state.Seconds += _time.Clock.GetElapsedTime(started).TotalSeconds;
        state.Save(statePath);
        if (options.Recipes && state.Lit.Count > 0)
        {
            await new SweepRecipes(game, output).WriteAndConfirmAsync(plan, state, cancel);
            state.Save(statePath);
        }
        if (options.ReportLeads && plan.Steps.Any(s => s.Leads is { Count: > 0 }))
        {
            SweepLeads.Report(game.CoverageRoot, plan, state, options.Instance, output);
            state.Save(statePath);
        }
        await StopSpeedAsync(cancel);
        return state;
    }

    void Say(int index, StepOutcome outcome)
    {
        var lit = outcome.New.Count > 0 ? "  NEW: " + string.Join(", ", outcome.New.Select(n => n.Type)) : "";
        var how = outcome.FallbackUsed ? " (direct)" : "";
        var check = outcome.Checked switch
        {
            true => " checked" + (outcome.CheckNote is { Length: > 0 } note ? $" ({note})" : ""),
            false => " CHECK FAILED: " + outcome.CheckNote,
            _ => "",
        };
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{index}] {outcome.Id}: {(outcome.Ok ? "ok" : "FAILED")}{how}{check} {outcome.Seconds:0.0} s{lit}")
            + (outcome.Error is { } error ? "  " + error.Split('\n')[0] : ""));
    }

    /// <summary>The capture on, labelled for this sweep, the time scale set; a checkpoint the first time.</summary>
    async Task ArmAsync(SweepPlan plan, SweepOptions options, SweepState state, bool checkpoint, CancellationToken cancel)
    {
        if (checkpoint && (options.ResetEvery > 0 || options.Recipes))
        {
            try { await game.CheckpointAsync(cancel); }
            catch (CopyException e) { output.WriteLine("no checkpoint (the world cannot be reset in this run): " + e.Message); }
        }
        await game.ToolAsync("bg3_seen_start", new JsonObject
        {
            ["label"] = $"sweep:{plan.Kind}:arm",
            ["role"] = Role,
            ["run"] = state.Run,
            ["stretch"] = plan.Kind,
        }, cancel);
        if (Math.Abs(options.Speed - 1.0) > 1e-9)
            await game.LuaAsync("server", $"return GK.Speed.Set({PlanBuilder.Invariant(options.Speed)})", TimeSpan.FromSeconds(20), cancel);
        // The party cannot die while steps make dummies hostile: a host killed by a dummy of the
        // contexts plan ended the game (game over) and every later step with it.
        await game.LuaAsync("server", "return GK.Stage.Guard(true)", TimeSpan.FromSeconds(20), cancel);
        // What the capture meets in its first ticks is the level itself, not any step: drained
        // before the first step so that it is not counted as the first step's yield.
        await _time.Wait(TimeSpan.FromSeconds(1.5), cancel);
        await game.ToolAsync("bg3_seen_new", [], cancel);
    }

    async Task StopSpeedAsync(CancellationToken cancel)
    {
        try { await game.LuaAsync("server", "return GK.Speed.Set(1)", TimeSpan.FromSeconds(20), cancel); }
        catch (CopyException) { }
    }

    async Task<StepOutcome> StepWithWatchdogAsync(SweepPlan plan, SweepStep step, int index, bool fresh, SweepOptions options, CancellationToken cancel)
    {
        using var watch = new StepWatchdog(_time, TimeSpan.FromSeconds(options.StepTimeout), cancel);
        try
        {
            return await StepAsync(plan, step, index, fresh, options, watch, watch.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return StepOutcome.Failed(step, index, watch, $"the game held the step over {options.StepTimeout:0} s", hang: true);
        }
        catch (CopyException e) when (Silent(e) || !game.Alive())
        {
            return StepOutcome.Failed(step, index, watch, e.Message.Split('\n')[0], hang: true);
        }
    }

    static bool Silent(CopyException e) =>
        e.Message.StartsWith("no answer from", StringComparison.Ordinal) || e.Message.Contains("not running", StringComparison.Ordinal)
        || e.Message.Contains("process is gone", StringComparison.Ordinal);

    async Task<StepOutcome> StepAsync(SweepPlan plan, SweepStep step, int index, bool fresh, SweepOptions options, StepWatchdog watch, CancellationToken cancel)
    {
        var label = Label(plan.Kind, step.Id);
        var quick = TimeSpan.FromSeconds(15);
        foreach (var side in new[] { "server", "client" })
            await game.LuaAsync(side, $"return GK.Seen.Mark({PlanBuilder.Lua(label)})", quick, cancel);

        var needTarget = step.How.Contains('T', StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(step.How, @"\bT\b");
        var dummies = await game.LuaAsync("server", Call(DummiesLua, fresh ? "true" : "false", needTarget ? "true" : "false"), quick, cancel);
        for (var wait = 0; dummies.Field("ready")?.GetValue<bool>() != true && wait < 20; wait++)
        {
            await watch.WaitAsync(TimeSpan.FromMilliseconds(250));
            dummies = await game.LuaAsync("server", Call(DummiesLua, "false", needTarget ? "true" : "false"), quick, cancel);
        }
        if (dummies.Field("ready")?.GetValue<bool>() != true)
            return StepOutcome.Failed(step, index, watch, "the dummy did not come into being (GK.Stage.Spawn of `human`)");

        var counts = step.Expects is { Count: > 0 } && step.Fallback is not null ? await CountsAsync(step.Expects, cancel) : null;
        string? error = null;
        try { await game.LuaAsync("server", Prefixed(step.How), TimeSpan.FromSeconds(20), cancel); }
        catch (CopyException e) when (!Silent(e) && game.Alive()) { error = e.Message.Replace("Lua error on server:\n", "", StringComparison.Ordinal); }
        await watch.WaitAsync(Settle(step.Settle, options));

        var fallbackUsed = false;
        if (counts is not null && step.Fallback is { } fallback)
        {
            var after = await CountsAsync(step.Expects!, cancel);
            if (!step.Expects!.Any(type => after.GetValueOrDefault(type) > counts.GetValueOrDefault(type)))
            {
                fallbackUsed = true;
                foreach (var side in new[] { "server", "client" })
                    await game.LuaAsync(side, $"return GK.Seen.Mark({PlanBuilder.Lua(label + ":direct")})", quick, cancel);
                try
                {
                    await game.LuaAsync("server", Prefixed(fallback.How), TimeSpan.FromSeconds(20), cancel);
                    error = null;
                }
                catch (CopyException e) when (!Silent(e) && game.Alive()) { error ??= e.Message.Replace("Lua error on server:\n", "", StringComparison.Ordinal); }
                await watch.WaitAsync(Settle(1.0, options));
            }
        }

        bool? checkedOk = null;
        string? checkNote = null;
        if (step.Check is { } check)
        {
            try
            {
                var answer = await game.LuaAsync("server", Prefixed("return (function()\n" + check + "\nend)()"), quick, cancel);
                if (answer is JsonObject told)
                {
                    checkedOk = told["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var fine) && fine;
                    checkNote = told["note"]?.ToString();
                }
                else
                {
                    checkedOk = answer is JsonValue v && v.TryGetValue<bool>(out var yes) && yes;
                    if (checkedOk != true) checkNote = answer is null ? "the check answered nothing" : answer.ToString();
                }
            }
            catch (CopyException e) when (!Silent(e) && game.Alive())
            {
                checkedOk = false;
                checkNote = e.Message.Replace("Lua error on server:\n", "", StringComparison.Ordinal).Split('\n')[0];
            }
        }

        var found = await game.ToolAsync("bg3_seen_new", [], cancel);
        var lit = new List<LitType>();
        foreach (var row in found?["new_to_the_record"] as JsonArray ?? [])
        {
            if (row?["Name"]?.GetValue<string>() is not { } name) continue;
            var side = row["side"]?.GetValue<string>() ?? "server";
            var seenLabel = row["Label"]?.GetValue<string>() ?? label;
            var by = seenLabel.EndsWith(":direct", StringComparison.Ordinal) ? "direct" : "carrier";
            lit.Add(new LitType(name, side, StepOf(plan.Kind, seenLabel) ?? step.Id, by));
        }

        foreach (var undo in new[] { step.Undo, fallbackUsed ? step.Fallback?.Undo : null })
        {
            if (undo is null) continue;
            try { await game.LuaAsync("server", Prefixed(undo), quick, cancel); }
            catch (CopyException e) when (!Silent(e) && game.Alive()) { }
        }
        return new StepOutcome
        {
            Index = index, Id = step.Id, Ok = error is null, Error = error, Seconds = Math.Round(watch.Total.TotalSeconds, 2),
            New = lit, FallbackUsed = fallbackUsed, Checked = checkedOk, CheckNote = checkNote,
        };
    }

    /// <summary>The step's Lua with its dummies as locals.</summary>
    static string Prefixed(string how) => Locals + how;

    /// <summary>The locals every step's Lua has: its dummies, and H, the host (a party member) for
    /// steps that are a player's own action.</summary>
    public const string Locals = "local D, T = SweepDummy, SweepTarget\nlocal H = Osi.GetHostCharacter()\n";

    /// <summary>The real time that lets the game run for this many of its own seconds.</summary>
    static TimeSpan Settle(double gameSeconds, SweepOptions options) =>
        TimeSpan.FromSeconds(Math.Max(0.3, gameSeconds / Math.Max(0.1, options.Speed)));

    async Task<Dictionary<string, double>> CountsAsync(IReadOnlyList<string> types, CancellationToken cancel)
    {
        var names = "{" + string.Join(", ", types.Select(PlanBuilder.Lua)) + "}";
        var answer = await game.LuaAsync("server",
            $"local counts, out = Ext.Debug.GetComponentCounts(), {{}}\nfor _, name in ipairs({names}) do out[name] = counts[name] or 0 end\nreturn out",
            TimeSpan.FromSeconds(15), cancel);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, value) in answer.AsFields() ?? [])
            if (value is JsonValue v && v.TryGetValue<double>(out var n)) result[name] = n;
        return result;
    }

    public static string Label(string kind, string id) => $"sweep:{kind}:{id}";

    /// <summary>The step id a label names, or null for a label that is not one of this sweep's steps.</summary>
    public static string? StepOf(string kind, string label)
    {
        var prefix = $"sweep:{kind}:";
        if (!label.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var id = label[prefix.Length..];
        if (id.EndsWith(":direct", StringComparison.Ordinal)) id = id[..^":direct".Length];
        return id == "arm" ? null : id;
    }

    async Task DrainIntoAsync(SweepState state, StepOutcome at, CancellationToken cancel)
    {
        var before = VerifiedNames(game.CoverageRoot);
        try
        {
            var drained = await game.ToolAsync("bg3_seen_drain", [], cancel);
            foreach (var problem in drained?["disagreements"] as JsonArray ?? [])
                if (problem?.ToString() is { Length: > 0 } text) state.Disagreements.Add(text);
        }
        catch (CopyException e)
        {
            output.WriteLine("drain failed: " + e.Message.Split('\n')[0]);
            return;
        }
        var after = VerifiedNames(game.CoverageRoot);
        var fresh = after.Except(before).Order(StringComparer.Ordinal).ToList();
        if (fresh.Count == 0) return;
        at.Verified.AddRange(fresh);
        state.Verified.UnionWith(fresh);
        output.WriteLine($"  drain: {fresh.Count} types verified live: {string.Join(", ", fresh.Take(12))}{(fresh.Count > 12 ? ", ..." : "")}");
    }

    /// <summary>side|name of every type coverage.json records as verified.</summary>
    internal static HashSet<string> VerifiedNames(string root)
    {
        var path = Path.Combine(root, "coverage.json");
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return names;
        using var stream = File.OpenRead(path);
        var index = JsonNode.Parse(stream);
        foreach (var row in index?["components"] as JsonArray ?? [])
            if (row?["verified"] is not null && row["name"]?.GetValue<string>() is { } name)
                names.Add($"{row["side"]?.GetValue<string>()}|{name}");
        return names;
    }

    /// <summary>After a hang: the copy answering again, or started again, and the capture armed.</summary>
    async Task RecoverAsync(SweepPlan plan, SweepOptions options, SweepState state, CancellationToken cancel)
    {
        state.Recoveries++;
        if (game.Alive())
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    await game.LuaAsync("server", "return true", TimeSpan.FromSeconds(3), cancel);
                    await game.LuaAsync("client", "return true", TimeSpan.FromSeconds(3), cancel);
                    output.WriteLine("  the game answers again");
                    await ArmAsync(plan, options, state, checkpoint: false, cancel);
                    return;
                }
                catch (CopyException) { }
            }
        }
        output.WriteLine("  the game does not answer: restarting the copy");
        state.Restarts++;
        await game.RestartAsync(cancel);
        await ArmAsync(plan, options, state, checkpoint: true, cancel);
    }

    async Task ResetAsync(SweepPlan plan, SweepOptions options, SweepState state, CancellationToken cancel)
    {
        try
        {
            await game.ResetWorldAsync(cancel);
            state.Resets++;
            await ArmAsync(plan, options, state, checkpoint: false, cancel);
        }
        catch (CopyException e)
        {
            output.WriteLine("world reset failed: " + e.Message.Split('\n')[0]);
            await RecoverAsync(plan, options, state, cancel);
        }
    }
}

/// <summary>A type never seen before that a step made appear.</summary>
/// <param name="By">carrier (the step's own entry) or direct (its AddBoosts fallback).</param>
public sealed record LitType(string Type, string Side, string Step, string By);

/// <summary>What one step did.</summary>
public sealed record StepOutcome
{
    public required int Index { get; init; }
    public required string Id { get; init; }
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public double Seconds { get; init; }
    public List<LitType> New { get; init; } = [];
    /// <summary>Types the drain after this step recorded as verified live (side|name).</summary>
    public List<string> Verified { get; init; } = [];
    public bool FallbackUsed { get; init; }
    public bool Hang { get; init; }
    /// <summary>What the step's check said: true, false, or null when the step has none.</summary>
    public bool? Checked { get; init; }
    public string? CheckNote { get; init; }
    /// <summary>The step was not run: the plan says it cannot be tried.</summary>
    public bool Blocked { get; init; }

    internal static StepOutcome Failed(SweepStep step, int index, StepWatchdog watch, string error, bool hang = false) => new()
    {
        Index = index, Id = step.Id, Ok = false, Error = error, Seconds = Math.Round(watch.Total.TotalSeconds, 2), Hang = hang,
    };
}

/// <summary>The state file of a run: where it stands, what each step did, what was lit. Written after every step.</summary>
public sealed class SweepState
{
    public string Plan { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Run { get; set; } = "";
    public int Next { get; set; }
    public double Seconds { get; set; }
    public int Recoveries { get; set; }
    public int Restarts { get; set; }
    public int Resets { get; set; }
    public List<StepOutcome> Steps { get; set; } = [];
    public Dictionary<string, int> Hangs { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> Skipped { get; set; } = new(StringComparer.Ordinal);
    public List<LitType> Lit { get; set; } = [];
    public HashSet<string> Verified { get; set; } = new(StringComparer.Ordinal);
    public List<RecipeOutcome> Recipes { get; set; } = [];
    /// <summary>What the drains said disagreed with its layout (type, field, expected and actual), as the drain wrote it.</summary>
    public HashSet<string> Disagreements { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The lead types reported back already (`leads report`), so a resumed run does not report twice.</summary>
    public HashSet<string> Reported { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The newest outcome of a step replaces an older one of the same index.</summary>
    public void Record(StepOutcome outcome)
    {
        Steps.RemoveAll(s => s.Index == outcome.Index);
        Steps.Add(outcome);
    }

    public static SweepState Load(string path, SweepPlan plan, string planPath, string run)
    {
        if (File.Exists(path))
        {
            var state = JsonSerializer.Deserialize<SweepState>(File.ReadAllText(path), SweepPlan.Json)
                ?? throw new SweepException($"{path} is not a sweep state file");
            if (state.Kind != plan.Kind)
                throw new SweepException($"{path} is the state of a `{state.Kind}` plan, not of this `{plan.Kind}` one; give another --state");
            return state;
        }
        return new SweepState { Plan = Path.GetFileName(planPath), Kind = plan.Kind, Run = run };
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, SweepPlan.Json).ReplaceLineEndings("\n"));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The numbers of the run, as `sweep status` and the end of `sweep run` say them.</summary>
    public JsonObject Summary()
    {
        var ok = Steps.Count(s => s.Ok);
        var minutes = Seconds / 60;
        return new JsonObject
        {
            ["kind"] = Kind,
            ["run"] = Run,
            ["steps_run"] = Steps.Count,
            ["steps_ok"] = ok,
            ["steps_failed"] = Steps.Count - ok,
            ["next"] = Next,
            ["minutes"] = Math.Round(minutes, 1),
            ["steps_per_minute"] = minutes > 0 ? Math.Round(Steps.Count / minutes, 1) : null,
            ["fallbacks_used"] = Steps.Count(s => s.FallbackUsed),
            ["steps_blocked"] = Steps.Count(s => s.Blocked),
            ["checks_passed"] = Steps.Count(s => s.Checked == true),
            ["checks_failed"] = new JsonArray([.. Steps.Where(s => s.Checked == false).Select(s => (JsonNode)$"{s.Id}: {s.CheckNote}")]),
            ["hangs"] = Hangs.Values.Sum(),
            ["skipped"] = new JsonArray([.. Skipped.Order(StringComparer.Ordinal).Select(s => (JsonNode)s!)]),
            ["recoveries"] = Recoveries,
            ["restarts"] = Restarts,
            ["world_resets"] = Resets,
            ["never_seen_types_lit"] = Lit.Count,
            ["lit"] = new JsonArray([.. Lit.Select(l => (JsonNode)$"{l.Type} ({l.Side}) by {l.Step} [{l.By}]")]),
            ["verified_live_by_this_run"] = Verified.Count,
            ["recipes_written"] = Recipes.Count,
            ["recipes_confirmed"] = Recipes.Count(r => r.Confirmed),
            ["disagreements"] = new JsonArray([.. Disagreements.Order(StringComparer.Ordinal).Select(d => (JsonNode)d)]),
            ["leads_reported"] = Reported.Count,
        };
    }
}

/// <summary>A recipe the sweep wrote, and whether its replay from a clean state passed.</summary>
public sealed record RecipeOutcome(string Step, string Recipe, IReadOnlyList<string> Types, bool Confirmed, string Verdict);
