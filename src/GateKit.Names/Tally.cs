using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Names;

/// <summary>
/// How much of what is written down is still a machine's name and not ours.
///
/// Where a register has no upper bound - nothing says how many fields the engine has - this is the
/// measure instead: among the fields actually written down, how many still carry a name the machine
/// invented (<c>field_8</c>, <c>_Pad5</c>, <c>unknown_</c>) and not one that means something. It
/// needs no knowledge of the true total, which is the point. Padding is counted apart: <c>_Pad5</c>
/// is not a name nobody found, it is the absence of a field.
///
/// It also counts where each name came from, which is the ladder that matters: <c>game</c> and
/// <c>toolkit</c> are facts, <c>inferred</c> is reasoning from the code, <c>extender</c> is Script
/// Extender's name that nobody has checked, <c>unknown</c> says so. So the headline is not "a name
/// that is not a machine's" - a name copied from the extender is that too - but a name with
/// evidence behind it: <c>inferred</c>, <c>toolkit</c> or <c>game</c>.
///
/// This is one of the counts the project reports about itself, so it reads the committed layout
/// files and nothing else: no game, no database, no network.
/// </summary>
public sealed partial class Tally
{
    [GeneratedRegex(@"^(field_|_Pad|unknown|unk_|pad\d|dummy)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Machine();

    /// <summary>A name the machine made up, padding included.</summary>
    public static bool IsMachine(string name) => Machine().IsMatch(name);

    public static bool IsPadding(string name) => name.StartsWith("_Pad", StringComparison.Ordinal);

    public int Layouts { get; private set; }
    public int Fields { get; private set; }
    /// <summary>Every name that is not a machine's, whatever its rung.</summary>
    public int Named { get; private set; }
    /// <summary>Names at a rung with evidence behind it: inferred, toolkit or game.</summary>
    public int NamedWithEvidence { get; private set; }
    /// <summary>Script Extender's names that nobody has checked against the game's code.</summary>
    public int ExtenderUnchecked { get; private set; }
    /// <summary>Names that are not a machine's and carry no rung above unknown.</summary>
    public int NamedUnknown => Named - NamedWithEvidence - ExtenderUnchecked;
    public int MachineNamed { get; private set; }
    public int Padding { get; private set; }
    /// <summary>Machine names that are not padding: the fields that still want a reader.</summary>
    public int Owed => MachineNamed - Padding;
    /// <summary>Names that are the game's or the Toolkit's own.</summary>
    public int Facts => BySource.Where(s => s.Source is "game" or "toolkit").Sum(s => s.Fields);

    /// <summary>The rungs that put evidence behind a name.</summary>
    public static bool HasEvidence(string source) => source is "inferred" or "toolkit" or "game";

    /// <summary>(source, fields), most first; where equal, in the order first met.</summary>
    public List<(string Source, int Fields)> BySource { get; private set; } = [];
    /// <summary>(rung, names, machine names) along the ladder, strongest first, then any other source met.</summary>
    public List<(string Rung, int Named, int Machine)> ByRung { get; private set; } = [];
    /// <summary>(namespace, machine names, fields) for namespaces that carry a machine name, most first.</summary>
    public List<(string Namespace, int Machine, int Fields)> ByNamespace { get; private set; } = [];
    /// <summary>(component, machine names, fields) for every layout counted.</summary>
    public List<(string Component, int Machine, int Fields)> Components { get; } = [];

    /// <summary>The first two parts of an engine name: <c>esv::lock</c> of <c>esv::lock::Foo</c>.</summary>
    public static string NamespaceOf(string component) => string.Join("::", component.Split("::").Take(2));

    /// <summary>The layouts the measure is taken over: those in the build (confirmed or declared),
    /// not systems, with at least one field, and inside the namespace asked for.</summary>
    public static IEnumerable<LayoutFile> Counted(IEnumerable<LayoutFile> files, string prefix = "") =>
        files.Where(f => !f.IsSystem && f.InBuild && f.Fields.Count > 0
                         && (prefix.Length == 0 || f.Component.StartsWith(prefix, StringComparison.Ordinal)));

    public static Tally Measure(IEnumerable<LayoutFile> files, string prefix = "")
    {
        var tally = new Tally();
        var sources = new List<string>();
        var sourceCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var rungNamed = new Dictionary<string, int>(StringComparer.Ordinal);
        var spaces = new List<string>();
        var spaceMachine = new Dictionary<string, int>(StringComparer.Ordinal);
        var spaceTotal = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Counted(files, prefix))
        {
            tally.Layouts++;
            var space = NamespaceOf(file.Component);
            int total = 0, machine = 0;
            foreach (var field in file.Fields)
            {
                var name = LayoutFile.Text(field?["name"]) ?? "";
                var source = LayoutFile.Text(field?["source"]) is { Length: > 0 } s ? s : "(none)";
                tally.Fields++;
                total++;
                if (!sourceCount.TryAdd(source, 1)) sourceCount[source]++;
                else sources.Add(source);
                spaceTotal[space] = spaceTotal.GetValueOrDefault(space) + 1;
                if (IsPadding(name)) tally.Padding++;
                if (IsMachine(name))
                {
                    tally.MachineNamed++;
                    machine++;
                    if (!spaceMachine.TryAdd(space, 1)) spaceMachine[space]++;
                    else spaces.Add(space);
                }
                else
                {
                    tally.Named++;
                    rungNamed[source] = rungNamed.GetValueOrDefault(source) + 1;
                    if (HasEvidence(source)) tally.NamedWithEvidence++;
                    else if (source == "extender") tally.ExtenderUnchecked++;
                }
            }
            tally.Components.Add((file.Component, machine, total));
        }
        tally.BySource = sources.Select(s => (s, sourceCount[s])).OrderByDescending(s => s.Item2).ToList();
        tally.ByRung = FieldNames.Ladder.Reverse().Concat(sources.Where(s => !FieldNames.Ladder.Contains(s)))
            .Select(r => (r, rungNamed.GetValueOrDefault(r), sourceCount.GetValueOrDefault(r) - rungNamed.GetValueOrDefault(r))).ToList();
        tally.ByNamespace = spaces.Select(s => (s, spaceMachine[s], spaceTotal[s])).OrderByDescending(s => s.Item2).ToList();
        return tally;
    }

    /// <summary>The components where a reader would pay off most: the largest share of machine names
    /// among those with at least <paramref name="minFields"/> fields.</summary>
    public List<(string Component, int Machine, int Fields)> Worst(int minFields = 6, int top = 30) =>
        Components.Where(c => c.Fields >= minFields)
            .OrderByDescending(c => (double)c.Machine / c.Fields).ThenByDescending(c => c.Machine)
            .ThenByDescending(c => c.Fields).ThenByDescending(c => c.Component, StringComparer.Ordinal)
            .Take(top).ToList();

    static string Percent(int part, int whole, int digits) =>
        ((double)part / whole * 100).ToString("F" + digits, CultureInfo.InvariantCulture) + "%";

    public void Print(TextWriter output)
    {
        output.WriteLine($"fields written down: {Fields}  (in {Layouts} layouts)");
        output.WriteLine($"  named with evidence          : {NamedWithEvidence} ({Percent(NamedWithEvidence, Fields, 1)})  inferred, toolkit or game");
        output.WriteLine($"  extender's name, unchecked   : {ExtenderUnchecked} ({Percent(ExtenderUnchecked, Fields, 1)})");
        if (NamedUnknown > 0)
            output.WriteLine($"  a name at rung unknown       : {NamedUnknown} ({Percent(NamedUnknown, Fields, 1)})");
        output.WriteLine($"  the machine's                : {MachineNamed} ({Percent(MachineNamed, Fields, 1)}), of which {Padding} is padding");
        output.WriteLine($"  owed a name                  : {Owed} ({Percent(Owed, Fields, 1)})");
        output.WriteLine();
        output.WriteLine("by rung, strongest first:      names  machine's   all fields");
        foreach (var (rung, named, machine) in ByRung)
            output.WriteLine($"  {rung,-10} {named,22} {machine,10} {named + machine,8} ({Percent(named + machine, Fields, 1)})");
        output.WriteLine($"  -> established as fact (the game's or the Toolkit's own name): {Facts} ({Percent(Facts, Fields, 1)})");
        output.WriteLine();
        output.WriteLine("namespaces carrying the most machine names:");
        foreach (var (space, machine, fields) in ByNamespace.Take(15))
            output.WriteLine($"  {space,-32} {machine,4} of {fields,-5} ({Percent(machine, fields, 0)})");
    }

    public void PrintWorst(TextWriter output, int minFields, int top)
    {
        output.WriteLine($"components most in need of a reader ({minFields} or more fields):");
        foreach (var (component, machine, fields) in Worst(minFields, top))
            output.WriteLine($"  {Percent(machine, fields, 0),4}  {machine,3}/{fields,-3} {component}");
    }

    /// <summary>The counts alone; shares are left to the reader, so nothing depends on rounding.</summary>
    public JsonObject ToJson(IEnumerable<string> unreadable) => new()
    {
        ["layouts"] = Layouts,
        ["fields"] = Fields,
        ["named"] = Named,
        ["named_with_evidence"] = NamedWithEvidence,
        ["extender_unchecked"] = ExtenderUnchecked,
        ["named_unknown"] = NamedUnknown,
        ["machine"] = MachineNamed,
        ["padding"] = Padding,
        ["owed"] = Owed,
        ["facts"] = Facts,
        ["by_source"] = new JsonObject(BySource.Select(s => KeyValuePair.Create(s.Source, (JsonNode?)s.Fields))),
        ["by_rung"] = new JsonObject(ByRung.Select(r => KeyValuePair.Create(r.Rung, (JsonNode?)new JsonObject { ["named"] = r.Named, ["machine"] = r.Machine }))),
        ["by_namespace"] = new JsonArray(ByNamespace.Select(s => (JsonNode)new JsonObject
            { ["namespace"] = s.Namespace, ["machine"] = s.Machine, ["fields"] = s.Fields }).ToArray()),
        ["unreadable"] = new JsonArray(unreadable.Select(p => (JsonNode)p).ToArray()),
    };
}
