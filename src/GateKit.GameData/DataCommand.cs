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
        var jobs = new Option<int>("--jobs") { Description = "files read at the same time", DefaultValueFactory = _ => Math.Max(1, Environment.ProcessorCount - 1) };
        var force = new Option<bool>("--force") { Description = "build even when no package changed since the last build" };
        var allKeys = new Option<bool>("--all-keys") { Description = "keep the keys of every timeline track (animation, camera, emotion, ...), not only the gear tracks; adds several GB" };
        var scenesOnly = new Option<bool>("--scenes-only") { Description = "read only timelines, dialogs, templates and placed characters/items" };
        var build = new Command("build", "read the game's packages and write the index") { game, dataFolder, languages, jobs, force, allKeys, scenesOnly };
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
}
