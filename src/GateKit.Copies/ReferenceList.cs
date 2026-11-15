using GateKit.Platform;

namespace GateKit.Copies;

/// <summary>What the verifier does with a <see cref="ReferenceList"/>: makes one from a report that
/// proves an install, and holds an install to one (<see cref="ReferenceListSource"/>).</summary>
public static class ReferenceLists
{
    public static Invariants ToInvariants(this ReferenceList list, string name, string file) => new()
    {
        Source = "reference",
        AppId = list.AppId,
        BuildId = list.BuildId,
        Parts = [new InvariantPart(name, list.BuildId, null, "read", list.Folders.Count + list.Files.Count, file, null)],
        Entries =
        [
            .. list.Folders.Select(f => new ExpectedEntry(f, EntryKind.Folder, 0, null, null, null, name)),
            .. list.Files.Select(f => new ExpectedEntry(f.Path, EntryKind.File, f.Size,
                f.Sha1 is null ? null : Convert.FromHexString(f.Sha1), f.Sha256 is null ? null : Convert.FromHexString(f.Sha256), null, name,
                Executable: GameFingerprint.IsKeyFile(f.Path))),
        ],
    };

    /// <summary>The list a full verification of a whole install makes, when that verification proved
    /// every listed file as delivered. Refuses anything less: a copy, a quick pass, a reference
    /// list as the proof, or any file modified, missing or unread.</summary>
    public static ReferenceList From(VerifyReport report)
    {
        if (report.Invariants is not { } inv) throw new CopyException("no source proved this install: there is nothing to write a reference list from");
        var why = inv.Source == "reference" ? "it was verified against a reference list, which cannot prove another"
            : report.Request.Mode != VerifyMode.Full ? "only a full pass reads every file"
            : report.Request.Held is not null ? "a copy holds only part of the game; write it from the installed game"
            : report.Verdict is not ("clean" or "extras") ? $"its verdict is {report.Verdict}"
            : inv.BuildId is null ? "the source names no build"
            : null;
        if (why is not null) throw new CopyException($"no reference list written: {why}");
        var folders = inv.Entries.Where(e => e.Kind == EntryKind.Folder).Select(e => e.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var files = new List<ReferenceFile>();
        foreach (var entry in inv.Entries.Where(e => e.Kind == EntryKind.File).GroupBy(e => e.Path, StringComparer.Ordinal).Select(g => g.First()))
        {
            report.Hashes.TryGetValue(entry.Path, out var hashes);
            if (entry.Size > 0 && hashes.Sha256 is null) throw new CopyException($"no reference list written: {entry.Path} was not hashed");
            files.Add(new ReferenceFile(entry.Path, entry.Size, entry.Size == 0 ? null : hashes.Sha256,
                entry.Size == 0 ? null : hashes.Sha1));
        }
        return new ReferenceList
        {
            AppId = inv.AppId,
            BuildId = inv.BuildId!,
            MadeFrom = inv.Source,
            MadeFromParts = [.. inv.Parts.Select(p => p.Version is null ? p.Id : $"{p.Id}:{p.Version}")],
            Made = report.Finished,
            Folders = folders,
            Files = [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)],
        };
    }
}
