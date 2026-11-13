using System.CommandLine;

namespace GateKit.Release;

/// <summary>
/// The two commands every publishable database has: <c>export-release</c>, which turns the built
/// file into release assets, and <c>install</c>, which brings one back. Both are built here so
/// the index and the symbols behave identically.
/// </summary>
public static class ReleaseCommands
{
    /// <summary><c>&lt;group&gt; export-release --out FOLDER</c>.</summary>
    public static Command Export(DatabaseKind kind, Option<string> database)
    {
        var into = new Option<string>("--out") { Description = "folder the assets and manifest.json are written to", Required = true };
        var level = new Option<int>("--level") { Description = "Zstandard level (1 fastest, 19 smallest)", DefaultValueFactory = _ => new ExportOptions().Level };
        var workers = new Option<int>("--workers") { Description = "compression threads (0: one)", DefaultValueFactory = _ => 0 };
        var partMb = new Option<int>("--part-mb")
        {
            Description = "largest part in MB; a bigger database is split into numbered parts (GitHub refuses an asset over 2 GB)",
            DefaultValueFactory = _ => (int)(new ExportOptions().PartBytes / (1024 * 1024)),
        };
        var command = new Command("export-release",
            $"compress {kind.Id} into release assets with a manifest (the files published as {ReleasePin.DefaultRepository})")
        { into, level, workers, partMb };
        command.SetAction((parse, cancel) =>
        {
            var output = parse.InvocationConfiguration.Output;
            var error = parse.InvocationConfiguration.Error;
            try
            {
                DatabaseExport.Export(kind, parse.GetValue(database)!, parse.GetValue(into)!, new ExportOptions
                {
                    Level = parse.GetValue(level),
                    Workers = parse.GetValue(workers),
                    PartBytes = (long)parse.GetValue(partMb) * 1024 * 1024,
                }, output, cancel);
                return Task.FromResult(0);
            }
            catch (ReleaseException e)
            {
                error.WriteLine("ERROR: " + e.Message);
                return Task.FromResult(1);
            }
        });
        return command;
    }

    /// <summary><c>&lt;group&gt; install [--from X] [--force]</c>.</summary>
    public static Command Install(DatabaseKind kind, Option<string> database)
    {
        var from = new Option<string?>("--from")
        {
            Description = $"folder, file:// or http(s):// base URL holding the assets, instead of the release pinned in {ReleasePin.FileName}",
        };
        var force = new Option<bool>("--force") { Description = "replace a database that is already there" };
        var command = new Command("install",
            $"fetch {kind.Id} from the published release instead of building it, and check every byte") { from, force };
        command.SetAction(async (parse, cancel) =>
        {
            var output = parse.InvocationConfiguration.Output;
            var error = parse.InvocationConfiguration.Error;
            try
            {
                var pin = parse.GetValue(from) is { Length: > 0 } ? null : ReleasePin.ReadFromRepository();
                var source = pin?.Source() ?? AssetSources.For(parse.GetValue(from)!);
                await DatabaseInstall.InstallAsync(kind, source, pin?.Manifest ?? DatabaseManifest.FileName,
                    parse.GetValue(database)!, parse.GetValue(force), output, cancel);
                return 0;
            }
            catch (ReleaseException e)
            {
                error.WriteLine("ERROR: " + e.Message);
                return 1;
            }
        });
        return command;
    }
}
