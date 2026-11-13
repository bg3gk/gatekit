using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace GateKit.Symbols.Demangling;

/// <summary>
/// One undecoration run. The parser reads the decorated name into small type nodes and renders them the way
/// dbghelp's undname engine does: a type is printed around a "declarator" (the name or the inner part of a
/// pointer, array or function type), so <c>int (* __ptr64 x)[2]</c> and <c>void (__cdecl*)(int)</c> come out
/// in the same order as dbghelp prints them.
/// </summary>
internal sealed class Undecorator
{
    // Nesting deeper than this (or deeper than the thread's stack allows) is treated as a failure.
    private const int MaxDepth = 2000;

    private const int CvConst = 1;
    private const int CvVolatile = 2;

    private const int ModPtr64 = 1;      // E
    private const int ModUnaligned = 2;  // F
    private const int ModRestrict = 4;   // I

    private readonly string _s;
    private readonly bool _noMs;
    private readonly bool _nameOnly;
    private int _p;
    private int _depth;

    // Back-reference tables: names ('0'..'9' in a qualified name) and argument types ('0'..'9' in a list).
    private string[] _names = new string[10];
    private int _nameCount;
    private Node[] _args = new Node[10];
    private int _argCount;

    public Undecorator(string mangled, UndecorateFlags flags)
    {
        _s = mangled;
        _noMs = (flags & UndecorateFlags.NoMsKeywords) != 0;
        _nameOnly = (flags & UndecorateFlags.NameOnly) != 0;
    }

    public string Run()
    {
        try
        {
            return ParseSymbol(top: true);
        }
        catch (FormatException)
        {
            return _s;
        }
    }

    // ---------------------------------------------------------------- input helpers

    private char Peek(int offset = 0)
    {
        int i = _p + offset;
        return i < _s.Length ? _s[i] : '\0';
    }

    private char Next()
    {
        if (_p >= _s.Length)
        {
            throw Fail();
        }
        return _s[_p++];
    }

    private void Expect(char c)
    {
        if (Next() != c)
        {
            throw Fail();
        }
    }

    private bool TryConsume(char c)
    {
        if (Peek() == c && _p < _s.Length)
        {
            _p++;
            return true;
        }
        return false;
    }

    private bool TryConsume(string text)
    {
        if (string.CompareOrdinal(_s, _p, text, 0, text.Length) == 0)
        {
            _p += text.Length;
            return true;
        }
        return false;
    }

    private static FormatException Fail() => new();

    private void Enter()
    {
        if (++_depth > MaxDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw Fail();
        }
    }

    private void Leave() => _depth--;

    // ---------------------------------------------------------------- numbers and identifiers

    /// <summary>Encoded number: optional '?' for negative, then '0'..'9' (1..10) or hex digits 'A'..'P' ending with '@'.</summary>
    private (bool Negative, ulong Value) ParseNumber()
    {
        bool negative = TryConsume('?');
        char c = Next();
        if (c >= '0' && c <= '9')
        {
            return (negative, (ulong)(c - '0' + 1));
        }
        ulong value = 0;
        while (c != '@')
        {
            if (c < 'A' || c > 'P')
            {
                throw Fail();
            }
            value = (value << 4) | (uint)(c - 'A');
            c = Next();
        }
        return (negative, value);
    }

    private string ParseSignedText()
    {
        var (negative, value) = ParseNumber();
        string text = value.ToString(CultureInfo.InvariantCulture);
        return negative ? "-" + text : text;
    }

    private string ParseIdentifier()
    {
        int end = _s.IndexOf('@', _p);
        if (end < 0 || end == _p)
        {
            throw Fail();
        }
        string id = _s.Substring(_p, end - _p);
        _p = end + 1;
        return id;
    }

    private void Memorize(string name)
    {
        if (_nameCount < _names.Length)
        {
            _names[_nameCount++] = name;
        }
    }

    // ---------------------------------------------------------------- symbols

    private enum NameKind
    {
        Plain,
        Constructor,
        Destructor,
        Conversion,
        Vcall,
        LocalStaticGuard,
    }

    /// <summary>Parses a whole decorated symbol starting at '?' and returns its text for the current flags.</summary>
    private string ParseSymbol(bool top)
    {
        Enter();
        Expect('?');
        if (Peek() == '?' && Peek(1) == '_' && Peek(2) == 'C' && Peek(3) == '@' && Peek(4) == '_')
        {
            SkipStringLiteral();
            Leave();
            return "`string'";
        }

        var (first, kind) = ParseFirstFragment();
        List<string> scopes = ParseScopes();
        if (kind is NameKind.Constructor or NameKind.Destructor)
        {
            if (scopes.Count == 0)
            {
                throw Fail();
            }
            first = (kind == NameKind.Destructor ? "~" : "") + scopes[0];
        }
        string name = JoinName(scopes, first);

        string result;
        if (top && _nameOnly && kind is not (NameKind.Conversion or NameKind.Vcall))
        {
            result = name;
        }
        else
        {
            result = ParseEncoding(name, kind);
        }
        Leave();
        return result;
    }

