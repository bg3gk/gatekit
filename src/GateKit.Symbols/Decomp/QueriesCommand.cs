using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GateKit.Symbols.Decomp;

/// <summary>
/// `queries`: the queries a system's update walks, and the component pointers it takes from them.
/// `survey` counts the shape in both builds and changes nothing.
/// </summary>
internal static class QueriesCommand
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 1 };

    static string Hex(long value) => value.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Why a value `index * N + base` is or is not one component of the system's specs.</summary>
    internal static string Classify(StrideValue value, IReadOnlyCollection<string> types, IReadOnlyDictionary<string, long> sizes)
    {
        if (value.Stride == QueryShapes.DescriptionSize && QueryShapes.IsGlobal(value.Index))
            return "the query's own description (global * 0x58)";
        if (value.Offset)
            return "a constant is added (a field's address)";
        if (value.Stride <= 0)
            return "the base is a pointer to a structure";
        if (types.Count == 0)
            return "the system's macOS entry names no spec";
        int fit = types.Count(t => sizes.TryGetValue(t, out long size) && size == value.Stride);
        bool unsized = types.Any(t => !sizes.ContainsKey(t));
        return fit switch
        {
            0 when value.Stride == 8 => "stride 8 and no type of that size (a table of pointers or handles)",
            0 => unsized ? "no sized type of that size, and a type of the specs is not sized" : "no type of that size",
            1 => unsized ? "one type of that size, but a type of the specs is not sized" : "one type of that size",
            _ => "several types of that size",
        };
    }

    public static int Map(DecompSession s, string target, string source, bool asJson, bool dryRun, int rounds, int wait, int show)
    {
        var watch = Stopwatch.StartNew();
        var reading = QueryReader.Read(s, target, source, m => s.Err.WriteLine(m));
        foreach (var note in reading.Notes)
            s.Err.WriteLine(note);
        if (reading.Specs.Count == 0)
            throw new SymbolsException($"{source} names no query: nothing to tie the globals of {target} to");
        var result = QueryMap.Map(reading, rounds);
        var all = reading.Systems.SelectMany(q => q.DistinctGlobals).ToHashSet();
        int several = result.Kept.Count(g => g.Systems > 1);
        if (!dryRun)
        {
            s.CloseDb();
            RefreshCommand.ThroughACopy(s.Settings, ".queries", wait, db => QueryMap.Store(db, target, reading, result));
        }
        if (asJson)
        {
            var root = new JsonObject
            {
                ["target"] = target, ["source"] = source, ["stored"] = !dryRun,
                ["query_globals"] = all.Count,
                ["systems_used"] = new JsonArray([.. result.SystemsUsed.Select(n => (JsonNode)n)]),
                ["kept"] = result.Kept.Count, ["kept_by_several_systems"] = several, ["kept_by_one_system"] = result.Kept.Count - several,
                ["kept_by_position_alone"] = result.Kept.Count(g => g.Round == 0),
                ["checked"] = result.Checked, ["checked_agree"] = result.CheckedAgree,
                ["statements"] = result.Statements, ["statements_agree"] = result.StatementsAgree,
                ["disagree"] = result.Disagree.Count, ["shared_spec"] = result.SharedSpec.Count,
                ["registrations"] = new JsonObject
                {
                    ["read"] = reading.Registrations.Count, ["one_answer"] = reading.Registrations.Count(r => r.Value.Count == 1),
                    ["whole"] = reading.Registrations.Count(r => r.Value.Count == 1 && r.Value[0].Whole),
                    ["tied_by_several_systems"] = new JsonObject { ["same_types"] = result.RegisteredSeveral[0], ["other_types"] = result.RegisteredSeveral[1] },
                    ["tied_by_one_system"] = new JsonObject { ["same_types"] = result.RegisteredOne[0], ["other_types"] = result.RegisteredOne[1] },
                    ["dropped"] = result.RegistrationDiffers.Count, ["kept_unchecked"] = result.Unregistered.Count,
                },
                ["globals"] = new JsonArray([.. result.Kept.Select(g => (JsonNode)new JsonObject { ["rva"] = Hex(g.Rva), ["spec"] = g.Spec, ["systems"] = g.Systems, ["round"] = g.Round })]),
                ["disagreements"] = new JsonArray([.. result.Disagree.Select(d => (JsonNode)new JsonObject
                {
                    ["rva"] = Hex(d.Rva), ["specs"] = new JsonArray([.. d.Specs.Select(x => (JsonNode)new JsonObject { ["spec"] = x.Spec, ["systems"] = x.Systems })]),
                })]),
                ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1),
            };
            s.Out.WriteLine(root.ToJsonString(Indented).ReplaceLineEndings("\n"));
            return 0;
        }
        string Share(int part, int whole) => whole == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * part / whole:F1} %");
        s.Out.WriteLine($"{target}: query globals tied to the specs of {source}");
        s.Out.WriteLine($"  query globals the systems' updates read             {all.Count,6}");
        s.Out.WriteLine($"  systems lined up, per round                         {string.Join(", ", result.SystemsUsed),6}");
        s.Out.WriteLine($"  globals more than one system speaks of              {result.Checked,6}");
        s.Out.WriteLine($"    every system names the same spec                  {result.CheckedAgree,6}  ({Share(result.CheckedAgree, result.Checked)})");
        s.Out.WriteLine($"    statements that agree with the global's majority  {result.StatementsAgree,6} of {result.Statements}  ({Share(result.StatementsAgree, result.Statements)})");
        s.Out.WriteLine($"  dropped: systems disagree                           {result.Disagree.Count,6}");
        s.Out.WriteLine($"  dropped: another global is tied to the same spec    {result.SharedSpec.Count,6}");
        int registered = reading.Registrations.Count(r => all.Contains(r.Key)), registeredOnce = reading.Registrations.Count(r => all.Contains(r.Key) && r.Value.Count == 1);
        s.Out.WriteLine($"  globals whose registration was read                 {registered,6}  ({registeredOnce} with one answer, {reading.Registrations.Count(r => r.Value.Count == 1 && r.Value[0].Whole)} with the ten counts adding up)");
        s.Out.WriteLine($"  tied by several systems, registration stores the spec's types   {result.RegisteredSeveral[0],6} of {result.RegisteredSeveral.Sum()}  ({Share(result.RegisteredSeveral[0], result.RegisteredSeveral.Sum())})");
        s.Out.WriteLine($"  tied by one system, registration stores the spec's types        {result.RegisteredOne[0],6} of {result.RegisteredOne.Sum()}  ({Share(result.RegisteredOne[0], result.RegisteredOne.Sum())})");
        s.Out.WriteLine($"  dropped: the registration stores other types        {result.RegistrationDiffers.Count,6}");
        s.Out.WriteLine($"  kept                                                {result.Kept.Count,6}  ({several} by several systems, {result.Kept.Count - several} by one; "
                        + $"{result.Kept.Count(g => g.Round == 0)} by position alone, {result.Kept.Count(g => g.Round > 0)} between anchors; {result.Unregistered.Count} with no registration to check against)");
        if (show > 0)
            s.Out.WriteLine("    no registration read, for instance: " + string.Join(' ', all.Where(g => !reading.Registrations.ContainsKey(g)).Order().Take(show).Select(g => "+0x" + Hex(g)))
                            + "; read but not whole: " + string.Join(' ', reading.Registrations.Where(r => r.Value.Count != 1 || !r.Value[0].Whole).Select(r => r.Key).Order().Take(show).Select(g => "+0x" + Hex(g))));
        foreach (var d in result.Disagree.Take(show))
            s.Out.WriteLine($"    +0x{d.Rva:X}: " + string.Join(" | ", d.Specs.Select(x => $"{x.Systems} x {x.Spec}")));
        int whole = reading.Registrations.Count(r => r.Value.Count == 1 && r.Value[0].Whole);
        int rows = result.Kept.Select(g => g.Rva).Union(reading.Registrations.Where(r => r.Value.Count == 1 && r.Value[0].Whole).Select(r => r.Key)).Count();
        s.Out.WriteLine(dryRun ? "  nothing stored (--dry-run)" : $"  stored in query_globals of {s.Settings.DbPath}: {rows} globals, {result.Kept.Count} with a spec, {whole} with their registered types");
        s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  in {watch.Elapsed.TotalSeconds:F0} s"));
        return 0;
    }

    /// <summary>Where the guards that name nothing stand, against the functions this typing reaches:
    /// reads the JSON digests of a whole pass (`symbols digest --all --json --out DIR`).</summary>
    public static int Guards(DecompSession s, string target, string source, string? coverage, string digests, bool asJson)
    {
        var watch = Stopwatch.StartNew();
        if (!Directory.Exists(digests))
            throw new SymbolsException($"{digests} does not exist; write the digests with:  gatekit symbols digest --all --force --json --out {digests}");
        var sizes = QueryReader.LedgerSizes(RecordTypes.FindCoverage(coverage));
        var reading = QueryReader.Read(s, target, source, _ => { });
        var typing = QueryTyping.Decide(s, reading, sizes);
        var withForm = typing.Cases.Select(c => c.Function).ToHashSet();
        var withTyped = typing.Cases.Where(c => c.Outcome == QueryTyping.Typed).Select(c => c.Function).ToHashSet();
        var updatesOf = reading.Systems.ToDictionary(q => q.System, q => q.Functions.ToHashSet(), StringComparer.Ordinal);
        int files = 0, operations = 0, naming = 0;
        // [depth 1, depth 2+] of the operations whose guard names nothing
        int[] silent = new int[2], inTyped = new int[2], inForm = new int[2], updateTyped = new int[2];
        var made = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int rawInTyped = 0, rawInForm = 0;
        foreach (var file in Directory.EnumerateFiles(digests, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == TallyCommand.GuardsFile)
                continue;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            if (!doc.RootElement.TryGetProperty("binaries", out var binaries) || !binaries.TryGetProperty(target, out var part) || !part.TryGetProperty("operations", out var list))
                continue;
            files++;
            string system = doc.RootElement.TryGetProperty("system", out var name) ? name.GetString() ?? "" : "";
            var updates = updatesOf.GetValueOrDefault(system) ?? [];
            foreach (var op in list.EnumerateArray())
            {
                operations++;
                IEnumerable<string> Terms(string property) => op.TryGetProperty(property, out var g) && g.ValueKind == JsonValueKind.Object && g.TryGetProperty("terms", out var t)
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "") : [];
                if (Terms("guard").Concat(Terms("call_guard")).Any(Queries.PseudoGuard.Names))
                {
                    naming++;
                    continue;
                }
                int depth = op.TryGetProperty("depth", out var d) && d.GetInt32() > 1 ? 1 : 0;
                long function = op.TryGetProperty("function", out var f) && Py.Hex(f.GetString() ?? "") is long rva ? rva : -1;
                silent[depth]++;
                // what the terms that name nothing are made of (an operation may be in several rows)
                var terms = Terms("guard").Concat(Terms("call_guard")).ToList();
                void Made(string of, bool yes)
                {
                    if (yes) made[of] = made.GetValueOrDefault(of) + 1;
                }
                Made("no term at all (unconditional, or not extracted)", terms.Count == 0);
                Made("a dereference through a local or a parameter the decompiler has no type for ([x+0xNN])", terms.Any(t => t.Contains("+0x", StringComparison.Ordinal) && t.Contains('[', StringComparison.Ordinal)));
                Made("a parameter of the function", terms.Any(t => t.Contains("param_", StringComparison.Ordinal)));
                Made("the answer of a function without a name (FUN_)", terms.Any(t => t.Contains("FUN_", StringComparison.Ordinal)));
                Made("an unnamed global (DAT_)", terms.Any(t => t.Contains("DAT_", StringComparison.Ordinal)));
                Made("a field of a typed structure by a placeholder name (.field_0x)", terms.Any(t => t.Contains(".field_0x", StringComparison.Ordinal) || t.Contains(".field", StringComparison.Ordinal)));
                Made("only a loop over a list", terms.Count > 0 && terms.All(t => t == "for each item of a list"));
                bool raw = terms.Any(t => t.Contains("+0x", StringComparison.Ordinal) && t.Contains('[', StringComparison.Ordinal));
                if (raw && withTyped.Contains(function)) rawInTyped++;
                if (raw && withForm.Contains(function)) rawInForm++;
                if (withTyped.Contains(function)) inTyped[depth]++;
                if (withForm.Contains(function)) inForm[depth]++;
                if (updates.Any(withTyped.Contains)) updateTyped[depth]++;
            }
        }
        if (asJson)
        {
            JsonArray Pair(int[] a) => new(a[0], a[1]);
            s.Out.WriteLine(new JsonObject
            {
                ["target"] = target, ["digests"] = digests, ["systems"] = files, ["operations"] = operations, ["naming"] = naming,
                ["not_naming_by_depth"] = Pair(silent), ["in_a_function_with_a_typed_value"] = Pair(inTyped), ["in_a_function_with_a_value_of_a_column_form"] = Pair(inForm),
                ["in_a_system_with_a_typed_value"] = Pair(updateTyped),
                ["with_a_raw_dereference_in_a_function_with_a_typed_value"] = rawInTyped, ["with_a_raw_dereference_in_a_function_with_a_value_of_a_column_form"] = rawInForm,
                ["made_of"] = new JsonObject(made.Select(m => KeyValuePair.Create(m.Key, (JsonNode?)m.Value))), ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1),
            }.ToJsonString(Indented).ReplaceLineEndings("\n"));
            return 0;
        }
        s.Out.WriteLine($"{target}: {operations} operations of {files} systems in {digests}");
        s.Out.WriteLine($"  a guard naming a component, a field or a call        {naming,6}");
        s.Out.WriteLine($"  a guard naming nothing, or none                      {silent[0] + silent[1],6}  ({silent[0]} in the update, {silent[1]} in a function it calls)");
        s.Out.WriteLine($"    the operation's function has a value this typing types      {inTyped[0] + inTyped[1],6}  ({inTyped[0]} / {inTyped[1]})");
        s.Out.WriteLine($"    the operation's function has a value of a column's form     {inForm[0] + inForm[1],6}  ({inForm[0]} / {inForm[1]})");
        s.Out.WriteLine($"    the system's entry or update has a value this typing types  {updateTyped[0] + updateTyped[1],6}  ({updateTyped[0]} / {updateTyped[1]})");
        s.Out.WriteLine($"    of those, the guard holds a dereference through an untyped pointer: {rawInTyped} where a value is typed, {rawInForm} where a value has a column's form");
        s.Out.WriteLine("  what the guards that name nothing hold (an operation may be in several rows):");
        foreach (var (of, count) in made.OrderByDescending(m => m.Value))
            s.Out.WriteLine($"    {count,6}  {of}");
        s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  in {watch.Elapsed.TotalSeconds:F0} s"));
        return 0;
    }

    public static int Columns(DecompSession s, string target, string source, string? coverage, bool asJson, int forms)
    {
        var watch = Stopwatch.StartNew();
        var folder = RecordTypes.FindCoverage(coverage);
        var sizes = QueryReader.LedgerSizes(folder);
        var reading = QueryReader.Read(s, target, source, _ => { });
        var typing = QueryTyping.Decide(s, reading, sizes);

        // the rule, from the systems that have one query and its registration: per distance
        // between the pointer local and the index local, and per form of the base, how often the
        // stride is the ledger's size of the k-th type of each of the description's ten groups
        var table = new SortedDictionary<(long Distance, string Form), int[]>();
        foreach (var (system, _, v) in typing.Values)
        {
            if (v.Distance is not long distance)
                continue;
            if (!table.TryGetValue((distance, v.Form), out var row))
                table[(distance, v.Form)] = row = new int[12];
            row[0]++;
            if (QueryTyping.OnlyQuery(system, reading) is not { } r)
                continue;
            row[1]++;
            for (int g = 0; g < 10; g++)
                if (r.Group(g).ElementAtOrDefault(v.Column) is string type && sizes.TryGetValue(type, out long size) && size == v.Value.Stride)
                    row[2 + g]++;
        }
        var outcomes = typing.Cases.GroupBy(c => c.Outcome, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        int typed = outcomes.GetValueOrDefault(QueryTyping.Typed);
        if (asJson)
        {
            var root = new JsonObject
            {
                ["target"] = target, ["coverage"] = folder,
                ["values"] = typing.Values.Count, ["of_a_column_form"] = typing.Cases.Count,
                ["stride_check"] = new JsonObject
                {
                    ["one_query"] = new JsonObject { ["cases"] = typing.OneQueryChecked, ["agree"] = typing.OneQueryAgree },
                    ["any_query"] = new JsonObject { ["cases"] = typing.Checked, ["agree"] = typing.Agree },
                },
                ["without_the_place"] = new JsonObject { ["checked"] = typing.BlindChecked, ["same_type"] = typing.BlindAgree, ["another_type"] = typing.BlindDiffer },
                ["typed_by_fit_alone"] = typing.Cases.Count(c => c.Outcome == QueryTyping.Typed && c.ByFit),
                ["outcomes"] = new JsonObject(outcomes.OrderByDescending(o => o.Value).Select(o => KeyValuePair.Create(o.Key, (JsonNode?)o.Value))),
                ["typed"] = typed, ["functions_typed"] = typing.Cases.Where(c => c.Outcome == QueryTyping.Typed).Select(c => c.Function).Distinct().Count(),
                ["places"] = new JsonArray([.. table.OrderByDescending(t => t.Value[0]).Take(Math.Max(forms, 0)).Select(t => (JsonNode)new JsonObject
                {
                    ["distance"] = t.Key.Distance, ["form"] = t.Key.Form, ["cases"] = t.Value[0], ["one_query"] = t.Value[1],
                    ["agree_by_group"] = new JsonArray([.. t.Value[2..].Select(n => (JsonNode)n)]),
                })]),
                ["cases"] = new JsonArray([.. typing.Cases.Select(c => (JsonNode)new JsonObject
                {
                    ["system"] = c.System, ["function"] = Hex(c.Function), ["local"] = Hex(c.Value.Local), ["column"] = c.Value.Column, ["stride"] = c.Value.Value.Stride,
                    ["group"] = c.Group, ["outcome"] = c.Outcome, ["type"] = c.Type,
                })]),
                ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1),
            };
            s.Out.WriteLine(root.ToJsonString(Indented).ReplaceLineEndings("\n"));
            return 0;
        }
        string Share(int part, int whole) => whole == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * part / whole:F1} %");
        s.Out.WriteLine($"{target}: values index * N + base whose base is read through one stack local   {typing.Values.Count}  ({typing.Values.Count(v => v.Value.Distance != null)} with the index in a stack local too)");
        s.Out.WriteLine("  distance  cases  in a system with one query  stride = size of the k-th type of group 0..9          form of the base");
        foreach (var ((distance, form), row) in table.OrderByDescending(t => t.Value[0]).Take(forms))
            s.Out.WriteLine($"  {(distance < 0 ? "-" : "") + "0x" + Math.Abs(distance).ToString("x", CultureInfo.InvariantCulture),8}  {row[0],5}  {row[1],5}  [{string.Join(' ', row[2..].Select(n => n.ToString(CultureInfo.InvariantCulture).PadLeft(3)))}]  {form}");
        s.Out.WriteLine($"  the rule (QueryColumns.Groups): distance 0x50 is group 0, distance 0x40 is group 7; forms {"*(longlong *)*S"} and {"*(longlong *)S[k]"}");
        s.Out.WriteLine($"  values of a column's form                            {typing.Cases.Count,6}");
        s.Out.WriteLine($"  stride check, systems with one query                 {typing.OneQueryAgree,6} of {typing.OneQueryChecked}  ({Share(typing.OneQueryAgree, typing.OneQueryChecked)})");
        s.Out.WriteLine($"  stride check, the k-th type of some query of the system {typing.Agree,6} of {typing.Checked}  ({Share(typing.Agree, typing.Checked)})");
        s.Out.WriteLine($"  the rule without the place, where the place is known: the same type {typing.BlindAgree} of {typing.BlindChecked}, another type {typing.BlindDiffer}, left alone {typing.BlindChecked - typing.BlindAgree - typing.BlindDiffer}");
        foreach (var (outcome, count) in outcomes.OrderByDescending(o => o.Value))
            s.Out.WriteLine($"    {count,6}  {outcome}");
        s.Out.WriteLine($"  typed: {typed} values ({typing.Cases.Count(c => c.Outcome == QueryTyping.Typed && c.ByFit)} of them by fit alone, the index not being a stack local) in{typing.Cases.Where(c => c.Outcome == QueryTyping.Typed).Select(c => c.Function).Distinct().Count()} functions of "
                        + $"{typing.Cases.Where(c => c.Outcome == QueryTyping.Typed).Select(c => c.System).Distinct(StringComparer.Ordinal).Count()} systems");
        s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  in {watch.Elapsed.TotalSeconds:F0} s"));
        return 0;
    }

    public static int Survey(DecompSession s, string target, string source, string? coverage, string? only, bool asJson, int forms)
    {
        var watch = Stopwatch.StartNew();
        var folder = RecordTypes.FindCoverage(coverage);
        var sizes = QueryReader.LedgerSizes(folder);
        var reading = QueryReader.Read(s, target, source, m => s.Err.WriteLine(m));
        var systems = reading.Systems.Where(q => only == null || q.System.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();

        var classes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var formCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new JsonArray();
        int withCode = 0, withEntry = 0, withSpecs = 0, withGlobals = 0, agree = 0, both = 0, mentions = 0, other = 0, strides = 0, reassigned = 0, oneUpdates = 0;
        var globals = new HashSet<long>();
        var specs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in systems)
        {
            var distinctGlobals = q.DistinctGlobals;
            var distinctSpecs = q.DistinctSpecs;
            var types = distinctSpecs.Where(reading.Specs.ContainsKey).SelectMany(n => reading.Specs[n].Types).Distinct(StringComparer.Ordinal).ToList();
            if (q.Functions.Count > 0) withCode++;
            if (q.MacosEntry != null) withEntry++;
            if (distinctSpecs.Count > 0) withSpecs++;
            if (distinctGlobals.Count > 0) withGlobals++;
            if (distinctGlobals.Count > 0 && distinctSpecs.Count > 0)
            {
                both++;
                if (distinctGlobals.Count == distinctSpecs.Count) agree++;
            }
            mentions += q.Globals.Count;
            other += q.OtherDescriptions;
            globals.UnionWith(distinctGlobals);
            specs.UnionWith(distinctSpecs);
            // a local that takes values of more than one stride in the same function cannot be given one type by its name
            var stridesOfTarget = q.Strides.Where(v => !v.Value.Offset).GroupBy(v => (v.Function, v.Value.Target))
                .ToDictionary(g => g.Key, g => g.Select(v => v.Value.Stride).Distinct().Count());
            var list = new JsonArray();
            bool one = false;
            foreach (var (function, value) in q.Strides)
            {
                strides++;
                string why = Classify(value, types, sizes);
                if (why == "one type of that size")
                {
                    if (stridesOfTarget[(function, value.Target)] > 1)
                    {
                        why = "one type of that size, but the local takes another stride too";
                        reassigned++;
                    }
                    else
                        one = true;
                }
                classes[why] = classes.GetValueOrDefault(why) + 1;
                string form = QueryShapes.Form(value.Base);
                formCounts[form] = formCounts.GetValueOrDefault(form) + 1;
                list.Add(new JsonObject
                {
                    ["function"] = Hex(function), ["stride"] = value.Stride, ["offset"] = value.Offset, ["base_form"] = form, ["class"] = why,
                    ["types"] = new JsonArray([.. types.Where(t => sizes.TryGetValue(t, out long size) && size == value.Stride).Select(t => (JsonNode)t)]),
                });
            }
            if (one) oneUpdates++;
            rows.Add(new JsonObject
            {
                ["system"] = q.System,
                ["entry"] = q.Entry is long e ? Hex(e) : null,
                ["updates"] = new JsonArray([.. q.Updates.Select(u => (JsonNode)Hex(u))]),
                ["macos_entry"] = q.MacosEntry is long m ? Hex(m) : null,
                ["query_globals"] = new JsonArray([.. distinctGlobals.Select(g => (JsonNode)Hex(g))]),
                ["specs"] = new JsonArray([.. distinctSpecs.Select(n => (JsonNode)n)]),
                ["spec_types"] = types.Count,
                ["spec_types_unsized"] = types.Count(t => !sizes.ContainsKey(t)),
                ["other_descriptions"] = q.OtherDescriptions,
                ["strides"] = list,
            });
        }

        if (asJson)
        {
            var root = new JsonObject
            {
                ["target"] = target, ["source"] = source, ["coverage"] = folder,
                ["systems"] = new JsonObject
                {
                    ["total"] = systems.Count, ["with_pseudo_code"] = withCode, ["with_query_globals"] = withGlobals, ["with_macos_entry"] = withEntry,
                    ["with_specs"] = withSpecs, ["with_both"] = both, ["counts_agree"] = agree, ["with_a_value_of_one_type"] = oneUpdates,
                },
                ["query_globals"] = new JsonObject { ["distinct"] = globals.Count, ["mentions"] = mentions, ["other_descriptions"] = other },
                ["specs"] = new JsonObject { ["named"] = reading.SpecsNamed, ["mentioned"] = specs.Count },
                ["strides"] = new JsonObject { ["total"] = strides, ["classes"] = new JsonObject(classes.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value))) },
                ["base_forms"] = new JsonObject(formCounts.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(Math.Max(forms, 0))
                    .Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
                ["notes"] = new JsonArray([.. reading.Notes.Select(n => (JsonNode)n)]),
                ["rows"] = rows,
                ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1),
            };
            s.Out.WriteLine(root.ToJsonString(Indented).ReplaceLineEndings("\n"));
            return 0;
        }

        foreach (var note in reading.Notes)
            s.Err.WriteLine(note);
        if (folder == null)
            s.Err.WriteLine("no coverage record found (--coverage, GATEKIT_COVERAGE, or research/coverage of the repository): no size is known");
        s.Out.WriteLine($"{target} against {source}" + (only == null ? "" : $", systems named like {only}"));
        s.Out.WriteLine($"  systems with an update                              {systems.Count,6}");
        s.Out.WriteLine($"    with pseudo-code of the entry or an update        {withCode,6}");
        s.Out.WriteLine($"    that read a query global (global * 0x58)          {withGlobals,6}");
        s.Out.WriteLine($"    with a macOS SystemUpdate<S>                      {withEntry,6}");
        s.Out.WriteLine($"    whose macOS entry or methods mention a spec       {withSpecs,6}");
        s.Out.WriteLine($"    with both, and as many globals as specs           {agree,6} of {both}");
        s.Out.WriteLine($"  query globals: distinct                             {globals.Count,6}  ({mentions} mentions; {other} multiplications by 0x58 of something else)");
        s.Out.WriteLine($"  specs: named by the macOS symbol table              {reading.SpecsNamed,6}  ({specs.Count} mentioned by these systems)");
        s.Out.WriteLine($"  values assigned as index * N + base                 {strides,6}");
        foreach (var (why, count) in classes.OrderByDescending(c => c.Value))
            s.Out.WriteLine($"    {count,6}  {why}");
        s.Out.WriteLine($"  updates with at least one value of one type         {oneUpdates,6}");
        if (forms > 0)
        {
            s.Out.WriteLine($"  the {Math.Min(forms, formCounts.Count)} most frequent forms of the base (S a stack local, V another local, P a parameter, G a global):");
            foreach (var (form, count) in formCounts.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(forms))
                s.Out.WriteLine($"    {count,6}  {form}");
        }
        s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  in {watch.Elapsed.TotalSeconds:F0} s"));
        return 0;
    }
}
