using System.Globalization;
using System.Text.RegularExpressions;

namespace GateKit.Symbols.Decomp;

/// <summary>A value `index * N + base` whose base is the page of a column, read through a stack
/// local that holds the iterator's column pointers.</summary>
/// <param name="Value">The value.</param>
/// <param name="Local">The stack offset of the local the base is read through (the number in its name).</param>
/// <param name="Distance">How far below that local the index local stands on the stack: what tells
/// which part of the iterator's state the pointer is. Null when the index is not a stack local.</param>
/// <param name="Form">The base with the local written `S` and its index, if any, `k`.</param>
/// <param name="Column">The index the base applies to the local (0 when it only dereferences it).</param>
internal sealed record ColumnValue(StrideValue Value, long Local, long? Distance, string Form, int Column);

/// <summary>
/// Reads which column of an iterator a value is taken from, from the text of one function. The
/// iterator's state is the function's own stack, and the decompiler names a stack local by its
/// offset: so the local that holds the column pointers and the local that holds the entry index
/// stand a fixed distance apart whatever function they are in, and that distance says which part
/// of the state the pointer is (<see cref="Groups"/>).
/// </summary>
internal static partial class QueryColumns
{
    /// <summary>Distance between the column pointers and the entry index on the stack -> the group
    /// of the query's description whose components those columns are, in the registration's order.
    /// The state keeps the pointers of the components every entity has (group 0) 0x50 below the
    /// entry index and those of the optional ones (group 7) 0x40 below it; the counts behind this
    /// are `gatekit decomp queries columns`.</summary>
    public static readonly IReadOnlyDictionary<long, int> Groups = new Dictionary<long, int> { [0x50] = 0, [0x40] = 7 };

    /// <summary>The forms of a base that read the page of column k through the local S.</summary>
    public static bool IsColumnForm(string form) => form is "*(longlong *)*S" or "*(longlong *)S[k]";

    [GeneratedRegex(@"\b(?:local|[a-z]{1,3}Stack)_(?<offset>[0-9a-f]+)\b")]
    private static partial Regex StackLocal();

    [GeneratedRegex(@"S\[(?<k>0x[0-9a-fA-F]+|\d+)\]")]
    private static partial Regex Indexed();

    static long OffsetOf(Match local) => long.Parse(local.Groups["offset"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>The values of a function whose base and index are each read through one stack local.</summary>
    public static List<ColumnValue> Read(string code) => Read(QueryShapes.Strides(code));

    public static List<ColumnValue> Read(IEnumerable<StrideValue> strides)
    {
        var list = new List<ColumnValue>();
        foreach (var v in strides)
        {
            if (v.Offset || v.Stride <= 0 || (v.Stride == QueryShapes.DescriptionSize && QueryShapes.IsGlobal(v.Index)))
                continue;
            var locals = StackLocal().Matches(v.Base);
            var indices = StackLocal().Matches(v.Index);
            if (locals.Count != 1 || indices.Count > 1)
                continue;
            long at = OffsetOf(locals[0]);
            string form = v.Base[..locals[0].Index] + "S" + v.Base[(locals[0].Index + locals[0].Length)..];
            int column = 0;
            var indexed = Indexed().Match(form);
            if (indexed.Success)
            {
                string k = indexed.Groups["k"].Value;
                column = k.StartsWith("0x", StringComparison.Ordinal) ? int.Parse(k.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(k, CultureInfo.InvariantCulture);
                form = form[..indexed.Index] + "S[k]" + form[(indexed.Index + indexed.Length)..];
            }
            // an index the function keeps in a register says nothing of where the pointer stands
            list.Add(new ColumnValue(v, at, indices.Count == 1 ? at - OffsetOf(indices[0]) : null, form, column));
        }
        return list;
    }
}
