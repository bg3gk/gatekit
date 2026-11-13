using System.CommandLine;
using System.Text;
using GateKit.GameData.Query;

namespace GateKit.GameData;

public static partial class DataCommand
{
    /// <summary>Runs a question over the index; a question that cannot be answered prints its reason and exits with 1.</summary>
    internal static int Run(string database, Action<IndexQueries> query)
    {
        var output = Utf8(Console.OpenStandardOutput());
        var error = Utf8(Console.OpenStandardError());
        try
        {
            using var q = IndexQueries.Open(database, output, error);
            query(q);
            return 0;
        }
        catch (DataQueryException e)
        {
            error.WriteLine(e.Message);
            return 1;
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            error.WriteLine($"SQLite: {e.Message}");
            return 1;
        }
        finally
        {
            output.Flush();
            error.Flush();
        }
    }

    private static StreamWriter Utf8(Stream s) => new(s, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };

    private static Command Verb(string name, string help, Option<string> db, Action<ParseResult, IndexQueries> run, params Symbol[] symbols)
    {
        var c = new Command(name, help);
        foreach (var s in symbols)
        {
            switch (s)
            {
                case Option o: c.Options.Add(o); break;
                case Argument a: c.Arguments.Add(a); break;
            }
        }
        c.SetAction(r => Run(r.GetValue(db)!, q => run(r, q)));
        return c;
    }

    private static Option<string?> Package() => new("--package") { Description = "only this package (Gustav, Shared, ...)" };

    private static Option<int> Limit(int value = 100) => new("--limit") { Description = $"rows to print (default {value})", DefaultValueFactory = _ => value };

    private static Option<bool> Flag(string name, string help) => new(name) { Description = help };

    private static Option<string?> Text(string name, string help) => new(name) { Description = help };