    private void SkipStringLiteral()
    {
        // ??_C@_<width><length><hash>@<characters>@
        _p += 5;
        Next();
        ParseNumber();
        ParseNumber();
        int end = _s.IndexOf('@', _p);
        _p = end < 0 ? _s.Length : end + 1;
    }

    private static string JoinName(List<string> scopes, string first)
    {
        if (scopes.Count == 0)
        {
            return first;
        }
        var sb = new StringBuilder();
        for (int i = scopes.Count - 1; i >= 0; i--)
        {
            sb.Append(scopes[i]).Append("::");
        }
        return sb.Append(first).ToString();
    }

    /// <summary>The unqualified name of the symbol: identifier, operator, special name or template instance.</summary>
    private (string Text, NameKind Kind) ParseFirstFragment()
    {
        if (Peek() == '?')
        {
            if (Peek(1) == '$')
            {
                _p += 2;
                string instance = ParseTemplateInstance(out NameKind kind);
                return (instance, kind);
            }
            _p++;
            return ParseOperator();
        }
        if (Peek() >= '0' && Peek() <= '9')
        {
            return (BackrefName(Next()), NameKind.Plain);
        }
        string id = ParseIdentifier();
        Memorize(id);
        return (id, NameKind.Plain);
    }

    private string BackrefName(char digit)
    {
        int index = digit - '0';
        if (index >= _nameCount)
        {
            throw Fail();
        }
        return _names[index];
    }

    private (string Text, NameKind Kind) ParseOperator()
    {
        char c = Next();
        switch (c)
        {
            case '0': return ("", NameKind.Constructor);
            case '1': return ("", NameKind.Destructor);
            case 'B': return ("operator", NameKind.Conversion);
            case '_':
                return ParseUnderscoreOperator();
            default:
                string? op = SimpleOperator(c);
                if (op == null)
                {
                    throw Fail();
                }
                return (op, NameKind.Plain);
        }
    }

    private static string? SimpleOperator(char c) => c switch
    {
        '2' => "operator new",
        '3' => "operator delete",
        '4' => "operator=",
        '5' => "operator>>",
        '6' => "operator<<",
        '7' => "operator!",
        '8' => "operator==",
        '9' => "operator!=",
        'A' => "operator[]",
        'C' => "operator->",
        'D' => "operator*",
        'E' => "operator++",
        'F' => "operator--",
        'G' => "operator-",
        'H' => "operator+",
        'I' => "operator&",
        'J' => "operator->*",
        'K' => "operator/",
        'L' => "operator%",
        'M' => "operator<",
        'N' => "operator<=",
        'O' => "operator>",
        'P' => "operator>=",
        'Q' => "operator,",
        'R' => "operator()",
        'S' => "operator~",
        'T' => "operator^",
        'U' => "operator|",
        'V' => "operator&&",
        'W' => "operator||",
        'X' => "operator*=",
        'Y' => "operator+=",
        'Z' => "operator-=",
        _ => null,
    };

    private (string Text, NameKind Kind) ParseUnderscoreOperator()
    {
        char c = Next();
        switch (c)
        {
            case '0': return ("operator/=", NameKind.Plain);
            case '1': return ("operator%=", NameKind.Plain);
            case '2': return ("operator>>=", NameKind.Plain);
            case '3': return ("operator<<=", NameKind.Plain);
            case '4': return ("operator&=", NameKind.Plain);
            case '5': return ("operator|=", NameKind.Plain);
            case '6': return ("operator^=", NameKind.Plain);
            case '7': return ("`vftable'", NameKind.Plain);
            case '8': return ("`vbtable'", NameKind.Plain);
            case '9': return ("`vcall'", NameKind.Vcall);
            case 'A': return ("`typeof'", NameKind.Plain);
            case 'B': return ("`local static guard'", NameKind.LocalStaticGuard);
            case 'D': return ("`vbase destructor'", NameKind.Plain);
            case 'E': return ("`vector deleting destructor'", NameKind.Plain);
            case 'F': return ("`default constructor closure'", NameKind.Plain);
            case 'G': return ("`scalar deleting destructor'", NameKind.Plain);
            case 'H': return ("`vector constructor iterator'", NameKind.Plain);
            case 'I': return ("`vector destructor iterator'", NameKind.Plain);
            case 'J': return ("`vector vbase constructor iterator'", NameKind.Plain);
            case 'K': return ("`virtual displacement map'", NameKind.Plain);
            case 'L': return ("`eh vector constructor iterator'", NameKind.Plain);
            case 'M': return ("`eh vector destructor iterator'", NameKind.Plain);
            case 'N': return ("`eh vector vbase constructor iterator'", NameKind.Plain);
            case 'O': return ("`copy constructor closure'", NameKind.Plain);
            case 'R': return (ParseRttiName(), NameKind.Plain);
            case 'S': return ("`local vftable'", NameKind.Plain);
            case 'T': return ("`local vftable constructor closure'", NameKind.Plain);
            case 'U': return ("operator new[]", NameKind.Plain);
            case 'V': return ("operator delete[]", NameKind.Plain);
            case 'X': return ("`placement delete closure'", NameKind.Plain);
            case 'Y': return ("`placement delete[] closure'", NameKind.Plain);
            case '_':
                return ParseDoubleUnderscoreOperator();
            default:
                throw Fail();
        }
    }

