using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GateKit.Json;
using GateKit.Symbols.Pe;
using Iced.Intel;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Registry;

/// <summary>
/// The facts about a function that do not depend on the build - what finds it again after a patch
/// or in the other build, kept in its entry. Nothing here is code or text of the game: what it
/// calls by registry id (a callee without one by the hash of the callee's own facts), the text
/// constants and imports it refers to as hashes, the component types whose index it touches, and
/// its slots in virtual tables.
/// </summary>
/// <param name="Calls">Sorted, each once: a callee's registry id, <c>fp:</c> and the hash of a callee
/// without one (<see cref="Shallow"/>), <c>import:</c> and the hash of an imported function's name.</param>
/// <param name="Strings">The hashes of the text constants it loads (<see cref="TextHash"/>), sorted.</param>
/// <param name="Components">The component types whose index it touches, sorted.</param>
/// <param name="Vtables">The slots it fills: the table's name where it has one, and the slot's index.</param>
public sealed record Fingerprint(IReadOnlyList<string> Calls, IReadOnlyList<string> Strings, IReadOnlyList<string> Components,
    IReadOnlyList<(string? Table, int Slot)> Vtables)
{
    /// <summary>A body shorter than this, or of fewer instructions than <see cref="MinimumInstructions"/>, gets no hash:
    /// the few instruction sequences that short could be tried one by one until one gives the hash.</summary>
    public const int MinimumBodyBytes = 64;
    public const int MinimumInstructions = 16;

    /// <summary>What a hash is of, so that it is never mistaken for a hash of anything else.</summary>
    const string BodyDomain = "gatekit/registry/masked-body/1\n", TextDomain = "gatekit/registry/text/1\n", FactsDomain = "gatekit/registry/facts/1\n";

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (Calls.Count > 0) o["calls"] = Array(Calls);
        if (Strings.Count > 0) o["strings"] = Array(Strings);
        if (Components.Count > 0) o["components"] = Array(Components);
        if (Vtables.Count > 0)
            o["vtables"] = new JsonArray([.. Vtables.Select(v =>
            {
                var slot = new JsonObject();
                if (v.Table is not null) slot["table"] = v.Table;
                slot["slot"] = v.Slot;
                return (JsonNode)slot;
            })]);
        return o;
    }

    static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(s => (JsonNode)s)]);

    public static Fingerprint FromJson(JsonObject o) => new(
        (o["calls"] as JsonArray)?.Select(x => JsonText.Text(x)!).ToList() ?? [],
        (o["strings"] as JsonArray)?.Select(x => JsonText.Text(x)!).ToList() ?? [],
        (o["components"] as JsonArray)?.Select(x => JsonText.Text(x)!).ToList() ?? [],
        (o["vtables"] as JsonArray)?.OfType<JsonObject>().Select(v => (JsonText.Text(v["table"]), v["slot"]!.GetValue<int>())).ToList() ?? []);

    /// <summary>The hash of the facts as one text: what a caller without a name of its callee writes
    /// for it, and where the token of an unnamed id comes from.</summary>
    public string Hash() => Sha(FactsDomain + JsonText.Inline(ToJson()));

    /// <summary>A short token of the facts, for the id of a thing that has no name yet
    /// (<c>unnamed_3f2a9c1d</c>). Taken once, when the id is made; the id does not follow later changes.</summary>
    public string Token(int length = 8) => Hash()[..length];

    /// <summary>The hash a text of the binary is kept as (a string constant, an imported name): the first
    /// 16 hexadecimal digits of SHA-256. It tells two texts apart and holds none of them.</summary>
    public static string TextHash(string text) => Sha(TextDomain + text)[..16];

    static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The SHA-256 of a function's bytes with every byte that depends on where things are placed set
    /// to zero and marked: the displacement of a RIP-relative operand, the target of a near call or
    /// jump, and an immediate or displacement whose value falls inside the image (an absolute
    /// address, which a relocation would change). The hash is of the masked bytes and of the mask, so
    /// a masked byte never equals a zero of the code. Null when the body is shorter than
    /// <see cref="MinimumBodyBytes"/> or has fewer than <see cref="MinimumInstructions"/> instructions
    /// that are not padding.
    /// </summary>
    public static string? BodyHash(ReadOnlySpan<byte> code, ulong address, ulong imageBase, ulong imageEnd)
    {
        if (code.Length < MinimumBodyBytes) return null;
        var bytes = code.ToArray();
        var mask = new byte[bytes.Length];
        System.Array.Fill(mask, (byte)0xFF);
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
        var offset = 0;
        var instructions = 0;
        while (offset < bytes.Length)
        {
            decoder.IP = address + (ulong)offset;
            decoder.Decode(out var insn);
            if (insn.IsInvalid || insn.Length == 0 || offset + insn.Length > bytes.Length)
            {
                offset++;    // data inside the code (a jump table, padding): taken as it is
                continue;
            }
            if (insn.Mnemonic is not (Mnemonic.Int3 or Mnemonic.Nop)) instructions++;
            var co = decoder.GetConstantOffsets(insn);
            void Mask(int at, int size)
            {
                for (var k = at; k < at + size; k++) { bytes[offset + k] = 0; mask[offset + k] = 0; }
            }
            bool InImage(ulong value) => value >= imageBase && value < imageEnd;
            if (co.HasDisplacement)
            {
                if (insn.IsIPRelativeMemoryOperand) Mask(co.DisplacementOffset, co.DisplacementSize);
                else if (co.DisplacementSize >= 4 && InImage(insn.MemoryDisplacement64)) Mask(co.DisplacementOffset, co.DisplacementSize);
            }
            if (co.HasImmediate)
            {
                var near = insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;
                if (near && co.ImmediateSize == 4) Mask(co.ImmediateOffset, co.ImmediateSize);
                else if (co.ImmediateSize >= 4 && InImage(insn.GetImmediate(ImmediateOperand(insn)))) Mask(co.ImmediateOffset, co.ImmediateSize);
            }
            offset += insn.Length;
        }
        if (instructions < MinimumInstructions) return null;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(BodyDomain));
        sha.AppendData(bytes);
        sha.AppendData(mask);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    internal static int ImmediateOperand(in Instruction insn)
    {
        for (var i = 0; i < insn.OpCount; i++)
            if (insn.GetOpKind(i) is OpKind.Immediate8 or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate64
                or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 or OpKind.Immediate32to64)
                return i;
        return 0;
    }
}

