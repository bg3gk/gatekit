using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GateKit.Json;
using GateKit.Symbols.Registry;

namespace GateKit.Symbols.Decomp;

/// <summary>What the decompiler kept open answered: the text and the milliseconds its parts took.</summary>
internal sealed record ServedText(string Text, long ApplyMs, long DecompileMs, long RoundTripMs);

/// <summary>
/// `decomp serve`: the decompiler kept open on a binary's project, read-only (DecompServe.java),
/// answering one function at a time over a channel of files in <c>&lt;work&gt;/&lt;binary&gt;/serve</c>
/// with the structures and parameter types a request brings applied first. Nothing it applies is
/// saved: the project changes only through <c>decomp refresh</c>.
/// </summary>
internal static class DecompServer
{
    static readonly UTF8Encoding Utf8 = new(false);

    public static string Channel(DecompSettings settings, string binary) => Path.Combine(settings.Work, binary, "serve");

    /// <summary>Whether a decompiler is open on the binary (its channel says it is ready).</summary>
    public static bool Ready(DecompSettings settings, string binary) => File.Exists(Path.Combine(Channel(settings, binary), "ready"));

    /// <summary>Starts the decompiler on the binary's project and waits until it ends: on `--stop`,
    /// or after <paramref name="idleMinutes"/> without a request.</summary>
    public static int Serve(DecompSession s, string binaryName, string memory, int cpu, int idleMinutes)
    {
        var binary = Path.GetFileName(binaryName);
        var work = Path.Combine(s.Settings.Work, binary);
        if (!Directory.Exists(Path.Combine(work, "project.rep")))
            throw new SymbolsException($"{work} has no project of {binary} to open; the first pass is `gatekit decomp run {binary}`");
        var channel = Channel(s.Settings, binary);
        Directory.CreateDirectory(channel);
        foreach (var old in Directory.EnumerateFiles(channel)) File.Delete(old);
        s.Err.WriteLine($"{binary}: the decompiler opens the project read-only and answers on {channel}; `gatekit decomp serve {binary} --stop` ends it");
        return GhidraCommand.Launch(s, work,
            ["-process", binary, "-noanalysis", "-readOnly", "-scriptPath", GhidraCommand.FindScripts(),
             "-postScript", "DecompServe.java", channel, idleMinutes.ToString(CultureInfo.InvariantCulture)], memory, cpu, "last_serve.json");
    }

    /// <summary>Asks a decompiler kept open on the binary to end.</summary>
    public static int Stop(DecompSession s, string binary)
    {
        var channel = Channel(s.Settings, Path.GetFileName(binary));
        if (!Ready(s.Settings, Path.GetFileName(binary)))
        {
            s.Err.WriteLine($"no decompiler is open on {binary}");
            return 1;
        }
        File.WriteAllText(Path.Combine(channel, "stop"), "", Utf8);
        s.Out.WriteLine($"{binary}: asked to stop");
        return 0;
    }

    /// <summary>Sends one request and waits for its answer; null when no decompiler is open on the
    /// binary or it does not answer within <paramref name="wait"/>. An answer of error throws.</summary>
    public static ServedText? Ask(DecompSettings settings, string binary, IReadOnlyList<string> lines, TimeSpan wait)
    {
        if (!Ready(settings, binary)) return null;
        var channel = Channel(settings, binary);
        var id = Guid.NewGuid().ToString("N");
        var writing = Path.Combine(channel, id + ".writing-request");
        var clock = Stopwatch.StartNew();
        File.WriteAllText(writing, string.Concat(lines.Select(l => l + "\n")), Utf8);
        File.Move(writing, Path.Combine(channel, id + ".request"));
        var answer = Path.Combine(channel, id + ".answer");
        while (!File.Exists(answer))
        {
            if (clock.Elapsed > wait)
            {
                File.Delete(Path.Combine(channel, id + ".request"));
                return null;
            }
            Thread.Sleep(10);
        }
        var text = File.ReadAllText(answer, Encoding.UTF8);
        long round = clock.ElapsedMilliseconds;
        File.Delete(answer);
        var newline = text.IndexOf('\n');
        var head = (newline < 0 ? text : text[..newline]).Split('\t');
        if (head[0] != "ok" || head.Length < 3)
            throw new SymbolsException($"the decompiler kept open on {binary} refused the request: {string.Join(" ", head.Skip(1))}");
        return new ServedText(text[(newline + 1)..], long.Parse(head[1], CultureInfo.InvariantCulture), long.Parse(head[2], CultureInfo.InvariantCulture), round);
    }

    /// <summary>The parameter index of a storage in the Windows x64 convention, or null for a local.</summary>
    internal static int? ParameterIndex(string storage)
    {
        string[] registers = ["reg:rcx", "reg:rdx", "reg:r8", "reg:r9"];
        var at = Array.IndexOf(registers, storage);
        if (at >= 0) return at;
        if (storage.StartsWith("reg:xmm", StringComparison.Ordinal) && int.TryParse(storage[7..], CultureInfo.InvariantCulture, out var x) && x < 4) return x;
        if (storage.StartsWith("stack:+0x", StringComparison.Ordinal)) return (int)(RegistryStore.ParseHex(storage[7..]) / 8) - 1;
        return null;
    }

