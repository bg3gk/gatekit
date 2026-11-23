using System.CommandLine;
using System.Globalization;

namespace GateKit.Cli;

/// <summary>`trace-stacks`: the sampled call stacks a load trace wrote (the fork's LoadTrace switch,
/// <c>Ext.StartupVideos.LoadTraceDump</c>, its <c>.stacks.tsv</c>) summed without the game: for the
/// stages and threads asked, how many samples each function was on the stack (inclusive) and at the
/// top (leaf), and how many samples had any function of a named group on the stack - the share of a
/// whole subsystem (the Lua collector, a file reader) that no single function shows.</summary>
public static class TraceStacksCommand
{
    /// <summary>One row of the file: a stack seen <see cref="Samples"/> times.</summary>
    public sealed record Row(int Stage, string State, int Samples, string Thread, string[] Frames);

    public static List<Row> Read(string path)
    {
        var rows = new List<Row>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var cells = line.Split('\t');
            if (cells.Length < 5) continue;
            if (!int.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stage)) continue;
            if (!int.TryParse(cells[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var samples)) continue;
            rows.Add(new Row(stage, cells[1], samples, cells[3], cells[4].Split(';', StringSplitOptions.RemoveEmptyEntries)));
        }
        return rows;
    }

    /// <summary>A frame matches an address given as <c>module+0xRVA</c> or as a bare <c>0xRVA</c>
    /// (then of any module).</summary>
    public static bool Matches(string frame, string address) =>
        address.Contains('+')
            ? frame.Equals(address, StringComparison.OrdinalIgnoreCase)
            : frame.EndsWith("+" + address, StringComparison.OrdinalIgnoreCase);

    /// <summary>The sums, as lines: total, groups, inclusive and leaf tops.</summary>
    public static IEnumerable<string> Sum(IReadOnlyList<Row> rows, IReadOnlyCollection<int> stages, IReadOnlyCollection<string> threads,
        IReadOnlyCollection<string> within, IReadOnlyList<(string Name, string[] Addresses)> groups, int top, Func<IEnumerable<string>, Dictionary<string, string>>? namer)
    {
        var chosen = rows.Where(r => (stages.Count == 0 || stages.Contains(r.Stage))
                && (threads.Count == 0 || threads.Contains(r.Thread))
                && (within.Count == 0 || within.All(w => r.Frames.Any(f => Matches(f, w)))))
            .ToList();
        var total = chosen.Sum(r => r.Samples);
        yield return $"{total} samples in {chosen.Count} distinct stacks"
            + (stages.Count > 0 ? $"; stages {string.Join(",", stages)}" : "")
            + (threads.Count > 0 ? $"; threads {string.Join(",", threads)}" : "")
            + (within.Count > 0 ? $"; only stacks through {string.Join(" and ", within)}" : "");
        if (total == 0) yield break;
        string Pct(long n) => (100.0 * n / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        if (groups.Count > 0)
        {
            yield return "groups (a sample counts once when any of the group's functions is on its stack):";
            foreach (var (name, addresses) in groups)
            {
                var inGroup = chosen.Where(r => r.Frames.Any(f => addresses.Any(a => Matches(f, a)))).Sum(r => r.Samples);
                var leaf = chosen.Where(r => r.Frames.Length > 0 && addresses.Any(a => Matches(r.Frames[0], a))).Sum(r => r.Samples);
                yield return $"  {inGroup,7} {Pct(inGroup),6}  (leaf {leaf}, {Pct(leaf)})  {name}";
            }
        }

        var inclusive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var leaves = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in chosen)
        {
            foreach (var f in r.Frames.Distinct(StringComparer.OrdinalIgnoreCase))
                inclusive[f] = inclusive.GetValueOrDefault(f) + r.Samples;
            if (r.Frames.Length > 0) leaves[r.Frames[0]] = leaves.GetValueOrDefault(r.Frames[0]) + r.Samples;
        }
        var topInclusive = inclusive.OrderByDescending(p => p.Value).Take(top).ToList();
        var topLeaves = leaves.OrderByDescending(p => p.Value).Take(top).ToList();
        var names = namer?.Invoke(topInclusive.Select(p => p.Key).Concat(topLeaves.Select(p => p.Key)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(a => a.Contains("+0x", StringComparison.Ordinal) && (a.StartsWith("bg3", StringComparison.OrdinalIgnoreCase))))
            ?? new Dictionary<string, string>();
        yield return "inclusive:";
        foreach (var (f, n) in topInclusive)
            yield return $"  {n,7} {Pct(n),6}  {f}{(names.TryGetValue(f, out var name) ? "  " + name : "")}";
        yield return "leaf:";
        foreach (var (f, n) in topLeaves)
            yield return $"  {n,7} {Pct(n),6}  {f}{(names.TryGetValue(f, out var name) ? "  " + name : "")}";
    }

    public static Command Create()
    {
        var file = new Argument<string>("file") { Description = "a load trace's .stacks.tsv (Ext.StartupVideos.LoadTraceDump writes it beside the dump)" };
        var stage = new Option<int[]>("--stage") { Description = "only these stages (the dump's `== Stage N` numbers)", AllowMultipleArgumentsPerToken = true };
        var thread = new Option<string[]>("--thread") { Description = "only these thread ids (the dump names each thread's roles)", AllowMultipleArgumentsPerToken = true };
        var within = new Option<string[]>("--within") { Description = "only the stacks that pass through every one of these functions (module+0xRVA, or 0xRVA of any module)", AllowMultipleArgumentsPerToken = true };
        var group = new Option<string[]>("--group") { Description = "name=addr,addr,...: the samples with any of these functions on the stack, summed once (repeat for several groups)" };
        var top = new Option<int>("--top") { Description = "how many functions of each list", DefaultValueFactory = _ => 30 };
        var names = new Option<bool>("--names") { Description = "beside each game function, the registry's id or the databases' name (slower)" };
        var command = new Command("trace-stacks", "sum the sampled stacks of a load trace (its .stacks.tsv): per stage and thread, each function's inclusive and leaf samples and the share of named groups of functions") { file, stage, thread, within, group, top, names };
        command.SetAction(parse =>
        {
            var rows = Read(parse.GetValue(file)!);
            var groups = new List<(string, string[])>();
            foreach (var g in parse.GetValue(group) ?? [])
            {
                var eq = g.IndexOf('=');
                if (eq <= 0) { Console.Error.WriteLine($"--group {g}: name=addr,addr expected"); return 2; }
                groups.Add((g[..eq], g[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
            Func<IEnumerable<string>, Dictionary<string, string>>? namer = null;
            if (parse.GetValue(names))
                namer = addresses =>
                {
                    var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (address, id, _, other) in GateKit.Symbols.Registry.LiveCommands.NameMany(addresses,
                                 GateKit.Symbols.Registry.RegistryStore.Find(null), GateKit.Symbols.Registry.LiveLayer.OfHome(), TextWriter.Null))
                        if ((id ?? other) is { } n) named[address] = id is not null ? "code:" + id : n;
                    return named;
                };
            foreach (var line in Sum(rows, parse.GetValue(stage) ?? [], parse.GetValue(thread) ?? [], parse.GetValue(within) ?? [], groups, parse.GetValue(top), namer))
                Console.Out.WriteLine(line);
            return 0;
        });
        return command;
    }
}