    private (string Text, NameKind Kind) ParseDoubleUnderscoreOperator()
    {
        char c = Next();
        switch (c)
        {
            case 'A': return ("`managed vector constructor iterator'", NameKind.Plain);
            case 'B': return ("`managed vector destructor iterator'", NameKind.Plain);
            case 'C': return ("`eh vector copy constructor iterator'", NameKind.Plain);
            case 'D': return ("`eh vector vbase copy constructor iterator'", NameKind.Plain);
            case 'E': return ("`dynamic initializer for '" + ParseDynamicTarget() + "''", NameKind.Plain);
            case 'F': return ("`dynamic atexit destructor for '" + ParseDynamicTarget() + "''", NameKind.Plain);
            case 'G': return ("`vector copy constructor iterator'", NameKind.Plain);
            case 'H': return ("`vector vbase copy constructor iterator'", NameKind.Plain);
            case 'I': return ("`managed vector copy constructor iterator'", NameKind.Plain);
            case 'J': return ("`local static thread guard'", NameKind.Plain);
            case 'K': return ("operator \"\" " + ParseIdentifier(), NameKind.Plain);
            case 'L': return ("operator co_await", NameKind.Plain);
            case 'M': return ("operator<=>", NameKind.Plain);
            default:
                throw Fail();
        }
    }

    private string ParseDynamicTarget()
    {
        if (Peek() == '?')
        {
            string inner = ParseSymbol(top: false);
            Expect('@');
            return inner;
        }
        return ParseIdentifier();
    }

    private string ParseRttiName()
    {
        char c = Next();
        switch (c)
        {
            case '0':
            {
                Node type;
                if (TryConsume('?'))
                {
                    int cv = ParseCvLetter(out _);
                    type = ParseType(cv);
                }
                else
                {
                    type = ParseType(-1);
                }
                return Render(type, "") + " `RTTI Type Descriptor'";
            }
            case '1':
            {
                string a = ParseSignedText();
                string b = ParseSignedText();
                string c2 = ParseSignedText();
                string d = ParseSignedText();
                return "`RTTI Base Class Descriptor at (" + a + "," + b + "," + c2 + "," + d + ")'";
            }
            case '2': return "`RTTI Base Class Array'";
            case '3': return "`RTTI Class Hierarchy Descriptor'";
            case '4': return "`RTTI Complete Object Locator'";
            default:
                throw Fail();
        }
    }

    /// <summary>Enclosing scopes up to the terminating '@', innermost first.</summary>
    private List<string> ParseScopes()
    {
        var scopes = new List<string>(4);
        while (true)
        {
            char c = Peek();
            if (c == '@')
            {
                _p++;
                return scopes;
            }
            if (c == '\0')
            {
                throw Fail();
            }
            if (c >= '0' && c <= '9')
            {
                _p++;
                scopes.Add(BackrefName(c));
                continue;
            }
            if (c == '?')
            {
                char d = Peek(1);
                if (d == '$')
                {
                    _p += 2;
                    string t = ParseTemplateInstance();
                    Memorize(t);
                    scopes.Add(t);
                    continue;
                }
                if (d == '?')
                {
                    _p++;
                    scopes.Add("`" + ParseSymbol(top: false) + "'");
                    continue;
                }
                if (d == 'A')
                {
                    // The back-reference table keeps the raw identifier ("A0x1234"), not the printed text.
                    _p++;
                    Memorize(ParseIdentifier());
                    scopes.Add("`anonymous namespace'");
                    continue;
                }
                _p++;
                // Numbered local scope: ?<number>?<enclosing function symbol>
                var (_, n) = ParseNumber();
                string number = "`" + n.ToString(CultureInfo.InvariantCulture) + "'";
                if (TryConsume('?'))
                {
                    scopes.Add(number);
                    scopes.Add("`" + ParseSymbol(top: false) + "'");
                }
                else
                {
                    scopes.Add(number);
                }
                continue;
            }
            string id = ParseIdentifier();
            Memorize(id);
            scopes.Add(id);
        }
    }