    /// <summary>The lines of a structure for a request (as ApplyTypes.java reads them): every field
    /// that passes <paramref name="keep"/>, its kind and size from its type, never past the next
    /// field; a structure with no field kept is its size alone.</summary>
    internal static List<string> TypeLines(string id, JsonObject entry, Func<JsonObject, bool> keep)
    {
        var size = entry["size"]?.GetValue<long>() ?? 0;
        var fields = (entry["fields"] as JsonArray ?? []).OfType<JsonObject>().Where(keep)
            .GroupBy(f => f["offset"]!.GetValue<long>()).Select(g => g.First()).OrderBy(f => f["offset"]!.GetValue<long>()).ToList();
        var lines = new List<string>();
        for (var i = 0; i < fields.Count; i++)
        {
            var offset = fields[i]["offset"]!.GetValue<long>();
            var room = i + 1 < fields.Count ? fields[i + 1]["offset"]!.GetValue<long>() - offset : size > offset ? size - offset : 8;
            var type = (JsonText.Text(fields[i]["type"]) ?? "").Replace('\t', ' ');
            var (kind, bytes) = RecordTypes.Known(type) is { } k && k.Size <= room ? k : ("bytes", (int)Math.Min(room, 0x10000));
            var name = string.Concat((JsonText.Text(fields[i]["name"]) ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_'));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"type\t{id}\t{Math.Max(size, offset + bytes)}\t{offset:X}\t{bytes}\t{kind}\t{name}\t{type}"));
        }
        if (lines.Count == 0 && size > 0) lines.Add(string.Create(CultureInfo.InvariantCulture, $"type\t{id}\t{size}\t0\t0\tbytes\t_\tsize only"));
        return lines;
    }

    /// <summary>
    /// The request for a function from the registry: its parameters that point at a structure on an
    /// applied rung - a tie or a typed parameter, both applied - with only the applied fields of that
    /// structure; what is shown is never sent. Null when the function has no entry.
    /// </summary>
    internal static List<string> Request(long rva, JsonObject? entry, Func<string, JsonObject?> types)
    {
        var lines = new List<string>();
        var rung = JsonText.Text(entry?["rung"]);
        var parameters = (entry?["parameters"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var pointed = new List<(int Index, string Type)>();
        foreach (var tie in (entry?["ties"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!Rungs.IsApplied(JsonText.Text(tie["rung"]) ?? rung) || JsonText.Text(tie["type"]) is not { } type) continue;
            var via = parameters.FirstOrDefault(p => JsonText.Text(p["name"]) == JsonText.Text(tie["via"]));
            if (via is not null && ParameterIndex(JsonText.Text(via["storage"])!) is { } index) pointed.Add((index, type));
        }
        foreach (var p in parameters)
            if (JsonText.Text(p["type_id"]) is { } type && Rungs.IsApplied(JsonText.Text(p["rung"]) ?? rung) && ParameterIndex(JsonText.Text(p["storage"])!) is { } index)
                pointed.Add((index, type));
        foreach (var (index, type) in pointed.Distinct())
        {
            if (types(type) is not { } typeEntry) continue;
            var typeRung = JsonText.Text(typeEntry["rung"]);
            lines.AddRange(TypeLines(type, typeEntry, f => Rungs.IsApplied(JsonText.Text(f["rung"]) ?? typeRung)));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"param\t{rva:X}\t{index}\t{type}"));
        }
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"function\t{rva:X}"));
        return lines;
    }

    static int CountArrows(string text)
    {
        var n = 0;
        for (var i = text.IndexOf("->", StringComparison.Ordinal); i >= 0; i = text.IndexOf("->", i + 2, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>`decomp serve --ask`: times one function decompiled again, the given registry types put
    /// on its parameters first (every field, whatever its rung: the project is open read-only and the
    /// text is not printed). Prints the milliseconds of each repetition only.</summary>
    public static int Measure(DecompSession s, string address, IReadOnlyList<string> parameters, int count, Func<string, JsonObject?> types)
    {
        var named = NameAtCommand.Resolve(s, address) ?? throw new SymbolsException($"no function found at {address}");
        if (!Ready(s.Settings, named.Binary)) throw new SymbolsException($"no decompiler is open on {named.Binary}: `gatekit decomp serve {named.Binary}` first");
        var lines = new List<string>();
        foreach (var p in parameters)
        {
            var at = p.IndexOf('=');
            if (at <= 0 || !int.TryParse(p[..at], CultureInfo.InvariantCulture, out var index)) throw new SymbolsException($"{p}: a parameter is given as index=type (0=esv::roll::stream::Stream)");
            var type = p[(at + 1)..];
            var entry = types(type) ?? throw new SymbolsException($"{type} is not a type of the registry");
            lines.AddRange(TypeLines(type, entry, _ => true));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"param\t{named.Start:X}\t{index}\t{type}"));
        }
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"function\t{named.Start:X}"));
        if (File.ReadAllText(Path.Combine(Channel(s.Settings, named.Binary), "ready")).Trim() is { Length: > 0 } opened)
            s.Out.WriteLine($"{named.Binary}: the decompiler took {opened} ms to open the program");
        for (var i = 0; i < count; i++)
        {
            var served = Ask(s.Settings, named.Binary, i == 0 ? lines : [lines[^1]], TimeSpan.FromSeconds(120))
                ?? throw new SymbolsException($"the decompiler kept open on {named.Binary} did not answer within 120 s");
            s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{named.Binary}+0x{named.Start:X} #{i + 1}{(i == 0 && parameters.Count > 0 ? " with the types" : "")}: apply {served.ApplyMs} ms, decompile {served.DecompileMs} ms, round trip {served.RoundTripMs} ms, {served.Text.Count(c => c == '\n')} lines, {CountArrows(served.Text)} fields by name (->)"));
        }
        return 0;
    }
}
