using System.Globalization;
using System.Text;

namespace GateKit.Symbols.Registry;

/// <summary>A registry id that breaks the rules of research/coverage/registry/README.md: the message is for the user.</summary>
public sealed class RegistryException(string message) : Exception(message);

/// <summary>
/// The id of an entry of the registry: the name as the game writes it. Scope is separated by
/// <c>::</c> (<c>esv::RollSystem::RollDice</c>); a member - a field of a type, a parameter or local
/// of a function - follows the entity after a dot (<c>eoc::HealthComponent.Hp</c>); a piece the
/// compiler made of a function follows after <c>@</c> (<c>RollDice@thunk</c>); an overload that
/// clashes carries its parameter types (<c>SetTranslate(Vector3f*)</c>); template arguments stay
/// in the name (<c>Array&lt;int&gt;::Add</c>). The separators count only outside angle brackets and
/// parentheses, and the symbol of an <c>operator</c> is part of its name.
///
/// The file of an entity is its id with <c>::</c> turned into folders, each part written by
/// <see cref="EscapePart"/>; the rule is reversible (<see cref="FromPath"/>), and two ids that differ
/// only by case are refused, because they would share one file on a file system without case.
/// </summary>
public static class RegistryId
{
    /// <summary>The scope a thing whose owner is not known lives under until it is placed.</summary>
    public const string Unplaced = "unplaced";

    /// <summary>The word of an id that has no name yet: <c>&lt;scope&gt;::unnamed_&lt;token&gt;</c>.</summary>
    public const string UnnamedPrefix = "unnamed_";

    /// <summary>The longest relative path of an entry's file under the registry (Windows keeps whole paths under 260 characters).</summary>
    public const int MaxRelativePath = 160;

    static readonly string[] OperatorSymbols =
    [
        "()", "[]", "->*", "->", "<<=", ">>=", "<=>", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "++", "--",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<", ">", "+", "-", "*", "/", "%", "^", "&", "|", "~", "!", "=", ",",
    ];

    /// <summary>An id split at its separators.</summary>
    /// <param name="Scope">The parts of the entity's name, outermost first; the last is the entity itself.</param>
    /// <param name="Piece">The piece of a function after <c>@</c>, or null.</param>
    /// <param name="Member">The member path after the first dot (<c>Hp</c>, <c>Roll.Total</c>), or null.</param>
    public sealed record Parts(IReadOnlyList<string> Scope, string? Piece, string? Member)
    {
        /// <summary>The entity's id: the scope joined, with its piece; no member.</summary>
        public string Entity => string.Join("::", Scope) + (Piece is null ? "" : "@" + Piece);
    }

