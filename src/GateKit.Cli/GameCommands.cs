using System.CommandLine;
using GateKit.Json;
using GateKit.Platform;
using GateKit.Copies;

namespace GateKit.Cli;

/// <summary>`game`: the game's files themselves (docs/contracts/cli.md, `game verify`).</summary>
public static class GameCommands
{
    public static Command Create()
    {
        var command = new Command("game", "the game's own files: whether they are exactly what Steam delivered");
        command.Subcommands.Add(Verify());
        return command;
    }

    static Command Verify()
    {
        var quick = new Option<bool>("--quick") { Description = "compare every size, hash only the executables and libraries (default: hash every file)" };
        var json = new Option<bool>("--json") { Description = $"the report as JSON ({VerifyReport.Kind})" };
        var limit = new Option<int>("--limit") { Description = "lines listed per kind of finding in the text report (0: all)", DefaultValueFactory = _ => 50 };
        var threads = new Option<int>("--threads") { Description = "files hashed at once (default: one per processor, at most 16)" };
        var source = new Option<string?>("--source")
        {
            Description = "only this source of invariants: " + string.Join(", ", InvariantSources.All.Select(s => s.Id)) + " (default: the first that applies)",
        };
        var reference = new Option<string?>("--reference") { Description = "hold the game to this reference list, whatever its executables" };
        var writeReference = new Option<bool>("--write-reference")
        {
            Description = "after a full pass that proves the installed game clean by another source, write its reference list (into <home>/reference/game-files)",
        };
        var output = new Option<string?>("--out") { Description = "with --write-reference: the file to write instead" };
        var command = new Command("verify",
            "hold the installed game (or a copy, with --instance) to a source of invariants - Steam's own manifests on this machine, or a "
            + "reference list - file by file; list what is modified, missing or extra; record a fingerprint. Never writes into the game")
            { quick, json, limit, threads, source, reference, writeReference, output };
        command.SetAction((parse, cancel) =>
        {
            try
            {
                var platform = GamePlatform.Current;
                var settings = CopySettings.Resolve(platform, instanceOverride: parse.GetValue(CopyCommands.InstanceOption));
                var layout = new CopyLayout(settings, platform);
                var mode = parse.GetValue(quick) ? VerifyMode.Quick : VerifyMode.Full;
                var write = parse.GetValue(writeReference);
                if (write && mode == VerifyMode.Quick) throw new CopyException("--write-reference needs the full pass: drop --quick");
                var request = GameIntegrity.Request(layout, platform, mode) with
                {
                    Source = parse.GetValue(source) ?? (parse.GetValue(reference) is null ? null : "reference"),
                    Reference = parse.GetValue(reference) is { } file ? Path.GetFullPath(file) : null,
                    CollectHashes = write,
                };
                if (parse.GetValue(threads) is > 0 and var n) request = request with { Threads = n };
                Console.Error.WriteLine($"verifying {layout.Game} ({(mode == VerifyMode.Quick ? "quick" : "full")}) ...");
                var report = GameIntegrity.Verify(layout, request, Console.Error, live: !Console.IsErrorRedirected, cancel);
                string? refused = null;
                if (write)
                {
                    try { GameIntegrity.WriteReference(report, parse.GetValue(output)); }
                    catch (CopyException e) { refused = e.Message; }
                }
                if (parse.GetValue(json))
                {
                    var node = report.ToJson();
                    var misfits = SchemaSet.Embedded.Validate(node, VerifyReport.Kind);
                    if (misfits.Count > 0)
                        throw new InvalidOperationException("the report does not fit its schema: " + string.Join("; ", misfits.Select(m => $"{m.Pointer}: {m.What}")));
                    Console.Out.Write(JsonText.Format(node, VerifyReport.Kind));
                }
                else
                {
                    foreach (var line in report.Lines(parse.GetValue(limit))) Console.Out.WriteLine(line);
                }
                if (refused is not null)
                {
                    Console.Error.WriteLine("ERROR: " + refused);
                    return Task.FromResult(report.ExitCode == 0 ? 1 : report.ExitCode);
                }
                return Task.FromResult(report.ExitCode);
            }
            catch (Exception e) when (e is CopyException or IOException or UnauthorizedAccessException or JsonProblemException)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                return Task.FromResult(1);
            }
        });
        return command;
    }
}
