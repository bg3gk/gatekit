using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;

namespace GateKit.Json;

/// <summary>The <c>json</c> command: check, format, show and describe GateKit's own files
/// (docs/contracts/cli.md, "json"; the rules: docs/contracts/data-format.md).</summary>
public static class JsonCommands
{
    const string About =
        "GateKit's own data files: strict JSON, every kind of file held to its schema (schemas/, bound to paths by schemas/index.json)\n\n"
        + "check finds what does not fit, as file:line:column; fmt writes the one canonical layout; show prints any file for a person; "
        + "schema prints or lists the schemas.";

    /// <summary>Folders a walk of the tree never enters: build output, dependencies, other checkouts, state.</summary>
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", "external", ".claude", ".state", ".vs" };

    public static Command CreateCommand()
    {
        var root = new Command("json", About);
        var rootOption = new Option<string?>("--root")
        {
            Description = "the tree to work on and whose schemas/ to use (default: the checkout the working folder is in; else the schemas built in)",
            Recursive = true,
        };
        root.Options.Add(rootOption);

        int Run(ParseResult p, Func<Tree, TextWriter, TextWriter, int> body)
        {
            var output = p.InvocationConfiguration.Output;
            var error = p.InvocationConfiguration.Error;
            try
            {
                return body(Tree.Find(p.GetValue(rootOption)), output, error);
            }
            catch (Exception e) when (e is JsonProblemException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                error.WriteLine(e.Message);
                return 1;
            }
        }

        // check
        {
            var paths = new Argument<string[]>("paths") { Description = "files or folders (default: every file of the tree the registry knows)", Arity = ArgumentArity.ZeroOrMore };
            var any = new Option<bool>("--any") { Description = "also read every other .json and .jsonl strictly (no schema)" };
            var counts = new Option<bool>("--counts") { Description = "the problems counted by kind, rule and place instead of listed" };
            var json = new Option<bool>("--json") { Description = "the answer as JSON" };
            var limit = new Option<int>("--limit") { Description = "problems listed per file", DefaultValueFactory = _ => 20 };
            var c = new Command("check", "read every file strictly and hold it to its schema; exit 1 with file:line:column for each problem") { paths, any, counts, json, limit };
            c.SetAction(p => Run(p, (tree, output, _) =>
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var report = new CheckReport();
                foreach (var file in tree.Files(p.GetValue(paths) ?? [], p.GetValue(any)))
                    report.Add(tree.Relative(file), tree.Check(file));
                started.Stop();
                if (p.GetValue(json)) output.Write(JsonText.Format(report.ToJson(p.GetValue(limit))));
                else if (p.GetValue(counts)) report.PrintCounts(output);
                else report.Print(output, p.GetValue(limit));
                output.WriteLine($"{report.Files} files, {report.Rows} ledger rows, {report.Problems.Count} problems in {report.FilesWithProblems} files ({started.Elapsed.TotalSeconds:0.00} s)");
                return report.Problems.Count > 0 ? 1 : 0;
            }));
            root.Subcommands.Add(c);
        }
        // fmt
        {
            var paths = new Argument<string[]>("paths") { Description = "files or folders (default: every file of the tree the registry knows)", Arity = ArgumentArity.ZeroOrMore };
            var check = new Option<bool>("--check") { Description = "change nothing: list the files that are not in the canonical layout, exit 1 if any" };
            var c = new Command("fmt", "write files in the one canonical layout (keys in the schema's order, 2-space indent, one line when it fits in 100 columns, LF, UTF-8 without BOM)") { paths, check };
            c.SetAction(p => Run(p, (tree, output, error) =>
            {
                int changed = 0, broken = 0, files = 0;
                foreach (var file in tree.Files(p.GetValue(paths) ?? [], any: false))
                {
                    files++;
                    var bytes = File.ReadAllBytes(file);
                    string canonical;
                    try { canonical = tree.Canonical(file, bytes); }
                    catch (JsonProblemException e)
                    {
                        broken++;
                        foreach (var problem in e.Problems.Take(5)) error.WriteLine(tree.Relative(problem.ToString()));
                        continue;
                    }
                    if (canonical == Encoding.UTF8.GetString(bytes)) continue;
                    changed++;
                    if (p.GetValue(check)) output.WriteLine($"{tree.Relative(file)}: not in the canonical layout");
                    else
                    {
                        File.WriteAllText(file + ".fmt.tmp", canonical, JsonText.Utf8);
                        File.Move(file + ".fmt.tmp", file, overwrite: true);
                        output.WriteLine($"{tree.Relative(file)}: formatted");
                    }
                }
                output.WriteLine($"{files} files, {changed} {(p.GetValue(check) ? "not canonical" : "formatted")}, {broken} that do not parse");
                return (p.GetValue(check) && changed > 0) || broken > 0 ? 1 : 0;
            }));
            root.Subcommands.Add(c);
        }
        // show
        {
            var file = new Argument<string>("file") { Description = "any JSON or JSONL file" };
            var pointer = new Argument<string?>("pointer") { Description = "a JSON pointer inside it, e.g. /creators/0 (a ledger's rows are /0, /1, ...)", Arity = ArgumentArity.ZeroOrOne };
            var limit = new Option<int>("--limit") { Description = "rows of a table shown", DefaultValueFactory = _ => 50 };
            var c = new Command("show", "print a file, or the part a pointer names, for a person: an aligned outline, arrays of rows as tables") { file, pointer, limit };
            c.SetAction(p => Run(p, (tree, output, error) =>
            {
                var path = p.GetValue(file)!;
                JsonNode node;
                if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    node = new JsonArray([.. JsonText.ReadLines(path, null, problem => error.WriteLine(problem), tree.Set).Select(r => (JsonNode)r)]);
                else
                    node = JsonText.Parse(File.ReadAllBytes(path), path).Root;
                var at = p.GetValue(pointer) ?? "";
                var part = at.Length == 0 ? node : JsonPointer.Find(node, at) ?? throw new ArgumentException($"{path}: nothing at {at}");
                Outline.Print(output, part, p.GetValue(limit));
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        // schema
        {
            var kind = new Argument<string?>("kind") { Description = "a kind, such as record/sightings", Arity = ArgumentArity.ZeroOrOne };
            var list = new Option<bool>("--list") { Description = "every kind, its version, what it is and what is bound to it" };
            var c = new Command("schema", "print the schema of a kind, or list them all") { kind, list };
            c.SetAction(p => Run(p, (tree, output, _) =>
            {
                var set = tree.Set;
                if (p.GetValue(list) || p.GetValue(kind) is null)
                {
                    foreach (var schema in set.All.OrderBy(s => s.Kind, StringComparer.Ordinal))
                    {
                        output.WriteLine($"{schema.Id}  {schema.Description}");
                        foreach (var b in set.PathBindings.Where(b => b.Schema == schema)) output.WriteLine($"    file {b.Pattern}");
                        foreach (var b in set.ToolBindings.Where(b => b.Schema == schema)) output.WriteLine($"    tool {b.Pattern}");
                    }
                    return 0;
                }
                output.Write(JsonText.Format(set.Get(p.GetValue(kind)!).Root));
                return 0;
            }));
            root.Subcommands.Add(c);
        }
        return root;
    }

    /// <summary>The tree a command works on: its root, its schemas, and how its files are found and judged.</summary>
    internal sealed class Tree(string? root, SchemaSet set)
    {
        public string? Root { get; } = root;
        public SchemaSet Set { get; } = set;

        /// <summary>The tree given, else the checkout around the working folder (the first folder up
        /// with schemas/index.json); its schemas when it has them, else the built-in ones.</summary>
        public static Tree Find(string? given)
        {
            string? root = given is null ? null : Path.GetFullPath(given);
            if (root is null)
                for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "schemas", "index.json"))) { root = dir.FullName; break; }
            if (root is not null && !Directory.Exists(root)) throw new ArgumentException($"{root}: no such folder");
            var schemas = root is null ? null : Path.Combine(root, "schemas");
            var set = schemas is not null && File.Exists(Path.Combine(schemas, "index.json")) ? SchemaSet.FromFolder(schemas) : SchemaSet.Embedded;
            return new Tree(root, set);
        }

        public string Relative(string text) => Root is null ? text : text.Replace(Root + Path.DirectorySeparatorChar, "").Replace('\\', '/');

        /// <summary>The files to work on: those given (a folder is walked), or the whole tree; from a walk
        /// only what the registry binds, unless <paramref name="any"/>.</summary>
        public IEnumerable<string> Files(string[] given, bool any)
        {
            if (given.Length == 0)
            {
                if (Root is null) throw new ArgumentException("not inside a checkout (no schemas/index.json above the working folder): name the files, or --root");
                given = [Root];
            }
            foreach (var path in given)
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full)) { yield return full; continue; }
                if (!Directory.Exists(full)) throw new ArgumentException($"{path}: no such file or folder");
                foreach (var file in Walk(full).Order(StringComparer.Ordinal))
                {
                    var known = Set.ForPath(file) is not null;
                    var data = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                    if (known || (any && data)) yield return file;
                }
            }
        }

        static IEnumerable<string> Walk(string folder)
        {
            foreach (var file in Directory.EnumerateFiles(folder)) yield return file;
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                if (Skipped.Contains(Path.GetFileName(dir))) continue;
                foreach (var file in Walk(dir)) yield return file;
            }
        }

        /// <summary>The kind of a file, or null: its binding, else the document's own <c>schema</c> field.</summary>
        public string? KindOf(string file, JsonNode? root) => JsonText.ResolveKind(file, root, null, Set);

        /// <summary>Every problem of one file, and its rows when it is a ledger.</summary>
        public (List<JsonProblem> Problems, int Rows, string? Kind) Check(string file)
        {
            var problems = new List<JsonProblem>();
            var source = Relative(file);
            byte[] bytes;
            try { bytes = JsonText.ReadBytes(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ([new JsonProblem(source, 0, 0, "", "io", e.Message)], 0, null);
            }
            if (file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                var kind = Set.ForPath(file)?.Schema.Id;
                var rows = JsonText.ParseLines(bytes, source, kind, Set, problems);
                var total = rows.Count + problems.Select(p => p.Line).Distinct().Count();
                return (problems, total, kind);
            }
            try
            {
                var parsed = JsonText.Parse(bytes, source);
                var kind = KindOf(file, parsed.Root);
                if (kind is not null)
                {
                    if (!Set.TryGet(kind, out _, out var why)) problems.Add(parsed.Problem("/schema", "kind", why));
                    else problems.AddRange(Set.Validate(parsed, kind));
                }
                return (problems, 0, kind);
            }
            catch (JsonProblemException e)
            {
                return ([.. e.Problems], 0, Set.ForPath(file)?.Schema.Id);
            }
        }

        /// <summary>The canonical text of a file (its rows one per line for a ledger).</summary>
        public string Canonical(string file, byte[] bytes)
        {
            if (file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                var kind = Set.ForPath(file)?.Schema.Id;
                var problems = new List<JsonProblem>();
                // a ledger is formatted row by row; a row that does not parse stops it
                var rows = JsonText.ParseLines(bytes, file, null, Set, problems);
                var fatal = problems.Where(p => p.Rule is "syntax" or "duplicate" or "torn").ToList();
                if (fatal.Count > 0) throw new JsonProblemException(fatal);
                var text = new StringBuilder();
                foreach (var row in rows)
                {
                    if (kind is not null) Set.Validate(row, kind);   // the one conversion of empty containers
                    text.Append(JsonText.FormatRow(row, kind, Set)).Append('\n');
                }
                return text.ToString();
            }
            // the one fault fmt mends rather than reports: a byte order mark
            var parsed = JsonText.Parse(HasBom(bytes) ? bytes[3..] : bytes, file);
            var resolved = KindOf(file, parsed.Root);
            if (resolved is not null && !Set.TryGet(resolved, out _, out _)) resolved = null;
            if (resolved is not null) Set.Validate(parsed.Root, resolved);
            return JsonText.Format(parsed.Root, resolved, Set);
        }

        static bool HasBom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    /// <summary>What a check found over many files.</summary>
    sealed class CheckReport
    {
        public int Files, Rows, FilesWithProblems;
        public readonly List<(JsonProblem Problem, string? Kind)> Problems = [];

        public void Add(string file, (List<JsonProblem> Problems, int Rows, string? Kind) result)
        {
            Files++;
            Rows += result.Rows;
            if (file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) _rowsOf[file] = result.Rows;
            if (result.Problems.Count > 0) FilesWithProblems++;
            Problems.AddRange(result.Problems.Select(p => (p, result.Kind)));
        }

        public void Print(TextWriter output, int limit)
        {
            foreach (var group in Problems.GroupBy(p => p.Problem.Source))
            {
                foreach (var (problem, _) in group.Take(limit)) output.WriteLine(problem);
                if (group.Count() > limit) output.WriteLine($"{group.Key}: ... and {group.Count() - limit} more (--limit, or --counts for the tally)");
            }
        }

        IEnumerable<(string Kind, string Rule, string Place, int Count, JsonProblem Example)> Tally() =>
            Problems.GroupBy(p => (Kind: p.Kind ?? "(no kind)", p.Problem.Rule, Place: JsonPointer.Generalize(p.Problem.Pointer)))
                .Select(g => (g.Key.Kind, g.Key.Rule, g.Key.Place.Length == 0 ? "(the row)" : g.Key.Place, g.Count(), g.First().Problem))
                .OrderBy(t => t.Kind, StringComparer.Ordinal).ThenByDescending(t => t.Item4);

        public void PrintCounts(TextWriter output)
        {
            foreach (var (kind, rule, place, count, example) in Tally())
                output.WriteLine($"{kind,-22} {count,7}  {rule,-10} {place,-28} e.g. {example}");
            foreach (var (file, rows, of) in RowsThatDoNotFit())
                output.WriteLine($"{file}: {rows} of {of} rows do not fit");
        }

        readonly Dictionary<string, int> _rowsOf = new(StringComparer.Ordinal);

        /// <summary>For each ledger with problems: how many of its rows have at least one.</summary>
        public IEnumerable<(string File, int Rows, int Of)> RowsThatDoNotFit() =>
            Problems.GroupBy(p => p.Problem.Source).Where(g => _rowsOf.ContainsKey(g.Key))
                .Select(g => (g.Key, g.Select(p => p.Problem.Line).Distinct().Count(), _rowsOf[g.Key]));

        public JsonObject ToJson(int limit) => new()
        {
            ["files"] = Files,
            ["rows"] = Rows,
            ["problems"] = Problems.Count,
            ["counts"] = new JsonArray([.. Tally().Select(t => (JsonNode)new JsonObject
            {
                ["kind"] = t.Kind, ["rule"] = t.Rule, ["place"] = t.Place, ["count"] = t.Count, ["example"] = t.Example.ToString(),
            })]),
            ["rows_that_do_not_fit"] = new JsonArray([.. RowsThatDoNotFit().Select(r => (JsonNode)new JsonObject
            {
                ["file"] = r.File, ["rows"] = r.Rows, ["of"] = r.Of,
            })]),
            ["listed"] = new JsonArray([.. Problems.GroupBy(p => p.Problem.Source).SelectMany(g => g.Take(limit)).Select(p => (JsonNode)new JsonObject
            {
                ["file"] = p.Problem.Source, ["line"] = p.Problem.Line, ["column"] = p.Problem.Column, ["pointer"] = p.Problem.Pointer,
                ["rule"] = p.Problem.Rule, ["what"] = p.Problem.What,
            })]),
        };
    }
}