/// <summary>The facts of a function that depend on the build: they live in the build's table.</summary>
/// <param name="Size">Bytes of the function, its split-off parts included.</param>
/// <param name="Body">The hash of its masked body (<see cref="Fingerprint.BodyHash"/>), or null.</param>
public sealed record Placement(long Size, string? Body);

/// <summary>
/// Reads fingerprints from the home's databases: the symbol database for size, calls, strings,
/// component indices and virtual tables; the binary's bytes for the body, taken only from a file
/// whose SHA-256 is the one the pseudo-code database records for that binary.
/// </summary>
public sealed class FingerprintReader(SqliteConnection symbols, SqliteConnection? decomp, Func<string, long, string?>? registryName = null)
{
    readonly Dictionary<string, long?> dlls = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (PeImage Image, string Note)?> images = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(long, long), string> shallow = [];

    /// <summary>Where the file of a binary may be (the decompiler project's copy first, then the game's).</summary>
    public Func<string, IEnumerable<string>> Candidates { get; init; } = _ => [];

    /// <summary>What happened to the body hash of the last fingerprint read, for the report.</summary>
    public string? BodyNote { get; private set; }

    long? Dll(string binary)
    {
        if (!dlls.TryGetValue(binary, out var id))
            dlls[binary] = id = Sql.Long(symbols.Scalar("SELECT id FROM dlls WHERE name = ? COLLATE NOCASE", binary));
        return id;
    }

    /// <summary>The build of the game a binary's bytes are, as the pseudo-code database records them; null when it does not say.</summary>
    public string? BuildOf(string binary) =>
        decomp is null ? null : Try(() => decomp.Scalar("SELECT game_build FROM binary_meta WHERE binary = ? COLLATE NOCASE", binary) as string);

