using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Symbols.Decomp;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Queries;

/// <summary>
/// `symbols digest SYSTEM`: what a system's per-frame update does, in facts and without its
/// pseudo-code, short enough for a model to describe the system from it. The header: the update
/// entries per binary and the Toolkit and macOS counterparts with how sure each match is. Then per
/// binary the component operations in call order, each with the guard read off the pseudo-code
/// around its call (<see cref="PseudoGuard"/>); the constants of the guards, the strings and
/// FixedStrings referred to; the calls out (named functions, Osiris, stats, other systems); and
/// what is not known (unnamed functions, unclassified helpers, inlined component accesses, fields
/// the record has no name for). Only names, addresses, short conditions and short texts are
/// printed: never a function body.
/// </summary>
internal static partial class SystemDigest
{
    static readonly string[] Binaries = ["bg3_dx11.exe", "bg3.exe"];
    static readonly string[] Engine = ["ecs::", "ls::", "std::", "__security", "_guard_", "operator_", "memcpy", "memset", "free", "malloc"];
    const string Macos = "bg3_macos_x64";
    const int MaxText = 60;

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 1, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"TypeIndex<([^;<>]+(?:<[^;<>]*>)?)>_SystemsContext", RegexOptions.CultureInvariant)]
    private static partial Regex SystemIndex();

    [GeneratedRegex(@"TypeIndex<([^;<>]+(?:<[^;<>]*>)?)>_(?:OneFrame|Unchecked)?ComponentTypeIdContext", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentIndex();

    [GeneratedRegex(@"(?<![\w+\[])(?:0x[0-9a-fA-F]+|\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Constant();

    [GeneratedRegex(@"([A-Za-z_][\w:]*)\+0x([0-9a-f]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UnnamedField();

    /// <summary>One digest, built once and printed as text or JSON.</summary>
    internal sealed class Digest
    {
        public required string System;
        public JsonObject Json = [];
        public List<string> Lines = [];
        public int Operations, Extracted, Always, NotExtracted, Named, RawLines, RawFunctions;
    }

    public static void Write(SymbolContext c, string system, string? dll, bool json)
    {
        var db = c.Decomp.Db ?? throw new SymbolsException($"{c.Decomp.Path} does not exist: the update entries come from the pseudo-code (gatekit decomp systems)");
        var guards = new OperationGuards(db, RecordTypes.FindCoverage(null));
        var d = Build(c, db, guards, system, dll);
        if (json) c.WriteLine(d.Json.ToJsonString(Indented).ReplaceLineEndings("\n"));
        else foreach (var line in d.Lines) c.WriteLine(line);
    }

    /// <summary>`--all`: one file per system with update entries, in <paramref name="folder"/>.</summary>
    public static void WriteAll(SymbolContext c, string folder, string? dll, bool json, string? prefix = null, bool force = false)
    {
        var db = c.Decomp.Db ?? throw new SymbolsException($"{c.Decomp.Path} does not exist: the update entries come from the pseudo-code (gatekit decomp systems)");
        var guards = new OperationGuards(db, RecordTypes.FindCoverage(null));
        List<string> systems;
        try
        {
            systems = db.Query("SELECT DISTINCT u.system FROM system_updates u JOIN binaries b ON b.id = u.binary_id WHERE b.name IN (?, ?) ORDER BY u.system",
                               dll ?? Binaries[0], dll ?? Binaries[1]).Select(r => Py.Str(r[0])).ToList();
        }
        catch (SqliteException)
        {
            throw new SymbolsException($"{c.Decomp.Path} has no table system_updates; make it with:  gatekit decomp systems --rebuild --binary <binary>");
        }
        if (Repository(folder) is string repo)
            c.Note($"note: {folder} is inside the checkout {repo}; the digests are derived from the game's code and are not to be committed");
        Directory.CreateDirectory(folder);
        if (prefix != null) systems = [.. systems.Where(s => s.StartsWith(prefix, StringComparison.Ordinal))];
        int ops = 0, extracted = 0, always = 0, not = 0, named = 0, lines = 0, raw = 0, skipped = 0, n = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var system in systems)
        {
            n++;
            int at = system.LastIndexOf("::", StringComparison.Ordinal);
            string sub = Path.Combine(folder, SystemFiles.Folder(at > 0 ? system[..at] : "_"));
            string file = Path.Combine(sub, SystemFiles.Safe(at > 0 ? system[(at + 2)..] : system) + (json ? ".json" : ".txt"));
            if (!force && File.Exists(file))
            {
                skipped++;
                continue;
            }
            var started = clock.Elapsed.TotalSeconds;
            var d = Build(c, db, guards, system, dll);
            Directory.CreateDirectory(sub);
            c.Note(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{n} of {systems.Count}  {system}  {clock.Elapsed.TotalSeconds - started:F1} s  ({clock.Elapsed.TotalSeconds:F0} s in all)"));
            File.WriteAllText(file, json ? d.Json.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n" : string.Join("\n", d.Lines) + "\n");
            (ops, extracted, always, not, named, lines, raw) = (ops + d.Operations, extracted + d.Extracted, always + d.Always, not + d.NotExtracted, named + d.Named, lines + d.Lines.Count, raw + d.RawLines);
        }
        c.Note(Timing());
        // the counts of a whole pass are what `decomp tally` reports; a partial pass leaves the file alone
        if (skipped == 0 && prefix == null && dll == null)
            TallyCommand.WriteGuards(folder, new GuardTally(Py.Now(), systems.Count, ops, extracted, named, always, not));
        c.WriteLine($"{systems.Count - skipped} digests written to {folder}" + (skipped > 0 ? $" ({skipped} already there, kept; --force writes them again)" : "")
                    + string.Create(System.Globalization.CultureInfo.InvariantCulture, $" in {clock.Elapsed.TotalSeconds:F0} s") + $": {ops} operations, guards extracted for {extracted} ({named} naming a component or a field), "
                    + $"{always} unconditional, {not} not extracted; {lines} digest lines for {raw} lines of pseudo-code");
    }

    // where the time goes, for the summary of --all
    static readonly System.Diagnostics.Stopwatch FactsTime = new(), GuardsTime = new(), CounterpartsTime = new(), RestTime = new();

    static string Timing() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"time: operations walked {FactsTime.Elapsed.TotalSeconds:F0} s, guards {GuardsTime.Elapsed.TotalSeconds:F0} s, counterparts {CounterpartsTime.Elapsed.TotalSeconds:F0} s, "
        + $"calls/strings/unknowns {RestTime.Elapsed.TotalSeconds:F0} s");

    static string? Repository(string folder)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(folder)); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        return null;
    }

    sealed record Op(ComponentFact Fact, string FunctionName, GuardResult? Outer, GuardResult Inner, int Order);

    internal static Digest Build(SymbolContext c, SqliteConnection db, OperationGuards guards, string system, string? dll)
    {
        var binaries = dll != null ? [dll] : Binaries;
        var d = new Digest { System = system };
        var L = d.Lines;
        var j = d.Json;
        int split = system.LastIndexOf("::", StringComparison.Ordinal);
        j["system"] = system;
        j["namespace"] = split > 0 ? system[..split] : "";
        var updates = new List<(string Binary, long Thunk, long? Updater, string Note)>();
        try
        {
            foreach (var binary in binaries)
                foreach (var r in db.Query("SELECT u.thunk, u.updater, u.note FROM system_updates u JOIN binaries b ON b.id = u.binary_id WHERE b.name = ? AND u.system = ?", binary, system))
                    updates.Add((binary, PyText.L(r[0]), PyText.Long(r[1]), Py.Str(r[2])));
        }
        catch (SqliteException)
        {
            throw new SymbolsException($"{c.Decomp.Path} has no table system_updates; make it with:  gatekit decomp systems --rebuild --binary <binary>");
        }
        L.Add($"{system}   (namespace {j["namespace"]})");
        var ju = new JsonArray();
        j["updates"] = ju;
        if (updates.Count == 0)
        {
            L.Add($"  ? no per-frame update entry is known in {string.Join(" or ", binaries)} (gatekit decomp systems --binary ...)");
            j["unknowns"] = new JsonArray("no update entry");
            return d;
        }
        L.Add("update entries");
        foreach (var u in updates)
        {
            string name = u.Updater is long up ? guards.NameOf(u.Binary, up) : "";
            L.Add($"  {u.Binary}  entry +0x{u.Thunk:X}" + (u.Updater is long v && v != u.Thunk ? $" -> update +0x{v:X}" : u.Updater == null ? " -> update ?" : "")
                  + (name.Length > 0 && !Unnamed(name) ? $" {Cut(name, 90)}" : "") + $"   ({u.Note})");
            ju.Add(new JsonObject { ["binary"] = u.Binary, ["entry"] = Hex(u.Thunk), ["update"] = u.Updater is long w ? Hex(w) : null, ["name"] = name, ["note"] = u.Note });
        }

        // counterparts
        CounterpartsTime.Start();
        L.Add("counterparts");
        var jc = new JsonArray();
        j["counterparts"] = jc;
        foreach (var u in updates.Where(u => u.Updater != null).DistinctBy(u => (u.Binary, u.Updater)))
        {
            long rva = u.Updater!.Value;
            bool any = false;
            if (c.Game(u.Binary) is { } game)
                foreach (var r in c.Db.Query("SELECT toolkit_dll, toolkit_rva, method, relation FROM matches WHERE game_dll = ? AND game_rva = ? LIMIT 3", game.Id, rva).ToList())
                {
                    string label = c.Label(PyText.L(r[0]), PyText.L(r[1]));
                    L.Add($"  Toolkit: {u.Binary} update = {Cut(label, 100)}   (matched by {Py.Str(r[2])}, {Py.Str(r[3])})");
                    jc.Add(new JsonObject { ["kind"] = "toolkit", ["binary"] = u.Binary, ["update"] = Hex(rva), ["name"] = label, ["method"] = Py.Str(r[2]), ["relation"] = Py.Str(r[3]) });
                    any = true;
                }
            try
            {
                foreach (var r in db.Query("SELECT b.name, m.toolkit_rva, m.score, m.margin, f.name FROM code_matches m JOIN binaries b ON b.id = m.toolkit_binary"
                                           + " JOIN binaries g ON g.id = m.game_binary LEFT JOIN functions f ON f.binary_id = m.toolkit_binary AND f.rva = m.toolkit_rva"
                                           + " WHERE g.name = ? AND m.game_rva = ? ORDER BY m.score DESC LIMIT 1", u.Binary, rva).ToList())
                {
                    double score = Convert.ToDouble(r[2], CultureInfo.InvariantCulture), margin = Convert.ToDouble(r[3], CultureInfo.InvariantCulture);
                    L.Add(string.Create(CultureInfo.InvariantCulture, $"  Toolkit: {u.Binary} update ~ {Py.Str(r[0])} {Cut(Py.Str(r[4]), 90)}   (pseudo-code match, score {score:F2}, margin {margin:F2}{(score >= 0.6 && margin >= 0.2 ? "" : "; weak")})"));
                    jc.Add(new JsonObject { ["kind"] = "toolkit_code", ["binary"] = u.Binary, ["update"] = Hex(rva), ["toolkit"] = Py.Str(r[0]), ["name"] = Py.Str(r[4]), ["score"] = score, ["margin"] = margin });
                    any = true;
                }
            }
            catch (SqliteException)
            {
                // no code_matches table
            }
            if (!any) L.Add($"  Toolkit: {u.Binary} update +0x{rva:X} has no matched Toolkit function");
        }
        try
        {
            var mac = PyText.Long(db.Scalar("SELECT id FROM binaries WHERE name = ?", Macos));
            if (mac != null)
            {
                var all = c.MacFunctions ??= [.. db.Query("SELECT name, rva FROM functions WHERE binary_id = ? AND name IS NOT NULL", mac)
                    .Select(r => (Py.Str(r[0]), PyText.L(r[1]))).OrderBy(x => x.Item1, StringComparer.Ordinal)];
                string entryName = $"ecs::_private::SystemRegistrationHelper::SystemUpdate<{system}>", prefix = system + "::";
                int at = First(all, entryName);
                long entry = at < all.Count && all[at].Name == entryName ? all[at].Rva : -1;
                var methods = new List<string>();
                for (int k = First(all, prefix); k < all.Count && all[k].Name.StartsWith(prefix, StringComparison.Ordinal); k++)
                    if (all[k].Name[prefix.Length..] is var m && !m.StartsWith('~') && !methods.Contains(m)) methods.Add(m);
                L.Add("  macOS: " + (entry >= 0 ? $"entry {Macos}+0x{entry:X} SystemUpdate<...> (by name: certain)" : "no SystemUpdate entry of this name")
                      + (methods.Count > 0 ? $"; {methods.Count} methods by name: {Cut(string.Join(", ", methods), 140)}" : "; no methods by name"));
                jc.Add(new JsonObject { ["kind"] = "macos", ["entry"] = entry >= 0 ? $"{Macos}+0x{entry:X}" : null, ["methods"] = new JsonArray([.. methods.Select(m => (JsonNode)m)]), ["basis"] = "by name" });
            }
        }
        catch (SqliteException)
        {
            // no macOS build loaded
        }

        CounterpartsTime.Stop();
        var jb = new JsonObject();
        j["binaries"] = jb;
        HashSet<(string, string)>? firstKinds = null;
        foreach (var binary in updates.Select(u => u.Binary).Distinct())
        {
            var b = Binary(c, db, guards, system, binary, updates.Where(u => u.Binary == binary && u.Updater != null).Select(u => u.Updater!.Value).Distinct().Order().ToList(), d);
            jb[binary] = b.Json;
            var kinds = b.Kinds;
            if (firstKinds != null && dll == null && kinds.SetEquals(firstKinds))
            {
                L.Add($"{binary}: the same {b.Count} operations (kind and type) as {Binaries[0]}; its addresses are in --json or --dll {binary}");
                continue;
            }
            firstKinds ??= kinds;
            L.AddRange(b.Lines);
            d.Operations += b.Count;
            d.Extracted += b.Extracted;
            d.Always += b.Always;
            d.NotExtracted += b.NotExtracted;
            d.Named += b.Named;
            d.RawLines += b.RawLines;
            d.RawFunctions += b.RawFunctions;
        }
        L.Add($"measure: {L.Count + 1} digest lines; {d.RawLines} lines of pseudo-code in {d.RawFunctions} functions a reader would open");
        j["measure"] = new JsonObject { ["digest_lines"] = L.Count, ["raw_lines"] = d.RawLines, ["raw_functions"] = d.RawFunctions, ["operations"] = d.Operations,
                                        ["guards_extracted"] = d.Extracted, ["guards_named"] = d.Named, ["unconditional"] = d.Always, ["not_extracted"] = d.NotExtracted };
        return d;
    }

    sealed class BinaryPart
    {
        public JsonObject Json = [];
        public List<string> Lines = [];
        public HashSet<(string, string)> Kinds = [];
        public int Count, Extracted, Always, NotExtracted, Named, RawLines, RawFunctions;
    }

    static BinaryPart Binary(SymbolContext c, SqliteConnection db, OperationGuards guards, string system, string binary, List<long> roots, Digest d)
    {
        var part = new BinaryPart();
        var L = part.Lines;
        var binaryId = db.Scalar("SELECT id FROM binaries WHERE name = ?", binary);
        if (binaryId == null || roots.Count == 0)
        {
            L.Add($"{binary}: ? the update itself is not known");
            return part;
        }
        if (!c.Helpers.TryGetValue(binary, out var helpers)) c.Helpers[binary] = helpers = HelpersCommand.Load(db, binary);
        var helperRvas = helpers.Values.GroupBy(h => h.Type).ToDictionary(g => g.Key, g => g.Select(h => h.Rva).ToHashSet(), StringComparer.Ordinal);
        var calleesOf = new Dictionary<long, List<long>>();
        List<long> Callees(long f)
        {
            if (!calleesOf.TryGetValue(f, out var list))
                calleesOf[f] = list = db.Query("SELECT DISTINCT callee FROM calls INDEXED BY calls_caller WHERE binary_id = ? AND caller = ?", binaryId, f).Select(r => PyText.L(r[0])).ToList();
            return list;
        }
        bool EngineName(string n) => Engine.Any(e => n.StartsWith(e, StringComparison.Ordinal));

        // the operations, in call order
        FactsTime.Start();
        var facts = ComponentFacts.Of(c, binary).GetValueOrDefault(system) ?? [];
        FactsTime.Stop();
        if (!c.Sites.TryGetValue(binary, out var sites)) c.Sites[binary] = sites = new ComponentFacts.CallSites(c, binary);
        long Order(long root, long function)
        {
            var at = sites.Of(root, function);
            return at.Count > 0 ? at.Min() : long.MaxValue;
        }
        var ops = new List<Op>();
        foreach (var f in facts)
        {
            var same = facts.Where(x => x.Function == f.Function && x.Helper == f.Helper && x.Site != null).Select(x => x.Site!.Value).Order().ToList();
            int occurrence = f.Site is long s ? Math.Max(0, same.IndexOf(s)) : 0;
            GuardsTime.Start();
            var inner = guards.OfCall(binary, f.Function, f.Helper, occurrence, HelpersCommand.Name(f.Kind, f.Type));
            GuardResult? outer = null;
            long root = roots.FirstOrDefault(r => r == f.Function || Callees(r).Contains(f.Function), roots[0]);
            if (f.Depth > 1 && root != f.Function)
                outer = guards.OfCall(binary, root, f.Function, 0);
            GuardsTime.Stop();
            ops.Add(new Op(f, guards.NameOf(binary, f.Function), outer, inner, 0));
        }
        ops = [.. ops.OrderBy(o => o.Fact.Depth == 1 ? (o.Fact.Site ?? long.MaxValue) : long.MaxValue)
                     .ThenBy(o => o.Fact.Depth == 1 ? 0 : roots.Select(r => Order(r, o.Fact.Function)).Min())
                     .ThenBy(o => o.Fact.Function).ThenBy(o => o.Fact.Site ?? long.MaxValue)];

        var jops = new JsonArray();
        part.Json["operations"] = jops;
        int extracted = 0;
        foreach (var o in ops)
        {
            var f = o.Fact;
            part.Kinds.Add((f.Kind, f.Type));
            bool ok = o.Inner.Status == "extracted" || (o.Outer?.Status == "extracted");
            if (ok)
            {
                extracted++;
                if (o.Inner.Terms.Concat(o.Outer?.Terms ?? []).Any(PseudoGuard.Names)) part.Named++;
            }
            else if (o.Inner.Status == "always" && (o.Outer == null || o.Outer.Status == "always")) part.Always++;
            else part.NotExtracted++;
            jops.Add(new JsonObject
            {
                ["kind"] = f.Kind, ["type"] = f.Type, ["function"] = Hex(f.Function), ["function_name"] = Unnamed(o.FunctionName) ? null : o.FunctionName,
                ["site"] = f.Site is long s ? Hex(s) : null, ["depth"] = f.Depth, ["confidence"] = f.Confidence,
                ["guard"] = GuardJson(o.Inner), ["call_guard"] = o.Outer is null ? null : GuardJson(o.Outer),
            });
        }
        part.Count = ops.Count;
        part.Extracted = extracted;
        L.Add($"{binary}: component operations in call order ({ops.Count}; a guard extracted for {extracted}, naming a component or a field in {part.Named})");
        if (ops.Count == 0)
            L.Add($"  ? none found: inlined, deeper than depth 2, or no helpers classified (gatekit decomp helpers --binary {binary})");
        int n = 0;
        for (int i = 0; i < ops.Count; i++)
        {
            var o = ops[i];
            string where = o.Fact.Depth == 1 ? "in the update" : $"in {Short(o.FunctionName, o.Fact.Function)}";
            string guard = Guard(o);
            int same = 1;
            while (i + same < ops.Count && ops[i + same].Fact.Kind == o.Fact.Kind && ops[i + same].Fact.Type == o.Fact.Type && ops[i + same].Fact.Function == o.Fact.Function
                   && Guard(ops[i + same]) == guard)
                same++;
            L.Add($"  {++n,3} {o.Fact.Kind,-6} {o.Fact.Type}  {where}" + (o.Fact.Site is long s ? $" at +0x{s:X}" : " at ?") + (same > 1 ? $" (x{same})" : "")
                  + $"  [{o.Fact.Confidence}, depth {o.Fact.Depth}]  {Cut(guard, 260)}");
            i += same - 1;
        }

        RestTime.Start();
        // the functions the walk covers and what they call
        var depth1 = roots.SelectMany(Callees).Distinct().Order().ToList();
        var covered = roots.Concat(depth1.Where(x => !helpers.ContainsKey(x) && !EngineName(guards.NameOf(binary, x)))).Distinct().ToList();
        foreach (var f in covered)
            if (guards.Code(binary, f) is string text)
            {
                part.RawLines += text.Count(ch => ch == '\n');
                part.RawFunctions++;
            }
        var texts = covered.Select(f => (f, guards.Code(binary, f) ?? "")).ToList();

        // calls out
        var named = new SortedSet<string>(StringComparer.Ordinal);
        var unnamed = new List<(long Rva, long Size, string Name)>();
        var unknownHelpers = new SortedSet<string>(StringComparer.Ordinal);
        var osiris = new SortedSet<string>(StringComparer.Ordinal);
        var stats = new SortedSet<string>(StringComparer.Ordinal);
        var others = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var callee in depth1)
        {
            if (helpers.TryGetValue(callee, out var h))
            {
                if (h.Family is not ("add" or "remove" or "get" or "has")) unknownHelpers.Add(HelpersCommand.Name(h.Family, h.Type));
                continue;
            }
            var name = guards.NameOf(binary, callee);
            if (Unnamed(name))
                unnamed.Add((callee, PyText.L(db.Scalar("SELECT size FROM functions WHERE binary_id = ? AND rva = ?", binaryId, callee) ?? 0L), name.Length > 0 ? name : "?"));
            else if (!EngineName(name) || name.StartsWith("ls::", StringComparison.Ordinal) && !name.StartsWith("ls::Fixed", StringComparison.Ordinal))
                named.Add(Cut(name, 90));
        }
        foreach (var f in covered)
            foreach (var callee in Callees(f).Append(f))
            {
                if (helpers.TryGetValue(callee, out var h))
                {
                    if (h.Family is not ("add" or "remove" or "get" or "has" or "registration")) unknownHelpers.Add(HelpersCommand.Name(h.Family, h.Type));
                    continue;
                }
                var name = guards.NameOf(binary, callee);
                if (name.Contains("Osiris", StringComparison.Ordinal) || name.StartsWith("osi::", StringComparison.Ordinal) || name.Contains("::Story", StringComparison.Ordinal)) osiris.Add(Cut(name, 90));
                if (name.StartsWith("stats::", StringComparison.Ordinal) || name.Contains("RPGStats", StringComparison.Ordinal) || name.Contains("::stats::", StringComparison.Ordinal)) stats.Add(Cut(name, 90));
                if (Regex.Match(name, @"^([\w:]+System)::") is { Success: true } m && m.Groups[1].Value != system) others.Add(Cut(name, 90));
            }
        foreach (var (_, text) in texts)
            foreach (Match m in SystemIndex().Matches(text))
                if (m.Groups[1].Value != system) others.Add($"{m.Groups[1].Value} (reads its index: its queue or its state)");
        L.Add($"{binary}: calls out");
        L.Add($"  named ({named.Count}): " + (named.Count == 0 ? "none" : Cut(string.Join(", ", named), 400)));
        if (osiris.Count > 0) L.Add($"  Osiris: {Cut(string.Join(", ", osiris), 300)}");
        if (stats.Count > 0) L.Add($"  stats: {Cut(string.Join(", ", stats), 300)}");
        if (others.Count > 0) L.Add($"  other systems: {Cut(string.Join(", ", others), 400)}");
        part.Json["calls"] = new JsonObject
        {
            ["named"] = Array(named), ["osiris"] = Array(osiris), ["stats"] = Array(stats), ["other_systems"] = Array(others),
            ["unnamed"] = new JsonArray([.. unnamed.Select(u => (JsonNode)new JsonObject { ["function"] = Hex(u.Rva), ["size"] = u.Size })]),
        };

        // constants, strings, FixedStrings
        var constants = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var o in ops)
            foreach (var t in o.Inner.Terms.Concat(o.Outer?.Terms ?? []))
                foreach (Match m in Constant().Matches(t))
                    constants.Add(m.Value);
        var texts2 = new SortedSet<string>(StringComparer.Ordinal);
        var identifiers = new SortedSet<string>(StringComparer.Ordinal);
        var fixedStrings = new SortedSet<string>(StringComparer.Ordinal);
        var game = c.Game(binary);
        if (game != null)
            foreach (var f in covered)
                foreach (var r in c.Db.Query("SELECT s.text FROM string_refs r INDEXED BY string_refs_func JOIN strings s ON s.id = r.string_id WHERE r.func_rva = ? AND s.dll_id = ?", f, game.Value.Id))
                {
                    var s = Py.Str(r[0]);
                    (s.Contains(' ', StringComparison.Ordinal) ? texts2 : identifiers).Add($"\"{Cut(s, MaxText)}\"");
                }
        try
        {
            if (game != null)
                foreach (var f in covered)
                    foreach (var r in c.Db.Query("SELECT DISTINCT target_rva FROM xrefs INDEXED BY xrefs_func WHERE dll_id = ? AND func_rva = ?", game.Value.Id, f).ToList())
                        foreach (var fs in db.Query("SELECT text FROM fixed_strings WHERE binary_id = ? AND rva = ?", binaryId, r[0]))
                            fixedStrings.Add($"\"{Cut(Py.Str(fs[0]), MaxText)}\"");
        }
        catch (SqliteException)
        {
            // no fixed_strings table
        }
        L.Add($"{binary}: constants, strings, FixedStrings");
        L.Add("  constants in the guards: " + (constants.Count == 0 ? "none" : Cut(string.Join(", ", constants), 200)));
        if (texts2.Count > 0) L.Add($"  texts (logs, asserts): {Cut(string.Join(", ", texts2), 400)}");
        if (identifiers.Count > 0) L.Add($"  identifiers: {Cut(string.Join(", ", identifiers), 400)}");
        if (fixedStrings.Count > 0) L.Add($"  FixedStrings: {Cut(string.Join(", ", fixedStrings), 400)}");
        if (texts2.Count + identifiers.Count + fixedStrings.Count == 0) L.Add("  ? no string known");
        part.Json["constants"] = Array(constants);
        part.Json["strings"] = new JsonObject { ["texts"] = Array(texts2), ["identifiers"] = Array(identifiers), ["fixed_strings"] = Array(fixedStrings) };

        // unknowns
        var inlined = new SortedSet<string>(StringComparer.Ordinal);
        var opTypes = ops.Select(o => o.Fact.Type).ToHashSet(StringComparer.Ordinal);
        foreach (var (f, text) in texts)
            foreach (Match m in ComponentIndex().Matches(text))
                if (!(helperRvas.TryGetValue(m.Groups[1].Value, out var rvas) && Callees(f).Any(rvas.Contains)))
                    inlined.Add($"{m.Groups[1].Value} in {Short(guards.NameOf(binary, f), f)}");
        var noField = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var o in ops)
            foreach (var t in o.Inner.Terms.Concat(o.Outer?.Terms ?? []))
                foreach (Match m in UnnamedField().Matches(t))
                    if (m.Groups[1].Value.Contains("::", StringComparison.Ordinal) || char.IsUpper(m.Groups[1].Value[0]))
                        noField.Add(m.Value);
        var names = guards.Names(binary);
        var noLayout = opTypes.Where(t => !names.Fields.ContainsKey(t)).Order(StringComparer.Ordinal).ToList();
        var reasons = ops.Where(o => o.Inner.Status == "not_extracted").GroupBy(o => o.Inner.Note ?? "?").Select(g => $"{g.Count()} {g.Key}").ToList();
        L.Add($"{binary}: unknowns");
        L.Add($"  unnamed functions called by the update: {unnamed.Count}" + (unnamed.Count == 0 ? "" : " (largest: "
              + string.Join(", ", unnamed.OrderByDescending(u => u.Size).Take(6).Select(u => $"+0x{u.Rva:X} {u.Size} bytes")) + ")"));
        if (unknownHelpers.Count > 0) L.Add($"  helpers of no known family: {Cut(string.Join(", ", unknownHelpers), 300)}");
        if (inlined.Count > 0) L.Add($"  component indices read inline (no helper; an operation not listed above): {Cut(string.Join(", ", inlined), 400)}");
        if (noField.Count > 0) L.Add($"  fields the record has no name for: {Cut(string.Join(", ", noField), 300)}");
        if (noLayout.Count > 0) L.Add($"  no layout in the record (fields by offset only): {Cut(string.Join(", ", noLayout), 300)}");
        int noSite = ops.Count(o => o.Fact.Site == null);
        if (noSite > 0) L.Add($"  operations without a call site (the binary's file not read, or a call outside the function's first range): {noSite}");
        if (reasons.Count > 0) L.Add($"  guards not extracted: {string.Join("; ", reasons)}");
        L.Add("  not seen at all: operations deeper than depth 2, and what methods other than the update do");
        part.Json["unknowns"] = new JsonObject
        {
            ["unnamed_functions"] = unnamed.Count, ["unknown_helpers"] = Array(unknownHelpers), ["inlined"] = Array(inlined),
            ["fields_unnamed"] = Array(noField), ["no_layout"] = new JsonArray([.. noLayout.Select(t => (JsonNode)t)]), ["without_site"] = noSite,
        };
        RestTime.Stop();
        return part;
    }

    /// <summary>The guard of an operation: of the call in the update (depth 2) and of the helper's call.</summary>
    static string Guard(Op o) => o.Outer is null || o.Outer.Status == "always" ? o.Inner.Text
        : o.Inner.Status == "always" ? $"{o.Outer.Text} (the call of the function)" : $"{o.Outer.Text} (the call of the function); there {o.Inner.Text}";

    static JsonObject GuardJson(GuardResult g)
    {
        var o = new JsonObject { ["status"] = g.Status, ["text"] = g.Text };
        if (g.Terms.Count > 0) o["terms"] = new JsonArray([.. g.Terms.Select(t => (JsonNode)t)]);
        if (g.Partial) o["partial"] = true;
        if (g.Unnamed > 0) o["unnamed_terms"] = g.Unnamed;
        return o;
    }

    /// <summary>The first index whose name is not less than <paramref name="name"/> (ordinal).</summary>
    static int First(List<(string Name, long Rva)> sorted, string name)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (string.CompareOrdinal(sorted[mid].Name, name) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }


    static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode)i)]);

    static bool Unnamed(string name) => name.Length == 0 || name.StartsWith("FUN_", StringComparison.Ordinal) || name.StartsWith("thunk_FUN_", StringComparison.Ordinal);

    static string Short(string name, long rva) => Unnamed(name) ? $"+0x{rva:X}" : $"{Cut(name, 80)} (+0x{rva:X})";

    static string Hex(long rva) => $"0x{rva:X}";

    static string Cut(string s, int max) => s.Length > max ? s[..(max - 3)] + "..." : s;
}
