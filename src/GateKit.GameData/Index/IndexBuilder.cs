using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using GateKit.GameData.Packages;
using GateKit.GameData.Resources;
using GateKit.GameData.Text;
using LSLib.LS;
using Microsoft.Data.Sqlite;

namespace GateKit.GameData.Index;

/// <summary>Options of a build of the index.</summary>
public sealed class IndexBuildOptions
{
    /// <summary>The game's Data folder (the one with Gustav.pak).</summary>
    public required string DataFolder { get; init; }

    /// <summary>The database file to write.</summary>
    public required string Database { get; init; }

    /// <summary>Localizations to index (folders under Data/Localization).</summary>
    public IReadOnlyList<string> Languages { get; init; } = ["English"];

    /// <summary>Files read at the same time.</summary>
    public int Jobs { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Build even when the packages have not changed since the last build.</summary>
    public bool Force { get; init; }

    /// <summary>Keep the keys of every timeline track, not only the gear tracks.</summary>
    public bool AllKeys { get; init; }

    /// <summary>Read only timelines, dialogs, templates, placed characters and items.</summary>
    public bool ScenesOnly { get; init; }

    /// <summary>Progress and warnings (stderr in the command line).</summary>
    public TextWriter Log { get; init; } = TextWriter.Null;
}

/// <summary>What a build did.</summary>
public sealed record IndexBuildResult(bool UpToDate, string Database, double Seconds, IReadOnlyDictionary<string, double> Timing,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds the index: lists every package, reads every resource and text file straight out of the
/// packages (nothing is unpacked to disk), and writes one SQLite file. The database is written
/// next to the target as <c>.building</c> and moved over it when complete.
/// </summary>
public static class IndexBuilder
{
    private const string LooseFiles = "(loose files)";

    private sealed record Job(string Kind, string Package, string Rel, PackagedFileInfo? Info, string? LoosePath, bool Lsx);

    private sealed class FileResult
    {
        public required Job Job;
        public object? Data;
        public (string? Region, List<GenericObject> Objects)? Generic;
        public string? Problem;
    }

    /// <summary>The inputs a build depends on, as one string; equal strings mean nothing changed.</summary>
    private static string Inputs(IEnumerable<PackageFile> packages, string? header, IndexBuildOptions o) =>
        string.Join("|", packages.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + p.Stamp()))
        + "|header=" + (header == null ? "-" : new FileInfo(header).Length + ":" + new DateTimeOffset(File.GetLastWriteTimeUtc(header)).ToUnixTimeSeconds())
        + "|languages=" + string.Join(",", o.Languages) + "|scenes-only=" + o.ScenesOnly + "|all-keys=" + o.AllKeys + "|schema=1";

    /// <summary>The Toolkit leaves the Osiris header as a loose file next to the packages; the largest one wins.</summary>
    private static string? FindStoryHeader(string data)
    {
        var mods = Path.Combine(data, "Mods");
        if (!Directory.Exists(mods))
        {
            return null;
        }
        return Directory.GetDirectories(mods)
            .Select(d => Path.Combine(d, "Story", "RawFiles", "story_header.div"))
            .Where(File.Exists)
            .OrderBy(p => new FileInfo(p).Length).ThenBy(p => p, StringComparer.Ordinal)
            .LastOrDefault();
    }

    private static string? StoredInputs(string database)
    {
        if (!File.Exists(database))
        {
            return null;
        }
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            return Sql.Scalar(db, "SELECT value FROM meta WHERE key = 'inputs'") as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public static IndexBuildResult Build(IndexBuildOptions o)
    {
        var started = Stopwatch.StartNew();
        var timing = new Dictionary<string, double>();
        var log = o.Log;
        using var packages = new GamePackages(o.DataFolder);
        var paks = packages.Find(o.Languages);
        var header = FindStoryHeader(o.DataFolder);
        var inputs = Inputs(paks.Values, header, o);
        if (!o.Force && StoredInputs(o.Database) == inputs)
        {
            log.WriteLine($"{o.Database}: up to date (no package changed since it was built; --force builds anyway)");
            return new IndexBuildResult(true, o.Database, started.Elapsed.TotalSeconds, timing, []);
        }

        var warnings = new List<string>();
        warnings.AddRange(new[] { "Shared", "Gustav" }.Where(n => !paks.ContainsKey(n)).Select(n => $"package {n}.pak not found"));
        var pattern = o.ScenesOnly ? FileKinds.ScenesOnly() : FileKinds.Indexed();

        // List every package: the file catalog, and the files to read.
        log.WriteLine("listing game packages ...");
        var t = Stopwatch.StartNew();
        var catalog = new List<(string Package, string Path, long Size)>();
        var withFiles = new List<string>();
        var candidates = new List<(string Package, string Path, PackagedFileInfo Info)>();
        foreach (var name in FileKinds.PackageOrder(paks.Keys))
        {
            List<PackagedFile> listing;
            try
            {
                listing = packages.List(paks[name]);
            }
            catch (Exception e) when (e is InvalidDataException or NotAPackageException or IOException)
            {
                warnings.Add($"package {name}.pak could not be read: {e.Message}");
                continue;
            }
            catalog.AddRange(listing.Select(f => (name, f.Path, f.Size)));
            if (listing.Any(f => f.Info == null && pattern.IsMatch(f.Path)))
            {
                warnings.Add($"package {name}.pak could only be listed: its resource and text files were not read");
            }
            var wanted = listing.Where(f => f.Readable && pattern.IsMatch(f.Path)).ToList();
            if (wanted.Count > 0)
            {
                withFiles.Add(name);
                candidates.AddRange(wanted.Select(f => (name, f.Path, f.Info!)));
            }
        }
        timing["list"] = t.Elapsed.TotalSeconds;

        // A resource has one name however it is stored: X.lsf is read as X.lsx. A later package
        // replaces the same path; a binary .lsf is preferred to a text .lsx of the same name.
        var jobs = new Dictionary<string, Job>(StringComparer.Ordinal);
        var binary = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (package, path, info) in candidates)
        {
            var converted = path.EndsWith(".lsf", StringComparison.OrdinalIgnoreCase);
            var rel = converted ? path[..^4] + ".lsx" : path;
            var kind = FileKinds.Classify(rel);
            var key = rel.ToLowerInvariant();
            if (kind != null && (converted || !binary.Contains(key)))
            {
                jobs[key] = new Job(kind, package, rel, info, null, !converted && rel.EndsWith(".lsx", StringComparison.OrdinalIgnoreCase));
                if (converted)
                {
                    binary.Add(key);
                }
            }
        }
        if (header != null)
        {
            var rel = Path.GetRelativePath(o.DataFolder, header).Replace('\\', '/');
            jobs[rel.ToLowerInvariant()] = new Job("text", LooseFiles, rel, null, header, false);
        }
        else
        {
            warnings.Add("story_header.div not found under Data/Mods (it comes with the Toolkit): no Osiris function list");
        }
        var order = new List<string>(withFiles) { LooseFiles };
        var rank = order.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
        var list = jobs.Values
            .OrderBy(j => rank[j.Package])
            .ThenBy(j => FileKinds.ModRank(FileKinds.ModOf(j.Rel)))
            .ThenBy(j => j.Rel.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
        log.WriteLine($"reading {list.Count} files with {o.Jobs} threads ...");

        var full = Path.GetFullPath(o.Database);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = Path.ChangeExtension(full, ".building");
        if (File.Exists(tmp))
        {
            File.Delete(tmp);
        }
        SqliteConnection.ClearAllPools();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        void Count(string key, long n = 1) => counts[key] = counts.GetValueOrDefault(key) + n;

        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString()))
        {
            db.Open();
            Sql.Exec(db, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-400000; PRAGMA threads=8;");
            Sql.Exec(db, Schema.Tables);
            t.Restart();
            var writer = new Writer(db);
            using (var tx = db.BeginTransaction())
            {
                writer.Prepare(tx);
                var n = 0;
                foreach (var r in ReadAll(packages, list, o))
                {
                    if (++n % 5000 == 0)
                    {
                        log.WriteLine($"  {n}/{list.Count}");
                    }
                    writer.Add(r.Job.Kind, r.Job.Package, r.Job.Rel, r.Data, r.Generic, r.Problem, Count);
                }
                timing["read"] = t.Elapsed.TotalSeconds;
                t.Restart();
                writer.Link(tx);
                writer.Finish(catalog, Count);
                ReadLocalization(packages, paks, o.Languages, writer, warnings, Count);
                writer.Dispose();
                tx.Commit();
            }
            Sql.Exec(db, Schema.Finish);
            timing["finish"] = t.Elapsed.TotalSeconds;

            var unbanked = Convert.ToInt64(Sql.Scalar(db, "SELECT count(*) FROM timelines WHERE uuid IS NULL"), CultureInfo.InvariantCulture);
            if (unbanked > 0)
            {
                warnings.Add($"{unbanked} timeline files have no entry in a timeline bank (no resource id, no dialog link)");
            }
            if (writer.NestedComponents > 0)
            {
                warnings.Add($"{writer.NestedComponents} EffectComponent nodes are nested inside another one");
            }
            var total = started.Elapsed.TotalSeconds;
            var seconds = timing.Select(p => new KeyValuePair<string, double>(p.Key, Math.Round(p.Value, 1)))
                .Append(new("total", Math.Round(total, 1)));
            var meta = new List<(string, string)>
            {
                ("built", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                ("game", Path.GetFullPath(Path.Combine(o.DataFolder, ".."))),
                ("packages", string.Join(", ", order)),
                ("languages", string.Join(", ", o.Languages)),
                ("files", PyJson.Numbers(counts.Select(p => new KeyValuePair<string, double>(p.Key, p.Value)))),
                ("seconds", PyJson.Numbers(seconds)),
                ("warnings", PyJson.Strings(warnings)),
                ("builder", "GateKit.GameData (LSLib " + typeof(Package).Assembly.GetName().Version + ")"),
                ("inputs", inputs),
            };
            using (var insert = new Insert(db, null, Sql.Positional("INSERT INTO meta VALUES (?,?)"), 2))
            {
                foreach (var (k, v) in meta)
                {
                    insert.Run(k, v);
                }
            }
            Sql.Exec(db, "ANALYZE");
        }
        SqliteConnection.ClearAllPools();
        File.Move(tmp, full, overwrite: true);
        timing["total"] = started.Elapsed.TotalSeconds;
        var size = new FileInfo(full).Length / 1e6;
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{full}: {size:0} MB, {timing["total"]:0} s ({string.Join(", ", timing.Where(p => p.Key != "total").Select(p => $"{p.Key} {p.Value:0}"))})"));
        foreach (var w in warnings)
        {
            log.WriteLine("warning: " + w);
        }
        return new IndexBuildResult(false, full, timing["total"], timing, warnings);
    }

    /// <summary>Reads the files on several threads and hands the results back in job order.</summary>
    private static IEnumerable<FileResult> ReadAll(GamePackages packages, List<Job> list, IndexBuildOptions o)
    {
        var jobs = Math.Max(1, o.Jobs);
        var channel = Channel.CreateBounded<Task<FileResult>>(new BoundedChannelOptions(jobs * 32) { SingleReader = true, SingleWriter = true });
        using var gate = new SemaphoreSlim(jobs);
        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var job in list)
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    var task = Task.Run(() =>
                    {
                        try
                        {
                            return ReadFile(job, o.AllKeys);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    });
                    await channel.Writer.WriteAsync(task).ConfigureAwait(false);
                }
            }
            finally
            {
                channel.Writer.Complete();
            }
        });
        var reader = channel.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var task))
            {
                yield return task.GetAwaiter().GetResult();
            }
        }
        producer.GetAwaiter().GetResult();
    }

    private static byte[] Bytes(Job job) => job.Info != null ? GamePackages.ReadAll(job.Info) : File.ReadAllBytes(job.LoosePath!);

    /// <summary>Reads one file: the text, or the data of its dedicated reader and its generic objects.</summary>
    private static FileResult ReadFile(Job job, bool allKeys)
    {
        var result = new FileResult { Job = job };
        GenericReader? generic = null;
        try
        {
            var bytes = Bytes(job);
            if (job.Kind == "text")
            {
                result.Data = (bytes.LongLength, DecodeText(bytes));
                return result;
            }
            ResourceReader? dedicated = job.Kind switch
            {
                "timeline" => new TimelineReader(allKeys),
                "scene" => new SceneReader(),
                "dialog" => new DialogReader(),
                "voice" => new VoiceReader(),
                "bank" => FlatReader.Bank(),
                "templates" or "entities" => FlatReader.Templates(),
                "speakergroups" => FlatReader.SpeakerGroups(),
                _ => null,
            };
            if (job.Kind is not ("timeline" or "scene" or "dialog" or "voice"))
            {
                generic = new GenericReader();
            }
            var readers = new[] { dedicated, generic }.OfType<ResourceReader>().ToArray();
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                ResourceEvents.Read(stream, job.Lsx, readers);
            }
            result.Data = dedicated switch
            {
                TimelineReader r => r.Result(),
                SceneReader r => r.Result(),
                DialogReader r => r.Result(),
                VoiceReader r => r.Result(),
                FlatReader r => r.Result(),
                _ => null,
            };
            result.Generic = generic?.Result();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result.Data = null;
            result.Problem = $"{e.GetType().Name}: {e.Message}";
            if (job.Kind == "generic" && generic != null)
            {
                result.Generic = generic.Result();
                result.Problem += " (objects before the break were kept)";
            }
        }
        return result;
    }

    /// <summary>UTF-8 text without a byte order mark, with \n line ends.</summary>
    internal static string DecodeText(byte[] raw)
    {
        var start = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(raw, start, raw.Length - start).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void ReadLocalization(GamePackages packages, Dictionary<string, PackageFile> paks, IReadOnlyList<string> languages,
        Writer writer, List<string> warnings, Action<string, long> count)
    {
        foreach (var lang in languages)
        {
            var files = new List<PackagedFile>();
            if (paks.TryGetValue(lang, out var pak))
            {
                files = packages.List(pak).Where(f => f.Readable && f.Path.EndsWith(".loca", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (files.Count == 0)
            {
                warnings.Add($"no localization found for {lang}");
            }
            foreach (var f in files)
            {
                try
                {
                    var stem = f.Path[..^".loca".Length];
                    var parts = stem.Split('/', 3);
                    var variant = parts[^1];
                    if (string.Equals(variant, lang, StringComparison.OrdinalIgnoreCase))
                    {
                        variant = "";
                    }
                    using var stream = new MemoryStream(GamePackages.ReadAll(f.Info!), writable: false);
                    using var reader = new LocaReader(stream);
                    writer.AddLoca(lang, variant, reader.Read());
                    count("loca files", 1);
                }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
                {
                    writer.AddProblem($"{lang}/{f.Path}", $"{e.GetType().Name}: {e.Message}");
                }
            }
        }
    }
}
