using System.Text;
using System.Text.Json.Nodes;
using GateKit.Json;

namespace GateKit.Symbols.Registry;

/// <summary>One place that cites an address: a file and its line, or a field of a registry entry.</summary>
/// <param name="Kind">code (a registry id) or test (a test's path).</param>
/// <param name="Address">The id or path as written, without its prefix.</param>
/// <param name="File">The file, relative to the repository's root, '/' between segments.</param>
/// <param name="Line">The line (from 1) of a mention in running text; 0 for a typed field.</param>
/// <param name="Field">The JSON pointer of a typed field, or null for running text.</param>
public sealed record Citation(string Kind, string Address, string File, int Line, string? Field)
{
    public string Where => Field is null ? $"{File}:{Line}" : $"{File}#{Field}";
}

/// <summary>
/// Every citation of code and of tests in the repository (registry/README.md, "Citing code and
/// tests"): in running text by the prefixes <c>code:</c> and <c>test:</c>, in the registry's data by
/// the fields whose kind the schema fixes. `gatekit refs` lists them, `registry check` resolves them,
/// `registry rename` rewrites them.
/// </summary>
public static class RegistryRefs
{
    /// <summary>The files whose text is searched for citations.</summary>
    static readonly string[] TextExtensions = [".md", ".txt", ".lua", ".cs", ".json", ".jsonl", ".yml", ".yaml", ".xml", ".java", ".sh"];

    static readonly string[] Skipped = [".git", "bin", "obj", "external", "node_modules", ".claude", ".vs"];

