using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace GateKit.Mcp;

/// <summary>
/// What every part of a component's memory holds, across the live instances of it - the
/// starting point for a layout hypothesis. A port of the survey and the printing of
/// <c>research/coverage/probe.py</c>; the reading itself is done in the game by
/// <c>GK.Probe.Dump</c>.
///
/// Per 8-byte slot it says what the values look like (a live entity handle, a pointer into the
/// executable, a heap pointer, a plain value); per 4-byte half, how many distinct values there
/// are, their range, whether every one reads as an ordinary float, and the bytes themselves.
/// A part that is the same in every instance is a constant or a default; a part that differs
/// with the situation is one a run can force a value into and prove.
/// </summary>
public static class Survey
{
    /// <summary>
    /// The binary an offset into the executable belongs to. The stand starts
    /// <c>bin/bg3_dx11.exe</c>, and an address read from the running game is an offset into
    /// <b>that</b> one - not into <c>bg3.exe</c>, which the symbol database also holds. The two
    /// are different builds with different addresses (the sound session measured one global at
    /// <c>bg3.exe+0x62D49F0</c> and <c>bg3_dx11.exe+0x604B1B8</c>), so naming an offset against
    /// the wrong binary gives a plausible name for the wrong function, which is the worst kind
    /// of wrong.
    /// </summary>
    const string RunningBinary = "bg3_dx11.exe";

    public static string Render(JsonNode? dumped, string component, bool raw)
    {
        var instances = Instances(dumped);
        var total = (int)(LayoutCheck.Integer(dumped?["TotalSize"]) ?? 0);
        var inline = (int)(LayoutCheck.Integer(dumped?["InlineSize"]) ?? 0);
        if (instances.Count == 0)
            return $"{component}: nothing alive carries it. It may need a situation that creates it - "
                + "bg3_coverage_todo tells you where it has been seen before, if anywhere.";

        var lines = new StringBuilder()
            .Append(component).Append(": ").Append(instances.Count).Append(" instances, ")
            .Append(total).Append(" bytes (inline ").Append(inline).Append(")\n");

        var size = total;
        foreach (var instance in instances) size = Math.Min(size, instance.Bytes.Length);

        for (var offset = 0; offset < size; offset += 4)
        {
            lines.Append("  +0x").Append(offset.ToString("X3", CultureInfo.InvariantCulture)).Append("  ");

            if (offset % 8 == 0 && offset + 8 <= size)
            {
                var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var rvas = new SortedSet<long>();
                foreach (var instance in instances)
                {
                    if (instance.Slots.ElementAtOrDefault(offset / 8) is not JsonObject slot) continue;
                    var kind = slot["Kind"]?.GetValue<string>() ?? "?";
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                    if (kind == "module" && LayoutCheck.Integer(slot["Rva"]) is { } rva) rvas.Add(rva);
                }
                lines.Append("qword ").Append(string.Join(",", kinds.Select(p => $"{p.Key}x{p.Value}")));
                foreach (var rva in rvas.Take(2))
                    lines.Append(" [+0x").Append(rva.ToString("X", CultureInfo.InvariantCulture))
                         .Append(": `gatekit symbols signature ").Append(RunningBinary).Append("+0x")
                         .Append(rva.ToString("X", CultureInfo.InvariantCulture)).Append("`]");
                lines.Append("  | ");
            }
            else
            {
                lines.Append("    | ");
            }

            var width = Math.Min(4, size - offset);
            var raws = instances.Select(i => Read(i.Bytes, offset, width)).ToList();
            var distinct = raws.Distinct().Order().ToList();
            lines.Append(distinct.Count == 1
                ? $"always 0x{distinct[0]:X}"
                : $"{distinct.Count} values 0x{raws.Min():X}..0x{raws.Max():X}");

            if (width == 4 && raws.Any(r => r != 0) && raws.All(r => PlausibleFloat((uint)r)))
            {
                var floats = distinct.Take(4).Select(r => BitConverter.UInt32BitsToSingle((uint)r));
                lines.Append("  float ").Append(string.Join(" ", floats.Select(f => f.ToString("G4", CultureInfo.InvariantCulture))));
            }

            lines.Append("  bytes ");
            for (var b = 0; b < width; b++)
            {
                var at = offset + b;
                var seen = instances.Select(i => i.Bytes[at]).Distinct().Order().Take(4);
                lines.Append(string.Join("/", seen.Select(v => v.ToString("x2", CultureInfo.InvariantCulture)))).Append(' ');
            }
            lines.Append('\n');
        }

        if (raw)
        {
            lines.Append("\nraw:\n");
            foreach (var instance in instances)
                lines.Append("  entity ").Append(instance.Entity).Append(": ").Append(instance.Hex).Append('\n');
        }

        lines.Append("\nA part that is the same in every instance is a constant or a default; a part that differs is one you can "
            + "force a value into and prove. One reading is not a finding - repeat the situation, or force the value, or read the "
            + "code, and say in bg3_recipe which of those you did.\n");
        return lines.ToString();
    }

