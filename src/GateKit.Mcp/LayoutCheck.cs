using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GateKit.Mcp;

/// <summary>
/// A layout against the running game: for every live instance of a component, each field must
/// hold what the raw bytes at its offset say it holds. The port of the "mapped" stage of
/// <c>research/coverage/check.py</c> - the comparison that turns a layout from a reading of the
/// game's code into a fact about the running game, and the only thing that moves a type into
/// <c>verified live</c>.
///
/// It needs far less of <c>generate.py</c> than it looks: the comparison only ever asks a field
/// type for its width, and every type it can compare is either a primitive of known size, an
/// <c>EntityHandle</c>, a container whose length sits at a fixed offset, or a fixed-size array of
/// handles. Anything else is simply not compared - a silent pass is never claimed.
/// </summary>
public static partial class LayoutCheck
{
    /// <summary>Script Extender gives nil for the null handle, which is this and not zero.</summary>
    const ulong NullEntity = 0xFFC0000000000000;

    /// <summary>Width in bytes of the primitive field types the comparison understands.</summary>
    static readonly Dictionary<string, int> Primitives = new(StringComparer.Ordinal)
    {
        ["uint8_t"] = 1, ["int8_t"] = 1, ["uint16_t"] = 2, ["int16_t"] = 2,
        ["uint32_t"] = 4, ["int32_t"] = 4, ["uint64_t"] = 8, ["int64_t"] = 8,
    };

    /// <summary>A layout file as the comparison needs it.</summary>
    public sealed record Layout(string Component, string ExtName, int Size, bool Tag, IReadOnlyList<Field> Fields,
        string File, string Status = "");

    public sealed record Field(string Name, int Offset, string Type);

    /// <summary>
    /// The layouts that are in the Script Extender build and can be compared: status confirmed or
    /// declared, not held back, not a system (those are objects with behaviour, not memory).
    /// </summary>
    public static IReadOnlyList<Layout> Load(string coverageRoot, string prefix = "")
    {
        var folder = Path.Combine(coverageRoot, "layouts");
        if (!Directory.Exists(folder)) return [];
        var layouts = new List<Layout>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            JsonObject? file;
            try { file = JsonNode.Parse(File.ReadAllText(path))?.AsObject(); }
            catch (Exception) { continue; }
            if (file is null) continue;
            if (file["kind"]?.GetValue<string>() == "system") continue;
            if (file["hold"] is not null) continue;
            if (file["status"]?.GetValue<string>() is not ("confirmed" or "declared")) continue;
            if (file["component"]?.GetValue<string>() is not { } component) continue;
            if (file["ext_name"]?.GetValue<string>() is not { } extName) continue;
            if (!component.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var fields = new List<Field>();
            foreach (var entry in file["fields"] as JsonArray ?? [])
            {
                if (entry is not JsonObject field) continue;
                var name = field["name"]?.GetValue<string>();
                var type = field["type"]?.GetValue<string>();
                if (name is null || type is null) continue;
                if (name.StartsWith("_Pad", StringComparison.Ordinal)) continue;   // padding has no value to read
                if (type.Contains("void*", StringComparison.Ordinal)) continue;    // hidden from Lua: no property to compare
                fields.Add(new Field(name, (int)(Integer(field["offset"]) ?? 0), type));
            }
            layouts.Add(new Layout(component, extName, (int)(Integer(file["size"]) ?? 0),
                file["tag"] is not null, fields, Path.GetFileName(path),
                file["status"]?.GetValue<string>() ?? ""));
        }
        return layouts;
    }

