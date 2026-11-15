using System.CommandLine;
using GateKit.GameData.Index;
using GateKit.Release;

namespace GateKit.GameData;

/// <summary>The <c>gatekit data</c> command group.</summary>
public static partial class DataCommand
{
    /// <summary><c>data build|info|survey|...|sql</c> and <c>data query search|about|...</c>.</summary>
    public static Command CreateCommand()
    {
        var db = new Option<string>("--db")
        {
            Description = "the index database (default: BG3_INDEX_DB, else gameindex.sqlite in the GateKit folder)",
            Recursive = true,
            DefaultValueFactory = _ => DataPaths.IndexDatabase,
        };
        var data = new Command("data", "the offline index of the game's data: build it, ask it");
        data.Options.Add(db);
        data.Subcommands.Add(BuildCommand(db));
        data.Subcommands.Add(StampCommand(db));
        // Building stays the first-class path; these two are the alternative for whoever would
        // rather fetch the same database than spend the minutes making it.
        data.Subcommands.Add(ReleaseCommands.Install(DatabaseKind.Index, db));
        data.Subcommands.Add(ReleaseCommands.Export(DatabaseKind.Index, db));
        AddIndexQueries(data, db);
        data.Subcommands.Add(QueryCommand(db));
        return data;
    }

    private static Command BuildCommand(Option<string> db)
    {
        var game = new Option<string?>("--game") { Description = "game folder (default: BG3_GAME); its Data folder is read" };
        var dataFolder = new Option<string?>("--data") { Description = "the game's Data folder itself, instead of --game" };
        var languages = new Option<string[]>("--languages")
        {
            Description = "localizations to index (default: English)",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => ["English"],
        };
        var localization = new Option<string[]>("--localization")
        {
            Description = "another folder shaped like Data/Localization to read language packs from (repeatable), e.g. a Steam depot downloaded on its own",
            AllowMultipleArgumentsPerToken = true,
        };
        var jobs = new Option<int>("--jobs") { Description = "files read at the same time", DefaultValueFactory = _ => Math.Max(1, Environment.ProcessorCount - 1) };
        var force = new Option<bool>("--force") { Description = "build even when no package changed since the last build" };
        var allKeys = new Option<bool>("--all-keys") { Description = "keep the keys of every timeline track (animation, camera, emotion, ...), not only the gear tracks; adds several GB" };
        var scenesOnly = new Option<bool>("--scenes-only") { Description = "read only timelines, dialogs, templates and placed characters/items" };
        var build = new Command("build", "read the game's packages and write the index") { game, dataFolder, languages, localization, jobs, force, allKeys, scenesOnly };
        build.SetAction(r =>
        {
            var folder = DataPaths.GameData(r.GetValue(dataFolder), r.GetValue(game));
            if (folder == null)
            {
                Console.Error.WriteLine("no game folder: pass --game DIR or --data DIR, or set BG3_GAME");
                return 1;
            }
            var result = IndexBuilder.Build(new IndexBuildOptions
            {
                DataFolder = folder,
                Database = r.GetValue(db)!,
                Languages = r.GetValue(languages) ?? ["English"],
                Localization = r.GetValue(localization) ?? [],
                Jobs = r.GetValue(jobs),
                Force = r.GetValue(force),
                AllKeys = r.GetValue(allKeys),
                ScenesOnly = r.GetValue(scenesOnly),
                Log = Console.Error,
            });
            return Run(r.GetValue(db)!, q => q.Info(null));
        });
        return build;
    }

    private static Command StampCommand(Option<string> db)
    {
        var game = new Option<string?>("--game") { Description = "game folder whose packages are compared (default: the folder the index records)" };
        var dataFolder = new Option<string?>("--data") { Description = "the game's Data folder itself, instead of --game" };
        var localization = new Option<string[]>("--localization")
        {
            Description = "a folder shaped like Data/Localization the build read language packs from (repeatable)",
            AllowMultipleArgumentsPerToken = true,
        };
        var jobs = new Option<int>("--jobs") { Description = "files hashed at the same time", DefaultValueFactory = _ => Math.Max(1, Environment.ProcessorCount - 1) };
        var dry = new Option<bool>("--dry-run") { Description = "say what would be recorded; write nothing" };
        var stamp = new Command("stamp",
            "record the game build of an index from evidence: every input it recorded, found again unchanged and its SHA-256 looked up in the reference lists")
            { game, dataFolder, localization, jobs, dry };
        stamp.SetAction(r =>
        {
            var database = r.GetValue(db)!;
            var named = r.GetValue(dataFolder) ?? (r.GetValue(game) is { } g ? Path.Combine(g, "Data") : null);
            IndexStampResult result;
            try
            {
                result = IndexStamp.Examine(database, named, r.GetValue(localization) ?? [], GateKit.Platform.BuildEvidence.OfHome(), r.GetValue(jobs));
            }
            catch (Exception e) when (e is InvalidOperationException or DirectoryNotFoundException or GateKit.Home.MissingDatabaseException)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
            foreach (var input in result.Inputs)
                Console.WriteLine($"  {input.Outcome,-8} {input.Name}{(input.Build is null ? "" : $" (build {input.Build})")}: {input.Detail}");
            Console.WriteLine($"{GateKit.Platform.GameFingerprint.BuildKey}: {result.Build ?? "(not written: no build shared by the inputs found)"}");
            Console.WriteLine($"{GateKit.Platform.GameFingerprint.VerdictKey}: {result.Verdict}");
            Console.WriteLine($"{IndexStamp.EvidenceKey}: {result.Evidence}");
            if (r.GetValue(dry))
            {
                Console.WriteLine("dry run: nothing written");
                return 0;
            }
            IndexStamp.Write(database, result);
            Console.WriteLine($"written into {Path.GetFullPath(database)}");
            return 0;
        });
        return stamp;
    }
}
