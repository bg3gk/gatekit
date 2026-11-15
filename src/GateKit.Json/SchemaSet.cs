using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Json;

/// <summary>One schema file: the kind it describes (its path under <c>schemas/</c>), its version and
/// its text.</summary>
public sealed class Schema
{
    internal Schema(string kind, int version, JsonObject root, ParsedJson parsed)
    {
        Kind = kind;
        Version = version;
        Root = root;
        Parsed = parsed;
    }

    /// <summary>The kind, such as <c>record/sightings</c>: the file's path under schemas/ without <c>.schema.json</c>.</summary>
    public string Kind { get; }

    public int Version { get; }

    /// <summary><c>kind/version</c>: what a document's <c>schema</c> field says and what the registry binds a file to.</summary>
    public string Id => $"{Kind}/{Version}";

    public JsonObject Root { get; }

    public string? Description => JsonText.Text(Root["description"]) ?? JsonText.Text(Root["title"]);

    internal ParsedJson Parsed { get; }
}

/// <summary>What the registry says about a path or a tool: the kind and version it is bound to.</summary>
public sealed record Binding(string Pattern, Schema Schema, string? About);

/// <summary>
/// The schemas of every kind of file and tool answer GateKit has (<c>schemas/**/*.schema.json</c>,
/// embedded in this assembly) and the registry (<c>schemas/index.json</c>) that binds paths and tool
/// names to them.
///
/// A schema is JSON Schema 2020-12 cut down to a subset implemented here, with our own messages:
/// type, properties, required, additionalProperties, enum, const, items, pattern, minLength,
/// minimum, maximum, $ref within the set, oneOf on a const discriminator, format (uint64, int64,
/// time) and the annotations description, title, $id, $schema and $defs. A schema that uses any
/// other keyword is refused when the set is loaded. Two rules of the format live here rather than
/// in every reader: absent means not known (a null where a type is given is refused), and where a
/// schema says object an empty array is read as an empty object, and the reverse - the one
/// conversion, because the bridge's Lua cannot tell the two apart.
/// </summary>
public sealed class SchemaSet
{
    /// <summary>The keywords of the subset.</summary>
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "$schema", "$id", "$ref", "$defs", "title", "description",
        "type", "properties", "required", "additionalProperties", "enum", "const", "items",
        "pattern", "minLength", "minimum", "maximum", "oneOf", "format",
    };

    /// <summary>The formats a string may be declared to have.</summary>
    public static readonly IReadOnlySet<string> Formats = new HashSet<string>(StringComparer.Ordinal) { "uint64", "int64", "time" };

    static readonly string[] TypeNames = ["object", "array", "string", "integer", "number", "boolean", "null"];

    /// <summary>The one spelling of a time: UTC to the second.</summary>
    public const string TimeFormat = "yyyy-MM-ddTHH:mm:ssZ";

    readonly Dictionary<string, Schema> _schemas;
    readonly List<(Regex Glob, Binding Binding)> _paths = [];
    readonly Dictionary<string, Binding> _tools = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

    SchemaSet(Dictionary<string, Schema> schemas) => _schemas = schemas;

    static readonly Lazy<SchemaSet> EmbeddedSet = new(() => Load(EmbeddedFiles(), "schemas"));

    /// <summary>The schemas this build of GateKit was made with.</summary>
    public static SchemaSet Embedded => EmbeddedSet.Value;

    /// <summary>The schemas of a folder (a checkout's <c>schemas/</c>): its <c>*.schema.json</c> and its <c>index.json</c>.</summary>
    public static SchemaSet FromFolder(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllBytes(f)))
            .Where(f => f.Item1.EndsWith(".schema.json", StringComparison.Ordinal) || f.Item1 == "index.json")
            .ToList();
        return Load(files, folder.Replace('\\', '/').TrimEnd('/'));
    }

    static List<(string, byte[])> EmbeddedFiles()
    {
        var assembly = typeof(SchemaSet).Assembly;
        var files = new List<(string, byte[])>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var path = name.Replace('\\', '/');
            if (!path.StartsWith("schemas/", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files.Add((path["schemas/".Length..], memory.ToArray()));
        }
        return files;
    }

    /// <summary>Loads and checks a set: every schema within the subset, every reference resolved, the
    /// registry's every binding to a schema that exists at that version. Throws with every problem.</summary>
    static SchemaSet Load(IReadOnlyList<(string Path, byte[] Bytes)> files, string where)
    {
        var problems = new List<JsonProblem>();
        var schemas = new Dictionary<string, Schema>(StringComparer.Ordinal);
        ParsedJson? index = null;
        foreach (var (path, bytes) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ParsedJson parsed;
            try { parsed = JsonText.Parse(bytes, where + "/" + path); }
            catch (JsonProblemException e) { problems.AddRange(e.Problems); continue; }
            if (path == "index.json") { index = parsed; continue; }
            var kind = path[..^".schema.json".Length];
            if (parsed.Root is not JsonObject root) { problems.Add(parsed.Problem("", "schema", "a schema is an object")); continue; }
            var id = JsonText.Text(root["$id"]);
            var version = 0;
            if (id is null || !id.StartsWith(kind + "/", StringComparison.Ordinal) || !int.TryParse(id[(kind.Length + 1)..], out version) || version < 1)
            {
                problems.Add(parsed.Problem("/$id", "schema", $"`$id` is \"{kind}/<version>\" (the file's path under schemas/ and a version from 1), not {root["$id"]?.ToJsonString() ?? "absent"}"));
                continue;
            }
            schemas[kind] = new Schema(kind, version, root, parsed);
        }
        var set = new SchemaSet(schemas);
        foreach (var schema in schemas.Values) set.CheckSchema(schema, schema.Root, "", problems);
        if (index is not null) set.LoadIndex(index, problems);
        else problems.Add(new JsonProblem(where + "/index.json", 0, 0, "", "registry", "the registry index.json is missing"));
        if (problems.Count > 0) throw new JsonProblemException(problems);
        return set;
    }

    /// <summary>Every schema, by kind.</summary>
    public IReadOnlyCollection<Schema> All => _schemas.Values;

    /// <summary>The bindings of paths, in the registry's order (the first that matches a path wins).</summary>
    public IEnumerable<Binding> PathBindings => _paths.Select(p => p.Binding);

    /// <summary>The bindings of tool names.</summary>
    public IEnumerable<Binding> ToolBindings => _tools.Values;

    /// <summary>The schema of a kind, given as <c>kind</c> or <c>kind/version</c> (which must be the version this set has).</summary>
    public Schema Get(string kind) => TryGet(kind, out var schema, out var why) ? schema : throw new ArgumentException(why);

    public bool TryGet(string kind, out Schema schema, out string why)
    {
        why = "";
        var name = kind;
        int? version = null;
        var slash = kind.LastIndexOf('/');
        if (slash > 0 && int.TryParse(kind[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            name = kind[..slash];
            version = v;
        }
        if (!_schemas.TryGetValue(name, out schema!))
        {
            why = $"no schema of kind {name}; `gatekit json schema --list` lists them";
            return false;
        }
        if (version is { } n && n != schema.Version)
        {
            why = $"{name} is at version {schema.Version} here, not {n}: the file was written for another version of GateKit";
            return false;
        }
        return true;
    }

    /// <summary>The binding of a file by its path: the first glob of the registry its trailing segments match.</summary>
    public Binding? ForPath(string path)
    {
        var normal = path.Replace('\\', '/');
        foreach (var (glob, binding) in _paths)
            if (glob.IsMatch(normal)) return binding;
        return null;
    }

    /// <summary>The schema of a tool's answer, if the registry names one.</summary>
    public Binding? ForTool(string tool) => _tools.GetValueOrDefault(tool);

    // ------------------------------------------------------------------------------------------
    // The registry

    void LoadIndex(ParsedJson index, List<JsonProblem> problems)
    {
        if (_schemas.ContainsKey("registry/index"))
        {
            foreach (var p in Validate(index.Root, "registry/index")) problems.Add(index.Problem(p.Pointer, p.Rule, p.What));
            if (problems.Count > 0) return;
        }
        var number = 0;
        foreach (var entry in index.Root["files"] as JsonArray ?? [])
        {
            var at = $"/files/{number++}";
            var pattern = JsonText.Text(entry?["path"]) ?? "";
            if (Bind(index, at, entry, problems) is { } schema) _paths.Add((Glob(pattern), new Binding(pattern, schema, JsonText.Text(entry?["about"]))));
        }
        number = 0;
        foreach (var entry in index.Root["tools"] as JsonArray ?? [])
        {
            var at = $"/tools/{number++}";
            var tool = JsonText.Text(entry?["tool"]) ?? "";
            if (Bind(index, at, entry, problems) is { } schema && !_tools.TryAdd(tool, new Binding(tool, schema, JsonText.Text(entry?["about"]))))
                problems.Add(index.Problem(at + "/tool", "registry", $"the tool {tool} is bound twice"));
        }
    }

    Schema? Bind(ParsedJson index, string at, JsonNode? entry, List<JsonProblem> problems)
    {
        var kind = JsonText.Text(entry?["kind"]) ?? "";
        if (!kind.Contains('/') || !int.TryParse(kind[(kind.LastIndexOf('/') + 1)..], out _))
        {
            problems.Add(index.Problem(at + "/kind", "registry", $"a binding names kind and version, such as record/sightings/1, not {kind}"));
            return null;
        }
        if (TryGet(kind, out var schema, out var why)) return schema;
        problems.Add(index.Problem(at + "/kind", "registry", why));
        return null;
    }

    /// <summary>A glob of the registry as a regular expression on the trailing segments of a path:
    /// <c>*</c> within a segment, <c>**/</c> any number of segments.</summary>
    internal static Regex Glob(string pattern)
    {
        var sb = new System.Text.StringBuilder("(?:^|/)");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; sb.Append("(?:[^/]+/)*"); }
                else sb.Append(".*");
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    // ------------------------------------------------------------------------------------------
    // Checking the schemas themselves

    void CheckSchema(Schema file, JsonNode? node, string at, List<JsonProblem> problems)
    {
        void Bad(string pointer, string what) => problems.Add(file.Parsed.Problem(pointer, "schema", what));
        if (node is JsonValue v && v.GetValueKind() is JsonValueKind.True) return;   // `true`: anything
        if (node is not JsonObject o) { Bad(at, "a schema is an object"); return; }
        foreach (var (key, value) in o)
        {
            var here = JsonPointer.Append(at, key);
            if (!Keywords.Contains(key)) { Bad(here, $"`{key}` is outside the subset GateKit implements ({string.Join(", ", Keywords.Order(StringComparer.Ordinal))})"); continue; }
            switch (key)
            {
                case "type":
                    var names = value is JsonArray list ? list.Select(JsonText.Text).ToList() : [JsonText.Text(value)];
                    foreach (var name in names)
                        if (name is null || !TypeNames.Contains(name)) Bad(here, $"`type` is one of {string.Join(", ", TypeNames)} or a list of them");
                    break;
                case "properties":
                    if (value is not JsonObject props) { Bad(here, "`properties` is an object of schemas"); break; }
                    if (o["additionalProperties"] is null) Bad(at.Length == 0 ? "/properties" : at, "a schema with `properties` says `additionalProperties` (false, true or a schema): strict by statement, not by default");
                    foreach (var (name, sub) in props) CheckSchema(file, sub, JsonPointer.Append(here, name), problems);
                    if (at.Length == 0 && props["schema"] is { } own && JsonText.Text(own["const"]) != file.Id)
                        Bad(JsonPointer.Append(here, "schema"), $"a document's `schema` field is the const \"{file.Id}\"");
                    break;
                case "required":
                    if (value is not JsonArray req || req.Any(r => JsonText.Text(r) is null)) { Bad(here, "`required` is a list of names"); break; }
                    foreach (var name in req.Select(JsonText.Text))
                        if (o["properties"] is not JsonObject p || !p.ContainsKey(name!)) Bad(here, $"`{name}` is required and not among the properties");
                    break;
                case "additionalProperties":
                    if (value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False) break;
                    CheckSchema(file, value, here, problems);
                    break;
                case "items":
                    CheckSchema(file, value, here, problems);
                    break;
                case "$defs":
                    if (value is not JsonObject defs) { Bad(here, "`$defs` is an object of schemas"); break; }
                    foreach (var (name, sub) in defs) CheckSchema(file, sub, JsonPointer.Append(here, name), problems);
                    break;
                case "enum":
                    if (value is not JsonArray { Count: > 0 }) Bad(here, "`enum` is a list of the values allowed");
                    break;
                case "pattern":
                    try { _ = new Regex(JsonText.Text(value) ?? throw new ArgumentException("not a string"), RegexOptions.CultureInvariant); }
                    catch (ArgumentException e) { Bad(here, $"`pattern` is a regular expression: {e.Message}"); }
                    break;
                case "minLength":
                case "minimum":
                case "maximum":
                    if (JsonText.KindOf(value) is not ("integer" or "number")) Bad(here, $"`{key}` is a number");
                    break;
                case "format":
                    if (JsonText.Text(value) is not { } f || !Formats.Contains(f)) Bad(here, $"`format` is one of {string.Join(", ", Formats)}");
                    break;
                case "$ref":
                    if (JsonText.Text(value) is not { } target || Resolve(file, target) is null) Bad(here, $"`$ref` {value?.ToJsonString()} names nothing in the set (a kind, kind#/$defs/name or #/$defs/name)");
                    break;
                case "oneOf":
                    if (value is not JsonArray { Count: > 0 } branches) { Bad(here, "`oneOf` is a list of schemas"); break; }
                    var index = 0;
                    foreach (var branch in branches) CheckSchema(file, branch, JsonPointer.Append(here, index++), problems);
                    if (Discriminator(file, branches) is null)
                        Bad(here, "`oneOf` is supported on a discriminator only: every branch an object with the same property given as a different `const`");
                    break;
                case "$id":
                case "$schema":
                case "title":
                case "description":
                    if (JsonText.Text(value) is null) Bad(here, $"`{key}` is text");
                    break;
            }
        }
    }

    /// <summary>The property every branch gives as a const, if there is one.</summary>
    string? Discriminator(Schema file, JsonArray branches)
    {
        string? found = null;
        var objects = branches.Select(b => b is JsonObject o ? Follow(file, o) : null).ToList();
        if (objects.Any(o => o is null)) return null;
        var first = objects[0]!.Value.Json["properties"] as JsonObject;
        foreach (var (name, _) in first ?? [])
        {
            var values = objects.Select(o => (o!.Value.Json["properties"] as JsonObject)?[name]?["const"]).ToList();
            if (values.All(v => v is not null) && values.Select(v => v!.ToJsonString()).Distinct().Count() == values.Count)
            {
                found = name;
                break;
            }
        }
        return found;
    }

    // ------------------------------------------------------------------------------------------
    // References

    internal readonly record struct Node(Schema File, JsonObject Json);

    /// <summary>What a <c>$ref</c> names, from the file it is written in.</summary>
    internal Node? Resolve(Schema from, string reference)
    {
        var hash = reference.IndexOf('#');
        var kind = hash < 0 ? reference : reference[..hash];
        var pointer = hash < 0 ? "" : reference[(hash + 1)..];
        var file = kind.Length == 0 ? from : _schemas.GetValueOrDefault(kind);
        if (file is null) return null;
        try { return JsonPointer.Find(file.Root, pointer) is JsonObject target ? new Node(file, target) : null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>A schema with its references followed (a $ref beside other keywords: the target, then the rest).</summary>
    internal Node? Follow(Schema file, JsonObject node)
    {
        for (var hops = 0; hops < 32; hops++)
        {
            if (JsonText.Text(node["$ref"]) is not { } reference) return new Node(file, node);
            if (Resolve(file, reference) is not { } next) return null;
            (file, node) = (next.File, next.Json);
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------
    // Validation

    /// <summary>A place where a value does not fit, before it is given a line.</summary>
    public readonly record struct Misfit(string Pointer, string Rule, string What);

    /// <summary>Checks <paramref name="value"/> against the schema of <paramref name="kind"/>, turning
    /// an empty array into an empty object where the schema says object (and the reverse) in place.
    /// Empty when it fits.</summary>
    public List<Misfit> Validate(JsonNode? value, string kind)
    {
        var schema = Get(kind);
        var misfits = new List<Misfit>();
        Check(new Node(schema, schema.Root), value, null, misfits);
        return misfits;
    }

    /// <summary>Where a value stands while it is checked: its container and its key or index. The
    /// pointer is spelled only for a misfit, and the container is what the one conversion writes to.</summary>
    sealed class Loc(Loc? parent, JsonNode holder, string? key, int index)
    {
        readonly Loc? _parent = parent;
        readonly JsonNode _holder = holder;
        readonly string? _key = key;
        readonly int _index = index;

        public static string Pointer(Loc? loc) => loc is null ? ""
            : loc._key is not null ? JsonPointer.Append(Pointer(loc._parent), loc._key) : JsonPointer.Append(Pointer(loc._parent), loc._index);

        public static void Replace(Loc? loc, JsonNode value)
        {
            if (loc?._holder is JsonObject o) o[loc._key!] = value;
            else if (loc?._holder is JsonArray a) a[loc._index] = value;
        }
    }

    /// <summary>Checks a parsed document and gives each misfit its line and column.</summary>
    public List<JsonProblem> Validate(ParsedJson parsed, string kind) =>
        [.. Validate(parsed.Root, kind).Select(m => parsed.Problem(m.Pointer, m.Rule, m.What))];

    static string Name(string pointer) => pointer.Length == 0 ? "the document" : $"`{pointer}`";

    static string Show(JsonNode? value)
    {
        var text = value is null ? "null" : JsonText.Inline(value, compact: false);
        return text.Length > 60 ? text[..57] + "..." : text;
    }

    /// <summary>A schema node read once: its keywords as fields, its references followed.</summary>
    sealed class Rules
    {
        public required Schema File;
        public required JsonObject Json;
        public string[]? Types;
        public JsonNode? Const;
        public bool HasConst;
        public JsonArray? Enum;
        public int? MinLength;
        public Regex? Pattern;
        public string? Format;
        public string? Minimum, Maximum;
        public JsonObject? Properties;
        public string[] Required = [];
        public JsonObject? Extra;
        public bool ExtraForbidden;
        public JsonObject? Items;
        public JsonArray? OneOf;
        public string? Discriminator;
    }

    readonly ConcurrentDictionary<JsonObject, Rules?> _rules = new(ReferenceEqualityComparer.Instance);

    Rules? RulesOf(Node schema) => _rules.GetOrAdd(schema.Json, _ =>
    {
        if (Follow(schema.File, schema.Json) is not { } f) return null;
        var s = f.Json;
        return new Rules
        {
            File = f.File,
            Json = s,
            Types = s["type"] is JsonArray list ? [.. list.Select(t => JsonText.Text(t)!)] : JsonText.Text(s["type"]) is { } one ? [one] : null,
            Const = s["const"],
            HasConst = s.ContainsKey("const"),
            Enum = s["enum"] as JsonArray,
            MinLength = s["minLength"] is JsonValue min ? min.GetValue<int>() : null,
            Pattern = JsonText.Text(s["pattern"]) is { } p ? _patterns.GetOrAdd(p, x => new Regex(x, RegexOptions.CultureInvariant)) : null,
            Format = JsonText.Text(s["format"]),
            Minimum = s["minimum"] is JsonValue lo ? JsonText.NumberText(lo) : null,
            Maximum = s["maximum"] is JsonValue hi ? JsonText.NumberText(hi) : null,
            Properties = s["properties"] as JsonObject,
            Required = s["required"] is JsonArray req ? [.. req.Select(r => JsonText.Text(r)!)] : [],
            Extra = s["additionalProperties"] as JsonObject,
            ExtraForbidden = s["additionalProperties"] is JsonValue flag && flag.GetValueKind() == JsonValueKind.False,
            Items = s["items"] as JsonObject,
            OneOf = s["oneOf"] as JsonArray,
            Discriminator = s["oneOf"] is JsonArray branches ? Discriminator(f.File, branches) : null,
        };
    });

    void Check(Node schema, JsonNode? value, Loc? loc, List<Misfit> misfits)
    {
        string At() => Loc.Pointer(loc);
        if (RulesOf(schema) is not { } r) { misfits.Add(new(At(), "schema", "the schema's reference is broken")); return; }
        var file = r.File;

        if (r.Types is { } types)
        {
            var kind = JsonText.KindOf(value);
            // the one conversion: Lua writes an empty table as []
            if (value is JsonArray { Count: 0 } && types.Contains("object") && !types.Contains("array"))
            {
                value = new JsonObject();
                Loc.Replace(loc, value);
                kind = "object";
            }
            else if (value is JsonObject { Count: 0 } && types.Contains("array") && !types.Contains("object"))
            {
                value = new JsonArray();
                Loc.Replace(loc, value);
                kind = "array";
            }
            var fits = types.Contains(kind) || (kind == "integer" && types.Contains("number"));
            if (!fits)
            {
                if (kind == "null")
                    misfits.Add(new(At(),"null", $"{Name(At())} is null; absent means not known - leave the field out (expected {string.Join(" or ", types)})"));
                else
                    misfits.Add(new(At(),"type", $"{Name(At())} is {Article(kind)} {Show(value)}; expected {string.Join(" or ", types.Select(t => Article(t) + " " + t))}"));
                return;
            }
        }
        if (r.HasConst && !JsonNode.DeepEquals(r.Const, value))
            misfits.Add(new(At(), "const", $"{Name(At())} is {Show(value)}; it is always {Show(r.Const)}"));
        if (r.Enum is { } allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
            misfits.Add(new(At(), "enum", $"{Name(At())} is {Show(value)}; it is one of {string.Join(", ", allowed.Select(a => JsonText.Text(a) ?? Show(a)))}"));

        if (value is JsonValue scalar)
        {
            var valueKind = scalar.GetValueKind();
            if (valueKind == JsonValueKind.String && (r.MinLength is not null || r.Pattern is not null || r.Format is not null))
            {
                var text = scalar.GetValue<string>();
                if (r.MinLength is { } min && text.Length < min)
                    misfits.Add(new(At(), "minLength", text.Length == 0 ? $"{Name(At())} is empty; leave it out when it is not known" : $"{Name(At())} is shorter than {min} characters"));
                if (r.Pattern is { } pattern && !pattern.IsMatch(text))
                    misfits.Add(new(At(), "pattern", $"{Name(At())} is {Show(value)}; it matches {pattern}"));
                if (r.Format is { } format && FormatProblem(format, text) is { } why)
                    misfits.Add(new(At(), "format", $"{Name(At())} is {Show(value)}; {why}"));
            }
            if (valueKind == JsonValueKind.Number && (r.Minimum is not null || r.Maximum is not null))
            {
                var raw = JsonText.NumberText(scalar);
                if (r.Minimum is { } lo && JsonText.CompareNumbers(raw, lo) < 0)
                    misfits.Add(new(At(), "minimum", $"{Name(At())} is {raw}; at least {lo}"));
                if (r.Maximum is { } hi && JsonText.CompareNumbers(raw, hi) > 0)
                    misfits.Add(new(At(), "maximum", $"{Name(At())} is {raw}; at most {hi}"));
            }
        }

        if (value is JsonObject obj)
        {
            var props = r.Properties;
            foreach (var key in r.Required)
                if (!obj.ContainsKey(key)) misfits.Add(new(At(), "required", $"{Name(At())} has no `{key}`; it is required"));
            // a copy: the one conversion may replace a member while it is walked
            foreach (var (key, child) in obj.ToArray())
            {
                if (props is not null && props.TryGetPropertyValue(key, out var sub) && sub is not null)
                    CheckAny(file, sub, child, new Loc(loc, obj, key, 0), misfits);
                else if (r.Extra is { } extra) Check(new Node(file, extra), child, new Loc(loc, obj, key, 0), misfits);
                else if (r.ExtraForbidden)
                    misfits.Add(new(JsonPointer.Append(At(), key), "additional", $"{Name(At())} has `{key}`, which its schema does not know{Known(props)}"));
            }
        }
        if (value is JsonArray array && r.Items is { } items)
        {
            for (var i = 0; i < array.Count; i++)
                CheckAny(file, items, array[i], new Loc(loc, array, null, i), misfits);
        }
        if (r.OneOf is { } branches && r.Discriminator is { } by)
        {
            var said = (value as JsonObject)?[by];
            var branch = branches.OfType<JsonObject>().Select(b => Follow(file, b)!.Value)
                .FirstOrDefault(b => JsonNode.DeepEquals((b.Json["properties"] as JsonObject)?[by]?["const"], said));
            if (branch.Json is null)
                misfits.Add(new(value is JsonObject ? JsonPointer.Append(At(), by) : At(), "oneOf",
                    $"{Name(JsonPointer.Append(At(), by))} is {Show(said)}; it is one of {string.Join(", ", branches.OfType<JsonObject>().Select(b => Show(Follow(file, b)?.Json["properties"]?[by]?["const"])))}"));
            else
                Check(branch, value, loc, misfits);
        }
    }

    void CheckAny(Schema file, JsonNode schema, JsonNode? value, Loc? loc, List<Misfit> misfits)
    {
        if (schema is JsonObject o) Check(new Node(file, o), value, loc, misfits);
    }

    static string Known(JsonObject? props)
    {
        if (props is null || props.Count == 0) return "";
        var names = props.Select(p => p.Key).ToList();
        return names.Count <= 12 ? $" ({string.Join(", ", names)})" : "";
    }

    static string Article(string kind) => kind is "object" or "array" or "integer" ? "an" : "a";

    /// <summary>Why a string does not have its declared format, or null.</summary>
    public static string? FormatProblem(string format, string text) => format switch
    {
        "uint64" => text.Length is > 0 and <= 20 && text.All(char.IsAsciiDigit) && (text.Length == 1 || text[0] != '0') && ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? null : "expected an unsigned 64-bit integer written as decimal digits (how a 64-bit value travels from Lua)",
        "int64" => text.Length is > 0 and <= 20 && (text == "0" || System.Text.RegularExpressions.Regex.IsMatch(text, "^-?[1-9][0-9]*$")) && long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? null : "expected a signed 64-bit integer written as decimal digits",
        "time" => text.Length == 20 && DateTime.TryParseExact(text, TimeFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out _)
            ? null : $"a time is UTC to the second, {TimeFormat}",
        _ => null,
    };

    // ------------------------------------------------------------------------------------------
    // The order of keys, for the canonical layout

    /// <summary>The schema of a member of an object, where the schema says one.</summary>
    internal Node? Member(Node? schema, JsonObject value, string key)
    {
        if (Branch(schema, value) is not { } s) return null;
        if ((s.Json["properties"] as JsonObject)?[key] is JsonObject sub) return Follow(s.File, sub);
        if (s.Json["additionalProperties"] is JsonObject extra) return Follow(s.File, extra);
        return null;
    }

    /// <summary>The schema of an array's items, where the schema says one.</summary>
    internal Node? Items(Node? schema)
    {
        if (schema is not { } s || Follow(s.File, s.Json) is not { } f) return null;
        return f.Json["items"] is JsonObject items ? Follow(f.File, items) : null;
    }

    /// <summary>The keys of <paramref name="value"/> in the order its schema lists them, then the others as they were.</summary>
    internal List<string> Order(Node? schema, JsonObject value)
    {
        var keys = value.Select(p => p.Key).ToList();
        if (Branch(schema, value) is not { } s || s.Json["properties"] is not JsonObject props) return keys;
        var ordered = props.Select(p => p.Key).Where(value.ContainsKey).ToList();
        ordered.AddRange(keys.Where(k => !props.ContainsKey(k)));
        return ordered;
    }

    Node? Branch(Node? schema, JsonObject value)
    {
        if (schema is not { } start || Follow(start.File, start.Json) is not { } s) return null;
        if (s.Json["oneOf"] is JsonArray branches && Discriminator(s.File, branches) is { } by)
        {
            foreach (var b in branches.OfType<JsonObject>())
                if (Follow(s.File, b) is { } f && JsonNode.DeepEquals((f.Json["properties"] as JsonObject)?[by]?["const"], value[by]))
                    return f;
        }
        return s;
    }

    /// <summary>The root of a kind's schema, for walking alongside a document.</summary>
    internal Node RootOf(string kind)
    {
        var schema = Get(kind);
        return new Node(schema, schema.Root);
    }
}
