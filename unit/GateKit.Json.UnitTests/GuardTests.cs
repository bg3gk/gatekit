using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace GateKit.Json.UnitTests;

/// <summary>
/// The guard against new ad-hoc JSON: every place in src/ outside GateKit.Json that parses or
/// writes JSON on its own is on the allow-list beside this file, with its count and why. A file
/// that gains a site, a new file with one, and an entry whose sites are gone all fail - so the
/// list can only shrink, one migration at a time.
/// </summary>
public class GuardTests(ITestOutputHelper output)
{
    static string AllowList => Path.Combine(Repo.Root, "unit", "GateKit.Json.UnitTests", "json-sites.allow.json");

    /// <summary>The sites in src/: file (relative, with '/') -> count.</summary>
    static SortedDictionary<string, int> Scan(IReadOnlyList<Regex> patterns)
    {
        var src = Path.Combine(Repo.Root, "src");
        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Repo.Root, file).Replace('\\', '/');
            if (relative.StartsWith("src/GateKit.Json/", StringComparison.Ordinal) || relative.Contains("/bin/") || relative.Contains("/obj/")) continue;
            var text = File.ReadAllText(file);
            var count = patterns.Sum(p => p.Matches(text).Count);
            if (count > 0) found[relative] = count;
        }
        return found;
    }

    [Fact]
    public void No_new_JSON_site_appears_outside_the_core_and_the_list_only_shrinks()
    {
        var list = JsonText.Read(AllowList).AsObject();
        var patterns = list["patterns"]!.AsArray().Select(p => new Regex(JsonText.Text(p)!, RegexOptions.CultureInvariant)).ToList();
        var allowed = list["sites"]!.AsArray().Select(s => s!.AsObject()).ToDictionary(s => JsonText.Text(s["file"])!, StringComparer.Ordinal);
        var found = Scan(patterns);

        var problems = new List<string>();
        foreach (var (file, count) in found)
        {
            if (!allowed.TryGetValue(file, out var entry)) problems.Add($"{file}: {count} new JSON sites - use GateKit.Json (JsonText) instead");
            else if (count > (int)entry["count"]!) problems.Add($"{file}: {count} sites, the list allows {(int)entry["count"]!} - use GateKit.Json for the new ones");
            else if (count < (int)entry["count"]!) problems.Add($"{file}: {count} sites, the list still says {(int)entry["count"]!} - lower it (the list only shrinks)");
        }
        foreach (var file in allowed.Keys.Where(f => !found.ContainsKey(f)))
            problems.Add($"{file}: on the list, and has no JSON sites any more - remove its entry");

        foreach (var project in found.GroupBy(f => f.Key.Split('/')[1]).OrderBy(g => g.Key, StringComparer.Ordinal))
            output.WriteLine($"{project.Key,-20} {project.Sum(f => f.Value),5} sites in {project.Count(),3} files");
        output.WriteLine($"{"all",-20} {found.Values.Sum(),5} sites in {found.Count,3} files");

        if (problems.Count > 0)
        {
            // what the list would be, for a person to compare (reasons kept where an entry has one)
            var proposal = new StringBuilder();
            foreach (var (file, count) in found)
            {
                var reason = allowed.TryGetValue(file, out var e) ? JsonText.Text(e["reason"]) : "not migrated yet";
                proposal.Append(JsonText.Inline(new JsonObject { ["file"] = file, ["count"] = count, ["reason"] = reason }, compact: false)).Append(",\n");
            }
            output.WriteLine(proposal.ToString());
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
