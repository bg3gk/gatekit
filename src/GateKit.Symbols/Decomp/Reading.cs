using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GateKit.Json;
using GateKit.Symbols.Registry;

namespace GateKit.Symbols.Decomp;

/// <summary>One id the registry or the live layer holds at a function's start.</summary>
internal sealed record KnownId(string Id, string Rung, bool Live);

/// <summary>One thing our reading says about a line of the decompiler's text: what the machine
/// wrote there, what we read it as, the id and rung behind it, and whether it was written into the
/// code (an applied rung) or beside it (a shown one). Lines count from 1.</summary>
internal sealed record ReadingNote(int Line, string Kind, string Machine, string Ours, string Id, string Rung, bool Applied)
{
    public JsonObject ToJson() => new()
    {
        ["line"] = Line, ["kind"] = Kind, ["machine"] = Machine, ["ours"] = Ours, ["id"] = Id, ["rung"] = Rung, ["applied"] = Applied,
    };
}

/// <summary>Our reading of one function: its text by the rule of certainty, the header above it,
/// the legend of structures below it, and every note made.</summary>
internal sealed record Reading(string Text, IReadOnlyList<string> Header, IReadOnlyList<string> Legend, IReadOnlyList<ReadingNote> Notes, IReadOnlyList<KnownId> Ids);

/// <summary>
/// What the registry and the home's live layer know of one binary in one build, as a reading needs
/// it: the ids at each function start (from the build's table and the live claims placed there) and
/// the entry of any id, the live layer's newest claim before the committed file.
/// </summary>
internal sealed class ReadingKnowledge
{
    public string Binary { get; }
    public string? Build { get; }
    readonly Dictionary<long, List<KnownId>> at = [];
    readonly Dictionary<string, JsonObject> live = new(StringComparer.Ordinal);
    readonly Func<string, JsonObject?> committed;
    readonly Func<long, (string? Keep, bool Folded)?> settled;

    public ReadingKnowledge(string binary, string? build, Func<string, JsonObject?> committed, Func<long, (string? Keep, bool Folded)?>? settled = null)
    {
        Binary = binary;
        Build = build;
        this.committed = committed;
        this.settled = settled ?? (_ => null);
    }

    /// <summary>How the ids at a function's start stand together (<see cref="Standings"/>).</summary>
    public (IdStanding Standing, string? Keep) Standing(long rva)
    {
        var ids = At(rva);
        if (ids.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() < 2) return (IdStanding.One, null);
        return Standings.Of(ids.Any(i => i.Live), settled(rva));
    }

    /// <summary>Puts an id at a function's start; the same id twice is kept once, a live one wins.</summary>
    public void Place(long rva, KnownId known)
    {
        if (!at.TryGetValue(rva, out var list)) at[rva] = list = [];
        var i = list.FindIndex(k => k.Id == known.Id);
        if (i < 0) list.Add(known);
        else if (known.Live) list[i] = known;
    }

    /// <summary>Lays a live claim's entry over the committed one of its id.</summary>
    public void Claim(string id, JsonObject entry) => live[id] = entry;

    /// <summary>The ids at a function's start, the highest rung first (the committed one on a tie).</summary>
    public IReadOnlyList<KnownId> At(long rva) =>
        at.TryGetValue(rva, out var list) ? [.. list.OrderBy(k => Rungs.Rank(k.Rung)).ThenBy(k => k.Live)] : [];

    public IEnumerable<long> Starts => at.Keys;

    /// <summary>The entry of an id: the live layer's newest claim, else the committed file; null when neither has it.</summary>
    public JsonObject? Entry(string id) => live.TryGetValue(id, out var e) ? e : committed(id);

    /// <summary>The knowledge of a binary from a checkout's registry and the live layer. The table is
    /// that of <paramref name="build"/> (the build the pseudo-code database is stamped with), else
    /// the only table the binary has; a live claim of another build is left out.</summary>
    public static ReadingKnowledge Of(RegistryStore? store, IReadOnlyList<Registry.Claim> claims, string binary, string? build, LiveLayer? live = null)
    {
        var table = store?.BuildFor(binary, build);
        var known = build ?? table;
        var k = new ReadingKnowledge(binary, known, id => store?.Get(id)?.Json, rva =>
        {
            try { return live?.Settled(binary, known, rva); }
            catch (Exception e) when (e is IOException or GateKit.Json.JsonProblemException or UnauthorizedAccessException) { return null; }
        });
        if (store is not null && table is not null)
            foreach (var row in store.Table(table, binary))
                k.Place(row.Rva, new KnownId(row.Id, JsonText.Text(store.Get(row.Id)?.Json["rung"]) ?? "unknown", false));
        foreach (var claim in claims)
        {
            if (claim.Kind == EntryKind.Function && (claim.Binary is null || !string.Equals(claim.Binary, binary, StringComparison.OrdinalIgnoreCase))) continue;
            if (claim.Build is not null && k.Build is not null && claim.Build != k.Build) continue;
            k.Claim(claim.Id, claim.Entry);
            if (claim.Kind == EntryKind.Function && claim.Rva is { } rva) k.Place(rva, new KnownId(claim.Id, claim.Rung ?? "unknown", true));
        }
        return k;
    }
}

