using System.Text.Json.Nodes;

namespace GateKit.Json;

public static partial class JsonText
{
    /// <summary>
    /// The kind a file is held to: the one given, else the registry's binding of its path, else the
    /// <c>schema</c> field the document carries; null when none of them says (the file is read as
    /// strict JSON only).
    /// </summary>
    public static string? ResolveKind(string path, JsonNode? root, string? kind = null, SchemaSet? set = null)
    {
        if (kind is not null) return kind;
        set ??= SchemaSet.Embedded;
        if (set.ForPath(path) is { } binding) return binding.Schema.Id;
        return Text((root as JsonObject)?["schema"]);
    }

    /// <summary>A document, read strictly and held to its kind (see <see cref="ResolveKind"/>).
    /// A missing file throws <see cref="FileNotFoundException"/>, a broken or misfitting one
    /// <see cref="JsonProblemException"/>: never an empty object in their place.</summary>
    public static JsonNode Read(string path, string? kind = null, SchemaSet? set = null) => ReadDocument(path, kind, set).Root;

    /// <summary>The document or null when there is no such file; a file that exists and is broken still throws.</summary>
    public static JsonNode? ReadIfExists(string path, string? kind = null, SchemaSet? set = null) =>
        File.Exists(path) ? Read(path, kind, set) : null;

    /// <summary>As <see cref="Read"/>, with the text kept for positions.</summary>
    public static ParsedJson ReadDocument(string path, string? kind = null, SchemaSet? set = null)
    {
        if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{path} is a ledger, one row per line: read it with ReadLines");
        if (!File.Exists(path)) throw new FileNotFoundException($"{path}: no such file", path);
        var parsed = Parse(ReadBytes(path), path);
        Check(parsed, ResolveKind(path, parsed.Root, kind, set), set);
        return parsed;
    }

    /// <summary>Holds a parsed document to a kind; throws with every problem. A null kind checks nothing.</summary>
    public static void Check(ParsedJson parsed, string? kind, SchemaSet? set = null)
    {
        if (kind is null) return;
        set ??= SchemaSet.Embedded;
        if (!set.TryGet(kind, out _, out var why)) throw new JsonProblemException(parsed.Problem("", "kind", why));
        var problems = set.Validate(parsed, kind);
        if (problems.Count > 0) throw new JsonProblemException(problems);
    }

    /// <summary>Writes a document in the canonical layout after holding it to its kind (given, bound to
    /// the path, or named by its <c>schema</c> field - one of them must say), atomically: a temporary
    /// file beside it, then a rename. On a misfit nothing is written.</summary>
    public static void Write(string path, JsonNode node, string? kind = null, SchemaSet? set = null)
    {
        set ??= SchemaSet.Embedded;
        var resolved = ResolveKind(path, node, kind, set)
            ?? throw new ArgumentException($"{path}: no schema says what this file is - give a kind, or bind the path in schemas/index.json");
        var misfits = set.Validate(node, resolved);
        if (misfits.Count > 0) throw new JsonProblemException([.. misfits.Select(m => new JsonProblem(path, 0, 0, m.Pointer, m.Rule, m.What))]);
        WriteAtomically(path, Format(node, resolved, set));
    }

    /// <summary>Replaces a whole ledger, every row held to its kind, atomically. The caller holds the
    /// ledger's lock when others may append meanwhile.</summary>
    public static void WriteLines(string path, IEnumerable<JsonNode> rows, string? kind = null, SchemaSet? set = null)
    {
        set ??= SchemaSet.Embedded;
        var resolved = RowKind(path, kind, set);
        WriteAtomically(path, Lines(path, rows.ToList(), resolved, set));
    }

