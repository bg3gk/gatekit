using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using GateKit.Symbols.Demangling;
using GateKit.Symbols.Pe;
using GateKit.Symbols.Queries;
using GateKit.Symbols.ScriptExtender;
using Microsoft.Data.Sqlite;

namespace GateKit.Symbols.Build;

/// <summary>Options of `symbols build`.</summary>
public sealed class BuildOptions
{
    public string Db { get; set; } = SymbolPaths.SymbolsDb;
    public string Toolkit { get; set; } = SymbolPaths.Toolkit;
    public string Game { get; set; } = SymbolPaths.Game;
    public string? Bg3Se { get; set; } = SymbolPaths.Bg3Se;
    public bool Recursive { get; set; }
    public int Jobs { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
    public bool ExportsOnly { get; set; }
    public bool NoPseudoCode { get; set; }
    public bool CheckMatching { get; set; }
    /// <summary>The pseudo-code database whose code_matches serve as seeds (default: SymbolPaths.DecompDb).</summary>
    public string? DecompDb { get; set; }
}

/// <summary>
/// Reads the Toolkit, the game executables and a Script Extender source tree and writes
/// symbols.sqlite: exports with undecorated declarations, imports, functions, text constants,
/// direct calls, virtual tables, Toolkit functions found in the game, fields from accessors,
/// Script Extender's symbols, ECS records and layouts. The same schema as the Python tool.
/// </summary>
public static partial class SymbolBuilder
{
    static readonly string[] GameBinaries = ["bin/bg3.exe", "bin/bg3_dx11.exe"];
    static readonly Regex TypeNamePattern = new(@"ls::GetTypeName<(.*)>\(void\)$", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>The columns derived from a decorated name, before classes are told from namespaces.</summary>
    internal sealed record Description(string FullName, string[] Scopes, string Method, string Signature, string Access, string Kind, bool Member);

    sealed record ExportRow(int Dll, uint Ordinal, uint? Rva, string? Forwarder, string? Name, Description D);

    /// <summary>What one binary yields; filled in parallel, written in file order.</summary>
    sealed class FileResult
    {
        public required string Path;
        public required string Origin;
        public required string Root;
        public int Id;
        public string NameLower => System.IO.Path.GetFileName(Path).ToLowerInvariant();
        public PeImage? Pe;
        public string? Note;
        public int Exports, Imports;
        public List<ExportRow> ExportRows = [];
        public List<PeImport> ImportRows = [];
        public bool Analysed;
        public IReadOnlyList<FunctionRange> Ranges = [];
        public Dictionary<uint, uint> PartOf = [];
        public Dictionary<uint, long> Sizes = [];
        public List<(uint Rva, string Text, HashSet<uint> Users)> Texts = [];
        public Dictionary<uint, HashSet<uint>> LeaRefs = [];
        public SePatterns.Result? Se;
        public Dictionary<uint, List<Callee>>? Folded;
        public Dictionary<uint, Callee>? Alias;
        public Dictionary<uint, List<List<Callee>>> FirstParts = [];
        public int FunctionCount;
        public uint Whole(uint f) => PartOf.TryGetValue(f, out var p) ? p : f;
    }

    /// <summary>Builds the database; progress goes to <paramref name="log"/>, the summary to <paramref name="output"/>.</summary>
    public static void Run(BuildOptions options, TextWriter output, TextWriter log)
    {
        if (!Directory.Exists(options.Toolkit))
            throw new SymbolsException($"Toolkit folder not found: {options.Toolkit}\nPass --toolkit or set BG3_TOOLKIT.");
        var dbPath = Path.GetFullPath(options.Db);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var tmp = Path.ChangeExtension(dbPath, ".building");
        if (File.Exists(tmp))
            File.Delete(tmp);
        var clock = Stopwatch.StartNew();
        var started = DateTime.Now;

        var comparer = Decomp.Py.PathOrder;
        var toolkitFiles = Directory.EnumerateFiles(options.Toolkit, "*", options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".dll" or ".exe")
            .OrderBy(p => Path.GetRelativePath(options.Toolkit, p).Replace('\\', '/'), comparer)
            .Select(p => new FileResult { Path = p, Origin = "toolkit", Root = options.Toolkit }).ToList();
        var gameFiles = GameBinaries.Select(b => Path.Combine(options.Game, b)).Where(File.Exists)
            .Select(p => new FileResult { Path = p, Origin = "game", Root = options.Game }).ToList();
        if (gameFiles.Count == 0)
            log.WriteLine($"game executables not found under {options.Game}: no strings, type names or matches for the game");
        var files = toolkitFiles.Concat(gameFiles).ToList();
        for (int i = 0; i < files.Count; i++)
            files[i].Id = i + 1;

        // 1. Every binary on its own, in parallel.
        int done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Jobs) }, f =>
        {
            ReadFile(f, options);
            int n = Interlocked.Increment(ref done);
            if (n % 25 == 0)
                lock (log)
                    log.WriteLine($"  {n}/{files.Count} files read");
        });
        log.WriteLine($"  {files.Count} files read, {clock.Elapsed.TotalSeconds:F1} s");