    /// <summary>The id split into scope, piece and member; a malformed id throws <see cref="RegistryException"/>.</summary>
    public static Parts Parse(string id)
    {
        if (string.IsNullOrEmpty(id)) throw new RegistryException("an id is a name, not empty");
        var scope = new List<string>();
        var current = new StringBuilder();
        string? piece = null, member = null;
        int angle = 0, paren = 0;
        var i = 0;
        while (i < id.Length)
        {
            var c = id[i];
            if (current.Length == 0 && piece is null && angle == 0 && paren == 0 && OperatorAt(id, i) is { } op)
            {
                current.Append(op);
                i += op.Length;
                continue;
            }
            if (char.IsControl(c)) throw new RegistryException($"{id}: a control character is not part of a name");
            if (angle == 0 && paren == 0)
            {
                if (c == ':' && i + 1 < id.Length && id[i + 1] == ':')
                {
                    if (piece is not null) throw new RegistryException($"{id}: the piece after @ ends the entity's name");
                    if (current.Length == 0) throw new RegistryException($"{id}: an empty part between ::");
                    scope.Add(current.ToString());
                    current.Clear();
                    i += 2;
                    continue;
                }
                if (c == '@')
                {
                    if (piece is not null || current.Length == 0) throw new RegistryException($"{id}: one piece, after the function's name (RollDice@thunk)");
                    scope.Add(current.ToString());
                    current.Clear();
                    piece = "";
                    i++;
                    continue;
                }
                if (c == '.')
                {
                    member = id[(i + 1)..];
                    break;
                }
                if (c == ':' || char.IsWhiteSpace(c) || c == ',')
                    throw new RegistryException($"{id}: '{c}' stands only inside <...> or (...)");
            }
            if (c == '<') angle++;
            else if (c == '>') { if (--angle < 0) throw new RegistryException($"{id}: a > that closes nothing"); }
            else if (c == '(') paren++;
            else if (c == ')') { if (--paren < 0) throw new RegistryException($"{id}: a ) that closes nothing"); }
            current.Append(c);
            i++;
        }
        if (angle != 0 || paren != 0) throw new RegistryException($"{id}: brackets that are not closed");
        if (piece is null)
        {
            if (current.Length == 0) throw new RegistryException($"{id}: the name ends with a separator");
            scope.Add(current.ToString());
        }
        else
        {
            if (current.Length == 0) throw new RegistryException($"{id}: @ names a piece (RollDice@thunk)");
            piece = current.ToString();
        }
        if (member is not null)
        {
            if (member.Length == 0 || member.Split('.').Any(m => m.Length == 0 || !m.All(ch => char.IsLetterOrDigit(ch) || ch == '_')))
                throw new RegistryException($"{id}: a member is a word after a dot (eoc::HealthComponent.Hp), a deeper one after another dot");
        }
        return new Parts(scope, piece, member);
    }

    static string? OperatorAt(string id, int i)
    {
        if (string.CompareOrdinal(id, i, "operator", 0, 8) != 0) return null;
        var rest = i + 8;
        if (rest >= id.Length) return null;
        if (char.IsLetterOrDigit(id[rest]) || id[rest] == '_') return null;
        foreach (var symbol in OperatorSymbols)
            if (string.CompareOrdinal(id, rest, symbol, 0, symbol.Length) == 0)
                return id.Substring(i, 8 + symbol.Length);
        return null;
    }

    /// <summary>Whether the text is a well-formed id.</summary>
    public static bool IsValid(string id)
    {
        try { Parse(id); return true; }
        catch (RegistryException) { return false; }
    }

    /// <summary>The entity an id belongs to (itself, or the owner of its member).</summary>
    public static string EntityOf(string id) => Parse(id).Entity;

    /// <summary>The key two ids share when they differ only by case.</summary>
    public static string CaseKey(string id) => id.ToUpperInvariant();

    /// <summary>Whether an id names something that has no name yet.</summary>
    public static bool IsUnnamed(string id) => Parse(id).Scope[^1].StartsWith(UnnamedPrefix, StringComparison.Ordinal);

    /// <summary>The id of a thing with no name yet: its owner's scope (or <c>unplaced</c>) and a token from its fingerprint.</summary>
    public static string Unnamed(string? ownerScope, string token) =>
        (string.IsNullOrEmpty(ownerScope) ? Unplaced : ownerScope) + "::" + UnnamedPrefix + token;

    /// <summary>A name as the old ledgers wrote it, placed: a name without a scope goes under <c>unplaced</c>.</summary>
    public static string Placed(string name) => Parse(name).Scope.Count == 1 ? Unplaced + "::" + name : name;

    /// <summary>The name an old ledger wrote for a placed id: <c>unplaced::</c> taken off again.</summary>
    public static string Unplace(string id) => id.StartsWith(Unplaced + "::", StringComparison.Ordinal) ? id[(Unplaced.Length + 2)..] : id;