/// <summary>
/// Our reading of a function, made from the decompiler's text and the registry as it is asked for
/// and never stored (registry/README.md, "Rungs"). What is certain is written into the code, what
/// is a guess beside it: a name, a variable or a field on an applied rung replaces the machine's
/// text; one on a shown rung leaves the machine's text and adds a comment at the end of the line
/// with the reading and its rung. A variable known only by the decompiler's temporary is always
/// shown, since its storage is not known. A field is written by name only where the variable is tied
/// to its structure, both on applied rungs, and the arithmetic is in bytes.
/// </summary>
internal static partial class OurReading
{
    [GeneratedRegex(@"\bFUN_([0-9a-fA-F]{8,16})\b")]
    private static partial Regex MachineName();

    static readonly long[] ImageBases = [0x140000000, 0x180000000];

    sealed class Lines(string text)
    {
        public readonly List<string> Text = Py.SplitLines(text);
        public readonly Dictionary<int, List<string>> Comments = [];
        public readonly List<ReadingNote> Notes = [];

        public void Note(int i, string kind, string machine, string ours, string id, string rung, bool applied)
        {
            Notes.Add(new ReadingNote(i + 1, kind, machine, ours, id, rung, applied));
            if (applied) return;
            if (!Comments.TryGetValue(i, out var list)) Comments[i] = list = [];
            var comment = $"{machine}: {ours} [{rung}]";
            if (!list.Contains(comment)) list.Add(comment);
        }

        public string Render()
        {
            var b = new StringBuilder();
            for (var i = 0; i < Text.Count; i++)
            {
                if (i > 0) b.Append('\n');
                b.Append(Text[i]);
                if (Comments.TryGetValue(i, out var list)) b.Append("   // ").Append(string.Join(" | ", list));
            }
            return b.ToString();
        }
    }

    static string RungOf(JsonNode? node, string? fallback) => JsonText.Text(node) ?? fallback ?? "unknown";