    /// <summary>The SHA-256 of a binary's file as the pseudo-code database records it; null when it does not say.</summary>
    public string? Sha256Of(string binary) =>
        decomp is null ? null : Try(() => decomp.Scalar("SELECT sha256 FROM binary_meta WHERE binary = ? COLLATE NOCASE", binary) as string);

    static T? Try<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (SqliteException) { return null; }
    }

    List<long> Parts(long dll, long start)
    {
        var parts = new List<long> { start };
        try { parts.AddRange(symbols.Query("SELECT rva FROM chunks WHERE dll_id = ? AND parent = ?", dll, start).Select(r => Sql.Long(r[0])!.Value)); }
        catch (SqliteException) { }
        return parts;
    }

    /// <summary>The build-independent facts of the function that starts at <paramref name="start"/>.</summary>
    public Fingerprint Read(string binary, long start)
    {
        var dll = Dll(binary) ?? throw new SymbolsException($"the symbol database does not know {binary}");
        return Facts(binary, dll, start, deep: true);
    }

    Fingerprint Facts(string binary, long dll, long start, bool deep)
    {
        var parts = Parts(dll, start);
        var calls = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
            foreach (var r in symbols.Query("SELECT callee_dll, callee_rva, import FROM calls WHERE dll_id = ? AND caller_rva = ?", dll, part))
            {
                if (r[2] is string import) { calls.Add("import:" + Fingerprint.TextHash(import)); continue; }
                if (r[0] is null || r[1] is null) continue;
                var calleeDll = Sql.Long(r[0])!.Value;
                var calleeRva = Sql.Long(r[1])!.Value;
                if (!deep) { calls.Add("call"); continue; }
                var calleeBinary = calleeDll == dll ? binary : symbols.Scalar("SELECT name FROM dlls WHERE id = ?", calleeDll) as string ?? binary;
                calls.Add(registryName?.Invoke(calleeBinary, calleeRva) ?? "fp:" + Shallow(calleeBinary, calleeDll, calleeRva)[..16]);
            }
        if (!deep)
        {
            var count = calls.Count(c => c == "call");
            calls.Remove("call");
            if (count > 0) calls.Add(string.Create(CultureInfo.InvariantCulture, $"calls:{count}"));
        }

        var strings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
            foreach (var r in symbols.Query("SELECT s.text FROM string_refs x JOIN strings s ON s.id = x.string_id WHERE s.dll_id = ? AND x.func_rva = ?", dll, part))
                if (r[0] is string text) strings.Add(Fingerprint.TextHash(text));

        var components = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var part in parts)
                foreach (var r in symbols.Query("SELECT DISTINCT e.type FROM xrefs x JOIN ecs_ids e ON e.dll_id = x.dll_id AND e.index_rva = x.target_rva WHERE x.dll_id = ? AND x.func_rva = ?", dll, part))
                    if (r[0] is string type) components.Add(type);
        }
        catch (SqliteException) { }

        var vtables = new List<(string?, int)>();
        try
        {
            foreach (var r in symbols.Query(
                "SELECT s.full_name, t.idx FROM vtab_slots t JOIN vtabs v ON v.id = t.vtab_id LEFT JOIN symbols s ON s.id = v.symbol_id WHERE v.dll_id = ? AND t.rva = ?", dll, start))
                vtables.Add((r[0] as string, (int)Sql.Long(r[1])!.Value));
        }
        catch (SqliteException) { }
        vtables = [.. vtables.Distinct().OrderBy(v => v.Item1 ?? "", StringComparer.Ordinal).ThenBy(v => v.Item2)];
        return new Fingerprint([.. calls], [.. strings], [.. components], vtables);
    }

    /// <summary>The hash a callee without a registry id is written as: its own facts, one level deep
    /// (its callees counted, not followed), so that a caller's fingerprint does not depend on the build.</summary>
    string Shallow(string binary, long dll, long rva)
    {
        if (!shallow.TryGetValue((dll, rva), out var hash))
            shallow[(dll, rva)] = hash = Facts(binary, dll, rva, deep: false).Hash();
        return hash;
    }

    /// <summary>The build-dependent facts: size and the hash of the masked body (<see cref="BodyNote"/> says why there is none).</summary>
    public Placement Place(string binary, long start)
    {
        var dll = Dll(binary) ?? throw new SymbolsException($"the symbol database does not know {binary}");
        long size = 0;
        foreach (var part in Parts(dll, start))
            size += Sql.Long(symbols.Scalar("SELECT size FROM functions WHERE dll_id = ? AND rva = ?", dll, part)) ?? 0;
        if (size == 0 && decomp is not null)
            size = Sql.Long(Try(() => decomp.Scalar("SELECT f.size FROM functions f JOIN binaries b ON b.id = f.binary_id WHERE b.name = ? COLLATE NOCASE AND f.rva = ?", binary, start))) ?? 0;
        string? body = null;
        BodyNote = null;
        if (ImageOf(binary) is { } at)
        {
            var image = at.Image;
            var o = image.Offset(start);
            var own = Sql.Long(symbols.Scalar("SELECT size FROM functions WHERE dll_id = ? AND rva = ?", dll, start)) ?? size;
            if (o < 0 || own <= 0 || o + own > image.Data.Length) BodyNote = "the function's bytes are not in the file";
            else
            {
                var imageEnd = image.ImageBase + image.Sections.Select(s => (ulong)s.Va + s.Size).DefaultIfEmpty(0UL).Max();
                body = Fingerprint.BodyHash(image.Data.AsSpan(o, (int)own), image.ImageBase + (ulong)start, image.ImageBase, imageEnd);
                BodyNote = body is null ? $"too short for a hash ({own} bytes; a hash needs {Fingerprint.MinimumBodyBytes} and {Fingerprint.MinimumInstructions} instructions)" : at.Note;
            }
        }
        else BodyNote = $"no file of {binary} whose SHA-256 is the one the pseudo-code database records for it, so no body hash";
        return new Placement(size, body);
    }

    /// <summary>One instruction of a signature: its bytes as hex with <c>??</c> for a byte that a
    /// relocation or another build's layout would change, and its text.</summary>
    public sealed record SignatureLine(string Bytes, string Text);

    /// <summary>
    /// The shortest run of whole instructions from a function's start that is found once in the
    /// executable sections of the binary's file: what a hook written against a byte pattern needs.
    /// Bytes masked as in <see cref="Fingerprint.BodyHash"/> (RIP-relative displacements, near call
    /// and jump targets, absolute addresses) are wildcards. <paramref name="found"/> is how many
    /// places the pattern matches when the limit of bytes was reached (1: unique). Null when the
    /// binary's file cannot be read.
    /// </summary>
    public IReadOnlyList<SignatureLine>? Signature(string binary, long start, int limit, out int found, int minimum = 0)
    {
        found = 0;
        if (ImageOf(binary) is not { } at) return null;
        var image = at.Image;
        var o = image.Offset(start);
        if (o < 0) return null;
        var imageEnd = image.ImageBase + image.Sections.Select(s => (ulong)s.Va + s.Size).DefaultIfEmpty(0UL).Max();
        var take = Math.Min(limit + 16, image.Data.Length - o);
        var bytes = image.Data.AsSpan(o, take).ToArray();
        var wild = new bool[bytes.Length];
        var lines = new List<SignatureLine>();
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(bytes), image.ImageBase + (ulong)start);
        var formatter = new Iced.Intel.NasmFormatter();
        var length = 0;
        List<int>? candidates = null;
        while (length < limit)
        {
            decoder.Decode(out var insn);
            if (insn.IsInvalid || insn.Length == 0 || length + insn.Length > bytes.Length) break;
            var co = decoder.GetConstantOffsets(insn);
            var offset = length;
            void Mask(int from, int size) { for (var k = from; k < from + size; k++) wild[offset + k] = true; }
            bool InImage(ulong value) => value >= image.ImageBase && value < imageEnd;
            if (co.HasDisplacement && (insn.IsIPRelativeMemoryOperand || (co.DisplacementSize >= 4 && InImage(insn.MemoryDisplacement64))))
                Mask(co.DisplacementOffset, co.DisplacementSize);
            if (co.HasImmediate)
            {
                var near = insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;
                if (near || (co.ImmediateSize >= 4 && InImage(insn.GetImmediate(Fingerprint.ImmediateOperand(insn))))) Mask(co.ImmediateOffset, co.ImmediateSize);
            }
            var text = new Iced.Intel.StringOutput();
            formatter.Format(insn, text);
            lines.Add(new SignatureLine(string.Join(' ', Enumerable.Range(offset, insn.Length).Select(k => wild[k] ? "??" : bytes[k].ToString("x2", CultureInfo.InvariantCulture))), text.ToStringAndReset()));
            length += insn.Length;

            // the places the pattern so far matches: every place at first, then only those still left
            bool Matches(int place)
            {
                if (place + length > image.Data.Length) return false;
                for (var k = 0; k < length; k++) if (!wild[k] && image.Data[place + k] != bytes[k]) return false;
                return true;
            }
            if (candidates is null)
            {
                if (length < 4 || wild[0]) continue;       // too short to be worth a scan of the whole code
                candidates = [];
                foreach (var s in image.Sections.Where(s => s.Executable))
                {
                    var span = image.Data.AsSpan((int)s.Raw, (int)Math.Min(s.RawSize, (uint)(image.Data.Length - s.Raw)));
                    var from = 0;
                    while (from < span.Length)
                    {
                        var hit = span[from..].IndexOf(bytes[0]);
                        if (hit < 0) break;
                        if (Matches((int)s.Raw + from + hit)) candidates.Add((int)s.Raw + from + hit);
                        from += hit + 1;
                    }
                }
            }
            else candidates = [.. candidates.Where(Matches)];
            found = candidates.Count;
            if (found <= 1 && length >= minimum) break;   // a caller may ask for more than the shortest: a pattern for several binaries
        }
        return lines;
    }

    /// <summary>The RVAs of the instructions of a function (by its start, as the binary's own table of
    /// functions gives its end) that refer to <paramref name="target"/> RIP-relatively, in order:
    /// where a pattern can stand that finds a global. Null when the binary's file cannot be read.</summary>
    public IReadOnlyList<long>? ReferencesTo(string binary, long function, long target) =>
        IpRelative(binary, function)?.Where(r => r.Target == target).Select(r => r.At).ToList();

    /// <summary>Every RIP-relative instruction of a function, in order: (its RVA, the RVA it refers
    /// to). Null when the binary's file cannot be read.</summary>
    public IReadOnlyList<(long At, long Target)>? IpRelative(string binary, long function)
    {
        if (ImageOf(binary) is not { } at) return null;
        var image = at.Image;
        var range = GateKit.Symbols.Build.Disassembly.ListedRanges(image).FirstOrDefault(r => r.Begin == (uint)function);
        var found = new List<(long, long)>();
        var o = image.Offset(function);
        if (range.End <= range.Begin || o < 0) return found;
        var length = (int)Math.Min(range.End - range.Begin, (uint)(image.Data.Length - o));
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(image.Data, o, length), (ulong)function);
        while (decoder.IP < (ulong)function + (ulong)length)
        {
            decoder.Decode(out var insn);
            if (insn.IsInvalid) break;
            if (insn.IsIPRelativeMemoryOperand && !insn.IsCallNearIndirect && !insn.IsJmpNearIndirect) found.Add(((long)insn.IP, (long)insn.IPRelativeMemoryAddress));
        }
        return found;
    }

    /// <summary>The RVAs in the executable sections of a binary's file where a byte pattern is found
    /// (<paramref name="wild"/> marks the bytes that may be anything), at most <paramref name="cap"/>
    /// of them. Null when the binary's file cannot be read.</summary>
    public IReadOnlyList<long>? Scan(string binary, byte[] bytes, bool[] wild, int cap = 8)
    {
        if (ImageOf(binary) is not { } at) return null;
        var image = at.Image;
        var found = new List<long>();
        var anchor = Array.FindIndex(wild, w => !w);
        if (anchor < 0 || bytes.Length == 0) return found;
        foreach (var s in image.Sections.Where(s => s.Executable))
        {
            var raw = (int)s.Raw;
            var span = image.Data.AsSpan(raw, (int)Math.Min(s.RawSize, (uint)(image.Data.Length - s.Raw)));
            var from = anchor;
            while (from < span.Length)
            {
                var hit = span[from..].IndexOf(bytes[anchor]);
                if (hit < 0) break;
                var place = from + hit - anchor;
                if (place + bytes.Length <= span.Length)
                {
                    var same = true;
                    for (var k = 0; k < bytes.Length && same; k++) same = wild[k] || span[place + k] == bytes[k];
                    if (same)
                    {
                        found.Add(s.Va + place);
                        if (found.Count >= cap) return found;
                    }
                }
                from += hit + 1;
            }
        }
        return found;
    }

    /// <summary>The bytes of a binary's file at an RVA; null when they are not in the file.</summary>
    public byte[]? BytesAt(string binary, long rva, int count)
    {
        if (ImageOf(binary) is not { } at) return null;
        var o = at.Image.Offset(rva);
        return o < 0 || o + count > at.Image.Data.Length ? null : at.Image.Data.AsSpan(o, count).ToArray();
    }

    /// <summary>The RVA the instruction at an RVA refers to: the target of a near call or jump, or the
    /// address of its RIP-relative operand. Null when the instruction refers to nothing of the kind.</summary>
    public long? Refers(string binary, long rva)
    {
        if (ImageOf(binary) is not { } at || BytesAt(binary, rva, 16) is not { } bytes) return null;
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(bytes), at.Image.ImageBase + (ulong)rva);
        decoder.Decode(out var insn);
        if (insn.IsInvalid) return null;
        if (insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64) return (long)(insn.NearBranchTarget - at.Image.ImageBase);
        if (insn.IsIPRelativeMemoryOperand) return (long)(insn.IPRelativeMemoryAddress - at.Image.ImageBase);
        return null;
    }

    /// <summary>Whether the symbol database knows a function that starts at the RVA.</summary>
    public bool StartsFunction(string binary, long rva) =>
        Dll(binary) is { } dll && symbols.Scalar("SELECT 1 FROM functions WHERE dll_id = ? AND rva = ?", dll, rva) is not null;

    (PeImage Image, string Note)? ImageOf(string binary)
    {
        if (images.TryGetValue(binary, out var known)) return known;
        (PeImage, string)? found = null;
        var recorded = Sha256Of(binary);
        if (recorded is not null)
            foreach (var path in Candidates(binary))
            {
                if (!File.Exists(path)) continue;
                byte[] data;
                try { data = File.ReadAllBytes(path); }
                catch (IOException) { continue; }
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), recorded, StringComparison.OrdinalIgnoreCase)) continue;
                try { found = (new PeImage(data), $"body hashed from the bytes the decompiler read (SHA-256 {recorded[..12]}...)"); }
                catch (InvalidDataException) { continue; }
                break;
            }
        images[binary] = found;
        return found;
    }

    /// <summary>The fingerprint as one line of text, for a person.</summary>
    public static string Describe(Fingerprint f, Placement? p)
    {
        var b = new StringBuilder();
        if (p is not null) b.Append(CultureInfo.InvariantCulture, $"size {p.Size}; ");
        b.Append(CultureInfo.InvariantCulture, $"calls {f.Calls.Count(c => !c.StartsWith("fp:", StringComparison.Ordinal) && !c.StartsWith("import:", StringComparison.Ordinal))} by id, ");
        b.Append(CultureInfo.InvariantCulture, $"{f.Calls.Count(c => c.StartsWith("fp:", StringComparison.Ordinal))} by fingerprint, {f.Calls.Count(c => c.StartsWith("import:", StringComparison.Ordinal))} imports");
        b.Append(CultureInfo.InvariantCulture, $"; strings {f.Strings.Count}; components {f.Components.Count}; vtable slots {f.Vtables.Count}");
        if (p is not null) b.Append("; body ").Append(p.Body is null ? "none" : p.Body[..16] + "...");
        return b.ToString();
    }
}