    /// <summary>A qualified name used as a type (class, struct, enum, member pointer class).</summary>
    private string ParseTypeName()
    {
        string first;
        char c = Peek();
        if (c >= '0' && c <= '9')
        {
            _p++;
            first = BackrefName(c);
        }
        else if (c == '?' && Peek(1) == '$')
        {
            _p += 2;
            first = ParseTemplateInstance();
            Memorize(first);
        }
        else
        {
            first = ParseIdentifier();
            Memorize(first);
        }
        return JoinName(ParseScopes(), first);
    }

    // ---------------------------------------------------------------- templates

    /// <summary>After "?$": template name and arguments; every instance has its own back-reference tables.</summary>
    private string ParseTemplateInstance() => ParseTemplateInstance(out _);

    private string ParseTemplateInstance(out NameKind nameKind)
    {
        Enter();
        string[] savedNames = _names;
        int savedNameCount = _nameCount;
        Node[] savedArgs = _args;
        int savedArgCount = _argCount;
        _names = new string[10];
        _nameCount = 0;
        _args = new Node[10];
        _argCount = 0;

        string name;
        nameKind = NameKind.Plain;
        if (Peek() == '?')
        {
            _p++;
            var (text, kind) = ParseOperator();
            if (kind is not (NameKind.Plain or NameKind.Conversion))
            {
                throw Fail();
            }
            name = text;
            nameKind = kind;
        }
        else
        {
            name = ParseIdentifier();
            Memorize(name);
        }

        var sb = new StringBuilder(name).Append('<');
        bool any = false;
        while (true)
        {
            char c = Peek();
            if (c == '@')
            {
                _p++;
                break;
            }
            if (c == '\0')
            {
                throw Fail();
            }
            string? arg = ParseTemplateArgument();
            if (arg == null)
            {
                continue;
            }
            if (any)
            {
                sb.Append(',');
            }
            sb.Append(arg);
            any = true;
        }
        if (sb[^1] == '>')
        {
            sb.Append(' ');
        }
        sb.Append('>');

        _names = savedNames;
        _nameCount = savedNameCount;
        _args = savedArgs;
        _argCount = savedArgCount;
        Leave();
        return sb.ToString();
    }

    /// <summary>One template argument; null for an argument that prints nothing (empty pack).</summary>
    private string? ParseTemplateArgument()
    {
        int start = _p;
        string? text = ParseTemplateArgumentCore();
        if (text != null && _p - start > 1 && _argCount < _args.Length && _s[start] == '$' && _s[start + 1] != '$')
        {
            // Non-type arguments take a back-reference slot too.
            _args[_argCount++] = new Prim { Name = text };
        }
        return text;
    }

    private string? ParseTemplateArgumentCore()
    {
        char c = Peek();
        if (c >= '0' && c <= '9')
        {
            _p++;
            return Render(BackrefArg(c), "");
        }
        if (c == '$')
        {
            char d = Peek(1);
            switch (d)
            {
                case '$':
                    char e = Peek(2);
                    if (e is 'V' or 'Z')
                    {
                        _p += 3;
                        return null;
                    }
                    break;
                case 'M':
                    // C++17 'auto' non-type parameter: the type, then the value without its '$'.
                    _p += 2;
                    ParseType(-1);
                    return ParseNonTypeValue(Next());
                case 'S':
                    _p += 2;
                    return null;
                default:
                    _p += 2;
                    return ParseNonTypeValue(d);
            }
        }
        int start = _p;
        Node node = ParseType(-1);
        MemorizeArg(node, start);
        return Render(node, "");
    }

    private string ParseNonTypeValue(char code)
    {
        switch (code)
        {
            case '0':
                return ParseSignedText();
            case '1':
                if (TryConsume('@'))
                {
                    return "NULL";
                }
                return "&" + ParseSymbol(top: false);
            case 'E':
                return ParseSymbol(top: false);
            default:
                throw Fail();
        }
    }

    private Node BackrefArg(char digit)
    {
        int index = digit - '0';
        if (index >= _argCount)
        {
            throw Fail();
        }
        return _args[index];
    }

    private void MemorizeArg(Node node, int start)
    {
        if (_p - start > 1 && _argCount < _args.Length)
        {
            _args[_argCount++] = node;
        }
    }

    // ---------------------------------------------------------------- encodings after the name

    private static string AccessPrefix(int group) => group switch
    {
        0 => "private: ",
        1 => "protected: ",
        _ => "public: ",
    };

