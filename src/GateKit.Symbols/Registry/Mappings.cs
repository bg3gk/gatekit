using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace GateKit.Symbols.Registry;

/// <summary>
/// The binding layer between the registry and a Script Extender's table of byte patterns
/// (<c>BinaryMappings.xml</c>): a mapping is a pattern found once in the game's code and the
/// symbols it leads to, either the place itself or what an instruction there refers to.
/// <see cref="Check"/> reads a table written by hand and says, for every symbol, where it lands in
/// each binary of the game and what the registry calls that place; <see cref="Emit"/> writes a
/// mapping from the registry alone - one pattern that is found once in every binary the registry
/// places the function in - so that a function the registry names needs no pattern written by hand.
/// </summary>
public static class Mappings
{
    /// <summary>One pattern of a table: its bytes, which of them may be anything, and its marks.</summary>
    public sealed record Pattern(string Name, byte[] Bytes, bool[] Wild, IReadOnlyDictionary<string, int> Marks, IReadOnlyList<Target> Targets);

    /// <summary>One symbol a pattern leads to.</summary>
    public sealed record Target(string Symbol, string Type, int Offset);

    /// <summary>The patterns of a table; a mapping with no bytes or no targets is left out.</summary>
    public static IReadOnlyList<Pattern> Read(string path)
    {
        var patterns = new List<Pattern>();
        foreach (var mapping in XDocument.Load(path).Descendants("Mapping"))
        {
            var bytes = new List<byte>();
            var wild = new List<bool>();
            var marks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var text in mapping.Nodes().OfType<XText>())
                foreach (var line in text.Value.Split('\n'))
                {
                    var code = line;
                    var comment = code.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) code = code[..comment];
                    foreach (var token in code.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (token[0] == '@') marks[token] = bytes.Count;
                        else if (token == "??") { bytes.Add(0); wild.Add(true); }
                        else if (token.Length == 2 && byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) { bytes.Add(b); wild.Add(false); }
                    }
                }
            var targets = new List<Target>();
            foreach (var target in mapping.Elements("Target"))
            {
                var symbol = (string?)target.Attribute("Symbol");
                if (symbol is null) continue;
                var at = (string?)target.Attribute("Offset") ?? "0";
                var offset = marks.TryGetValue(at, out var marked) ? marked
                    : int.TryParse(at.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? at[2..] : at,
                        at.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : -1;
                if (offset >= 0) targets.Add(new Target(symbol, (string?)target.Attribute("Type") ?? "Absolute", offset));
            }
            if (bytes.Count > 0 && targets.Count > 0)
                patterns.Add(new Pattern((string?)mapping.Attribute("Name") ?? "?", [.. bytes], [.. wild], marks, targets));
        }
        return patterns;
    }

    /// <summary>The registry id a table's symbol stands for: the table writes <c>::</c> as <c>__</c>.</summary>
    public static string IdOf(string symbol) => symbol.Replace("__", "::", StringComparison.Ordinal);

    /// <summary>The table's symbol of a registry id.</summary>
    public static string SymbolOf(string id) => RegistryId.Unplace(id).Replace("::", "__", StringComparison.Ordinal);

    static Dictionary<(string Binary, long Rva), string> Named(RegistryStore store)
    {
        var named = new Dictionary<(string, long), string>();
        foreach (var (build, binary) in store.Tables())
            foreach (var row in store.Table(build, binary))
                named[(binary, row.Rva)] = row.Id;
        return named;
    }

    /// <summary>The outcome of one symbol of a table in one binary.</summary>
    public sealed record Landing(string Symbol, string Binary, int Found, long? Rva, string? Id, bool Function, bool Agrees, bool Regenerates)
    {
        /// <summary>The mapping of the table that leads here.</summary>
        public string Mapping { get; init; } = "";

        /// <summary>Whether the symbol is what an instruction of the pattern refers to, and not a place of the pattern itself.</summary>
        public bool Indirect { get; init; }
    }

    /// <summary>Every symbol of a table in every binary the registry has a table for.</summary>
    public static IReadOnlyList<Landing> Land(IReadOnlyList<Pattern> patterns, FingerprintReader reader, RegistryStore store, int limit)
    {
        var named = Named(store);
        var binaries = store.Tables().Select(t => t.Binary).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var landings = new List<Landing>();
        foreach (var pattern in patterns)
            foreach (var binary in binaries)
            {
                var places = reader.Scan(binary, pattern.Bytes, pattern.Wild);
                foreach (var target in pattern.Targets)
                {
                    long? rva = null;
                    if (places is { Count: 1 })
                        rva = target.Type.Equals("Indirect", StringComparison.OrdinalIgnoreCase) ? reader.Refers(binary, places[0] + target.Offset) : places[0] + target.Offset;
                    string? id = null;
                    var function = false;
                    var regenerates = false;
                    if (rva is { } at)
                    {
                        named.TryGetValue((binary, at), out id);
                        function = reader.StartsFunction(binary, at);
                        if (function) regenerates = reader.Signature(binary, at, limit, out var found) is not null && found == 1;
                    }
                    var agrees = id is not null && string.Equals(RegistryId.Unplace(id), IdOf(target.Symbol), StringComparison.Ordinal);
                    landings.Add(new Landing(target.Symbol, binary, places?.Count ?? -1, rva, id, function, agrees, regenerates)
                        { Mapping = pattern.Name, Indirect = target.Type.Equals("Indirect", StringComparison.OrdinalIgnoreCase) });
                }
            }
        return landings;
    }

    /// <summary>Prints where every symbol of a table lands and what the registry knows of the place; 0.</summary>
    public static int Check(string path, FingerprintReader reader, RegistryStore store, int limit, bool verbose, TextWriter output)
    {
        var patterns = Read(path);
        var landings = Land(patterns, reader, store, limit);
        var symbols = landings.Select(l => l.Symbol).Distinct(StringComparer.Ordinal).Count();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{patterns.Count} patterns, {symbols} symbols in {path}"));
        foreach (var binary in landings.Select(l => l.Binary).Distinct(StringComparer.Ordinal))
        {
            var of = landings.Where(l => l.Binary == binary).ToList();
            var landed = of.Where(l => l.Rva is not null).ToList();
            var functions = landed.Where(l => l.Function).ToList();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{binary}: {landed.Count} of {of.Count} symbols land (pattern found once); {functions.Count} at a function's start, {landed.Count - functions.Count} elsewhere (data, or inside a function)"));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  the registry names {landed.Count(l => l.Id is not null)} of the places, {landed.Count(l => l.Agrees)} under the table's own name"));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  of the {functions.Count} functions, {functions.Count(l => l.Regenerates)} have a pattern of their own start found once within {limit} bytes: a mapping the registry could write"));
            foreach (var l in of.Where(l => verbose || l.Rva is null || (l.Id is not null && !l.Agrees)))
                output.WriteLine("  " + (l.Rva is null
                    ? $"{l.Symbol}: {(l.Found < 0 ? "the binary's file cannot be read" : l.Found == 0 ? "the pattern is not found" : l.Found == 1 ? "the instruction refers to nothing" : $"the pattern is found {l.Found} times")}"
                    : string.Create(CultureInfo.InvariantCulture, $"{l.Symbol}: +0x{l.Rva:X}{(l.Function ? "" : " (no function starts here)")}{(l.Id is null ? "; the registry has no name here" : l.Agrees ? "; " + l.Id : "; the registry calls it " + l.Id)}{(l.Function && !l.Regenerates ? "; no unique pattern of its own" : "")}")));
        }
        return 0;
    }

    /// <summary>
    /// A mapping for one function, from the registry alone: the shortest run of whole instructions
    /// from its start that is found once in EVERY binary the registry places it in, a byte that
    /// differs between the binaries left open. Null with <paramref name="why"/> when there is none
    /// within the limit.
    /// </summary>
    public static string? Emit(string id, FingerprintReader reader, RegistryStore store, int limit, out string why)
    {
        why = "";
        var places = store.Places(id).Select(p => (p.Binary, p.Row.Rva)).Distinct().OrderBy(p => p.Binary, StringComparer.Ordinal).ToList();
        if (places.Count == 0) { why = "the registry places it in no binary"; return null; }
        var (first, start) = places[0];
        for (var take = 0; ; take = take == 0 ? Math.Min(12, limit) : Math.Min(limit, take * 2))
        {
            var lines = reader.Signature(first, start, limit, out _, take);
            if (lines is null) { why = $"the file of {first} cannot be read"; return null; }
            var tokens = lines.Select(l => l.Bytes.Split(' ')).ToList();
            var length = tokens.Sum(t => t.Length);
            var bytes = new byte[length];
            var wild = new bool[length];
            var k = 0;
            foreach (var token in tokens.SelectMany(t => t))
            {
                if (token == "??") wild[k] = true;
                else bytes[k] = byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                k++;
            }
            foreach (var (binary, rva) in places.Skip(1))
            {
                if (reader.BytesAt(binary, rva, length) is not { } other) { why = $"the bytes of {binary} are not in its file"; return null; }
                for (var i = 0; i < length; i++) if (!wild[i] && other[i] != bytes[i]) wild[i] = true;
            }
            var unique = true;
            foreach (var (binary, rva) in places)
            {
                var found = reader.Scan(binary, bytes, wild, 2);
                if (found is null) { why = $"the file of {binary} cannot be read"; return null; }
                if (found.Count != 1 || found[0] != rva) { unique = false; break; }
            }
            if (unique)
            {
                var b = new StringBuilder();
                b.Append("    <!-- code:").Append(id).Append("; ").AppendJoin(", ", places.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Binary}+0x{p.Rva:X}"))).AppendLine(" -->");
                b.Append("    <Mapping Name=\"").Append(RegistryId.Unplace(id)).AppendLine("\" AllowFail=\"true\">");
                k = 0;
                for (var line = 0; line < lines.Count; line++)
                {
                    var written = string.Join(' ', Enumerable.Range(k, tokens[line].Length).Select(i => wild[i] ? "??" : bytes[i].ToString("x2", CultureInfo.InvariantCulture)));
                    b.Append("      ").Append(line == 0 ? "@start " : "").Append(written).Append(" // ").AppendLine(lines[line].Text);
                    k += tokens[line].Length;
                }
                b.Append("      <Target Type=\"Absolute\" Offset=\"@start\" Symbol=\"").Append(SymbolOf(id)).AppendLine("\" />");
                b.Append("    </Mapping>");
                return b.ToString();
            }
            if (take >= limit) { why = $"no run of instructions from its start is found once in every binary within {limit} bytes"; return null; }
        }
    }
}