    /// <summary>
    /// The raw bytes of each instance beside what Script Extender returned for the same
    /// instance's named fields, read in the same call. The port of
    /// <c>research/coverage/author_check.py</c>: the reader sees for themselves whether a
    /// field's value is where the layout claims it is.
    /// </summary>
    public static string RenderFields(JsonNode? rows)
    {
        var text = new StringBuilder();
        var any = false;
        foreach (var row in Each(rows))
        {
            if (row is not JsonObject entry) continue;
            any = true;
            text.Append("entity ").Append(entry["Entity"])
                .Append("  present through Script Extender: ").Append(entry["Present"]).Append('\n');
            var hex = entry["Hex"]?.GetValue<string>() ?? "";
            var bytes = Bytes(hex);
            for (var offset = 0; offset < bytes.Length; offset += 16)
            {
                var line = bytes.Skip(offset).Take(16).Select(b => b.ToString("x2", CultureInfo.InvariantCulture));
                text.Append("   +0x").Append(offset.ToString("X3", CultureInfo.InvariantCulture)).Append("  ")
                    .Append(string.Join(" ", line)).Append('\n');
            }
            // An empty Lua table arrives as [], not {}: a component with no fields read is not an error.
            if (entry["Values"] is JsonObject values)
            {
                foreach (var (field, value) in values)
                    text.Append("   ").Append(field).Append(" = ").Append(value?.ToJsonString() ?? "null").Append('\n');
            }
        }
        if (!any) return "nothing alive carries it, so there was nothing to compare.";
        text.Append("\nIf a field's value is not at the offset the layout puts it at, the layout is wrong - record that rather "
            + "than working around it. An unresolved field stays unresolved: a confident wrong name is worse than a missing one.\n");
        return text.ToString();
    }

