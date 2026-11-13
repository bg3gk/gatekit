using System.Buffers.Binary;
using System.Text;

namespace GateKit.Symbols.Tests;

/// <summary>
/// Writes a tiny x64 PE image for the tests: three sections (.text, .rdata, .pdata), an optional
/// export table, one import (kernel32.dll!Sleep), a text constant, a virtual table and an exception
/// table with a chained (split) function. Everything is made up; nothing comes from a real binary.
/// <para>Code (rva = 0x1000 + shift):
/// A +0x00: lea rax,[rip+Text]; call B; call [Sleep]; ret          (listed)
/// B +0x20: mov rax,[rcx+38h]; ret                                  (listed; a getter)
/// L +0x30: mov eax,1; ret                                          (leaf: not listed)
/// C +0x40: ret                                                     (listed; a later part of A)
/// D +0x50: lea rcx,[rip+Vtable]; ret                               (listed; takes the table's address)</para>
/// </summary>
public static class SyntheticPe
{
    public const uint Text = 0x2000, Vtable = 0x2020, Iat = 0x2350;
    public const string TextConstant = "HelloWorldText";
    public static readonly string[] ExportNames =
    [
        "?Run@Foo@ns@@QEAAXXZ",          // A: public: void ns::Foo::Run(void)
        "?GetValue@Foo@ns@@QEBA_JXZ",    // B: public: __int64 ns::Foo::GetValue(void)const
        "??0Foo@ns@@QEAA@XZ",            // D: public: ns::Foo::Foo(void)
        "??_7Foo@ns@@6B@",               // the virtual table
        "plain_c_function",              // L
    ];

    public static uint A(uint shift) => 0x1000 + shift;
    public static uint B(uint shift) => 0x1020 + shift;
    public static uint L(uint shift) => 0x1030 + shift;
    public static uint C(uint shift) => 0x1040 + shift;
    public static uint D(uint shift) => 0x1050 + shift;

    public static byte[] Build(ulong imageBase, uint shift, bool exports)
    {
        const int textRaw = 0x400, rdataRaw = 0x1400, pdataRaw = 0x2400, size = 0x2600;
        var f = new byte[size];
        void U16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(o), (ushort)v);
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(o), v);
        void U64(int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(o), v);
        void Bytes(int o, params byte[] b) => b.CopyTo(f, o);
        void Str(int o, string s) => Encoding.ASCII.GetBytes(s + "\0").CopyTo(f, o);
        int R(uint rva) => rdataRaw + (int)(rva - 0x2000);
        int T(uint rva) => textRaw + (int)(rva - 0x1000);

        // headers
        f[0] = (byte)'M'; f[1] = (byte)'Z';
        U32(0x3C, 0x80);
        Bytes(0x80, (byte)'P', (byte)'E', 0, 0);
        int coff = 0x84;
        U16(coff, 0x8664); U16(coff + 2, 3); U32(coff + 4, 0x5F000000); U16(coff + 16, 0xF0); U16(coff + 18, exports ? 0x2022 : 0x22);
        int opt = coff + 20;
        U16(opt, 0x20B);
        U64(opt + 24, imageBase);
        U32(opt + 32, 0x1000); U32(opt + 36, 0x200); U32(opt + 56, 0x4000); U32(opt + 60, 0x400);
        U32(opt + 108, 16);
        int dirs = opt + 112;
        if (exports) { U32(dirs + 0, 0x2100); U32(dirs + 4, 0x200); }
        U32(dirs + 8, 0x2300); U32(dirs + 12, 40);           // imports
        U32(dirs + 24, 0x3000); U32(dirs + 28, 4 * 12);      // exception table
        int sect = opt + 0xF0;
        void Section(int i, string name, uint va, uint vsize, int raw, uint rsize, uint flags)
        {
            int s = sect + 40 * i;
            Encoding.ASCII.GetBytes(name).CopyTo(f, s);
            U32(s + 8, vsize); U32(s + 12, va); U32(s + 16, rsize); U32(s + 20, (uint)raw); U32(s + 36, flags);
        }
        Section(0, ".text", 0x1000, 0x1000, textRaw, 0x1000, 0x60000020);
        Section(1, ".rdata", 0x2000, 0x1000, rdataRaw, 0x1000, 0x40000040);
        Section(2, ".pdata", 0x3000, 0x200, pdataRaw, 0x200, 0x40000040);

        // code: int3 everywhere, then the functions
        f.AsSpan(textRaw, 0x1000).Fill(0xCC);
        int Rel(uint from, uint to) => (int)(to - from);
        uint a = A(shift);
        Bytes(T(a), 0x48, 0x8D, 0x05); BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(T(a) + 3), Rel(a + 7, Text));
        Bytes(T(a) + 7, 0xE8); BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(T(a) + 8), Rel(a + 12, B(shift)));
        Bytes(T(a) + 12, 0xFF, 0x15); BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(T(a) + 14), Rel(a + 18, Iat));
        Bytes(T(a) + 18, 0xC3);
        Bytes(T(B(shift)), 0x48, 0x8B, 0x41, 0x38, 0xC3);
        Bytes(T(L(shift)), 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3);
        Bytes(T(C(shift)), 0xC3);
        uint d = D(shift);
        Bytes(T(d), 0x48, 0x8D, 0x0D); BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(T(d) + 3), Rel(d + 7, Vtable));
        Bytes(T(d) + 7, 0xC3);

        // read-only data
        Str(R(Text), TextConstant);
        U64(R(Vtable), imageBase + a);
        U64(R(Vtable) + 8, imageBase + B(shift));
        if (exports)
        {
            var at = new uint[] { a, B(shift), d, Vtable, L(shift) };
            int n = ExportNames.Length;
            int e = R(0x2100);
            U32(e + 12, 0x2180); U32(e + 16, 1); U32(e + 20, (uint)n); U32(e + 24, (uint)n);
            U32(e + 28, 0x2140); U32(e + 32, 0x2160); U32(e + 36, 0x2178);
            Str(R(0x2180), "test.dll");
            uint name = 0x21A0;
            for (int i = 0; i < n; i++)
            {
                U32(R(0x2140) + 4 * i, at[i]);
                U32(R(0x2160) + 4 * i, name);
                U16(R(0x2178) + 2 * i, i);
                Str(R(name), ExportNames[i]);
                name += (uint)ExportNames[i].Length + 1;
            }
        }
        // imports: kernel32.dll!Sleep
        U32(R(0x2300), 0x2340); U32(R(0x2300) + 12, 0x2360); U32(R(0x2300) + 16, Iat);
        U64(R(0x2340), 0x2370); U64(R(Iat), 0x2370);
        Str(R(0x2360), "kernel32.dll");
        Str(R(0x2370) + 2, "Sleep");
        // unwind information: plain for A, B, D; chained to A for C
        Bytes(R(0x2400), 0x01, 0, 0, 0);
        Bytes(R(0x2410), 0x21, 0, 0, 0);
        U32(R(0x2414), a); U32(R(0x2418), a + 19); U32(R(0x241C), 0x2400);
        // exception table
        void Entry(int i, uint begin, uint end, uint unwind)
        {
            U32(pdataRaw + 12 * i, begin); U32(pdataRaw + 12 * i + 4, end); U32(pdataRaw + 12 * i + 8, unwind);
        }
        Entry(0, a, a + 19, 0x2400);
        Entry(1, B(shift), B(shift) + 5, 0x2400);
        Entry(2, C(shift), C(shift) + 1, 0x2410);
        Entry(3, d, d + 8, 0x2400);
        return f;
    }
}