    /// <summary>
    /// One component's layout file, <b>whatever its status</b>, with every field it names -
    /// padding and pointers included, which <see cref="Load"/> leaves out because they cannot be
    /// compared against Script Extender.
    ///
    /// This is for reading a hypothesis against the bytes before anybody has declared it, which
    /// is the whole point: a layout that is not confirmed yet is exactly the one somebody is
    /// trying to confirm, and refusing to show it is how that loop ended up being run in hex.
    /// </summary>
    public static Layout? Find(string coverageRoot, string component)
    {
        var folder = Path.Combine(coverageRoot, "layouts");
        if (!Directory.Exists(folder)) return null;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            JsonObject? file;
            try { file = JsonNode.Parse(File.ReadAllText(path))?.AsObject(); }
            catch (Exception) { continue; }
            if (file?["component"]?.GetValue<string>() != component) continue;
            var fields = new List<Field>();
            foreach (var entry in file["fields"] as JsonArray ?? [])
            {
                if (entry is not JsonObject field) continue;
                if (field["name"]?.GetValue<string>() is not { } name) continue;
                if (field["type"]?.GetValue<string>() is not { } type) continue;
                fields.Add(new Field(name, (int)(Integer(field["offset"]) ?? 0), type));
            }
            return new Layout(component, file["ext_name"]?.GetValue<string>() ?? "",
                (int)(Integer(file["size"]) ?? 0), file["tag"] is not null, fields,
                Path.GetFileName(path), file["status"]?.GetValue<string>() ?? "a hypothesis");
        }
        return null;
    }

    /// <summary>What a field should return, read out of the raw bytes; null means "not compared".</summary>
    public static Want? Expected(Field field, byte[] raw)
    {
        var (type, at) = (field.Type, field.Offset);
        if (at < 0 || at >= raw.Length) return null;

        switch (type)
        {
            case "bool": return new Want.Boolean(raw[at] != 0);
            case "float":
                return at + 4 <= raw.Length ? new Want.Number(BitConverter.ToSingle(raw, at)) : null;
            case "EntityHandle":
                return at + 8 <= raw.Length ? new Want.Handle(ReadUnsigned(raw, at, 8)) : null;
            case "UserId":
                return at + 4 <= raw.Length ? new Want.Integer((long)ReadUnsigned(raw, at, 4)) : null;
        }

        // Array<T>: pointer, capacity at +8, size at +0xC. HashSet/HashMap: the size of its key array.
        if (type.StartsWith("Array<", StringComparison.Ordinal))
            return at + 16 <= raw.Length ? new Want.Length((long)ReadUnsigned(raw, at + 12, 4)) : null;
        if (type.StartsWith("HashSet<", StringComparison.Ordinal) || type.StartsWith("HashMap<", StringComparison.Ordinal))
            return at + 48 <= raw.Length ? new Want.Length((long)ReadUnsigned(raw, at + 44, 4)) : null;

        if (HandleArray().Match(type) is { Success: true } array)
        {
            var count = int.Parse(array.Groups[1].Value, CultureInfo.InvariantCulture);
            if (at + 8 * count > raw.Length) return null;
            return new Want.Handles([.. Enumerable.Range(0, count).Select(i => ReadUnsigned(raw, at + 8 * i, 8))]);
        }

        if (Primitives.TryGetValue(type, out var width))
        {
            if (at + width > raw.Length) return null;
            var value = (long)ReadUnsigned(raw, at, width);
            if (type.StartsWith("int", StringComparison.Ordinal) && width < 8 && value >= 1L << (8 * width - 1))
                value -= 1L << (8 * width);
            return new Want.Integer(value);
        }

        return null;   // a type the comparison cannot read is never counted as agreeing
    }

    /// <summary>Whether what Script Extender returned is what the bytes say.</summary>
    public static bool Same(Want want, JsonNode? got) => want switch
    {
        Want.Boolean b => got is JsonValue v && v.TryGetValue<bool>(out var g) && g == b.Value,
        Want.Number n => Real(got) is { } g
            && (g == n.Value || Math.Abs(n.Value - g) <= 1e-6 * Math.Max(1.0, Math.Abs(n.Value))),
        Want.Handle h => SameHandle(h.Value, got),
        Want.Handles many => SameHandles(many, got),
        Want.Length l => got is JsonObject o
            && (Integer(o["Length"]) == l.Value || Integer(o["Count"]) == l.Value),
        // Lua's integers are signed: 0xFFFF...FF comes back as -1, and that is the same number.
        Want.Integer i => Integer(got) is { } g && unchecked((ulong)g) == unchecked((ulong)i.Value),
        _ => false,
    };

    /// <summary>
    /// An integer out of JSON without going through a double on the way. Entity handles run past
    /// 2^53 (72057598332905975 is an ordinary one), so reading them as doubles would quietly
    /// round two different handles into the same number and call a wrong layout right.
    /// </summary>
    public static long? Integer(JsonNode? value)
    {
        if (value is not JsonValue number) return null;
        // A node parsed from JSON holds a JsonElement and answers any of these; one built in
        // memory is strongly typed and answers only its own, so every shape is asked for.
        if (number.TryGetValue<long>(out var exact)) return exact;
        if (number.TryGetValue<ulong>(out var unsigned)) return unchecked((long)unsigned);
        if (number.TryGetValue<int>(out var small)) return small;
        if (number.TryGetValue<uint>(out var smallUnsigned)) return smallUnsigned;
        // Last: a double, and only where it is exactly an integer and inside the range where a
        // double still counts one by one.
        if (number.TryGetValue<double>(out var real) && real == Math.Floor(real) && Math.Abs(real) <= 9007199254740992d)
            return (long)real;
        return null;
    }

    /// <summary>A number out of JSON whatever shape the node happens to hold.</summary>
    public static double? Real(JsonNode? value)
    {
        if (value is not JsonValue number) return null;
        if (number.TryGetValue<double>(out var real)) return real;
        if (number.TryGetValue<float>(out var single)) return single;
        return Integer(value);
    }

    static bool SameHandle(ulong want, JsonNode? got)
    {
        // Script Extender gives nil for the null handle, which the bytes write as 0 or NullEntity.
        if (got is null or JsonValue) return want is 0 or NullEntity;
        return got is JsonObject one && Integer(one["Entity"]) is { } raw && unchecked((ulong)raw) == want;
    }

    static bool SameHandles(Want.Handles want, JsonNode? got)
    {
        var items = (got as JsonObject)?["Items"];
        var list = items switch
        {
            JsonArray array => array.ToList(),
            JsonObject map => [.. Enumerable.Range(1, want.Values.Count).Select(i => map[i.ToString(CultureInfo.InvariantCulture)])],
            _ => new List<JsonNode?>(),
        };
        // Trailing nils are dropped on the way out of Lua: a short list is not a disagreement.
        while (list.Count < want.Values.Count) list.Add(null);
        return want.Values.Select((handle, i) => Same(new Want.Handle(handle), list[i])).All(ok => ok);
    }

    static ulong ReadUnsigned(byte[] raw, int offset, int width)
    {
        Span<byte> eight = stackalloc byte[8];
        raw.AsSpan(offset, width).CopyTo(eight);
        return BinaryPrimitives.ReadUInt64LittleEndian(eight);
    }

    [GeneratedRegex(@"^std::array<EntityHandle,\s*(\d+)>$")]
    private static partial Regex HandleArray();

    /// <summary>What a field should return. Each shape is compared its own way.</summary>
    public abstract record Want
    {
        public sealed record Boolean(bool Value) : Want;
        public sealed record Number(float Value) : Want;
        public sealed record Integer(long Value) : Want;
        public sealed record Handle(ulong Value) : Want;
        public sealed record Handles(IReadOnlyList<ulong> Values) : Want;
        public sealed record Length(long Value) : Want;
    }
}
