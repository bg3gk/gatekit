using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>What a listed entry is.</summary>
public enum EntryKind { File, Folder, Link }

/// <summary>One entry a source of invariants says the game folder holds.</summary>
/// <param name="Path">Inside the game folder, '/' between segments.</param>
/// <param name="Part">The part of the source that lists it (a Steam depot, a reference list).</param>
/// <param name="Sha1">SHA-1 of the content, when the source has it.</param>
/// <param name="Sha256">SHA-256 of the content, when the source has it; compared in preference to SHA-1.</param>
/// <param name="UserConfig">The store marks the file as the user's to change.</param>
/// <param name="Executable">The store marks the file as executable.</param>
public sealed record ExpectedEntry(string Path, EntryKind Kind, long Size, byte[]? Sha1, byte[]? Sha256, string? LinkTarget, string Part,
    bool UserConfig = false, bool Executable = false);

/// <summary>A part of a source and what became of it: a Steam depot and its manifest, a reference list.</summary>
/// <param name="Id">The depot id, or the reference list's file name.</param>
/// <param name="Version">The depot manifest id, or the reference list's build.</param>
/// <param name="Status">read, absent, unreadable or encrypted.</param>
public sealed record InvariantPart(string Id, string? Version, string? Dlc, string Status, int Entries, string? File, string? Detail);

/// <summary>A source considered for an install, and why it was or was not used.</summary>
public sealed record SourceConsidered(string Id, bool Used, string Why);

/// <summary>The expected contents of a game folder, as one source of invariants states them.</summary>
public sealed record Invariants
{
    /// <summary>The source's id: steam, reference.</summary>
    public required string Source { get; init; }
    public string? AppId { get; init; }
    public string? BuildId { get; init; }
    public required IReadOnlyList<InvariantPart> Parts { get; init; }
    public required IReadOnlyList<ExpectedEntry> Entries { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>The Steam depots, for the fingerprint; empty for other sources.</summary>
    public IReadOnlyList<SteamDepot> Depots { get; init; } = [];
    /// <summary>Every part was read: nothing the install holds went unlisted for want of a part.</summary>
    public bool Complete => Parts.All(p => p.Status == "read");
}

/// <summary>Where a source looks for an install's invariants.</summary>
/// <param name="Folder">The folder being verified (the installed game or a copy).</param>
/// <param name="Install">The installed game the folder is, or was copied from: where a store's own files are.</param>
/// <param name="BinFolder">The folder of the executable, inside <paramref name="Folder"/>.</param>
/// <param name="SteamRoot">Steam's own folder, or null.</param>
/// <param name="References">The folder of reference lists (<see cref="ReferenceList"/>), or null.</param>
/// <param name="Reference">A reference list named outright: used without matching.</param>
public sealed record InstallContext(string Folder, string Install, string BinFolder, string? SteamRoot, StringComparison Comparison,
    string? References = null, string? Reference = null);

/// <summary>
/// One place an install's expected file list can come from. The verifier knows nothing about any
/// store: it asks each source in turn and holds the folder to the first that applies.
/// </summary>
public interface IInvariantSource
{
    /// <summary>Short name, as the report shows it.</summary>
    string Id { get; }

    /// <summary>The install's invariants, or null when this source does not apply, with the reason.</summary>
    Invariants? Load(InstallContext context, out string why);
}

/// <summary>The sources GateKit knows, in the order they are asked.</summary>
public static class InvariantSources
{
    public static readonly IReadOnlyList<IInvariantSource> All = [new SteamDepotSource(), new ReferenceListSource(), new GogSource()];

    /// <summary>The first source that applies (or the one named), and what each said.</summary>
    public static (Invariants? Invariants, IReadOnlyList<SourceConsidered> Considered) Pick(InstallContext context,
        IReadOnlyList<IInvariantSource>? sources = null, string? only = null)
    {
        sources ??= All;
        if (only is not null && !sources.Any(s => s.Id == only))
            throw new CopyException($"no source of invariants called \"{only}\"; there are {string.Join(", ", sources.Select(s => s.Id))}");
        var said = new List<SourceConsidered>();
        foreach (var source in sources)
        {
            if (only is not null && source.Id != only) continue;
            var found = source.Load(context, out var why);
            said.Add(new SourceConsidered(source.Id, found is not null, why));
            if (found is not null) return (found, said);
        }
        return (null, said);
    }
}

/// <summary>Steam's own record, on this machine: the app manifest and the depot manifests in Steam's depot cache.</summary>
public sealed class SteamDepotSource : IInvariantSource
{
    public string Id => "steam";