        using var db = Sql.OpenWrite(tmp);
        db.Execute("PRAGMA journal_mode=OFF");
        db.Execute("PRAGMA synchronous=OFF");
        db.Execute("PRAGMA foreign_keys=OFF");     // as Python's sqlite3: dlls rows come after their exports
        Exec(db, SymbolSchema.Tables);

        var byText = new Dictionary<string, Dictionary<string, HashSet<(int, uint)>>>
        {
            ["toolkit"] = new(StringComparer.Ordinal),
            ["game"] = new(StringComparer.Ordinal),
        };
        var rows = new List<ExportRow>();
        var classes = new HashSet<string>(StringComparer.Ordinal);
        int stringId = 0;
        var ecsRows = new List<(int Dll, string Type, string? Context, uint Index, uint? ContextRva, string Source)>();
        var seSymbols = new List<(int Dll, string Name, uint Rva)>();

        using (var tx = db.BeginTransaction())
        {
            using var insImport = Insert(db, tx, "imports", 4);
            using var insFunc = Insert(db, tx, "functions", 4);
            using var insChunk = Insert(db, tx, "chunks", 3);
            using var insString = Insert(db, tx, "strings", 4);
            using var insRef = Insert(db, tx, "string_refs", 2);
            using var insType = Insert(db, tx, "type_names", 5);
            using var insSe = Insert(db, tx, "se_symbols", 5);
            using var insMap = Insert(db, tx, "se_mappings", 4);
            using var insEcs = Insert(db, tx, "ecs_ids", 6);
            using var insDll = Insert(db, tx, "dlls", 12);
            foreach (var f in files)
            {
                foreach (var e in f.ExportRows)
                {
                    if (e.D.Member && e.D.Scopes.Length > 0)
                        classes.Add(string.Join('\u0001', e.D.Scopes));
                    rows.Add(e);
                }
                foreach (var i in f.ImportRows)
                    Run(insImport, f.Id, i.FromDll, i.Name, i.Ordinal);
                if (f.Analysed)
                {
                    foreach (var r in f.Ranges)
                        Run(insFunc, f.Id, r.Begin, r.End - r.Begin, r.Leaf ? 1 : 0);
                    foreach (var (c, parent) in f.PartOf)
                        Run(insChunk, f.Id, c, parent);
                    var texts = byText[f.Origin];
                    foreach (var (rva, text, users) in f.Texts)
                    {
                        stringId++;
                        Run(insString, stringId, f.Id, rva, text);
                        foreach (var u in users)
                            Run(insRef, stringId, u);
                        if (!texts.TryGetValue(text, out var set))
                            texts[text] = set = [];
                        foreach (var u in users)
                            set.Add((f.Id, f.Whole(u)));
                        var m = TypeNamePattern.Match(text);
                        if (m.Success)
                        {
                            var type = m.Groups[1].Value;
                            Run(insType, f.Id, stringId, type, TypeKind(type), users.Count == 1 ? users.First() : null);
                        }
                    }
                    if (f.Se != null)
                    {
                        foreach (var s in f.Se.Symbols)
                        {
                            Run(insSe, f.Id, s.Name, s.Rva, s.Mapping, s.Note);
                            seSymbols.Add((f.Id, s.Name, s.Rva));
                        }
                        foreach (var r in f.Se.Report)
                            Run(insMap, f.Id, r.Mapping, r.Matches, r.Status);
                        var unique = new HashSet<(string, string?, uint, uint?, string)>();
                        var sorted = new List<(string Type, string? Context, uint Index, uint? ContextRva, string Source)>();
                        foreach (var e in f.Se.Ecs)
                            if (unique.Add((e.Type, e.Context, e.IndexRva, e.ContextRva, e.Source)))
                                sorted.Add((e.Type, e.Context, e.IndexRva, e.ContextRva, e.Source));
                        foreach (var e in sorted.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Index))
                        {
                            Run(insEcs, f.Id, e.Type, e.Context, e.Index, e.ContextRva, e.Source);
                            ecsRows.Add((f.Id, e.Type, e.Context, e.Index, e.ContextRva, e.Source));
                        }
                    }
                }
                long size = new FileInfo(f.Path).Length;
                Run(insDll, f.Id, Path.GetFileName(f.Path), Path.GetRelativePath(f.Root, f.Path), size,
                    f.Pe?.Timestamp, f.Pe?.Machine, f.Pe == null ? null : f.Pe.Managed ? 1 : 0, f.Exports, f.Imports, f.Note, f.Origin, f.FunctionCount);
                if (!f.Analysed)
                    f.Pe = null;                     // free the file's bytes
            }
            tx.Commit();
        }
        log.WriteLine($"  {files.Count} files, {rows.Count} exports written, {clock.Elapsed.TotalSeconds:F1} s");

        // 2. Exports. A scope is a class when something was exported as its member; everything before
        //    the first class in a qualified name is the namespace.
        (string Namespace, string Class) Split(string[] scopes)
        {
            for (int i = 1; i <= scopes.Length; i++)
                if (classes.Contains(string.Join('\u0001', scopes, 0, i)))
                    return (string.Join("::", scopes, 0, i - 1), string.Join("::", scopes, i - 1, scopes.Length - i + 1));
            return (string.Join("::", scopes), "");
        }
        var symbolInfo = new List<(int Dll, uint? Rva, string Kind, string FullName, string Namespace, string Class, string Method, string Signature)>(rows.Count);
        using (var tx = db.BeginTransaction())
        {
            using var ins = db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO symbols (dll_id, ordinal, rva, forwarder, mangled, full_name, namespace, class, method, signature, access, kind)"
                              + " VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)";
            for (int i = 1; i <= 12; i++)
                ins.Parameters.Add(new SqliteParameter("$" + i, null));
            foreach (var r in rows)
            {
                var (ns, cls) = Split(r.D.Scopes);
                Run(ins, r.Dll, r.Ordinal, r.Rva, r.Forwarder, r.Name, r.D.FullName, ns, cls, r.D.Method, r.D.Signature, r.D.Access, r.D.Kind);
                symbolInfo.Add((r.Dll, r.Rva, r.D.Kind, r.D.FullName, ns, cls, r.D.Method, r.D.Signature));
            }
            tx.Commit();
        }

        // 3. Virtual tables: the ones the Toolkit exports (slot by slot) and the ones found by shape.
        var analysed = files.Where(f => f.Analysed).ToList();
        var allTables = new Dictionary<long, (int Dll, List<uint> Slots)>();
        var tableRva = new Dictionary<long, uint>();
        var tableName = new Dictionary<long, string>();
        var vtabsByDll = new Dictionary<int, List<uint>>();
        {
            var bySymbolDll = new Dictionary<int, List<int>>();       // dll -> symbol indexes
            for (int i = 0; i < symbolInfo.Count; i++)
            {
                if (!bySymbolDll.TryGetValue(symbolInfo[i].Dll, out var l))
                    bySymbolDll[symbolInfo[i].Dll] = l = [];
                l.Add(i);
            }
            var shapes = new Dictionary<int, SortedDictionary<uint, List<uint>>>();
            var exportedOf = new Dictionary<int, Dictionary<uint, (long SymbolId, List<uint> Slots)>>();
            var slotRows = new Dictionary<int, List<(long, int, int, uint)>>();
            Parallel.ForEach(analysed, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Jobs) }, f =>
            {
                var mine = bySymbolDll.GetValueOrDefault(f.Id) ?? [];
                var stop = new HashSet<uint>(mine.Where(i => symbolInfo[i].Rva != null && symbolInfo[i].Kind is "vftable" or "data" or "special").Select(i => symbolInfo[i].Rva!.Value));
                var exported = new Dictionary<uint, (long, List<uint>)>();
                var slots = new List<(long, int, int, uint)>();
                foreach (var i in mine)
                {
                    if (symbolInfo[i].Kind != "vftable" || symbolInfo[i].Rva is not uint rva)
                        continue;
                    var s = f.Pe!.Vtable(rva, stop);
                    exported[rva] = (i + 1, s);
                    for (int k = 0; k < s.Count; k++)
                        slots.Add((i + 1, f.Id, k, s[k]));
                }
                var stopAll = new HashSet<uint>(stop);
                stopAll.UnionWith(exported.Keys);
                var shaped = f.Pe!.FindVtables(stopAll);
                lock (shapes)
                {
                    shapes[f.Id] = shaped;
                    exportedOf[f.Id] = exported;
                    slotRows[f.Id] = slots;
                }
            });
            using var tx = db.BeginTransaction();
            using var insSlot = Insert(db, tx, "vtable_slots", 4);
            using var insTab = db.CreateCommand();
            insTab.Transaction = tx;
            insTab.CommandText = "INSERT INTO vtabs (id, dll_id, rva, slots, symbol_id) VALUES ($1,$2,$3,$4,$5)";
            for (int i = 1; i <= 5; i++)
                insTab.Parameters.Add(new SqliteParameter("$" + i, null));
            using var insTabSlot = Insert(db, tx, "vtab_slots", 3);
            long nextTable = 0;
            foreach (var f in analysed)
            {
                foreach (var row in slotRows[f.Id])
                    Run(insSlot, row.Item1, row.Item2, row.Item3, row.Item4);
                var exported = exportedOf[f.Id];
                var shaped = shapes[f.Id];
                var list = vtabsByDll[f.Id] = [];
                foreach (var rva in shaped.Keys.Union(exported.Keys).Order())
                {
                    long? symbolId = exported.TryGetValue(rva, out var ex) ? ex.SymbolId : null;
                    var slots = ex.Slots is { Count: > 0 } ? ex.Slots : shaped.GetValueOrDefault(rva) ?? [];
                    if (slots.Count == 0)
                        continue;
                    long id = ++nextTable;
                    Run(insTab, id, f.Id, rva, slots.Count, symbolId);
                    for (int k = 0; k < slots.Count; k++)
                        Run(insTabSlot, id, k, slots[k]);
                    allTables[id] = (f.Id, slots);
                    tableRva[id] = rva;
                    if (symbolId != null)
                        tableName[id] = symbolInfo[(int)symbolId - 1].FullName;
                    list.Add(rva);
                }
            }
            tx.Commit();
        }
        log.WriteLine($"  virtual tables: {allTables.Count}, {clock.Elapsed.TotalSeconds:F1} s");

        // 4. Call graphs and matching.
        var matchRows = Match(options, files, rows, allTables, tableRva, tableName, byText, db, log);
        log.WriteLine($"  matching done, {clock.Elapsed.TotalSeconds:F1} s");

        // 5. Uses of known globals by the functions of the game.
        foreach (var f in gameFiles.Where(f => f.Analysed))
        {
            var wanted = new HashSet<uint>(ecsRows.Where(e => e.Dll == f.Id).Select(e => e.Index));
            wanted.UnionWith(seSymbols.Where(s => s.Dll == f.Id).Select(s => s.Rva));
            wanted.UnionWith(vtabsByDll.GetValueOrDefault(f.Id) ?? []);
            var xrefs = Disassembly.Xrefs(f.Pe!, wanted, options.Jobs);
            using var tx = db.BeginTransaction();
            using var ins = Insert(db, tx, "xrefs", 5);
            foreach (var (func, target, kind, uses) in xrefs)
                Run(ins, f.Id, func, target, kind.ToString(), uses);
            tx.Commit();
            log.WriteLine($"  {f.NameLower}: {xrefs.Count} references to known globals, {clock.Elapsed.TotalSeconds:F1} s");
        }

        // 6. Fields from accessors.
        WriteAccessors(db, files, rows, symbolInfo, matchRows);

        // 7. Script Extender's layouts, enumerations and Lua classes.
        if (!string.IsNullOrEmpty(options.Bg3Se))
            WriteLayouts(db, options.Bg3Se);
        else
            log.WriteLine("Script Extender source not found (--bg3se / BG3SE_SRC): no SE symbols, ECS records or layouts");

        Exec(db, SymbolSchema.Indexes);
        var glasses = Path.Combine(options.Toolkit, "Glasses.exe");
        string Version(string path)
        {
            try { return PeImage.Open(path).FileVersion(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return ""; }
        }
        using (var tx = db.BeginTransaction())
        {
            using var ins = Insert(db, tx, "meta", 2);
            Run(ins, "toolkit", options.Toolkit);
            Run(ins, "toolkit_file_version", File.Exists(glasses) ? Version(glasses) : "");
            Run(ins, "game", options.Game);
            Run(ins, "game_file_version", gameFiles.Count > 0 ? Version(gameFiles[0].Path) : "");
            Run(ins, "bg3se", options.Bg3Se ?? "");
            Run(ins, "built", started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Run(ins, "build_seconds", clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture));
            tx.Commit();
        }
        db.Close();
        SqliteConnection.ClearAllPools();
        File.Move(tmp, dbPath, overwrite: true);
        output.WriteLine($"{dbPath}: {files.Count} files, {rows.Count} exports, {clock.Elapsed.TotalSeconds:F1} s");
        using var check = SymbolContext.Connect(dbPath);
        using var context = new SymbolContext(check, output, log, SymbolsEnvironment.FromEnvironment());
        SymbolText.Stats(context);
    }

    internal static string TypeKind(string name)
    {
        if (name[..Math.Min(40, name.Length)].Contains("ecs::query::spec::Spec<", StringComparison.Ordinal))
            return "query";
        var bare = name.Split('<', 2)[0];
        if (bare.EndsWith("System", StringComparison.Ordinal))
            return "system";
        if (bare.EndsWith("Component", StringComparison.Ordinal))
            return "component";
        return "type";
    }

    static void ReadFile(FileResult f, BuildOptions options)
    {
        try
        {
            f.Pe = PeImage.Open(f.Path);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            f.Note = $"not read: {e.Message}";
            return;
        }
        var pe = f.Pe;
        try
        {
            foreach (var e in pe.Exports())
            {
                f.Exports++;
                var d = e.Name == null
                    ? new Description($"#{e.Ordinal}", [], "", "", "", "ordinal", false)
                    : Describe(e.Name);
                f.ExportRows.Add(new ExportRow(f.Id, e.Ordinal, e.Rva, e.Forwarder, e.Name, d));
            }
            f.ImportRows = pe.Imports().ToList();
            f.Imports = f.ImportRows.Count;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            f.Note = $"tables damaged: {e.Message}";
        }
        if (pe.Managed || options.ExportsOnly)
            return;
        try
        {
            var functions = pe.Functions();
            f.Ranges = pe.FunctionRanges();
            f.FunctionCount = functions.Length;
            // A function the compiler split into parts is one function for matching: its size is the
            // sum of the parts, its calls and text constants are those of all parts.
            f.PartOf = pe.ChunkParents();
            foreach (var r in f.Ranges)
            {
                var w = f.Whole(r.Begin);
                f.Sizes[w] = f.Sizes.GetValueOrDefault(w) + (r.End - r.Begin);
            }
            if (functions.Length > 0)
            {
                var (texts, refs) = pe.StringRefs();
                foreach (var (rva, text) in texts)
                    f.Texts.Add((rva, text, refs[rva]));
            }
            f.Analysed = true;
            foreach (var (target, users) in pe.LeaRefs)
                f.LeaRefs[target] = users.Select(f.Whole).ToHashSet();
            if (f.Origin == "game" && !string.IsNullOrEmpty(options.Bg3Se))
                f.Se = SePatterns.Scan(pe, options.Bg3Se);
            if (functions.Length > 0)
            {
                var (calls, alias) = pe.Calls();
                var folded = new Dictionary<uint, List<Callee>>();
                var ordered = calls.Keys.Order().ToArray();
                foreach (var func in ordered)                  // parts follow their function in the file
                {
                    var w = f.Whole(func);
                    if (!folded.TryGetValue(w, out var l))
                        folded[w] = l = [];
                    l.AddRange(calls[func]);
                }
                f.Folded = folded;
                f.Alias = alias;
                foreach (var func in ordered)
                    if (f.PartOf.ContainsKey(func) || folded[func].Count != calls[func].Count)
                    {
                        var w = f.Whole(func);
                        if (!f.FirstParts.TryGetValue(w, out var l))
                            f.FirstParts[w] = l = [];
                        l.Add(calls[func]);
                    }
            }
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            f.Note = $"code not analysed: {e.Message}";
        }
    }

    static readonly Regex AccessPattern = new(@"^(public|protected|private): ", RegexOptions.Compiled);
    static readonly Regex Special = new(@"^`(vftable|vbtable|vcall|RTTI|local vftable|scalar deleting|vector deleting|default constructor|"
                                        + @"vbase destructor|eh vector|copy constructor closure|local static guard|dynamic initializer|"
                                        + @"dynamic atexit|string)", RegexOptions.Compiled);

    /// <summary>dbghelp's result as the Python tool used it: null for failures, unchanged names and names still starting with '?'.</summary>
    static string? Undecorate(string mangled, UndecorateFlags flags)
    {
        var text = MsvcDemangler.Undecorate(mangled, flags);
        return text == null || text == mangled || text.StartsWith('?') ? null : text;
    }

    internal static Description Describe(string mangled)
    {
        if (!mangled.StartsWith('?'))
            return new Description(mangled, [], mangled, mangled, "", "c", false);
        var full = Undecorate(mangled, UndecorateFlags.NoMsKeywords);
        var name = Undecorate(mangled, UndecorateFlags.NameOnly);
        if (full == null || name == null)
            return new Description(mangled, [], "", full ?? "", "", "undecoded", false);
        var parts = SplitScopes(name);
        var method = parts[^1];
        var m = AccessPattern.Match(full);
        var access = m.Success ? m.Groups[1].Value : "";
        string kind;
        if (method == "`vftable'" || method.StartsWith("`vftable'{", StringComparison.Ordinal))
            kind = "vftable";
        else if (Special.IsMatch(method))
            kind = "special";
        else if (full.Replace("`anonymous namespace'", "").Contains('(') || method.Contains("operator", StringComparison.Ordinal))
            kind = access.Length > 0 ? "method" : "function";
        else
            kind = "data";
        bool member = access.Length > 0 || kind is "vftable" or "special";
        return new Description(name, parts[..^1], method, full, access, kind, member);
    }

    /// <summary>Splits a qualified name on the '::' that are not inside &lt;&gt;, (), `quotes' or brackets.</summary>
    internal static string[] SplitScopes(string name)
    {
        var parts = new List<string>();
        int depth = 0, start = 0, i = 0, n = name.Length;
        while (i < n)
        {
            char c = name[i];
            if (depth == 0 && i == start && string.CompareOrdinal(name, i, "operator", 0, 8) == 0)
            {
                if (i + 8 >= n || !(char.IsLetterOrDigit(name[i + 8]) || name[i + 8] == '_'))
                    break;                  // operator<, operator->, operator() ...: the rest is one name
            }
            if (c is '<' or '(' or '`' or '[' or '{')
                depth++;
            else if (c is '>' or ')' or '\'' or ']' or '}' && depth > 0)
                depth--;
            else if (c == ':' && depth == 0 && i + 1 < n && name[i + 1] == ':')
            {
                parts.Add(name[start..i]);
                i += 2;
                start = i;
                continue;
            }
            i++;
        }
        parts.Add(name[start..]);
        return [.. parts];
    }

    // ------------------------------------------------------------------ SQL helpers

    static void Exec(SqliteConnection db, string script)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = script;
        cmd.ExecuteNonQuery();
    }

    static SqliteCommand Insert(SqliteConnection db, SqliteTransaction tx, string table, int columns)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {table} VALUES ({string.Join(',', Enumerable.Range(1, columns).Select(i => "$" + i))})";
        for (int i = 1; i <= columns; i++)
            cmd.Parameters.Add(new SqliteParameter("$" + i, null));
        cmd.Prepare();
        return cmd;
    }

    static void Run(SqliteCommand cmd, params object?[] values)
    {
        for (int i = 0; i < values.Length; i++)
            cmd.Parameters[i].Value = values[i] switch
            {
                null => DBNull.Value,
                uint u => (long)u,
                var v => v,
            };
        cmd.ExecuteNonQuery();
    }
}
