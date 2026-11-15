using System.Globalization;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.Registry;

/// <summary>One function of the source build the pairing put in the target build, and what holds it there.</summary>
public sealed record Moved(string Id, long SourceRva, BuildRow Row, string How, bool? BodyAgrees);

/// <summary>One function the pairing could not move by itself, for a person to place: why not.</summary>
public sealed record Unmoved(string Id, long SourceRva, string Why);

/// <summary>
/// `registry reanchor`: the functions of a build table moved to another build or binary by the pairs
/// `decomp pair` stored (registry/README.md, "Fingerprints"). A function moves by itself when the
/// pairing gave it exactly one partner in the target above its measured bar (a seed or an accepted
/// pair); its new row carries the target's size and body hash. Everything else is listed for a person:
/// no pair above the bar, more than one partner, or a target table that already holds the id elsewhere.
/// </summary>
public static class Reanchor
{
    /// <summary>The plan for one source table: <paramref name="partners"/> gives, for a source start,
    /// the target starts paired with it above the bar (with how); <paramref name="place"/> the target's
    /// size and body hash at a start.</summary>
    public static (List<Moved> Moved, List<Unmoved> ForAPerson) Plan(IReadOnlyList<BuildRow> source,
        Func<long, IReadOnlyList<(long Rva, string How)>> partners, IReadOnlyList<BuildRow> existing, Func<long, (long? Size, string? Body)> place)
    {
        var moved = new List<Moved>();
        var unmoved = new List<Unmoved>();
        var held = existing.ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var row in source.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var found = partners(row.Rva).GroupBy(p => p.Rva).Select(g => g.First()).ToList();
            if (found.Count == 0)
            {
                unmoved.Add(new Unmoved(row.Id, row.Rva, "no pair above the measured bar"));
                continue;
            }
            if (found.Count > 1)
            {
                unmoved.Add(new Unmoved(row.Id, row.Rva, "paired with " + string.Join(", ", found.Select(f => RegistryStore.Hex(f.Rva)))));
                continue;
            }
            var (rva, how) = found[0];
            if (held.TryGetValue(row.Id, out var there) && there.Rva != rva)
            {
                unmoved.Add(new Unmoved(row.Id, row.Rva, $"the target's table holds it at {RegistryStore.Hex(there.Rva)}, the pair says {RegistryStore.Hex(rva)}"));
                continue;
            }
            var (size, body) = place(rva);
            bool? agrees = row.Body is null || body is null ? null : row.Body == body;
            moved.Add(new Moved(row.Id, row.Rva, new BuildRow(row.Id, rva, size, body), how, agrees));
        }
        return (moved, unmoved);
    }

    /// <summary>Runs the plan from the source binary's table into the target's, through the pairs of
    /// the pseudo-code database, and writes the target's table unless <paramref name="dry"/>.</summary>
    internal static int Run(DecompSession s, RegistryStore store, string source, string target, string? sourceBuildGiven, string? targetBuildGiven, bool dry)
    {
        var sourceBuild = store.BuildFor(source, sourceBuildGiven ?? FunctionNames.BuildAt(s, source))
            ?? throw new RegistryException($"no build of {source} has a table; say which with --source-build");
        var targetBuild = targetBuildGiven ?? FunctionNames.BuildAt(s, target)
            ?? throw new RegistryException($"the build of {target} is not recorded: `gatekit decomp stamp {target}`, or say it with --target-build");
        var db = s.DbIfExists ?? throw new SymbolsException("the pairs are read from the pseudo-code database, and there is none");
        var pairs = PairCommand.Partners(db, source);
        if (!pairs.Values.Any(list => list.Any(p => string.Equals(p.Binary, target, StringComparison.OrdinalIgnoreCase))))
            throw new RegistryException($"no pair of {source} and {target} is stored: `gatekit decomp pair --source {source} --target {target}` first");
        IReadOnlyList<(long, string)> Partners(long rva) => pairs.TryGetValue(rva, out var list)
            ? list.Where(p => string.Equals(p.Binary, target, StringComparison.OrdinalIgnoreCase))
                .Select(p => (p.Rva, string.Create(CultureInfo.InvariantCulture, $"{p.State} by {p.Rule}, score {p.Score:F2}, margin {p.Margin:F2}"))).ToList()
            : [];
        var reader = s.Symbols is null ? null : RegistryCommands.Reader(s, null);
        (long?, string?) Place(long rva)
        {
            if (reader is null) return (null, null);
            try { var p = reader.Place(target, rva); return (p.Size > 0 ? p.Size : null, p.Body); }
            catch (SymbolsException) { return (null, null); }
        }
        var existing = store.Table(targetBuild, target);
        var (moved, unmoved) = Plan(store.Table(sourceBuild, source), Partners, existing, Place);
        foreach (var m in moved)
            s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"moved  {m.Id}  {source}+0x{m.SourceRva:X} -> {target}+0x{m.Row.Rva:X}  ({m.How}; body {(m.BodyAgrees switch { true => "equal", false => "differs", null => "not compared" })})"));
        foreach (var u in unmoved)
            s.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"for a person  {u.Id}  {source}+0x{u.SourceRva:X}: {u.Why}"));
        s.Out.WriteLine($"{source} build {sourceBuild} -> {target} build {targetBuild}: {moved.Count} moved ({moved.Count(m => m.BodyAgrees == true)} with an equal body, "
            + $"{moved.Count(m => m.BodyAgrees == false)} with a different one), {unmoved.Count} for a person" + (dry ? "  [dry run: nothing written]" : ""));
        if (dry || moved.Count == 0) return 0;
        var rows = existing.Where(r => !moved.Any(m => m.Id == r.Id)).Concat(moved.Select(m => m.Row));
        store.WriteTable(targetBuild, target, rows);
        string? sha;
        try { sha = db.Scalar("SELECT sha256 FROM binary_meta WHERE binary = ? COLLATE NOCASE", target) as string; }
        catch (Microsoft.Data.Sqlite.SqliteException) { sha = null; }
        store.RecordBinary(targetBuild, target, sha);
        s.Out.WriteLine($"written: {store.Relative(store.TableFolder(targetBuild, target))}");
        return 0;
    }
}