    /// <summary>The text files of a repository, relative to its root, sorted. The unit tests (<c>unit/</c>) are
    /// left out: their citations are made-up data the tests feed the scanner.</summary>
    public static IEnumerable<string> TextFiles(string repository)
    {
        var pending = new Stack<string>();
        pending.Push(repository);
        var found = new List<string>();
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(folder))
                if (!Skipped.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)
                    && !(folder == repository && Path.GetFileName(sub) == "unit")) pending.Push(sub);
            foreach (var file in Directory.EnumerateFiles(folder))
                if (TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    found.Add(Path.GetRelativePath(repository, file).Replace('\\', '/'));
        }
        return found.Order(StringComparer.Ordinal);
    }

    /// <summary>The address that starts at <paramref name="start"/> of a line: the id or path up to the first
    /// character that cannot continue it outside brackets; a sentence's last full stop or comma is not part of it.</summary>
    public static string ScanAddress(string line, int start)
    {
        var depth = 0;
        var i = start;
        for (; i < line.Length; i++)
        {
            var c = line[i];
            if (c is '<' or '(') { depth++; continue; }
            if (c is '>' or ')')
            {
                if (depth == 0) break;
                depth--;
                continue;
            }
            if (depth > 0) continue;
            if (char.IsWhiteSpace(c) || c is '`' or '"' or '\'' or ']' or '[' or ';' or '|' or '{' or '}' or '*' or ',' or '\\') break;
        }
        var text = line[start..i];
        while (text.Length > 0 && text[^1] is '.' or ':' or '!' or '?') text = text[..^1];
        return text;
    }

    /// <summary>The prefixed mentions in one text.</summary>
    public static IEnumerable<(string Kind, string Address, int Line)> Mentions(string text)
    {
        var number = 0;
        foreach (var line in text.Split('\n'))
        {
            number++;
            foreach (var kind in new[] { "code", "test" })
            {
                var prefix = kind + ":";
                var at = 0;
                while ((at = line.IndexOf(prefix, at, StringComparison.Ordinal)) >= 0)
                {
                    var before = at == 0 ? ' ' : line[at - 1];
                    at += prefix.Length;
                    if (char.IsLetterOrDigit(before) || before == '_' || before == '-') continue;      // part of a longer word (unicode:, bytecode:)
                    var address = ScanAddress(line, at);
                    if (address.Length == 0 || !(char.IsLetter(address[0]) || address[0] == '_')) continue;
                    // an id always has a scope (unplaced:: at least); `code:match(` and `test:find(` are a script's calls on a variable
                    if (kind == "code" && !address.Contains("::", StringComparison.Ordinal)) continue;
                    if (kind == "test" && System.Text.RegularExpressions.Regex.IsMatch(address, @"^[A-Za-z_]\w*\(")) continue;
                    yield return (kind, address, number);
                }
            }
        }
    }

    /// <summary>Every citation in the repository: prefixed mentions in its text, and the typed fields of the registry's entries.</summary>
    public static List<Citation> All(RegistryStore store)
    {
        var repository = store.Repository;
        var list = new List<Citation>();
        foreach (var file in TextFiles(repository))
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(repository, file)); }
            catch (IOException) { continue; }
            if (!text.Contains("code:", StringComparison.Ordinal) && !text.Contains("test:", StringComparison.Ordinal)) continue;
            foreach (var (kind, address, line) in Mentions(text)) list.Add(new Citation(kind, address, file, line, null));
        }
        foreach (var entry in store.All())
        {
            var file = Path.GetRelativePath(repository, entry.Path).Replace('\\', '/');
            foreach (var (kind, value, pointer) in Typed(entry.Json, "")) list.Add(new Citation(kind, value, file, 0, pointer));
        }
        return list;
    }

    /// <summary>The fields of an entry whose kind the schema fixes as a citation: (code|test, value, pointer).</summary>
    public static IEnumerable<(string Kind, string Value, string Pointer)> Typed(JsonNode? node, string pointer)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    var here = JsonPointer.Append(pointer, key);
                    if (value is JsonValue && JsonText.Text(value) is { } text)
                    {
                        if (key is "concerns" or "type_id" or "returns_id" or "piece_of" or "global" or "function") yield return ("code", text, here);
                        else if (key == "type" && (pointer.EndsWith("/ties/" + LastIndex(pointer), StringComparison.Ordinal) || IsUnder(pointer, "bases"))) yield return ("code", text, here);
                        else if (key == "test" && JsonText.Text(o["kind"]) == "test") yield return ("test", text, here);
                    }
                    else
                        foreach (var inner in Typed(value, here)) yield return inner;
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var here = JsonPointer.Append(pointer, i);
                    if (pointer.EndsWith("/fingerprint/calls", StringComparison.Ordinal) && JsonText.Text(a[i]) is { } call)
                    {
                        if (!call.StartsWith("fp:", StringComparison.Ordinal) && !call.StartsWith("import:", StringComparison.Ordinal)) yield return ("code", call, here);
                        continue;
                    }
                    foreach (var inner in Typed(a[i], here)) yield return inner;
                }
                break;
        }
    }

    static string LastIndex(string pointer) => pointer[(pointer.LastIndexOf('/') + 1)..];

    static bool IsUnder(string pointer, string array) => pointer.Contains("/" + array + "/", StringComparison.Ordinal);

    /// <summary>Whether a citation of code names this id: the id itself, or one of its members.</summary>
    public static bool Names(string cited, string id) =>
        cited == id || (cited.StartsWith(id, StringComparison.Ordinal) && cited.Length > id.Length && cited[id.Length] == '.'
            && RegistryId.IsValid(cited) && RegistryId.EntityOf(cited) == id);

    /// <summary>Whether a citation of a test names this path or a case or step inside it.</summary>
    public static bool NamesTest(string cited, string path) => cited == path || cited.StartsWith(path + "/", StringComparison.Ordinal);

    /// <summary>
    /// The test a path cites, as far as it can be resolved: the longest leading part that is a test's
    /// file under <c>tests/</c> (any extension) or a folder of tests. What lies below a test's file
    /// (its case and step) is not checked.
    /// </summary>
    public static (bool Found, string? Unchecked) ResolveTest(string repository, string path)
    {
        var tests = Path.Combine(repository, "tests");
        var parts = path.Split('/');
        for (var n = parts.Length; n >= 1; n--)
        {
            var head = Path.Combine([tests, .. parts[..n]]);
            var folder = Path.GetDirectoryName(head)!;
            var name = Path.GetFileName(head);
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, name + ".*").Any())
                return (true, n == parts.Length ? null : string.Join("/", parts[n..]));
            if (n == parts.Length && Directory.Exists(head)) return (true, null);
        }
        return (false, null);
    }

    // --- rename ---

    /// <summary>
    /// Renames an entry: its file moves to the new id's path, the id in it and in every build table
    /// changes, and every citation of it or of its members - prefixed mentions in the repository's
    /// text, typed fields of every entry - is rewritten in the same act. The old id is left nowhere.
    /// Returns the citations rewritten.
    /// </summary>
    public static int Rename(RegistryStore store, string oldId, string newId, TextWriter output, LiveLayer? live = null)
    {
        var old = RegistryId.Parse(oldId);
        var fresh = RegistryId.Parse(newId);
        if (old.Member is not null || fresh.Member is not null) throw new RegistryException("an entity is renamed, not a member: a member's name is changed in its entity's file");
        var entry = store.Get(oldId) ?? throw new RegistryException($"{oldId}: no entry of that id");
        if (store.Get(newId) is { } taken) throw new RegistryException($"{newId} exists: {store.Relative(taken.Path)} holds it");
        var json = (JsonObject)entry.Json.DeepClone();
        json.Remove("schema");
        json["id"] = newId;
        if (!newId.StartsWith(RegistryId.Unplaced + "::", StringComparison.Ordinal)) json.Remove("owner");
        store.Write(entry.Kind, json, create: true);
        store.Delete(entry);
        foreach (var (build, binary) in store.Tables())
        {
            var rows = store.Table(build, binary);
            if (!rows.Any(r => r.Id == oldId)) continue;
            store.WriteTable(build, binary, rows.Select(r => r.Id == oldId ? r with { Id = newId } : r));
        }
        var rewritten = Recite(store, oldId, newId, output);
        if (live?.Rename(oldId, newId) is > 0 and var claims) output.WriteLine($"  the live layer: {claims} claim{(claims == 1 ? "" : "s")} follow");
        output.WriteLine($"{oldId} -> {newId}: {store.Relative(store.PathOf(entry.Kind, newId))}, {rewritten} citation{(rewritten == 1 ? "" : "s")} rewritten");
        return rewritten;
    }

    /// <summary>
    /// Removes a committed entry: its file, its rows in every build table and its live claims.
    /// Refused while anything outside the entry cites it - unless <paramref name="into"/> names the
    /// id it has become, and then every citation follows there.
    /// </summary>
    public static void Remove(RegistryStore store, string id, LiveLayer? live, TextWriter output, string? into = null)
    {
        var entry = store.Get(id) ?? throw new RegistryException($"{id}: no entry of that id");
        if (entry.Id != id) throw new RegistryException($"{id} is a member: an entity is removed, a member is taken out of its entity's file");
        var own = Path.GetRelativePath(store.Repository, entry.Path).Replace('\\', '/');
        if (into is null && All(store).Where(c => c.Kind == "code" && Names(c.Address, id) && c.File != own).ToList() is { Count: > 0 } cited)
            throw new RegistryException($"{id} is cited in {cited.Count} place{(cited.Count == 1 ? "" : "s")} (`gatekit refs {id}`), the first: {cited[0].Where}; nothing was removed");
        store.Delete(entry);
        foreach (var (build, binary) in store.Tables())
        {
            var rows = store.Table(build, binary);
            if (rows.RemoveAll(r => r.Id == id) > 0) store.WriteTable(build, binary, rows);
        }
        var rewritten = into is null ? 0 : Recite(store, id, into, output);
        var claims = live?.Withdraw(id, all: true).Count ?? 0;
        output.WriteLine($"removed {id}: its file, its rows" + (claims > 0 ? $", {claims} live claim{(claims == 1 ? "" : "s")}" : "")
            + (into is null ? "" : $"; {rewritten} citation{(rewritten == 1 ? "" : "s")} now name {into}"));
    }

    /// <summary>One text with every prefixed mention of an id, or of its members, rewritten to another id.</summary>
    public static string RewriteMentions(string text, string oldId, string newId, out int count)
    {
        count = 0;
        if (!text.Contains("code:" + oldId, StringComparison.Ordinal)) return text;
        var lines = text.Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            var b = new StringBuilder();
            var line = lines[n];
            var at = 0;
            int found;
            while ((found = line.IndexOf("code:", at, StringComparison.Ordinal)) >= 0)
            {
                var start = found + 5;
                var address = ScanAddress(line, start);
                b.Append(line, at, start - at);
                if (Names(address, oldId)) { b.Append(newId).Append(address[oldId.Length..]); count++; }
                else b.Append(address);
                at = start + address.Length;
            }
            b.Append(line, at, line.Length - at);
            lines[n] = b.ToString();
        }
        return string.Join("\n", lines);
    }

    /// <summary>Rewrites every citation of an id - typed fields of every entry, prefixed mentions in the
    /// repository's text, and the bare id under a key <c>id</c> of a data file that holds places
    /// (`registry places`), whose places are then written again. Returns the citations rewritten.</summary>
    static int Recite(RegistryStore store, string oldId, string newId, TextWriter output)
    {
        var rewritten = 0;
        // typed fields of every entry
        store.Refresh();
        foreach (var e in store.All().ToList())
        {
            var copy = (JsonObject)e.Json.DeepClone();
            var changed = RewriteTyped(copy, "", oldId, newId);
            if (changed == 0) continue;
            copy.Remove("schema");
            store.Write(e.Kind, copy, create: false);
            rewritten += changed;
        }
        // prefixed mentions in text
        foreach (var file in TextFiles(store.Repository))
        {
            var path = Path.Combine(store.Repository, file);
            var text = File.ReadAllText(path);
            var fresh = RewriteMentions(text, oldId, newId, out var count);
            if (count == 0) continue;
            File.WriteAllText(path, fresh, new UTF8Encoding(false));
            output.WriteLine($"  {file}: {count} mention{(count == 1 ? "" : "s")} rewritten");
            rewritten += count;
        }
        store.Refresh();
        // the bare id in a data file that holds places written from the build tables
        foreach (var path in PlacesFiles(store))
        {
            var root = JsonText.Read(path).AsObject();
            var count = RewriteBare(root, oldId, newId);
            if (count == 0) continue;
            root["places"] = LiveCommands.PlacesOf(store, root).Places;
            JsonText.Write(path, root);
            output.WriteLine($"  {Path.GetRelativePath(store.Repository, path).Replace('\\', '/')}: {count} id{(count == 1 ? "" : "s")} rewritten, its places written again");
            rewritten += count;
        }
        return rewritten;
    }

    /// <summary>The JSON data files under <c>mods/</c> that hold places written by `registry places`.</summary>
    public static IEnumerable<string> PlacesFiles(RegistryStore store)
    {
        var mods = Path.Combine(store.Repository, "mods");
        if (!Directory.Exists(mods)) yield break;
        foreach (var path in Directory.EnumerateFiles(mods, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            JsonObject? root;
            try
            {
                if (!File.ReadAllText(path).Contains("\"places\"", StringComparison.Ordinal)) continue;
                root = JsonText.Read(path) as JsonObject;
            }
            catch (Exception e) when (e is IOException or JsonProblemException or ArgumentException) { continue; }
            if (root?["places"] is JsonObject) yield return path;
        }
    }

    static int RewriteBare(JsonNode? node, string oldId, string newId)
    {
        var count = 0;
        if (node is JsonObject o)
            foreach (var (key, value) in o.ToList())
            {
                if (key == "places") continue;
                if (key == "id" && JsonText.Text(value) == oldId) { o[key] = newId; count++; }
                else count += RewriteBare(value, oldId, newId);
            }
        else if (node is JsonArray a)
            foreach (var item in a) count += RewriteBare(item, oldId, newId);
        return count;
    }

    /// <summary>Rewrites the citations of an id inside one entry (typed fields and prefixed mentions in its sentences).</summary>
    internal static int ReciteIn(JsonObject entry, string oldId, string newId)
    {
        var count = RewriteTyped(entry, "", oldId, newId);
        void Walk(JsonNode? node)
        {
            if (node is JsonObject o)
                foreach (var (key, value) in o.ToList())
                {
                    if (JsonText.Text(value) is { } text)
                    {
                        var fresh = RewriteMentions(text, oldId, newId, out var n);
                        if (n > 0) { o[key] = fresh; count += n; }
                    }
                    else Walk(value);
                }
            else if (node is JsonArray a)
                foreach (var item in a) Walk(item);
        }
        Walk(entry);
        return count;
    }

    static int RewriteTyped(JsonNode? node, string pointer, string oldId, string newId)
    {
        var count = 0;
        var cites = Typed(node, pointer).Where(c => c.Kind == "code" && Names(c.Value, oldId)).ToList();
        foreach (var (_, value, at) in cites)
        {
            var parts = JsonPointer.Split(at);
            JsonNode? parent = node;
            foreach (var part in parts[..^1])
                parent = parent is JsonArray a ? a[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)] : parent?[part];
            var last = parts[^1];
            var replaced = newId + value[oldId.Length..];
            if (parent is JsonArray array) array[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] = replaced;
            else if (parent is JsonObject o) o[last] = replaced;
            count++;
        }
        return count;
    }
}