    // --- the file of an id ---

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// One part of an id as a file or folder name. <c>::</c> inside template arguments becomes
    /// <c>..</c>; <c>&lt;</c> <c>[</c>, <c>&gt;</c> <c>]</c>, <c>*</c> <c>^</c>, a space <c>+</c>;
    /// every character that would be read otherwise - <c>% . [ ] ^ + / \ : ? " |</c>, a control
    /// character, anything outside ASCII - is <c>%</c> and two upper-case hexadecimal digits a byte of
    /// UTF-8, and so is <c>@</c>, which in a file name separates a function from its piece; a name
    /// Windows reserves (CON, NUL, COM1...) has its first letter written so. Everything else -
    /// letters, digits, <c>_ ( ) , ~ = ! &amp; - ' ; { } $ #</c> - is written as itself.
    /// </summary>
    public static string EscapePart(string part)
    {
        var b = new StringBuilder(part.Length + 8);
        for (var i = 0; i < part.Length; i++)
        {
            var c = part[i];
            if (c == ':' && i + 1 < part.Length && part[i + 1] == ':') { b.Append(".."); i++; continue; }
            switch (c)
            {
                case '<': b.Append('['); continue;
                case '>': b.Append(']'); continue;
                case '*': b.Append('^'); continue;
                case ' ': b.Append('+'); continue;
            }
            if (c is '%' or '.' or '[' or ']' or '^' or '+' or '/' or '\\' or ':' or '?' or '"' or '|' or '@' || c < 0x20 || c > 0x7E)
            {
                var bytes = char.IsHighSurrogate(c) && i + 1 < part.Length
                    ? Encoding.UTF8.GetBytes(part.Substring(i++, 2))
                    : Encoding.UTF8.GetBytes(c.ToString());
                foreach (var x in bytes) b.Append('%').Append(x.ToString("X2", CultureInfo.InvariantCulture));
                continue;
            }
            b.Append(c);
        }
        var name = b.ToString();
        if (Reserved.Contains(name)) name = "%" + ((byte)name[0]).ToString("X2", CultureInfo.InvariantCulture) + name[1..];
        return name;
    }

    /// <summary>The reverse of <see cref="EscapePart"/>.</summary>
    public static string UnescapePart(string name)
    {
        var b = new StringBuilder(name.Length);
        var bytes = new List<byte>();
        void Flush()
        {
            if (bytes.Count == 0) return;
            b.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '%' && i + 2 < name.Length
                && byte.TryParse(name.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var x))
            {
                bytes.Add(x);
                i += 2;
                continue;
            }
            Flush();
            if (c == '.' && i + 1 < name.Length && name[i + 1] == '.') { b.Append("::"); i++; continue; }
            b.Append(c switch { '[' => '<', ']' => '>', '^' => '*', '+' => ' ', _ => c });
        }
        Flush();
        return b.ToString();
    }

    /// <summary>
    /// The file of an entity, relative to its kind's folder, with '/' between segments:
    /// <c>esv/RollSystem/RollDice.json</c>. A member's id has no file of its own (it lives in its
    /// entity's); a path longer than <see cref="MaxRelativePath"/> is refused.
    /// </summary>
    public static string ToPath(string id)
    {
        var parts = Parse(id);
        if (parts.Member is not null) throw new RegistryException($"{id} is a member: it lives in the file of {parts.Entity}");
        var segments = parts.Scope.Select(EscapePart).ToList();
        if (parts.Piece is not null) segments[^1] += "@" + EscapePart(parts.Piece);
        var path = string.Join("/", segments) + ".json";
        if (path.Length > MaxRelativePath)
            throw new RegistryException($"{id}: its file would be {path.Length} characters long, more than {MaxRelativePath} - shorten a template argument, or the rule needs a way for such names");
        return path;
    }

    /// <summary>The id a file stands for, from its path relative to its kind's folder.</summary>
    public static string FromPath(string relative)
    {
        var path = relative.Replace('\\', '/');
        if (!path.EndsWith(".json", StringComparison.Ordinal)) throw new RegistryException($"{relative}: an entry's file ends in .json");
        path = path[..^5];
        var segments = path.Split('/');
        var last = segments[^1];
        string? piece = null;
        var at = last.IndexOf('@');
        if (at >= 0)
        {
            piece = UnescapePart(last[(at + 1)..]);
            segments[^1] = last[..at];
        }
        var id = string.Join("::", segments.Select(UnescapePart)) + (piece is null ? "" : "@" + piece);
        Parse(id);
        return id;
    }
}