    private string ParseEncoding(string name, NameKind kind)
    {
        char c = Next();
        if (c >= 'A' && c <= 'X')
        {
            int group = (c - 'A') / 8;
            int sub = (c - 'A') % 8 / 2;
            string prefix = AccessPrefix(group);
            string suffix = "";
            bool hasThis = true;
            switch (sub)
            {
                case 1:
                    prefix += "static ";
                    hasThis = false;
                    break;
                case 2:
                    prefix += "virtual ";
                    break;
                case 3:
                    prefix = "[thunk]:" + prefix + "virtual ";
                    suffix = "`adjustor{" + ParseSignedText() + "}' ";
                    break;
            }
            return ParseFunctionSymbol(prefix, name, suffix, hasThis, kind);
        }
        switch (c)
        {
            case 'Y':
            case 'Z':
                return ParseFunctionSymbol("", name, "", false, kind);
            case '$':
                return ParseSpecialThunk(name, kind);
            case '0':
            case '1':
            case '2':
                return ParseDataSymbol(AccessPrefix(c - '0') + "static ", name);
            case '3':
            case '4':
                return ParseDataSymbol("", name);
            case '5':
                if (kind == NameKind.LocalStaticGuard)
                {
                    string n = ParseSignedText();
                    return _nameOnly ? name : name + "{" + n + "}'";
                }
                throw Fail();
            case '6':
            case '7':
                return ParseVirtualTable(name);
            case '8':
            case '9':
                return name;
            default:
                throw Fail();
        }
    }

