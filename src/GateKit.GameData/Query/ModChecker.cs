using System.Text.RegularExpressions;
using GateKit.GameData.Index;
using GateKit.GameData.Resources;
using GateKit.GameData.Text;
using LSLib.LS;

namespace GateKit.GameData.Query;

/// <summary>
/// Checks a mod against the index: references that lead nowhere, and what the mod overrides. A
/// reference is a uuid, a stats name, a text handle or a file path written in the mod's data; it
/// is fine when the mod itself defines the target or the game does. A mod is a folder (with Mods/
/// and Public/ inside) or a .pak, read directly.
/// </summary>
public static partial class ModChecker
{
    private static readonly HashSet<string> OwnIdAttrs = new(StringComparer.Ordinal)
    {
        "MapKey", "UUID", "ID", "Id", "Guid", "GUID", "Identifier", "LineId", "_OriginalFileVersion_",
    };

    private const string ZeroUuid = "00000000-0000-0000-0000-000000000000";

    [GeneratedRegex(@"^(h[0-9a-f]{8}g[0-9a-f]{4}g[0-9a-f]{4}g[0-9a-f]{4}g[0-9a-f]{12})(;\d+)?$")]
    private static partial Regex HandleValue();

    [GeneratedRegex("contentuid=\"(h[0-9a-fg]+)\"")]
    private static partial Regex ContentUid();

