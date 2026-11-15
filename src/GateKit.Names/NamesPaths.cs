using GateKit.Copies;

namespace GateKit.Names;

/// <summary>A failure the user can act on: printed as it is, exit code 1.</summary>
public sealed class NamesException(string message) : Exception(message);

/// <summary>Where the record of layouts and the Script Extender fork are. Each can be said on the
/// command line or in the environment; nothing here points into a particular machine.</summary>
public static class NamesPaths
{
    static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The coverage record (the folder holding <c>layouts</c>): the option, else
    /// GATEKIT_COVERAGE, else <c>research/coverage</c> of the repository this runs from.</summary>
    public static string CoverageRoot(string? given = null)
    {
        var folder = given ?? Env("GATEKIT_COVERAGE");
        if (folder is null)
        {
            // The checkout one is standing in comes first, and the one this program was built in only
            // after it: a role working in a worktree of its own writes names into its own record, not
            // into the record of whoever built the binary it happens to run.
            var repository = Here(Directory.GetCurrentDirectory()) ?? CopySettings.RepositoryRoot()
                ?? throw new NamesException("the coverage record lives in the repository, and this is not a checkout of it; "
                    + "say where it is with --coverage or GATEKIT_COVERAGE");
            folder = Path.Combine(repository, "research", "coverage");
        }
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(Path.Combine(folder, "layouts")))
            throw new NamesException($"{folder} has no layouts folder");
        return folder;
    }

    /// <summary>The GateKit checkout a folder is inside, or null.</summary>
    public static string? Here(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GateKit.slnx")) && Directory.Exists(Path.Combine(dir.FullName, "research", "coverage", "layouts")))
                return dir.FullName;
        return null;
    }

    /// <summary>The Script Extender source tree declarations are written into: the option, else the
    /// home's (GATEKIT_SE_FORK, BG3SE_SRC or "seFork" in gatekit.json).</summary>
    public static string ForkTree(string? given = null)
    {
        var tree = given ?? GateKit.Home.GateKitHome.Current.SeFork.Path
            ?? throw new NamesException("no Script Extender source tree: say where the fork is with --tree, GATEKIT_SE_FORK "
                + "or \"seFork\" in gatekit.json (`gatekit doctor` lists every location)");
        tree = Path.GetFullPath(tree);
        if (IsSeTree(tree))
            return tree;

        // A folder that holds the fork's worktrees side by side (se-fork/bg3se, se-fork/bg3se-v34, ...):
        // one tree inside is the answer; several are named, because which one carries the run's names
        // is the user's choice and not something to guess.
        var inside = Directory.Exists(tree)
            ? Directory.GetDirectories(tree).Where(IsSeTree).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        return inside.Count switch
        {
            1 => inside[0],
            0 => throw new NamesException($"{tree} is not a Script Extender source tree (no BG3Extender/GameDefinitions), "
                + "and no folder in it is one"),
            _ => throw new NamesException($"{tree} is not a Script Extender source tree but holds {inside.Count}: "
                + string.Join(", ", inside.Select(Path.GetFileName)) + "; say which with --tree <that folder>"),
        };
    }

    static bool IsSeTree(string folder) => Directory.Exists(Path.Combine(folder, "BG3Extender", "GameDefinitions"));
}