    private string ParseVirtualTable(string name)
    {
        ParseModifiers();
        int cv = ParseCvLetter(out _);
        var bases = new List<string>();
        while (!TryConsume('@'))
        {
            bases.Add(ParseTypeName());
        }
        if (_nameOnly)
        {
            return name;
        }
        var sb = new StringBuilder();
        string cvWords = CvWords(cv);
        if (cvWords.Length > 0)
        {
            sb.Append(cvWords).Append(' ');
        }
        sb.Append(name);
        if (bases.Count > 0)
        {
            sb.Append("{for ");
            for (int i = 0; i < bases.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append("s ");
                }
                sb.Append('`').Append(bases[i]).Append('\'');
            }
            sb.Append('}');
        }
        return sb.ToString();
    }

    private string ParseSpecialThunk(string name, NameKind kind)
    {
        char c = Next();
        if (c == 'B' && kind == NameKind.Vcall)
        {
            string n = ParseSignedText();
            Expect('A');
            string cc = CallingConvention(Next());
            if (_nameOnly)
            {
                return name + "{" + n + "}'";
            }
            string head = cc.Length > 0 ? "[thunk]: " + cc + " " : "[thunk]:";
            return head + name + "{" + n + ",{flat}}' }'";
        }
        if (c >= '0' && c <= '5')
        {
            string prefix = "[thunk]:" + AccessPrefix((c - '0') / 2) + "virtual ";
            string a = ParseSignedText();
            string b = ParseSignedText();
            return ParseFunctionSymbol(prefix, name, "`vtordisp{" + a + "," + b + "}' ", true, kind);
        }
        throw Fail();
    }

    private string ParseFunctionSymbol(string prefix, string name, string nameSuffix, bool hasThis, NameKind kind)
    {
        Func f = ParseFunctionType(hasThis);
        if (kind == NameKind.Conversion)
        {
            if (f.Ret == null)
            {
                throw Fail();
            }
            name += " " + Render(f.Ret, "");
            f.Ret = null;
        }
        if (_nameOnly)
        {
            return name;
        }
        string core = name + nameSuffix;
        string cc = _noMs ? "" : f.CallConv;
        if (cc.Length > 0)
        {
            core = cc + " " + core;
        }
        return prefix + RenderFunction(f, core);
    }

    private string ParseDataSymbol(string prefix, string name)
    {
        Node type = ParseType(-1);
        int mods = ParseModifiers();
        int cv = ParseCvLetter(out bool member);
        if (member)
        {
            ParseTypeName();
        }
        if (_nameOnly)
        {
            return name;
        }
        if (type is Ptr or Prim)
        {
            type.Cv = cv;
            type.Extra = mods;
        }
        if (type is Ptr top)
        {
            top.NamedDecl = true;
        }
        return prefix + Render(type, name);
    }

    // ---------------------------------------------------------------- types

    private abstract class Node
    {
        public int Cv;
        public int Extra;     // modifiers that follow the cv qualifiers (storage class, $$C)
        public bool Trailing; // $$C: the cv qualifiers are followed by a space
    }

    private sealed class Prim : Node
    {
        public string Keyword = "";
        public string Name = "";
    }

    private sealed class Ptr : Node
    {
        public string Kind = "*";
        public string? Class;
        public int Mods;
        public int PointeeCv;
        public bool NamedDecl; // the declarator is a data symbol's name
        public Node Pointee = null!;
    }

    private sealed class Func : Node
    {
        public string CallConv = "";
        public Node? Ret;
        public string Args = "";
        public string This = "";
        public string Throw = "";
    }

    private sealed class Arr : Node
    {
        public string Dims = "";
        public Node Elem = null!;
    }

    private int ParseModifiers()
    {
        int mods = 0;
        while (true)
        {
            switch (Peek())
            {
                case 'E': mods |= ModPtr64; break;
                case 'F': mods |= ModUnaligned; break;
                case 'I': mods |= ModRestrict; break;
                default: return mods;
            }
            _p++;
        }
    }

    /// <summary>Cv letter: A-D plain, Q-T member pointer (class name follows).</summary>
    private int ParseCvLetter(out bool member)
    {
        char c = Next();
        if (c >= 'A' && c <= 'D')
        {
            member = false;
            return c - 'A';
        }
        if (c >= 'Q' && c <= 'T')
        {
            member = true;
            return c - 'Q';
        }
        throw Fail();
    }

    private static string? PrimitiveName(char c) => c switch
    {
        'C' => "signed char",
        'D' => "char",
        'E' => "unsigned char",
        'F' => "short",
        'G' => "unsigned short",
        'H' => "int",
        'I' => "unsigned int",
        'J' => "long",
        'K' => "unsigned long",
        'M' => "float",
        'N' => "double",
        'O' => "long double",
        'X' => "void",
        _ => null,
    };

    private static string? ExtendedName(char c) => c switch
    {
        'D' => "__int8",
        'E' => "unsigned __int8",
        'F' => "__int16",
        'G' => "unsigned __int16",
        'H' => "__int32",
        'I' => "unsigned __int32",
        'J' => "__int64",
        'K' => "unsigned __int64",
        'L' => "__int128",
        'M' => "unsigned __int128",
        'N' => "bool",
        'Q' => "char8_t",
        'S' => "char16_t",
        'U' => "char32_t",
        'W' => "wchar_t",
        _ => null,
    };

    private static string EnumKeyword(char c) => c switch
    {
        '0' => "enum char ",
        '1' => "enum unsigned char ",
        '2' => "enum short ",
        '3' => "enum unsigned short ",
        '4' => "enum ",
        '5' => "enum unsigned int ",
        '6' => "enum long ",
        '7' => "enum unsigned long ",
        _ => throw Fail(),
    };

    /// <summary>
    /// A data type. <paramref name="cv"/> is the cv qualification imposed by the context (a pointer's pointee
    /// letter), or -1 to take it from the type's own letter.
    /// </summary>
    private Node ParseType(int cv)
    {
        Enter();
        Node node = ParseTypeCore(cv);
        Leave();
        return node;
    }

    private Node ParseTypeCore(int cv)
    {
        char c = Next();
        string? prim = PrimitiveName(c);
        if (prim != null)
        {
            return new Prim { Name = prim, Cv = Math.Max(cv, 0) };
        }
        switch (c)
        {
            case '_':
            {
                char d = Next();
                if (d == '$')
                {
                    Node inner = ParseType(cv);
                    return new Prim { Name = "__w64 " + Render(inner, "") };
                }
                string? ext = ExtendedName(d);
                if (ext == null)
                {
                    throw Fail();
                }
                return new Prim { Name = ext, Cv = Math.Max(cv, 0) };
            }
            case 'T':
                return new Prim { Keyword = "union ", Name = ParseTypeName(), Cv = Math.Max(cv, 0) };
            case 'U':
                return new Prim { Keyword = "struct ", Name = ParseTypeName(), Cv = Math.Max(cv, 0) };
            case 'V':
                return new Prim { Keyword = "class ", Name = ParseTypeName(), Cv = Math.Max(cv, 0) };
            case 'W':
            {
                string keyword = EnumKeyword(Next());
                return new Prim { Keyword = keyword, Name = ParseTypeName(), Cv = Math.Max(cv, 0) };
            }
            case 'P':
            case 'Q':
            case 'R':
            case 'S':
                return ParsePointer("*", c - 'P', cv);
            case 'A':
                return ParsePointer("&", 0, cv);
            case 'B':
                return ParsePointer("&", CvVolatile, cv);
            case 'Y':
                return ParseArray();
            case '?':
                // Placeholder type such as "<auto>"; printed as a bare name.
                return new Prim { Name = ParseTypeName(), Cv = Math.Max(cv, 0) };
            case '$':
                return ParseDollarType(cv);
            default:
                throw Fail();
        }
    }

    private Node ParseDollarType(int cv)
    {
        Expect('$');
        char c = Next();
        switch (c)
        {
            case 'Q':
                return ParsePointer("&&", 0, cv);
            case 'R':
                return ParsePointer("&&", CvVolatile, cv);
            case 'T':
                return new Prim { Name = "std::nullptr_t", Cv = Math.Max(cv, 0) };
            case 'A':
            {
                Expect('6');
                return ParseFunctionType(hasThis: false);
            }
            case 'B':
                return ParseType(cv);
            case 'C':
            {
                int mods = ParseModifiers();
                int inner = ParseCvLetter(out _);
                Node node = ParseType(inner);
                node.Cv = inner;
                node.Extra = mods;
                node.Trailing = inner != 0 || (mods & ModUnaligned) != 0;
                return node;
            }
            default:
                throw Fail();
        }
    }

    private Arr ParseArray()
    {
        var (_, count) = ParseNumber();
        if (count == 0 || count > 64)
        {
            throw Fail();
        }
        var sb = new StringBuilder();
        for (ulong i = 0; i < count; i++)
        {
            sb.Append('[').Append(ParseSignedText()).Append(']');
        }
        return new Arr { Dims = sb.ToString(), Elem = ParseType(-1) };
    }

    private Ptr ParsePointer(string kind, int ownCv, int cvOverride)
    {
        var ptr = new Ptr { Kind = kind, Cv = cvOverride >= 0 ? cvOverride : ownCv };
        ptr.Mods = ParseModifiers();
        if (Peek() == '$' && Peek(1) == 'A')
        {
            // C++/CLI handle (^) or tracking reference (%).
            _p += 2;
            ptr.Kind = kind == "*" ? "^" : "%";
            ptr.Mods |= ParseModifiers();
        }
        char c = Peek();
        if (c == '6')
        {
            _p++;
            ptr.Pointee = ParseFunctionType(hasThis: false);
            return ptr;
        }
        if (c == '8')
        {
            _p++;
            ptr.Class = ParseTypeName();
            ptr.Pointee = ParseFunctionType(hasThis: true);
            return ptr;
        }
        int pointeeCv = ParseCvLetter(out bool member);
        if (member)
        {
            ptr.Class = ParseTypeName();
        }
        ptr.PointeeCv = pointeeCv;
        if (Peek() == 'Y')
        {
            _p++;
            ptr.Pointee = ParseArray();
        }
        else
        {
            ptr.Pointee = ParseType(pointeeCv);
        }
        return ptr;
    }

    private static string CallingConventionName(char c) => c switch
    {
        'A' or 'B' => "__cdecl",
        'C' or 'D' => "__pascal",
        'E' or 'F' => "__thiscall",
        'G' or 'H' => "__stdcall",
        'I' or 'J' => "__fastcall",
        'K' or 'L' => "",
        'M' or 'N' => "__clrcall",
        'O' or 'P' => "__eabi",
        'Q' => "__vectorcall",
        'S' => "__swift_1",
        'U' => "__swift_2",
        'W' => "__swift_3",
        _ => throw Fail(),
    };

    private string CallingConvention(char c) => _noMs ? "" : CallingConventionName(c);

    /// <summary>Function type after the access letter: [this qualifiers] calling convention, return, arguments, throw.</summary>
    private Func ParseFunctionType(bool hasThis)
    {
        var f = new Func();
        if (hasThis)
        {
            f.This = ParseThisQualifiers();
        }
        f.CallConv = CallingConventionName(Next());
        f.Ret = ParseReturnType();
        f.Args = ParseArguments();
        f.Throw = ParseThrow();
        return f;
    }

    private string ParseThisQualifiers()
    {
        int mods = 0;
        bool lref = false;
        bool rref = false;
        while (true)
        {
            char c = Peek();
            if (c == 'E') mods |= ModPtr64;
            else if (c == 'F') mods |= ModUnaligned;
            else if (c == 'I') mods |= ModRestrict;
            else if (c == 'G') lref = true;
            else if (c == 'H') rref = true;
            else break;
            _p++;
        }
        int cv = ParseCvLetter(out _);
        var sb = new StringBuilder();
        string words = CvWords(cv);
        if (words.Length > 0)
        {
            sb.Append(words).Append(' ');
        }
        if ((mods & ModPtr64) != 0 && !_noMs)
        {
            sb.Append(sb.Length == 0 ? " __ptr64" : "__ptr64");
        }
        if ((mods & ModRestrict) != 0 && !_noMs)
        {
            sb.Append(" __restrict");
        }
        if (lref)
        {
            sb.Append("& ");
        }
        if (rref)
        {
            sb.Append("&& ");
        }
        return sb.ToString();
    }

    private Node? ParseReturnType()
    {
        if (TryConsume('@'))
        {
            return null;
        }
        if (TryConsume('?'))
        {
            int cv = ParseCvLetter(out _);
            return ParseType(cv);
        }
        // A return type's own top-level cv qualifier is not printed (a 'Q' const pointer prints as '*').
        return ParseType(0);
    }

    private string ParseArguments()
    {
        if (TryConsume('X'))
        {
            return "void";
        }
        var sb = new StringBuilder();
        while (true)
        {
            char c = Peek();
            if (c == '@')
            {
                _p++;
                break;
            }
            if (c == 'Z')
            {
                _p++;
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }
                sb.Append("...");
                break;
            }
            if (c == '\0')
            {
                throw Fail();
            }
            Node node;
            if (c >= '0' && c <= '9')
            {
                _p++;
                node = BackrefArg(c);
            }
            else
            {
                int start = _p;
                node = ParseType(-1);
                MemorizeArg(node, start);
            }
            if (sb.Length > 0)
            {
                sb.Append(',');
            }
            sb.Append(Render(node, ""));
        }
        return sb.ToString();
    }

    private string ParseThrow()
    {
        if (TryConsume('Z'))
        {
            return "";
        }
        if (TryConsume("_E"))
        {
            return " noexcept";
        }
        throw Fail();
    }

    // ---------------------------------------------------------------- rendering

    private static string CvWords(int cv) => cv switch
    {
        1 => "const",
        2 => "volatile",
        3 => "const volatile",
        _ => "",
    };

    private void AppendCv(StringBuilder sb, int cv, bool trailing)
    {
        string words = CvWords(cv);
        if (words.Length > 0)
        {
            sb.Append(' ').Append(words);
            if (trailing)
            {
                sb.Append(' ');
            }
        }
    }

    private void AppendMods(StringBuilder sb, int mods)
    {
        if (_noMs)
        {
            return;
        }
        if ((mods & ModUnaligned) != 0)
        {
            sb.Append(" __unaligned ");
        }
        if ((mods & ModPtr64) != 0)
        {
            sb.Append(sb.Length > 0 && sb[^1] == ' ' ? "__ptr64" : " __ptr64");
        }
        if ((mods & ModRestrict) != 0)
        {
            sb.Append(" __restrict");
        }
    }

    private static void AppendDecl(StringBuilder sb, string decl)
    {
        if (decl.Length > 0)
        {
            if (sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }
            sb.Append(decl);
        }
    }

    private string Render(Node node, string decl)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw Fail();
        }
        switch (node)
        {
            case Prim p:
            {
                var sb = new StringBuilder();
                if (!_nameOnly || p.Keyword.StartsWith('e'))
                {
                    sb.Append(p.Keyword);
                }
                sb.Append(p.Name);
                AppendCv(sb, p.Cv, p.Trailing);
                AppendMods(sb, p.Extra);
                AppendDecl(sb, decl);
                return sb.ToString();
            }
            case Ptr ptr:
                return RenderPointer(ptr, decl);
            case Func f:
            {
                string cc = _noMs ? "" : f.CallConv;
                return RenderFunction(f, cc + decl);
            }
            case Arr a:
                return RenderArray(a, decl);
            default:
                throw Fail();
        }
    }

    private string RenderFunction(Func f, string core)
    {
        string text = core + "(" + f.Args + ")" + f.This + f.Throw;
        return f.Ret == null ? text : Render(f.Ret, text);
    }

    private string RenderArray(Arr a, string decl) => Render(a.Elem, decl + a.Dims);

    private string RenderPointer(Ptr ptr, string decl)
    {
        var sb = new StringBuilder();
        if (ptr.Pointee is Func f)
        {
            string cc = _noMs ? "" : f.CallConv;
            if (ptr.Class != null)
            {
                sb.Append(cc).Append(' ').Append(ptr.Class).Append("::").Append(ptr.Kind);
                AppendMods(sb, ptr.Mods);
                string words = CvWords(ptr.Cv);
                if (words.Length > 0)
                {
                    sb.Append(words);
                    if (ptr.Trailing)
                    {
                        sb.Append(' ');
                    }
                }
                AppendMods(sb, ptr.Extra);
                AppendDecl(sb, decl);
            }
            else
            {
                sb.Append(ptr.Kind);
                AppendMods(sb, ptr.Mods);
                string words = CvWords(ptr.Cv);
                bool spaced = sb.Length > ptr.Kind.Length;
                if (words.Length > 0)
                {
                    sb.Append(words);
                    if (ptr.Trailing)
                    {
                        sb.Append(' ');
                    }
                    spaced = true;
                }
                AppendMods(sb, ptr.Extra);
                spaced |= sb.Length > ptr.Kind.Length;
                if (decl.Length > 0)
                {
                    if ((spaced || ptr.NamedDecl) && sb[^1] != ' ')
                    {
                        sb.Append(' ');
                    }
                    sb.Append(decl);
                }
                sb.Insert(0, cc);
            }
            return RenderFunction(f, "(" + sb + ")");
        }

        if (ptr.Pointee is Arr a)
        {
            string pointeeCv = CvWords(ptr.PointeeCv);
            if (pointeeCv.Length > 0)
            {
                sb.Append(pointeeCv).Append(' ');
            }
        }
        if (ptr.Class != null)
        {
            sb.Append(ptr.Class).Append("::");
        }
        sb.Append(ptr.Kind);
        AppendMods(sb, ptr.Mods & ~ModUnaligned);
        AppendCv(sb, ptr.Cv, ptr.Trailing);
        AppendMods(sb, ptr.Extra);
        AppendDecl(sb, decl);
        string core = sb.ToString();
        if (ptr.Pointee is Arr array)
        {
            return RenderArray(array, "(" + core + ")");
        }
        if ((ptr.Mods & ModUnaligned) != 0 && !_noMs)
        {
            core = "__unaligned " + core;
        }
        return Render(ptr.Pointee, core);
    }
}