    /// <summary>The files of a mod: path inside the mod and a way to read the bytes.</summary>
    private static List<(string Rel, Func<byte[]> Read)> ModFiles(string source, List<IDisposable> keep)
    {
        if (File.Exists(source))
        {
            var package = new PackageReader().Read(source);
            keep.Add(package);
            return package.Files.Where(f => !f.IsDeletion())
                .Select(f => (f.Name.Replace('\\', '/'), (Func<byte[]>)(() => Packages.GamePackages.ReadAll(f)))).ToList();
        }
        return Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(source, f).Replace('\\', '/'), (Func<byte[]>)(() => File.ReadAllBytes(f)))).ToList();
    }

    public static void Check(IndexQueries q, string path, int examples, bool json, int limit)
    {
        var db = q.Db;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new DataQueryException($"not found: {path}");
        }
        var keep = new List<IDisposable>();
        try
        {
            var files = ModFiles(path, keep);
            // A binary .lsf is read as the .lsx it converts to; both names count as the mod's files.
            var dataFiles = new List<(string Rel, Func<byte[]> Read, bool Lsf)>();
            foreach (var (rel, read) in files)
            {
                dataFiles.Add((rel, read, false));
            }
            foreach (var (rel, read) in files.Where(f => f.Rel.EndsWith(".lsf", StringComparison.OrdinalIgnoreCase)))
            {
                dataFiles.Add((rel[..^4] + ".lsx", read, true));
            }
            var ownFiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (rel, _, _) in dataFiles)
            {
                ownFiles.Add(rel.ToLowerInvariant());
                if (rel.EndsWith(".lsx", StringComparison.OrdinalIgnoreCase))
                {
                    ownFiles.Add(rel.ToLowerInvariant()[..^4] + ".lsf");
                }
            }

            var objects = new List<(string Rel, GenericObject Object)>();
            var problemsRead = 0;
            var ownKeys = new HashSet<string>(StringComparer.Ordinal);
            var ownHandles = new HashSet<string>(StringComparer.Ordinal);
            var ownStats = new HashSet<string>(StringComparer.Ordinal);
            var statsEntries = new List<(string Rel, StatEntry Entry)>();
            foreach (var (rel, read, lsf) in dataFiles)
            {
                var low = rel.ToLowerInvariant();
                if (low.EndsWith(".lsx", StringComparison.Ordinal))
                {
                    var reader = new GenericReader();
                    try
                    {
                        using var stream = new MemoryStream(read(), writable: false);
                        ResourceEvents.Read(stream, !lsf, reader);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        problemsRead++;
                    }
                    foreach (var o in reader.Result().Objects)
                    {
                        objects.Add((rel, o));
                        if (o.Key != null)
                        {
                            ownKeys.Add(o.Key.ToLowerInvariant());
                        }
                    }
                }
                else if (low.EndsWith(".xml", StringComparison.Ordinal) && ("/" + low).Contains("/localization/", StringComparison.Ordinal))
                {
                    foreach (Match m in ContentUid().Matches(IndexBuilder.DecodeText(read())))
                    {
                        ownHandles.Add(m.Groups[1].Value);
                    }
                }
                else if (low.EndsWith(".loca", StringComparison.Ordinal))
                {
                    try
                    {
                        using var stream = new MemoryStream(read(), writable: false);
                        using var loca = new LocaReader(stream);
                        ownHandles.UnionWith(loca.Read().Entries.Select(e => e.Key));
                    }
                    catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
                    {
                        problemsRead++;
                    }
                }
                else if (low.EndsWith(".txt", StringComparison.Ordinal) && low.Contains("/stats/generated/", StringComparison.Ordinal))
                {
                    foreach (var s in TextParsers.ReadStats(IndexBuilder.DecodeText(read())))
                    {
                        ownStats.Add(s.Name);
                        statsEntries.Add((rel, s));
                    }
                }
            }

            var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool InGame(string uuid)
            {
                if (!cache.TryGetValue(uuid, out var found))
                {
                    found = db.One("SELECT 1 FROM objects WHERE key = ? LIMIT 1", uuid) != null
                            || db.One("SELECT 1 FROM templates WHERE uuid = ? UNION ALL SELECT 1 FROM entities WHERE uuid = ?"
                                      + " UNION ALL SELECT 1 FROM dialogs WHERE uuid = ? UNION ALL SELECT 1 FROM timelines WHERE uuid = ?"
                                      + " UNION ALL SELECT 1 FROM speaker_groups WHERE uuid = ? LIMIT 1", uuid, uuid, uuid, uuid, uuid) != null;
                    cache[uuid] = found;
                }
                return found;
            }

            var fileCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            var missing = new Dictionary<(string Kind, string Attr), List<(string File, string Object, string Value)>>();
            void Miss(string kind, string attr, string file, string obj, string value)
            {
                if (!missing.TryGetValue((kind, attr), out var list))
                {
                    missing[(kind, attr)] = list = [];
                }
                list.Add((file, obj, value));
            }
            var overrides = new List<(string File, string Object, string Key)>();
            var references = 0;
            foreach (var (rel, o) in objects)
            {
                if (rel.EndsWith("meta.lsx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;                                       // module ids and dependencies on other mods
                }
                var label = $"{o.Node} {o.Name ?? o.Key ?? ""}".Trim();
                if (o.Key != null && Q.IsUuid(o.Key) && InGame(o.Key.ToLowerInvariant()))
                {
                    overrides.Add((rel, label, o.Key));
                }
                foreach (var (attr, value) in o.Attrs)
                {
                    var leaf = attr[(attr.LastIndexOf('.') + 1)..];
                    if (string.IsNullOrEmpty(value) || OwnIdAttrs.Contains(leaf))
                    {
                        continue;
                    }
                    foreach (var raw in value.Split(';'))
                    {
                        var part = raw.Trim();
                        if (Q.IsUuid(part) && part != ZeroUuid)
                        {
                            references++;
                            if (!ownKeys.Contains(part.ToLowerInvariant()) && !InGame(part.ToLowerInvariant()))
                            {
                                Miss("uuid", attr, rel, label, part);
                            }
                        }
                    }
                    var m = HandleValue().Match(value);
                    if (m.Success)
                    {
                        references++;
                        var handle = m.Groups[1].Value;
                        if (!ownHandles.Contains(handle) && db.One("SELECT 1 FROM loca WHERE handle = ? LIMIT 1", handle) == null)
                        {
                            Miss("text", attr, rel, label, handle);
                        }
                    }
                    else if (IndexQueries.FileValue().IsMatch(value) && value.Replace('\\', '/').Contains('/', StringComparison.Ordinal))
                    {
                        references++;
                        var p = value.Replace('\\', '/');
                        var tail = "/" + p.ToLowerInvariant();
                        // a path may be written from the package root or relative to the mod's Public folder
                        if (ownFiles.Contains(p.ToLowerInvariant()) || ownFiles.Any(f => f.EndsWith(tail, StringComparison.Ordinal)))
                        {
                            continue;
                        }
                        if (!fileCache.TryGetValue(p, out var exists))
                        {
                            exists = db.One("SELECT 1 FROM files WHERE path = ? OR path = ? LIMIT 1", p, "Generated/" + p) != null
                                     || db.One("SELECT 1 FROM files WHERE path LIKE ? ESCAPE '\\' LIMIT 1", "%/" + Q.Like(p)[1..^1]) != null;
                            fileCache[p] = exists;
                        }
                        if (!exists)
                        {
                            Miss("file", attr, rel, label, value);
                        }
                    }
                }
            }
            foreach (var (rel, s) in statsEntries)
            {
                if (!string.IsNullOrEmpty(s.Parent))
                {
                    references++;
                    if (!ownStats.Contains(s.Parent) && db.One("SELECT 1 FROM stats WHERE name = ? LIMIT 1", s.Parent) == null)
                    {
                        Miss("stat", "using", rel, s.Name, s.Parent);
                    }
                }
                if (db.One("SELECT 1 FROM stats WHERE name = ? LIMIT 1", s.Name) != null)
                {
                    overrides.Add((rel, $"stat {s.Name}", s.Name));
                }
                foreach (var (field, value) in s.Fields)
                {
                    foreach (var part in value.Split(';'))
                    {
                        if (Q.IsUuid(part) && part != ZeroUuid)
                        {
                            references++;
                            if (!ownKeys.Contains(part.ToLowerInvariant()) && !InGame(part.ToLowerInvariant()))
                            {
                                Miss("uuid", $"stat field {field}", rel, s.Name, part);
                            }
                        }
                    }
                }
            }

            // Does this attribute normally hold a reference? Look at what the game itself writes there.
            double? Usual(string attr)
            {
                var id = db.Scalar("SELECT id FROM names WHERE name = ?", attr);
                if (id == null)
                {
                    return null;
                }
                var sample = db.Rows("SELECT value FROM attrs WHERE attr_id = ? AND length(value) = 36 LIMIT 40", id)
                    .Select(r => r.S("value")!).Where(v => Q.IsUuid(v) && v != ZeroUuid).ToList();
                if (sample.Count == 0)
                {
                    return null;
                }
                return sample.Count(v => InGame(v.ToLowerInvariant())) / (double)sample.Count;
            }

            var findings = new List<Dictionary<string, object?>>();
            foreach (var ((kind, attr), items) in missing.OrderBy(kv => -kv.Value.Count))
            {
                var share = kind == "uuid" && !attr.StartsWith("stat field", StringComparison.Ordinal) ? Usual(attr) : null;
                var verdict = share is < 0.2 ? "not a reference in the game's own data either: probably fine"
                    : share is >= 0.6 ? "a reference in the game's own data: probably a real problem"
                    : kind == "uuid" ? "unclear" : "target not found";
                findings.Add(new()
                {
                    ["kind"] = kind, ["attribute"] = attr, ["count"] = (long)items.Count, ["verdict"] = verdict,
                    ["resolves_in_game"] = share == null ? null : Math.Round(share.Value, 2),
                    ["examples"] = items.Take(limit).Select(e => new Dictionary<string, object?> { ["file"] = e.File, ["object"] = e.Object, ["value"] = e.Value }).ToList(),
                });
            }
            static int Rank(Dictionary<string, object?> m)
            {
                var v = (string)m["verdict"]!;
                return v.Contains("real problem", StringComparison.Ordinal) || v == "target not found" ? 0 : v == "unclear" ? 1 : 2;
            }
            findings = findings.OrderBy(Rank).ThenBy(m => -(long)m["count"]!).ToList();
            var notes = new List<string>();
            if (problemsRead > 0)
            {
                notes.Add($"{problemsRead} files were read only in part");
            }
            var result = new Dictionary<string, object?>
            {
                ["mod"] = path, ["files"] = (long)dataFiles.Count, ["objects"] = (long)objects.Count, ["references_checked"] = (long)references,
                ["notes"] = notes, ["missing"] = findings,
                ["overrides"] = overrides.Select(x => new Dictionary<string, object?> { ["file"] = x.File, ["object"] = x.Object, ["key"] = x.Key }).ToList(),
            };
            if (json)
            {
                q.Out.WriteLine(OfflineQueries.ToJson(result));
                return;
            }
            q.Out.WriteLine($"{path}: {dataFiles.Count} files, {objects.Count} objects, {statsEntries.Count} stats entries, {references} references checked");
            foreach (var note in notes)
            {
                q.Out.WriteLine($"  note: {note}");
            }
            if (findings.Count == 0)
            {
                q.Out.WriteLine("  every reference leads to something in the mod or in the game");
            }
            foreach (var m in findings)
            {
                q.Out.WriteLine($"  {m["count"]} x {m["kind"]} in {m["attribute"]} not found - {m["verdict"]}"
                                + (m["resolves_in_game"] is double share ? $" ({(int)(share * 100)}% of the game's own values there resolve)" : ""));
                foreach (var e in ((List<Dictionary<string, object?>>)m["examples"]!).Take(examples))
                {
                    q.Out.WriteLine($"      {e["value"]}   in {e["object"]}   [{e["file"]}]");
                }
            }
            if (overrides.Count > 0)
            {
                q.Out.WriteLine($"  overrides {overrides.Count} things the game defines:");
                foreach (var (file, obj, _) in overrides.Take(examples * 4))
                {
                    q.Out.WriteLine($"      {obj}   [{file}]");
                }
                if (overrides.Count > examples * 4)
                {
                    q.Out.WriteLine($"      ... {overrides.Count - examples * 4} more (--json for all)");
                }
            }
        }
        finally
        {
            foreach (var k in keep)
            {
                k.Dispose();
            }
        }
    }
}