    /// <summary>
    /// What moved between two readings of the same component, paired by entity. A field that
    /// changed when one thing was done and stayed put when another was is the strongest evidence
    /// this toolkit can produce without reading the game's code; a field that never moves
    /// whatever is done belongs in the record as unresolved, not guessed at.
    ///
    /// Entities that appeared or went away between the readings are reported too - for a
    /// component that lives one frame, that IS the event, and reporting it as "nothing changed"
    /// would be a lie by omission.
    /// </summary>
    public static string RenderMoved(JsonNode? before, JsonNode? after, string component)
    {
        // The same entity can come back twice from one walk of the storage classes, so the first
        // reading of each wins rather than the pairing blowing up on a duplicate key.
        var was = Instances(before).GroupBy(i => i.Entity).ToDictionary(g => g.Key, g => g.First());
        var now = Instances(after).GroupBy(i => i.Entity).ToDictionary(g => g.Key, g => g.First());
        var text = new StringBuilder()
            .Append(component).Append(": ").Append(was.Count).Append(" instances before, ").Append(now.Count).Append(" after\n");

        var appeared = now.Keys.Except(was.Keys).ToList();
        var gone = was.Keys.Except(now.Keys).ToList();
        if (appeared.Count > 0) text.Append("  appeared: ").Append(string.Join(", ", appeared)).Append('\n');
        if (gone.Count > 0) text.Append("  went away: ").Append(string.Join(", ", gone)).Append('\n');

        var moved = 0;
        foreach (var entity in was.Keys.Intersect(now.Keys).Order())
        {
            var (first, second) = (was[entity].Bytes, now[entity].Bytes);
            var size = Math.Min(first.Length, second.Length);
            var changed = new List<string>();
            for (var offset = 0; offset + 4 <= size; offset += 4)
            {
                var a = BitConverter.ToUInt32(first, offset);
                var b = BitConverter.ToUInt32(second, offset);
                if (a == b) continue;
                var line = $"    +0x{offset:X3}  0x{a:X8} -> 0x{b:X8}";
                if (PlausibleFloat(a) || PlausibleFloat(b))
                    line += $"   float {BitConverter.UInt32BitsToSingle(a).ToString("G6", CultureInfo.InvariantCulture)}"
                        + $" -> {BitConverter.UInt32BitsToSingle(b).ToString("G6", CultureInfo.InvariantCulture)}";
                changed.Add(line);
            }
            if (changed.Count == 0) continue;
            moved++;
            text.Append("  entity ").Append(entity).Append(":\n").Append(string.Join("\n", changed)).Append('\n');
        }

        if (moved == 0 && appeared.Count == 0 && gone.Count == 0)
        {
            text.Append("  nothing moved.\n\nThat is a result, not a failure - but check what it is a result OF before you "
                + "record it. If the world was paused through the moment, nothing had time to move; pass unpause. If the thing "
                + "you did does not touch this component, the answer is about your action, not the component.\n");
        }
        else
        {
            text.Append("\nA field that moves when you change one thing and stays put when you change another is evidence. "
                + "One reading of one entity is not: do it again, and on another entity, before you record it as a finding.\n");
        }
        return text.ToString();
    }

    /// <summary>A Lua array arrives as a list; an empty Lua table arrives as an object, and a
    /// table of rows may arrive as either. Walking it must never depend on which.</summary>
    static IEnumerable<JsonNode?> Each(JsonNode? value) => value switch
    {
        JsonArray array => array,
        JsonObject one => one.Select(pair => pair.Value),
        _ => [],
    };

    sealed record Instance(long Entity, string Hex, byte[] Bytes, JsonArray Slots);

    static List<Instance> Instances(JsonNode? dumped)
    {
        var found = new List<Instance>();
        foreach (var item in Each(dumped?["Instances"]))
        {
            if (item is not JsonObject instance) continue;
            var hex = instance["Hex"]?.GetValue<string>() ?? "";
            // Never through a double: handles run past 2^53, and two different entities rounded
            // together would be paired as one when what moved between two readings is counted.
            found.Add(new Instance(LayoutCheck.Integer(instance["Entity"]) ?? 0, hex, Bytes(hex),
                instance["Slots"] as JsonArray ?? []));
        }
        return found;
    }

    static byte[] Bytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    static long Read(byte[] bytes, int offset, int width)
    {
        Span<byte> eight = stackalloc byte[8];
        bytes.AsSpan(offset, width).CopyTo(eight);
        return BinaryPrimitives.ReadInt64LittleEndian(eight);
    }

    /// <summary>
    /// A 32-bit pattern that reads as an ordinary float: zero of either sign, a magnitude
    /// between 1e-10 and 1e7, +-FLT_MAX (the engine's "not set" for positions), infinity, or
    /// the canonical quiet NaN (a default: ls::SoundVirtualPositionComponent starts with it).
    /// Small integers read as floats come out at 1e-38 and below, so they still fail.
    /// </summary>
    static bool PlausibleFloat(uint raw)
    {
        if ((raw & 0x7FFFFFFF) is 0 or 0x7F7FFFFF or 0x7F800000 or 0x7FC00000) return true;
        var value = Math.Abs(BitConverter.UInt32BitsToSingle(raw));
        return !float.IsNaN(value) && value >= 1e-10f && value <= 1e7f;
    }
}
