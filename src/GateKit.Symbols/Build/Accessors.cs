using System.Text.RegularExpressions;

namespace GateKit.Symbols.Build;

/// <summary>
/// Class fields from trivial accessors. A method the Toolkit exports as GetX, IsX, HasX or SetX is
/// very often nothing but one instruction that touches [this + offset] and a return. Decoding that
/// instruction gives the offset and width of the field the method is named after. No disassembler
/// is needed for the few instruction forms compilers use here:
/// <code>
/// 48 8B 41 38             mov   rax, [rcx+38h]      get, 8 bytes
/// 0F B6 81 C5 00 00 00    movzx eax, byte [rcx+C5h] get, 1 byte
/// 8B 81 A8 00 00 00 / D1 E8 / 24 01   mov eax, [rcx+A8h]; shr eax, 1; and al, 1   get, bit 1 of a flags field
/// 48 8D 41 38             lea   rax, [rcx+38h]      ref (returns the address of the field)
/// 88 91 C5 00 00 00       mov   [rcx+C5h], dl       set, 1 byte
/// </code>
/// </summary>
public static class Accessors
{
    static readonly Regex Name = new(@"^(Get|Is|Has|Can|Should|Was|Set)([A-Z0-9]\w*)$", RegexOptions.Compiled);
    static readonly Regex AccessPrefix = new(@"^(public|protected|private): ", RegexOptions.Compiled);
    static readonly Regex StoragePrefix = new(@"^(virtual|static) ", RegexOptions.Compiled);

    /// <summary>('get' | 'set', field name) for an accessor-like method name, else null.</summary>
    public static (string Kind, string Field)? FieldName(string method)
    {
        var m = Name.Match(method);
        if (!m.Success)
            return null;
        string prefix = m.Groups[1].Value, rest = m.Groups[2].Value;
        if (prefix is "Get" or "Set")
            return (prefix.ToLowerInvariant(), rest);
        return ("get", prefix + rest);
    }

    /// <summary>Return type out of an undecorated declaration.</summary>
    public static string? ReturnType(string signature)
    {
        var head = signature.Split('(', 2)[0];
        head = AccessPrefix.Replace(head, "", 1);
        head = StoragePrefix.Replace(head, "", 1);
        int i = head.LastIndexOf(' ');
        if (i <= 0)
            return null;
        var t = head[..i].Trim();
        return t.Length > 0 ? t : null;
    }

    static (int Reg, int Disp, int Next)? ModRm(ReadOnlySpan<byte> code, int i)
    {
        if (i >= code.Length)
            return null;
        int modrm = code[i], mod = modrm >> 6, reg = (modrm >> 3) & 7, rm = modrm & 7;
        if (rm != 1 || mod == 3)
            return null;
        if (mod == 0)
            return (reg, 0, i + 1);
        if (mod == 1 && i + 1 < code.Length)
            return (reg, (sbyte)code[i + 1], i + 2);
        if (mod == 2 && i + 5 <= code.Length)
            return (reg, BitConverter.ToInt32(code.Slice(i + 1, 4)), i + 5);
        return null;
    }

    /// <summary>(kind, offset, width, bit) when the function is one access to [rcx + offset] followed by ret.</summary>
    public static (string Kind, int Offset, int? Width, int? Bit)? Decode(ReadOnlySpan<byte> code)
    {
        int i = 0, rex = 0, prefix = 0;
        bool size16 = false;
        while (i < code.Length && code[i] is 0x66 or 0xF2 or 0xF3)
        {
            if (code[i] == 0x66) size16 = true; else prefix = code[i];
            i++;
        }
        if (i < code.Length && code[i] >= 0x40 && code[i] <= 0x4F)
            rex = code[i++];
        if ((rex & 1) != 0)                 // base register is r9, not rcx
            return null;
        if (i >= code.Length)
            return null;
        int op = code[i];
        int wide = (rex & 8) != 0 ? 8 : size16 ? 2 : 4;
        string kind;
        int? width;
        int at;
        switch (op)
        {
            case 0x8B: kind = "get"; width = wide; at = i + 1; break;
            case 0x8A: kind = "get"; width = 1; at = i + 1; break;
            case 0x8D: kind = "ref"; width = null; at = i + 1; break;
            case 0x89: kind = "set"; width = wide; at = i + 1; break;
            case 0x88: kind = "set"; width = 1; at = i + 1; break;
            case 0x0F when i + 1 < code.Length:
            {
                int op2 = code[i + 1];
                at = i + 2;
                if (op2 is 0xB6 or 0xBE) { kind = "get"; width = 1; }
                else if (op2 is 0xB7 or 0xBF) { kind = "get"; width = 2; }
                else if (op2 == 0x10) { kind = "get"; width = prefix == 0xF3 ? 4 : prefix == 0xF2 ? 8 : 16; }
                else if (op2 == 0x11) { kind = "set"; width = prefix == 0xF3 ? 4 : prefix == 0xF2 ? 8 : 16; }
                else if (op2 is 0x28 or 0x6F) { kind = "get"; width = 16; }
                else return null;
                break;
            }
            default: return null;
        }
        var m = ModRm(code, at);
        if (m == null)
            return null;
        var (reg, disp, next) = m.Value;
        if (disp < 0)
            return null;
        var rest = code[next..];
        int? bit = null;
        if (kind == "get" && reg == 0)
        {
            // a flag kept as one bit: [shr eax, n] and al, 1
            int shift = 0;
            if (rest.Length >= 2 && rest[0] == 0xD1 && rest[1] == 0xE8) { shift = 1; rest = rest[2..]; }
            else if (rest.Length > 2 && rest[0] == 0xC1 && rest[1] == 0xE8) { shift = rest[2]; rest = rest[3..]; }
            if (rest.Length >= 2 && rest[0] == 0x24 && rest[1] == 0x01) { bit = shift; rest = rest[2..]; }
            else if (shift != 0) return null;
        }
        if (rest.Length == 0 || rest[0] != 0xC3)      // the access must be followed directly by ret
            return null;
        if (kind is "get" or "ref" && op != 0x0F && reg != 0)   // value must land in rax/eax/al
            return null;
        return (kind, disp, width, bit);
    }
}