    private static void AddIndexQueries(Command data, Option<string> db)
    {
        {
            var package = Package();
            data.Subcommands.Add(Verb("info", "what the database holds and how long it took to build", db, (r, q) => q.Info(r.GetValue(package)), package));
        }
        {
            var package = Package();
            var all = Flag("--all-files", "count shadowed duplicates too");
            data.Subcommands.Add(Verb("survey", "scenes per track type; for on/off tracks, scenes that hide something", db,
                (r, q) => q.Survey(r.GetValue(package), r.GetValue(all)), package, all));
        }
        foreach (var (name, track, what) in new[]
                 {
                     ("hidden-weapons", "TLShowWeapon", "an actor's weapons are"),
                     ("hidden-actors", "TLShowVisual", "a whole actor is"),
                     ("hidden-armor", "TLShowArmor", "pieces of an actor's clothing are"),
                 })
        {
            var package = Package();
            var limit = Limit();
            var title = Text("--name", "only timelines whose name contains this");
            var actor = Text("--actor", "only this actor: name or uuid of a template, placed character or speaker group");
            var count = Flag("--count", "print the totals only");
            data.Subcommands.Add(Verb(name, $"scenes where {what} hidden", db,
                (r, q) => q.Hidden(track, r.GetValue(title), r.GetValue(package), r.GetValue(actor), r.GetValue(count), r.GetValue(limit)),
                package, limit, title, actor, count));
        }
        {
            var limit = Limit();
            var name = new Argument<string>("name") { Description = "name (exact, else substring) or uuid" };
            var gear = Flag("--gear", "mark scenes that hide weapons/actors/armor or shape-shift");
            data.Subcommands.Add(Verb("template", "scenes a template, placed character/item or speaker group takes part in", db,
                (r, q) => q.Template(r.GetValue(name)!, r.GetValue(gear), r.GetValue(limit)), name, gear, limit, Package()));
        }
        {
            var name = new Argument<string>("name");
            data.Subcommands.Add(Verb("timeline", "one timeline in detail", db, (r, q) => q.Timeline(r.GetValue(name)!), name, Limit(), Package()));
        }
        {
            var type = new Argument<string>("type") { Description = "track type, e.g. TLShapeShift" };
            var hides = Flag("--hides", "on/off tracks: only scenes where the track hides something");
            var package = Package();
            var limit = Limit();
            data.Subcommands.Add(Verb("tracks", "scenes that have a track of the given type", db,
                (r, q) => q.Tracks(r.GetValue(type)!, r.GetValue(hides), r.GetValue(package), r.GetValue(limit)), type, hides, package, limit));
        }
        {
            var text = new Argument<string>("text");
            var lang = Text("--lang", "only this language");
            var where = Flag("--where", "also show the dialogs and templates that use each string");
            var limit = Limit();
            data.Subcommands.Add(Verb("text", "search localization by text or handle", db,
                (r, q) => q.Text(r.GetValue(text)!, r.GetValue(lang), r.GetValue(where), r.GetValue(limit)), text, lang, where, limit, Package()));
        }
        {
            var name = new Argument<string>("name") { Description = "dialog name (path under DialogsBinary, or its last part) or uuid" };
            var tree = Flag("--tree", "print the flow of nodes");
            var lang = new Option<string>("--lang") { Description = "language of the lines (default English)", DefaultValueFactory = _ => "English" };
            var limit = Limit();
            data.Subcommands.Add(Verb("dialog", "a dialog: speakers, and with --tree its flow, text, flag checks and flag changes", db,
                (r, q) => q.Dialog(r.GetValue(name)!, r.GetValue(tree), r.GetValue(lang)!, r.GetValue(limit)), name, tree, lang, limit, Package()));
        }
        {
            var name = new Argument<string>("name") { Description = "stats entry, object name or uuid" };
            var depth = new Option<int>("--depth") { Description = "how many references deep to follow (default 5)", DefaultValueFactory = _ => 5 };
            var fan = new Option<int>("--fan") { Description = "references to follow per object (default 12)", DefaultValueFactory = _ => 12 };
            var limit = Limit();
            data.Subcommands.Add(Verb("chain", "follow references from a stats entry, template or object down to resources and files", db,
                (r, q) => q.Chain(r.GetValue(name)!, r.GetValue(depth), r.GetValue(fan), r.GetValue(limit)), name, depth, fan, limit, Package()));
        }
        {
            var text = new Argument<string[]>("text") { Arity = ArgumentArity.OneOrMore };
            var dialog = Flag("--dialog", "the text is a dialog name: every recorded line of it");
            var speaker = Flag("--speaker", "the text is the name of a character, template or speaker group");
            var lang = new Option<string>("--lang") { DefaultValueFactory = _ => "English" };
            var limit = Limit();
            data.Subcommands.Add(Verb("voice", "recorded voice lines by text, handle or speaker uuid; --dialog NAME, --speaker NAME", db,
                (r, q) => q.Voice(string.Join(" ", r.GetValue(text)!), r.GetValue(dialog), r.GetValue(speaker), r.GetValue(lang)!, r.GetValue(limit)),
                text, dialog, speaker, lang, limit, Package()));
        }
        {
            var path = new Argument<string>("path") { Description = "the mod's folder (with Mods/ and Public/ inside) or its .pak" };
            var examples = new Option<int>("--examples") { Description = "examples to print per finding", DefaultValueFactory = _ => 5 };
            var json = Flag("--json", "print everything as JSON");
            var limit = Limit();
            data.Subcommands.Add(Verb("check-mod", "references of a mod (folder or .pak) that lead nowhere, and what it overrides", db,
                (r, q) => ModChecker.Check(q, r.GetValue(path)!, r.GetValue(examples), r.GetValue(json), r.GetValue(limit)),
                path, examples, json, limit, Package()));
        }
        {
            var text = new Argument<string[]>("text") { Description = "parts of the path; all must match", Arity = ArgumentArity.ZeroOrMore };
            var ext = Text("--ext", "only this extension");
            var summary = Flag("--summary", "count and size by extension instead of a list");
            var package = Package();
            var limit = Limit();
            data.Subcommands.Add(Verb("files", "search the catalog of every file in every package", db,
                (r, q) => q.Files(r.GetValue(text) ?? [], r.GetValue(ext), r.GetValue(package), r.GetValue(summary), r.GetValue(limit)),
                text, ext, summary, package, limit));
        }
        {
            var text = new Argument<string[]>("text") { Arity = ArgumentArity.OneOrMore };
            var node = Text("--node", "only this kind of node, e.g. GameObjects, Resource, Flags, Tags");
            var show = Flag("--show", "print the attributes of every object found");
            var full = Flag("--full", "do not cut long attribute lists");
            var package = Package();
            var limit = Limit();
            data.Subcommands.Add(Verb("find", "objects of any kind (templates, flags, tags, resources, races, ...) by name or uuid", db,
                (r, q) => q.Find(r.GetValue(text)!, r.GetValue(node), r.GetValue(package), r.GetValue(show), r.GetValue(full), r.GetValue(limit)),
                text, node, show, full, package, limit));
        }
        {
            var value = new Argument<string>("value");
            var limit = Limit();
            data.Subcommands.Add(Verb("refs", "everything that mentions a uuid, a name or another exact value", db,
                (r, q) => q.Refs(r.GetValue(value)!, r.GetValue(limit)), value, limit, Package()));
        }
        {
            var name = new Argument<string>("name");
            var limit = Limit();
            data.Subcommands.Add(Verb("stat", "a stats entry with the fields it inherits", db, (r, q) => q.Stat(r.GetValue(name)!, r.GetValue(limit)), name, limit, Package()));
        }
        {
            var name = new Argument<string>("name");
            var kind = Text("--kind", "event, call, query, syscall, sysquery");
            var uses = Flag("--uses", "also list the goals that use the name");
            var limit = Limit();
            data.Subcommands.Add(Verb("osiris", "Osiris events, calls and queries, and the story goals that use them", db,
                (r, q) => q.Osiris(r.GetValue(name)!, r.GetValue(kind), r.GetValue(uses), r.GetValue(limit)), name, kind, uses, limit, Package()));
        }
        {
            var text = new Argument<string>("text") { Description = "words that must all occur (whole words; '_' is part of a word)" };
            var kind = Text("--kind", "goal, stats, script, interface, text");
            var raw = Flag("--raw", "pass the text to SQLite FTS5 as it is (prefix*, OR, NEAR)");
            var limit = Limit();
            data.Subcommands.Add(Verb("grep", "full-text search over story goals, stats, scripts and interface files", db,
                (r, q) => q.Grep(r.GetValue(text)!, r.GetValue(kind), r.GetValue(raw), r.GetValue(limit)), text, kind, raw, limit, Package()));
        }
        {
            var query = new Argument<string>("query");
            data.Subcommands.Add(Verb("sql", "run a read-only SQL query on the index", db, (r, q) => q.Sql(r.GetValue(query)!), query));
        }
    }