    /// <summary>
    /// Appends rows to a ledger: each held to its kind first (all or none), written as canonical lines
    /// in one write, under the scope's lock - <paramref name="held"/> when the caller has it, else
    /// the file's own. Refuses a file whose last byte is not a newline (a torn row stays where it is
    /// until somebody looks at it).
    /// </summary>
    public static void Append(string path, IReadOnlyCollection<JsonNode> rows, string? kind = null, JsonLock? held = null, SchemaSet? set = null)
    {
        if (rows.Count == 0) return;
        set ??= SchemaSet.Embedded;
        var resolved = RowKind(path, kind, set);
        var bytes = Utf8.GetBytes(Lines(path, rows, resolved, set));
        using var own = held is null ? JsonLock.Take(path) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // a reader that maps the file (git while committing it) makes Windows refuse a write for a moment
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (file.Length > 0)
                {
                    file.Seek(-1, SeekOrigin.End);
                    if (file.ReadByte() != '\n')
                        throw new JsonProblemException(new JsonProblem(path, LineCount(path), 1, "", "torn",
                            "the last line has no newline: a write was cut off or the file was edited by hand; nothing was appended - finish or remove that line"));
                }
                file.Seek(0, SeekOrigin.End);
                file.Write(bytes);
                file.Flush(flushToDisk: false);
                return;
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>One row appended; see <see cref="Append(string, IReadOnlyCollection{JsonNode}, string?, JsonLock?, SchemaSet?)"/>.</summary>
    public static void Append(string path, JsonNode row, string? kind = null, JsonLock? held = null, SchemaSet? set = null) =>
        Append(path, [row], kind, held, set);

    /// <summary>
    /// The rows of a ledger in the order written, each read strictly and held to the ledger's kind.
    /// A row that does not fit - a broken line, an empty line, a torn last line without its newline -
    /// is a problem at its <c>file:line</c>: with <paramref name="onProblem"/> it is told there and
    /// left out, without it the whole read throws. A missing file throws.
    /// </summary>
    public static List<JsonObject> ReadLines(string path, string? kind = null, Action<JsonProblem>? onProblem = null, SchemaSet? set = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{path}: no such file", path);
        set ??= SchemaSet.Embedded;
        var resolved = kind ?? set.ForPath(path)?.Schema.Id;
        if (resolved is not null && !set.TryGet(resolved, out _, out var why)) throw new ArgumentException(why);
        var problems = new List<JsonProblem>();
        var rows = ParseLines(ReadBytes(path), path, resolved, set, problems);
        if (problems.Count > 0)
        {
            if (onProblem is null) throw new JsonProblemException(problems);
            foreach (var p in problems) onProblem(p);
        }
        return rows;
    }

    /// <summary>The rows, or null when there is no such file.</summary>
    public static List<JsonObject>? ReadLinesIfExists(string path, string? kind = null, Action<JsonProblem>? onProblem = null, SchemaSet? set = null) =>
        File.Exists(path) ? ReadLines(path, kind, onProblem, set) : null;

    /// <summary>The rows of ledger text; every problem added to <paramref name="problems"/>, its row left out.</summary>
    internal static List<JsonObject> ParseLines(byte[] bytes, string source, string? kind, SchemaSet set, List<JsonProblem> problems)
    {
        var rows = new List<JsonObject>();
        var start = 0;
        var line = 0;
        while (start < bytes.Length)
        {
            line++;
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            var torn = end < 0;
            if (torn) end = bytes.Length;
            var length = end - start;
            if (length > 0 && bytes[end - 1] == '\r') length--;
            var span = bytes.AsSpan(start, length);
            start = end + 1;
            if (span.IndexOfAnyExcept((byte)' ', (byte)'\t') < 0)
            {
                problems.Add(new JsonProblem(source, line, 1, "", "empty", "an empty line: a ledger holds one row on every line"));
                continue;
            }
            ParsedJson parsed;
            try { parsed = Parse(span.ToArray(), source, line); }
            catch (JsonProblemException e)
            {
                problems.Add(torn
                    ? new JsonProblem(source, line, 1, "", "torn", $"the last line is cut off (no newline, and it does not parse: {e.Problems[0].What})")
                    : e.Problems[0]);
                continue;
            }
            if (torn)
            {
                problems.Add(new JsonProblem(source, line, 1, "", "torn", "the last line has no newline: a write was cut off or the file was edited by hand"));
                continue;
            }
            if (parsed.Root is not JsonObject row)
            {
                problems.Add(parsed.Problem("", "type", "a ledger row is an object"));
                continue;
            }
            if (kind is not null)
            {
                var misfits = set.Validate(parsed, kind);
                if (misfits.Count > 0)
                {
                    problems.AddRange(misfits);
                    continue;
                }
            }
            rows.Add(row);
        }
        return rows;
    }

    static string RowKind(string path, string? kind, SchemaSet set) =>
        kind ?? set.ForPath(path)?.Schema.Id
        ?? throw new ArgumentException($"{path}: no schema says what its rows are - give a kind, or bind the path in schemas/index.json");

    static string Lines(string path, IReadOnlyCollection<JsonNode> rows, string kind, SchemaSet set)
    {
        var problems = new List<JsonProblem>();
        var text = new System.Text.StringBuilder();
        var number = 0;
        foreach (var row in rows)
        {
            number++;
            foreach (var m in set.Validate(row, kind))
                problems.Add(new JsonProblem($"{path} (new row {number})", 0, 0, m.Pointer, m.Rule, m.What));
            text.Append(FormatRow(row, kind, set)).Append('\n');
        }
        if (problems.Count > 0) throw new JsonProblemException(problems);
        return text.ToString();
    }

    static void WriteAtomically(string path, string text)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, text, Utf8);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, full, overwrite: true);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 40)
                {
                    Thread.Sleep(50);
                }
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>All of a file, shared with writers (a ledger being appended to, the game writing an answer), retried while held.</summary>
    internal static byte[] ReadBytes(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return bytes;
            }
            catch (FileNotFoundException) { throw; }
            catch (DirectoryNotFoundException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }

    static int LineCount(string path)
    {
        var bytes = ReadBytes(path);
        var lines = 1;
        foreach (var b in bytes)
            if (b == '\n') lines++;
        return lines;
    }
}
