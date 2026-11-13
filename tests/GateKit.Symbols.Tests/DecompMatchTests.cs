using System.IO.Hashing;
using System.Text;
using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.Tests;

/// <summary>Features of pseudo-code and the pairing by them, on toy functions.</summary>
public class DecompMatchTests
{
    const ulong Mask = (1UL << 63) - 1, K1 = 0x9E3779B97F4A7C15, K2 = 0xC2B2AE3D27D4EB4F;

    static long Number(ulong v) => (long)(unchecked(v * K1 + 1) & Mask);
    static long Pair(ulong a, ulong b) => (long)(unchecked((a * K1) ^ (b * K2 + 2)) & Mask);
    static long Text(string s) => (long)(XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(s)) & Mask);

    [Fact]
    public void FeaturesKeepConstantsAndTexts()
    {
        const string code = """
            int f(int a[77])
            {
              /* 0x999 */
              x = y + 0x10;
              z = 1;
              w = 0xffffffff;
              s = "abcd";
              t = "abc";
              q = 0x140001000;
              v = local_10 + 3.5;
              return 0x20;
            }
            """;
        // 77 is in the declaration and 0x999 in a comment; 1 is too common; 0xffffffff is -1 printed
        // as unsigned (1 after folding); "abc" is too short; 0x140001000 is an address of the image;
        // local_10 is a name and 3.5 not an integer
        long[] expected = [Number(16), Number(1), Pair(16, 1), Text("abcd"), Number(32), Pair(1, 32)];
        Array.Sort(expected);
        Assert.Equal(expected, CodeMatch.Features(code));
    }

    [Fact]
    public void FeaturesFoldNegativeNumbersPrintedUnsigned()
    {
        Assert.Equal(CodeMatch.Features("{ a = 8; }"), CodeMatch.Features("{ a = 0xfffffffffffffff8; }"));
        Assert.Equal(CodeMatch.Features("{ a = 8; }"), CodeMatch.Features("{ a = 0xfffffff8; }"));
    }

    [Fact]
    public void PairsAreMutualBestWithAClearLead()
    {
        var f = new Dictionary<long, long[]>
        {
            [1] = [1, 2, 3, 4, 5],
            [2] = [11, 12, 13, 14, 15],
            [3] = [21, 22, 23, 24],
            [10] = [1, 2, 3, 4, 9],
            [11] = [11, 12, 13, 14, 19],
            [12] = [31, 32, 33, 34],
            [13] = [21, 22, 23, 24, 29],
            [14] = [21, 22, 23, 24, 28],
        };
        var pairs = CodeMatch.Match(f, [1, 2, 3], [10, 11, 12, 13, 14], 0.10, 0.05, 0.0);
        // function 3 fits 13 and 14 equally well: no lead, no pair
        Assert.Equal([(1L, 10L), (2L, 11L)], pairs.Select(p => (p.Toolkit, p.Game)).ToList());
        Assert.All(pairs, p => Assert.InRange(p.Score, 0.5, 1.0));
        Assert.All(pairs, p => Assert.Equal(p.Score, p.Margin));
    }

    [Fact]
    public void UnqualifiedDropsTemplatesAndParameters() =>
        Assert.Equal("ns::A::f", CodeMatch.Unqualified("void __cdecl ns::A<int, B<C>>::f(int)"));
}