    /// <summary>Our reading of the function at <paramref name="rva"/>: <paramref name="raw"/> is the
    /// decompiler's text, <paramref name="signature"/> its signature (where a parameter's storage
    /// is read), <paramref name="textNames"/> the decompiler's own name of a function where it has
    /// a real one (rva -> name), so that a callee the decompiler named is found too.</summary>
    public static Reading Make(string raw, long rva, ReadingKnowledge k, string? signature, IReadOnlyDictionary<long, string>? textNames = null)
    {
        var lines = new Lines(raw);
        var ids = k.At(rva);
        var entry = ids.Count > 0 ? k.Entry(ids[0].Id) : null;

        // functions: the machine's name of every function the registry knows at its start
        for (var i = 0; i < lines.Text.Count; i++)
        {
            lines.Text[i] = MachineName().Replace(lines.Text[i], m =>
            {
                var address = long.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                foreach (var imageBase in ImageBases)
                    if (address >= imageBase && k.At(address - imageBase) is { Count: > 0 } there)
                        return Function(lines, i, m.Value, there, address - imageBase == rva, k.Standing(address - imageBase).Standing);
                return m.Value;
            });
            if (textNames is null) continue;
            foreach (var (start, name) in textNames)
            {
                if (name.StartsWith("FUN_", StringComparison.Ordinal) || k.At(start) is not { Count: > 0 } there || there[0].Id == name) continue;
                var pattern = new Regex(@"(?<![A-Za-z0-9_:~])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])");
                var line = i;
                lines.Text[i] = pattern.Replace(lines.Text[i], m => Function(lines, line, m.Value, there, start == rva, k.Standing(start).Standing));
            }
        }

        // variables: by storage, under the decompiler's name of that place
        var variables = new List<(string Machine, string Ours, string Rung, bool Applied, JsonObject Json)>();
        if (entry is not null)
            foreach (var v in (entry["parameters"] as JsonArray ?? []).Concat(entry["locals"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var storage = JsonText.Text(v["storage"])!;
                var machine = storage == "unresolved" ? JsonText.Text(v["decompiler_name"]) : RegistryForms.DecompilerNameOf(storage, signature);
                if (machine is null) continue;
                var rung = RungOf(v["rung"], JsonText.Text(entry["rung"]));
                variables.Add((machine, JsonText.Text(v["name"])!, rung, Rungs.IsApplied(rung) && storage != "unresolved", v));
            }
        foreach (var (machine, ours, rung, applied, _) in variables)
        {
            var word = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(machine) + @"(?![A-Za-z0-9_])");
            var first = true;
            for (var i = 0; i < lines.Text.Count; i++)
            {
                if (!word.IsMatch(lines.Text[i])) continue;
                if (applied)
                {
                    lines.Text[i] = word.Replace(lines.Text[i], ours);
                    lines.Note(i, "variable", machine, ours, ids[0].Id + "." + ours, rung, true);
                }
                else if (first) lines.Note(i, "variable", machine, ours, ids[0].Id + "." + ours, rung, false);
                first = false;
            }
        }

        // fields: through a variable tied to a structure
        var legend = new List<string>();
        var types = new List<(string Type, string Via, string Rung)>();
        if (entry is not null)
            foreach (var tie in (entry["ties"] as JsonArray ?? []).OfType<JsonObject>())
                if (JsonText.Text(tie["type"]) is { } type)
                    types.Add((type, JsonText.Text(tie["via"]) ?? "", RungOf(tie["rung"], JsonText.Text(entry["rung"]))));
        foreach (var (type, via, tieRung) in types)
        {
            var typeEntry = k.Entry(type);
            var fields = (typeEntry?["fields"] as JsonArray ?? []).OfType<JsonObject>()
                .GroupBy(f => f["offset"]!.GetValue<long>()).ToDictionary(g => g.Key, g => g.First());
            var typeRung = JsonText.Text(typeEntry?["rung"]);
            // the variable as the text spells it: ours where it was substituted, else the machine's
            var variable = variables.FirstOrDefault(v => v.Ours == via);
            var token = variable.Ours is null ? via : variable.Applied ? variable.Ours : variable.Machine;
            legend.AddRange(Legend(type, typeEntry, via, tieRung));
            if (token.Length == 0 || fields.Count == 0 || !CountsBytes(string.Join("\n", lines.Text), token)) continue;
            var v = @"(?<![A-Za-z0-9_])" + Regex.Escape(token);
            const string offset = @"(0x[0-9a-fA-F]+|\d+)";
            // a read or write through a cast, *(type *)(var + off), or the field's address, (var + off)
            var access = new Regex(@"(\*\s*\(\s*[A-Za-z_][A-Za-z0-9_ ]*\*+\s*\)\s*)?\(\s*" + v + @"\s*\+\s*" + offset + @"\s*\)");
            for (var i = 0; i < lines.Text.Count; i++)
            {
                var line = i;
                lines.Text[i] = access.Replace(lines.Text[i], m =>
                {
                    var isAddress = !m.Groups[1].Success;
                    var at = m.Groups[2].Value;
                    var value = at.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? long.Parse(at[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : long.Parse(at, CultureInfo.InvariantCulture);
                    if (!fields.TryGetValue(value, out var field)) return m.Value;
                    var name = JsonText.Text(field["name"])!;
                    var rung = RungOf(field["rung"], typeRung);
                    var applied = Rungs.IsApplied(rung) && Rungs.IsApplied(tieRung);
                    var shown = string.Create(CultureInfo.InvariantCulture, $"{token}->{name} /*+0x{value:X}*/");
                    var machine = string.Create(CultureInfo.InvariantCulture, $"{token} + 0x{value:X}");
                    lines.Note(line, "field", machine, type + "." + name, type + "." + name, Rungs.IsApplied(tieRung) ? rung : $"{rung}, tie {tieRung}", applied);
                    return !applied ? m.Value : isAddress ? "(&" + shown + ")" : shown;
                });
            }
        }
        foreach (var (machine, ours, rung, _, json) in variables)
            if (JsonText.Text(json["type_id"]) is { } typeId && !types.Any(t => t.Type == typeId))
                legend.AddRange(Legend(typeId, k.Entry(typeId), ours, rung));

        return new Reading(lines.Render(), Header(ids, k, rva), legend, lines.Notes, ids);
    }

    /// <summary>The text for a function's name where the registry knows its start: the id of the
    /// highest rung when that rung is applied, else the machine's name with the reading beside it;
    /// the other ids always beside it - of folded code, or claimed against it in a clash. The
    /// function's own name, when shown, is left to the header.</summary>
    static string Function(Lines lines, int i, string machine, IReadOnlyList<KnownId> there, bool own, IdStanding standing)
    {
        var best = there[0];
        var applied = Rungs.IsApplied(best.Rung);
        if (own && !applied) return machine;
        lines.Note(i, "function", machine, best.Id, best.Id, best.Rung, applied);
        var shown = applied ? best.Id : machine;
        foreach (var other in there.Skip(1))
            if (!own) lines.Note(i, "function", shown, (standing == IdStanding.Clash ? "also claimed, a clash: " : "the same code as ") + other.Id, other.Id, other.Rung, false);
        return shown;
    }

    /// <summary>The lines above the function: each id at its start with its rung, what it does, its
    /// evidence and the tests that prove it; or that the registry has nothing there.</summary>
    static List<string> Header(IReadOnlyList<KnownId> ids, ReadingKnowledge k, long rva)
    {
        var header = new List<string>();
        if (ids.Count == 0)
        {
            header.Add("// no entry of the registry" + (k.Build is null ? "" : $" in build {k.Build}"));
            return header;
        }
        var (standing, keep) = k.Standing(rva);
        if (Standings.Line(standing, keep, ids.Count, k.Binary, rva) is { } line) header.Add("// " + line);
        foreach (var known in ids)
        {
            var entry = k.Entry(known.Id);
            header.Add($"// {known.Id}  [{known.Rung}: {(Rungs.IsApplied(known.Rung) ? "applied, written into the code" : "shown, written beside the code")}]" + (known.Live ? " (live)" : ""));
            if (JsonText.Text(entry?["state"]) is { } state && state != "described") header.Add($"//   state: {state}");
            if (JsonText.Text(entry?["does"]) is { } does) header.Add($"//   does: {does}");
            if (entry?["guess"] is JsonObject guess)
            {
                header.Add($"//   the guess rests on: {JsonText.Text(guess["rests_on"])}");
                header.Add($"//   it would be confirmed by: {JsonText.Text(guess["would_confirm"])}");
            }
            var tests = new List<string>();
            foreach (var e in (entry?["evidence"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var kind = JsonText.Text(e["kind"]) ?? "text";
                if (JsonText.Text(e["test"]) is { } test) tests.Add(test);
                if (JsonText.Text(e["says"]) is { } says) header.Add($"//   evidence ({kind}): {says}");
            }
            header.Add(tests.Count > 0 ? "//   proven by: " + string.Join(", ", tests.Select(t => "test:" + t)) : "//   proven by: no test yet");
        }
        return header;
    }

    /// <summary>The lines of the legend for one structure the function uses.</summary>
    static IEnumerable<string> Legend(string type, JsonObject? entry, string via, string tieRung)
    {
        var typeRung = JsonText.Text(entry?["rung"]);
        var size = entry?["size"] is JsonValue s ? string.Create(CultureInfo.InvariantCulture, $", 0x{s.GetValue<long>():X} bytes") : "";
        yield return $"// {(via.Length > 0 ? via + " points at " : "")}{type}  [tie {tieRung}{(typeRung is null ? "" : ", type " + typeRung)}{size}]";
        var fields = (entry?["fields"] as JsonArray ?? []).OfType<JsonObject>().OrderBy(f => f["offset"]!.GetValue<long>()).ToList();
        if (entry is null) { yield return "//   not in the registry"; yield break; }
        if (fields.Count == 0) { yield return "//   no field named yet"; yield break; }
        foreach (var f in fields)
        {
            var rung = RungOf(f["rung"], typeRung);
            yield return string.Create(CultureInfo.InvariantCulture, $"//   +0x{f["offset"]!.GetValue<long>():X}  {JsonText.Text(f["name"])}")
                + (JsonText.Text(f["type"]) is { } t ? "  " + t : "") + $"  [{rung}]";
        }
    }

    /// <summary>Whether <c>name + n</c> in this pseudo-code counts bytes: the variable is declared
    /// as an integer or as a pointer to bytes. Not found, or a pointer to anything wider: no.</summary>
    internal static bool CountsBytes(string text, string name)
    {
        var declared = Regex.Match(text, @"(?<![A-Za-z0-9_])((?:unsigned\s+)?[A-Za-z_][A-Za-z0-9_]*)\s*(\**)\s*" + Regex.Escape(name) + @"\s*[,;)\[]");
        if (!declared.Success) return false;
        var type = declared.Groups[1].Value;
        if (declared.Groups[2].Value.Length == 0)
            return type is "longlong" or "ulonglong" or "long" or "ulong" or "undefined8" or "uint64_t" or "int64_t" or "size_t" or "uintptr_t";
        return declared.Groups[2].Value.Length == 1 && type is "char" or "uchar" or "byte" or "undefined" or "undefined1" or "void";
    }

    /// <summary>The JSON of one function for a program: the decompiler's text untouched and every
    /// note our reading makes of it.</summary>
    public static JsonObject ToJson(string binary, long rva, string? build, string raw, Reading reading) => new()
    {
        ["binary"] = binary,
        ["rva"] = RegistryStore.Hex(rva),
        ["build"] = build,
        ["ids"] = new JsonArray([.. reading.Ids.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id, ["rung"] = i.Rung, ["applied"] = Rungs.IsApplied(i.Rung), ["live"] = i.Live })]),
        ["raw"] = raw,
        ["annotations"] = new JsonArray([.. reading.Notes.Select(n => (JsonNode)n.ToJson())]),
    };
}