    // ------------------------------------------------------------------------------ data query

    private static Command QueryCommand(Option<string> db)
    {
        var json = new Option<bool>("--json") { Description = "print one JSON document", Recursive = true };
        var symbols = new Option<string>("--symbols-db")
        {
            Description = "the code symbol database (default: BG3_SYMBOLS_DB, else symbols.sqlite in the GateKit folder)",
            Recursive = true,
            DefaultValueFactory = _ => DataPaths.SymbolsDatabase,
        };
        var decomp = new Option<string>("--decomp-db")
        {
            Description = "the pseudo-code database (default: decomp.sqlite in BG3_DECOMP_WORK or the GateKit folder)",
            Recursive = true,
            DefaultValueFactory = _ => DataPaths.DecompDatabase,
        };
        var query = new Command("query", "offline lookups over the index, the symbol and the pseudo-code databases (the bg3-offline tools)");
        query.Options.Add(json);
        query.Options.Add(symbols);
        query.Options.Add(decomp);

        OfflineDatabases Paths(ParseResult r) => new(r.GetValue(db)!, r.GetValue(symbols)!, r.GetValue(decomp)!);

        foreach (var (name, help) in new[]
                 {
                     ("search", "anything named like this, in data and in code"), ("about", "everything known about one thing"),
                     ("scene", "a timeline with its dialog, actors and gear changes"), ("track", "a timeline track type in data and code"),
                     ("hook", "where a function is in the game and what is around it"),
                 })
        {
            var text = new Argument<string[]>("text") { Arity = ArgumentArity.OneOrMore };
            var limit = new Option<int>("--limit") { Description = "results per kind (search; default 10)", DefaultValueFactory = _ => 10 };
            var c = new Command(name, help) { text, limit };
            c.SetAction(r => Offline(r.GetValue(json), name == "search", q =>
            {
                var t = string.Join(" ", r.GetValue(text)!);
                return name switch
                {
                    "search" => q.Search(t, r.GetValue(limit)),
                    "about" => q.About(t),
                    "scene" => q.Scene(t),
                    "track" => q.Track(t),
                    _ => q.Hook(t),
                };
            }, Paths(r)));
            query.Subcommands.Add(c);
        }
        {
            var database = new Argument<string>("database") { Description = "index, symbols or decomp" };
            database.AcceptOnlyFromAmong(OfflineDatabases.Names);
            var sql = new Argument<string>("query");
            var limit = new Option<int>("--limit") { Description = "rows (default 500)", DefaultValueFactory = _ => 500 };
            var c = new Command("sql", "read-only SQL on one database") { database, sql, limit };
            c.SetAction(r => Offline(r.GetValue(json), false, q => q.RunSql(r.GetValue(database)!, r.GetValue(sql)!, r.GetValue(limit)), Paths(r), sqlText: true));
            query.Subcommands.Add(c);
        }
        {
            var write = new Option<string?>("--write") { Description = "write the schema as Markdown to this file" };
            var c = new Command("schema", "tables and columns of the databases") { write };
            c.SetAction(r =>
            {
                var file = r.GetValue(write);
                if (file != null)
                {
                    using var q = new OfflineQueries(Paths(r));
                    File.WriteAllText(file, q.SchemaMarkdown().Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
                    Console.Error.WriteLine($"wrote {file}");
                    return 0;
                }
                return Offline(r.GetValue(json), false, q => q.Schema(), Paths(r), schemaText: true);
            });
            query.Subcommands.Add(c);
        }
        return query;
    }

    private static int Offline(bool json, bool search, Func<OfflineQueries, object?> run, OfflineDatabases paths, bool sqlText = false, bool schemaText = false)
    {
        var output = Utf8(Console.OpenStandardOutput());
        var error = Utf8(Console.OpenStandardError());
        try
        {
            using var q = new OfflineQueries(paths);
            var result = run(q);
            if (json)
            {
                output.WriteLine(OfflineQueries.ToJson(result));
            }
            else if (search)
            {
                var rows = (List<Row>)result!;
                foreach (var group in rows.GroupBy(r => r.S("kind")))
                {
                    output.WriteLine($"{group.Key}:");
                    foreach (var r in group)
                    {
                        var label = Q.Cut(r.S("name") ?? r.S("uuid") ?? "", 110);
                        output.WriteLine($"  {label,-60}  -> {r.S("more")}");
                    }
                }
                if (rows.Count == 0)
                {
                    error.WriteLine("nothing found");
                }
            }
            else if (sqlText)
            {
                var d = (Dictionary<string, object?>)result!;
                output.WriteLine(string.Join("\t", (string[])d["columns"]!));
                foreach (var row in (List<object?[]>)d["rows"]!)
                {
                    output.WriteLine(string.Join("\t", row.Select(v => v == null ? "" : Q.Py(v))));
                }
            }
            else if (schemaText)
            {
                foreach (var (key, value) in (Dictionary<string, object?>)result!)
                {
                    var info = (Dictionary<string, object?>)value!;
                    output.WriteLine($"{key}: {(info.TryGetValue("path", out var p) ? p : "missing " + info["missing"])}");
                    if (info.TryGetValue("tables", out var tables))
                    {
                        foreach (var (tname, tv) in (Dictionary<string, object?>)tables!)
                        {
                            var t = (Dictionary<string, object?>)tv!;
                            var cols = string.Join(", ", ((List<Dictionary<string, object?>>)t["columns"]!).Select(c => c["name"]));
                            output.WriteLine($"  {tname,-22} {(t["rows"] != null ? Q.Py(t["rows"]) : "view"),10}  {cols}");
                        }
                    }
                }
            }
            else
            {
                OfflineQueries.Show(output, result);
            }
            return 0;
        }
        catch (DataQueryException e)
        {
            error.WriteLine(e.Message);
            return 1;
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            error.WriteLine($"SQLite: {e.Message}");
            return 1;
        }
        finally
        {
            output.Flush();
            error.Flush();
        }
    }
}