    public Invariants? Load(InstallContext context, out string why)
    {
        SteamAppState? app;
        try { app = SteamAppState.ForFolder(context.Install, context.Comparison); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = $"Steam's app manifest could not be read: {e.Message}";
            return null;
        }
        if (app is null)
        {
            why = "not a Steam install: no steamapps/appmanifest_*.acf names this folder";
            return null;
        }
        var notes = new List<string>();
        var caches = app.DepotCaches(context.SteamRoot, context.Comparison);
        if (caches.Count == 0) notes.Add("no depot cache folder found (<Steam>/depotcache)");
        var parts = new List<InvariantPart>();
        var entries = new List<ExpectedEntry>();
        foreach (var depot in app.Depots)
        {
            var file = DepotManifest.Find(caches, depot.DepotId, depot.ManifestId);
            if (file is null)
            {
                parts.Add(new InvariantPart(depot.DepotId, depot.ManifestId, depot.DlcAppId, "absent", 0, null,
                    $"{depot.DepotId}_{depot.ManifestId}.manifest is in no depot cache on this machine ({string.Join(", ", caches)})"));
                continue;
            }
            DepotManifest manifest;
            try { manifest = DepotManifest.Read(file); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                parts.Add(new InvariantPart(depot.DepotId, depot.ManifestId, depot.DlcAppId, "unreadable", 0, file, e.Message));
                continue;
            }
            if (manifest.FilenamesEncrypted)
            {
                parts.Add(new InvariantPart(depot.DepotId, depot.ManifestId, depot.DlcAppId, "encrypted", 0, file,
                    "its file names are encrypted with the depot's key, which is not on disk"));
                continue;
            }
            parts.Add(new InvariantPart(depot.DepotId, depot.ManifestId, depot.DlcAppId, "read", manifest.Files.Count, file, null));
            foreach (var f in manifest.Files)
            {
                var kind = f.IsDirectory ? EntryKind.Folder : f.IsLink ? EntryKind.Link : EntryKind.File;
                entries.Add(new ExpectedEntry(f.Name, kind, (long)f.Size, kind == EntryKind.File ? f.Sha1 : null, null, f.LinkTarget,
                    depot.DepotId, f.IsUserConfig, (f.Flags & DepotFileFlags.Executable) != 0));
            }
        }
        if (app.SharedDepots.Count > 0)
            notes.Add("shared depots " + string.Join(", ", app.SharedDepots.Select(s => $"{s.DepotId} (app {s.AppId})"))
                + " install into another app's folder and are not held here");
        why = $"app {app.AppId} build {app.BuildId ?? "(none)"}: {app.ManifestPath}";
        return new Invariants
        {
            Source = Id, AppId = app.AppId, BuildId = app.BuildId, Parts = parts, Entries = entries, Notes = notes, Depots = app.Depots,
        };
    }
}

/// <summary>
/// A reference list of GateKit's own (<see cref="ReferenceList"/>): every file of one build with
/// its size and hashes, written by `game verify --write-reference` from an install another source
/// proved clean. Any install, from any store, is held to the list whose executables it has.
/// </summary>
public sealed class ReferenceListSource : IInvariantSource
{
    public string Id => "reference";

    public Invariants? Load(InstallContext context, out string why)
    {
        if (context.Reference is { } named)
        {
            var list = ReferenceList.Read(named);
            why = $"named: {named}";
            return list.ToInvariants(Path.GetFileName(named), named);
        }
        if (context.References is null || !Directory.Exists(context.References))
        {
            why = $"no reference lists ({context.References ?? "no folder"})";
            return null;
        }
        var executables = GameFingerprint.KeyFiles(context.Folder, context.BinFolder)
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        if (executables.Count == 0)
        {
            why = "no executable beside the game's to match a reference list by";
            return null;
        }
        var mine = executables.ToDictionary(f => f, f => GameFingerprint.Hash(context.Folder, f).Sha256, StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(context.References, "*.json").Order(StringComparer.Ordinal).ToList();
        foreach (var file in files)
        {
            ReferenceList list;
            try { list = ReferenceList.Read(file); }
            catch (Exception e) when (e is IOException or GateKit.Json.JsonProblemException) { continue; }
            if (list.MatchesExecutables(mine))
            {
                why = $"its executables are those of {Path.GetFileName(file)}";
                return list.ToInvariants(Path.GetFileName(file), file);
            }
        }
        why = $"none of the {files.Count} reference lists in {context.References} has this install's executables";
        return null;
    }
}

/// <summary>
/// GOG installs. A GOG install carries <c>goggame-&lt;product&gt;.info</c> beside the game, which names
/// the product and build but lists no files; GOG's per-file lists (its depot manifests, with MD5
/// and SHA-256) come from GOG's content servers, and no copy of them on disk is established. So
/// this source says what it found and is not implemented; such an install is verified against a
/// reference list instead.
/// </summary>
public sealed class GogSource : IInvariantSource
{
    public string Id => "gog";

    public Invariants? Load(InstallContext context, out string why)
    {
        var info = Directory.Exists(context.Install) ? Directory.EnumerateFiles(context.Install, "goggame-*.info").FirstOrDefault() : null;
        why = info is null
            ? "not a GOG install (no goggame-*.info)"
            : $"a GOG install ({Path.GetFileName(info)}), but not implemented: GOG's per-file lists are fetched from its servers and none is on disk";
        return null;
    }
}
