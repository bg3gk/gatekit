using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GateKit.Symbols.Queries;

/// <summary>The condition under which a call happens, read off the pseudo-code around it.</summary>
/// <param name="Status">extracted (at least one condition), always (no condition encloses the call and
/// nothing jumps to it), not_extracted (see <paramref name="Note"/>).</param>
/// <param name="Terms">The conditions, outermost first, as text over named fields where the
/// decompiler shows them; an unnamed offset is written <c>[var+0xNN]</c> or <c>Type+0xNN</c>.</param>
/// <param name="Partial">A label lies between the start of an enclosing block and the call: the code
/// is also reached by a jump, so the conditions are those of one path only.</param>
/// <param name="Unnamed">How many of the terms still hold an unnamed local, offset or global.</param>
public sealed record GuardResult(string Status, IReadOnlyList<string> Terms, bool Partial, int Unnamed, string? Note)
{
    public static GuardResult NotExtracted(string why) => new("not_extracted", [], false, 0, why);

    /// <summary>"when A and B", "always", or "guard not extracted (why)".</summary>
    public string Text => Status switch
    {
        "extracted" => "when " + string.Join(" and ", Terms) + (Partial ? " (one path; also reached by a jump)" : ""),
        "always" => "always (no condition encloses it)",
        _ => "guard not extracted" + (Note is null ? "" : $" ({Note})"),
    };
}

/// <summary>What the guard reader knows beyond the text: the helpers by the name the pseudo-code
/// shows them under, and the record's field names by structure.</summary>
public sealed class GuardNames
{
    /// <summary>Helper name as called in the pseudo-code -> (family, component type).</summary>
    public Dictionary<string, (string Family, string Type)> Helpers { get; } = new(StringComparer.Ordinal);

    /// <summary>Component type (full engine name) -> offset -> field name, from the coverage record.</summary>
    public Dictionary<string, Dictionary<long, string>> Fields { get; } = new(StringComparer.Ordinal);

    /// <summary>The short name the decompiler prints a structure under -> the full name, when only one has it.</summary>
    public Dictionary<string, string?> Short { get; } = new(StringComparer.Ordinal);

    public void AddType(string full, Dictionary<long, string> fields)
    {
        Fields[full] = fields;
        var shortName = full[(full.LastIndexOf("::", StringComparison.Ordinal) is int at and >= 0 ? at + 2 : 0)..];
        Short[shortName] = Short.TryGetValue(shortName, out var had) && had != full ? null : full;
    }

    public string FullName(string shortOrFull) =>
        Fields.ContainsKey(shortOrFull) ? shortOrFull : Short.TryGetValue(shortOrFull, out var full) && full != null ? full : shortOrFull;
}

/// <summary>
/// A small reader of Ghidra's pseudo-code: the enclosing ifs, else branches, loops, switch cases,
/// early returns and the conjuncts before a call inside a condition, rewritten over the names the
/// pseudo-code and the record know (<c>has(T)</c> for a test of a component's presence,
/// <c>T.Field</c> for a field of one). It does not interpret gotos: a label on the way makes the
/// result "one path", and a call that is only reached through jumps has no guard it can say.
/// </summary>
public static partial class PseudoGuard
{
    const int MaxTerm = 110;

    [GeneratedRegex(@"^\s*([A-Za-z_]\w*)\s*:(?!:)", RegexOptions.CultureInvariant)]
    private static partial Regex Label();

    [GeneratedRegex(@"^\s*(case\s+(?<v>[^:]+)|default)\s*:(?!:)", RegexOptions.CultureInvariant)]
    private static partial Regex Case();

    [GeneratedRegex(@"\((?:const\s+)?(?:struct\s+|class\s+)?[A-Za-z_][\w:<>,]*(?:\s*\*)+\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex PointerCast();

    [GeneratedRegex(@"\((?:byte|char|uchar|short|ushort|int|uint|long|ulong|longlong|ulonglong|float|double|bool|undefined\d*|u?int\d+_t|code|int\d|uint\d|sbyte|word|dword|qword)\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ValueCast();

    [GeneratedRegex(@"\([A-Z]\w*\)0x0\b", RegexOptions.CultureInvariant)]
    private static partial Regex TypedZero();

    [GeneratedRegex(@"\*\s*\(\s*([A-Za-z_]\w*)\s*\+\s*(0x[0-9a-fA-F]+|\d+)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Deref();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*->\s*([A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex Arrow();

    [GeneratedRegex(@"\b(?:[A-Za-z]{1,4}Var\d+|local_\w+|param_\d+|in_\w+|unaff_\w+|extraout_\w+|(?:_?DAT|PTR|uRam|lRam|iRam)_[0-9a-fA-F]+|[a-z]Stack_\w+|FUN_[0-9a-fA-F]+)\b|\[[A-Za-z_]\w*\+0x|\w\+0x[0-9a-fA-F]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex UnnamedToken();

    [GeneratedRegex(@"^(?<a>(?:[^=!<>]|->)+?)\s*(?<op>==|!=|<=|>=|<|>)\s*(?<b>(?:[^=!<>]|->)+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Comparison();

    [GeneratedRegex(@"'\\0'", RegexOptions.CultureInvariant)]
    private static partial Regex NulChar();

    [GeneratedRegex(@"(?<![\w:])([A-Za-z_][\w:]*(?:<[^()]*>)?)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CallHead();

    [GeneratedRegex(@"\bhas\(|\bvalid\([\w:]+::|::\w+\.[A-Za-z_]\w*|\b\w+::\w+\(\)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedTerm();

    /// <summary>Whether a rendered condition names a component, a field of a known structure or a named call's answer.</summary>
    public static bool Names(string term) => NamedTerm().IsMatch(term);

    /// <summary>Whether a rendered condition still holds a decompiler's local, an unnamed offset or a global.</summary>
    public static bool HasUnnamed(string term) => UnnamedToken().IsMatch(term);

    [GeneratedRegex(@"^\(?\s*[a-zA-Z]{0,4}Var\d+\s*!=\s*(?:[a-zA-Z]{0,4}Var\d+|local_\w+)\s*\)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Iterator();

    /// <summary>The text with comments, string and character literals blanked (same length), so
    /// braces and parentheses can be counted.</summary>
    public static string Mask(string code)
    {
        var b = new StringBuilder(code);
        for (int i = 0; i < b.Length; i++)
        {
            char c = b[i];
            if (c == '/' && i + 1 < b.Length && b[i + 1] == '*')
            {
                int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? b.Length : end + 2;
                for (int k = i; k < end; k++)
                    if (b[k] != '\n') b[k] = ' ';
                i = end - 1;
            }
            else if (c == '/' && i + 1 < b.Length && b[i + 1] == '/')
            {
                while (i < b.Length && b[i] != '\n') b[i++] = ' ';
            }
            else if (c is '"' or '\'')
            {
                int k = i + 1;
                for (; k < b.Length && b[k] != c && b[k] != '\n'; k++)
                    if (b[k] == '\\') k++;
                // a character literal keeps a placeholder so that '\0' tests still read as a comparison
                for (int m = i + 1; m < Math.Min(k, b.Length); m++) b[m] = c == '\'' ? '0' : ' ';
                i = Math.Min(k, b.Length - 1);
            }
        }
        return b.ToString();
    }

    /// <summary>Where the calls of <paramref name="name"/> start in <paramref name="masked"/>, in text order.</summary>
    public static List<int> Calls(string masked, string name)
    {
        var at = new List<int>();
        if (name.Length == 0) return at;
        // Ghidra breaks a long name after "::" onto the next line
        string pattern = @"(?<![\w:])" + string.Join(@"::\s*", name.Split("::").Select(Regex.Escape)) + @"(?![\w<:])\s*\(";
        foreach (Match m in Regex.Matches(masked, pattern, RegexOptions.CultureInvariant))
            at.Add(m.Index);
        return at;
    }

    sealed class Frame
    {
        public string Kind = "body";
        public int Start;
        public List<string> Conds = [];
        // a while (true) loop: what holds after it is the condition of its one break
        public bool Forever;
        public List<(string Text, int Pos)> Breaks = [];
        public List<(string Text, int Pos)> Exits = [];
        public string? Case;
        public int CasePos;
        public string? Switch;
        public bool Label;
        // the if-chain that ended at this level, for an else that follows
        public List<string>? Chain;
        // this if-frame's own chain: the conditions of the if and else-ifs up to it
        public List<string>? Own;
        // an if of this level whose block only returns (or breaks): its negation holds after it
        public string? ExitCond;
        public int BodyStart;
    }

    /// <summary>The guard of the <paramref name="occurrence"/>-th call (0-based, text order) of
    /// <paramref name="callName"/> in the function's pseudo-code.</summary>
    public static GuardResult Find(string code, string callName, int occurrence, GuardNames names) => Find(code, Mask(code), callName, occurrence, names);

    /// <summary>The same, with the text already masked (<see cref="Mask"/>).</summary>
    public static GuardResult Find(string code, string masked, string callName, int occurrence, GuardNames names)
    {
        var calls = Calls(masked, callName);
        if (calls.Count == 0) return GuardResult.NotExtracted("the call is not in the pseudo-code under that name");
        int p = calls[Math.Clamp(occurrence, 0, calls.Count - 1)];
        return At(code, masked, p, names);
    }

    /// <summary>The guard of whatever starts at position <paramref name="p"/> of the pseudo-code.</summary>
    public static GuardResult At(string code, string masked, int p, GuardNames names)
    {
        int body = masked.IndexOf("\n{", StringComparison.Ordinal);
        if (body < 0 || body > p) return GuardResult.NotExtracted("no function body");
        var stack = new List<Frame> { new() { Kind = "body", Start = body + 1 } };
        int stmt = body + 2, paren = 0;
        var closed = new List<(int Start, int End)>();
        int i = body + 2;
        for (; i < p; i++)
        {
            char c = masked[i];
            if (c == '(') { paren++; continue; }
            if (c == ')') { paren = Math.Max(0, paren - 1); continue; }
            if (paren > 0) continue;
            var top = stack[^1];
            if (c == '\n')
            {
                // a label or a case at the start of the next line
                int eol = masked.IndexOf('\n', i + 1);
                var line = masked.AsSpan(i + 1, (eol < 0 ? masked.Length : eol) - i - 1).ToString();
                var cs = Case().Match(line);
                if (cs.Success && top.Kind == "switch")
                {
                    top.Case = cs.Groups["v"].Success ? $"{top.Switch} == {cs.Groups["v"].Value.Trim()}" : $"{top.Switch} matches no case";
                    top.Exits.Clear();
                    top.CasePos = i;
                    stmt = i + 1 + line.IndexOf(':', StringComparison.Ordinal) + 1;
                }
                else if (Label().Match(line) is { Success: true } lb && lb.Groups[1].Value != "default" && !line.Contains("::", StringComparison.Ordinal))
                {
                    top.Label = true;
                    stmt = i + 1 + line.IndexOf(':', StringComparison.Ordinal) + 1;
                }
                continue;
            }
            if (c == '{')
            {
                string header = Squash(masked[stmt..i]);
                string original = Squash(code[stmt..i]);
                var frame = new Frame { Start = i, BodyStart = i + 1 };
                bool chainContinues = false;
                if (header.StartsWith("else if", StringComparison.Ordinal))
                {
                    var cond = Paren(original, original.IndexOf("if", StringComparison.Ordinal) + 2);
                    frame.Kind = "if";
                    frame.Conds.AddRange((top.Chain ?? []).Select(Not));
                    frame.Conds.Add(cond);
                    frame.Own = [.. top.Chain ?? [], cond];
                    chainContinues = true;
                }
                else if (header.StartsWith("else", StringComparison.Ordinal))
                {
                    frame.Kind = "else";
                    frame.Conds.AddRange((top.Chain ?? []).Select(Not));
                }
                else if (header.StartsWith("if", StringComparison.Ordinal))
                {
                    var cond = Paren(original, 2);
                    frame.Kind = "if";
                    frame.Conds.Add(cond);
                    frame.Own = [cond];
                }
                else if (header.StartsWith("while", StringComparison.Ordinal))
                {
                    frame.Kind = "loop";
                    var cond = Paren(original, 5);
                    frame.Conds.Add(cond);
                    frame.Forever = cond is "true" or "1";
                }
                else if (header.StartsWith("for", StringComparison.Ordinal))
                {
                    frame.Kind = "loop";
                    var parts = Paren(original, 3).Split(';');
                    if (parts.Length == 3 && parts[1].Trim().Length > 0) frame.Conds.Add(parts[1].Trim());
                }
                else if (header.StartsWith("do", StringComparison.Ordinal))
                    frame.Kind = "loop";
                else if (header.StartsWith("switch", StringComparison.Ordinal))
                {
                    frame.Kind = "switch";
                    frame.Switch = Paren(original, 6);
                }
                else
                    frame.Kind = "block";
                Settle(top, header, chainContinues, i);
                stack.Add(frame);
                stmt = i + 1;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count == 1) break;
                var done = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                closed.Add((done.Start, i));
                var parent = stack[^1];
                parent.Chain = done.Kind == "if" ? done.Own : null;
                parent.ExitCond = null;
                // an if whose block only leaves: what follows it holds the negation
                if (done.Kind == "if" && done.Own is { Count: 1 } && OnlyLeaves(masked[done.BodyStart..i]))
                    parent.ExitCond = done.Own[0];
                if (done.Forever && done.Breaks.Count == 1)
                    parent.Exits.Add(done.Breaks[0]);
                stmt = i + 1;
                continue;
            }
            if (c == ';')
            {
                string header = Squash(masked[stmt..i]);
                string original = Squash(code[stmt..i]);
                Settle(top, header, false, stmt);
                // a braceless "if (c) return;" / "if (c) break;" / "if (c) goto LAB;" ahead of the call
                if (header.StartsWith("if", StringComparison.Ordinal))
                {
                    int close = Close(original, original.IndexOf('(', StringComparison.Ordinal));
                    string rest = close < 0 ? "" : original[(close + 1)..].Trim();
                    string cond = Paren(original, 2);
                    if (rest.StartsWith("return", StringComparison.Ordinal) || rest.StartsWith("break", StringComparison.Ordinal) || rest.StartsWith("continue", StringComparison.Ordinal))
                    {
                        top.Exits.Add((Not(cond), stmt));
                        if (rest.StartsWith("break", StringComparison.Ordinal) && top.Kind == "loop") top.Breaks.Add((cond, i));
                    }
                    else if (rest.StartsWith("goto ", StringComparison.Ordinal))
                    {
                        // a jump forward past the call skips it; a jump back is a loop
                        string label = rest[5..].Trim().TrimEnd(';').Trim();
                        int target = masked.IndexOf("\n" + label + ":", StringComparison.Ordinal);
                        if (target > p) top.Exits.Add((Not(cond), stmt));
                    }
                }
                stmt = i + 1;
            }
        }

        var terms = new List<(string Text, int Pos)>();
        bool partial = false;
        foreach (var f in stack)
        {
            // a loop over a list (two unnamed pointers compared) says "for each item", not its bounds
            if (f.Kind == "loop")
                foreach (var c in f.Conds)
                    terms.Add((Iterator().IsMatch(Squash(c)) ? "for each item of a list" : c, f.Start));
            else
                terms.AddRange(f.Conds.Select(c => (c, f.Start)));
            if (f.Case != null) terms.Add((f.Case, f.CasePos));
            terms.AddRange(f.Exits);
            partial |= f.Label;
        }
        // the call sits inside the condition of an if or a loop: the conjuncts before it hold
        string open = Squash(code[stmt..p]);
        if (open.StartsWith("if", StringComparison.Ordinal) || open.StartsWith("while", StringComparison.Ordinal) || open.StartsWith("else if", StringComparison.Ordinal))
            terms.AddRange(Before(open[(open.IndexOf('(', StringComparison.Ordinal) + 1)..]).Select(c => (c, stmt)));

        var env = Environment(code, masked, body, p, names);
        // an assignment inside a block that closed before the call does not hold on the way to it
        env.Closed.AddRange(closed);
        var rendered = terms.Select(t => Render(t.Text, t.Pos, env, names)).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        // x == 4 says more than x != 0, x != 1 before it (an else-chain over one value)
        var equal = rendered.Select(r => Regex.Match(r, @"^(.+) == ([\w']+)$", RegexOptions.CultureInvariant)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        rendered.RemoveAll(r => Regex.Match(r, @"^(.+) != ([\w']+)$", RegexOptions.CultureInvariant) is { Success: true } m && equal.Contains(m.Groups[1].Value));
        int unnamed = rendered.Count(t => UnnamedToken().IsMatch(t));
        if (rendered.Count == 0)
            return partial ? GuardResult.NotExtracted("reached only through a jump the reader does not follow") : new GuardResult("always", [], false, 0, null);
        return new GuardResult("extracted", rendered, partial, unnamed, null);
    }

    /// <summary>A statement at this level ends the if-chain and, past an if that only leaves, adds its negation.</summary>
    static void Settle(Frame top, string header, bool chainContinues, int pos)
    {
        if (header.StartsWith("else", StringComparison.Ordinal))
        {
            if (!chainContinues) top.Chain = null;
            top.ExitCond = null;
            return;
        }
        if (top.ExitCond != null)
        {
            top.Exits.Add((Not(top.ExitCond), pos));
            top.ExitCond = null;
        }
        top.Chain = null;
    }

    static bool OnlyLeaves(string block)
    {
        var s = Squash(block);
        return s.Length > 0 && s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } parts
               && parts[^1] is var last && (last.StartsWith("return", StringComparison.Ordinal) || last is "break" or "continue")
               && parts.Length <= 3;
    }

    static string Squash(string s)
    {
        var b = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { space = b.Length > 0; continue; }
            if (space) b.Append(' ');
            space = false;
            b.Append(c);
        }
        return b.ToString();
    }

    /// <summary>The index of the parenthesis that closes the one at <paramref name="open"/>, or -1.</summary>
    static int Close(string s, int open)
    {
        if (open < 0) return -1;
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>The text inside the first parentheses at or after <paramref name="from"/>.</summary>
    static string Paren(string s, int from)
    {
        int open = s.IndexOf('(', Math.Max(0, from));
        int close = Close(s, open);
        return open < 0 ? "" : close < 0 ? s[(open + 1)..].Trim() : s[(open + 1)..close].Trim();
    }

    /// <summary>The complete conjuncts before the end of an open condition: those joined by &amp;&amp;
    /// to what is still open at the call.</summary>
    static List<string> Before(string cond)
    {
        var conj = new List<List<string>> { new() };
        var starts = new List<int> { 0 };
        for (int i = 0; i < cond.Length; i++)
        {
            char c = cond[i];
            if (c == '(') { conj.Add([]); starts.Add(i + 1); }
            else if (c == ')' && conj.Count > 1) { conj.RemoveAt(conj.Count - 1); starts.RemoveAt(starts.Count - 1); }
            else if (c == '&' && i + 1 < cond.Length && cond[i + 1] == '&')
            {
                conj[^1].Add(cond[starts[^1]..i].Trim());
                starts[^1] = i + 2;
                i++;
            }
            else if (c == '|' && i + 1 < cond.Length && cond[i + 1] == '|')
            {
                // an alternative: what came before at this level is not required
                conj[^1].Clear();
                starts[^1] = i + 2;
                i++;
            }
            else if (c == ',' )
            {
                // a comma expression (x = f(), x != 0): its left side is no condition
                starts[^1] = i + 1;
            }
        }
        return [.. conj.SelectMany(l => l).Where(t => t.Length > 0 && !IsAssignment(t))];
    }

    /// <summary>The left sides of comma expressions, (x = f(), x != 0) -> (x != 0): an assignment is no condition.</summary>
    static string StripCommaAssignments(string t)
    {
        for (int guard = 0; guard < 20; guard++)
        {
            var m = Regex.Match(t, @"(?<![\w=!<>])([A-Za-z_]\w*) = (?!=)", RegexOptions.CultureInvariant);
            if (!m.Success) return t;
            int depth = 0, end = -1;
            for (int i = m.Index + m.Length; i < t.Length; i++)
            {
                if (t[i] == '(') depth++;
                else if (t[i] == ')') { if (depth == 0) break; depth--; }
                else if (t[i] == ',' && depth == 0) { end = i; break; }
            }
            // an assignment that is not the left of a comma stays as it is (the condition is its value)
            t = end < 0 ? t[..m.Index] + t[(m.Index + m.Length)..] : t[..m.Index] + t[(end + 1)..].TrimStart();
        }
        return t;
    }

    /// <summary>An assignment (x = ...), not a comparison.</summary>
    static bool IsAssignment(string t)
    {
        int depth = 0;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '=' && depth == 0)
            {
                bool comparison = (i + 1 < t.Length && t[i + 1] == '=') || (i > 0 && t[i - 1] is '=' or '!' or '<' or '>');
                if (!comparison) return true;
                if (i + 1 < t.Length && t[i + 1] == '=') i++;
            }
        }
        return false;
    }

    static string Not(string cond)
    {
        var t = cond.Trim();
        while (t.StartsWith('(') && Close(t, 0) == t.Length - 1) t = t[1..^1].Trim();
        if (t.StartsWith("not (", StringComparison.Ordinal) && Close(t, 4) == t.Length - 1) return t[5..^1];
        if (!t.Contains("&&", StringComparison.Ordinal) && !t.Contains("||", StringComparison.Ordinal) && Comparison().Match(t) is { Success: true } m)
        {
            string op = m.Groups["op"].Value switch { "==" => "!=", "!=" => "==", "<" => ">=", ">=" => "<", ">" => "<=", _ => ">" };
            return $"{m.Groups["a"].Value.Trim()} {op} {m.Groups["b"].Value.Trim()}";
        }
        if (t.StartsWith('!') && !t.StartsWith("!=", StringComparison.Ordinal)) return t[1..].Trim();
        return $"not ({t})";
    }

    /// <summary>What the locals hold at the call: a component got, added or tested by a helper, or a
    /// pointer to a structure the declarations or the signature name.</summary>
    sealed record Env(Dictionary<string, string> Component, Dictionary<string, string> Has, Dictionary<string, string> Struct, List<(int Pos, string Var, string? Rhs)> Alias)
    {
        /// <summary>Blocks closed before the call: an assignment inside one holds only for a condition inside it too.</summary>
        public List<(int Start, int End)> Closed { get; } = [];
    }

    [GeneratedRegex(@"^[ \t]*([A-Za-z_]\w*) = ([^;{}=]+);", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SimpleAssignment();

    [GeneratedRegex(@"[A-Za-z_]\w*\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CallIn();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*=\s*(?:\([^()]*\)\s*)*([A-Za-z_][\w:]*(?:<[^;{}]*?>)?)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^\s*(?:const\s+)?([A-Za-z_][\w:<>]*?)\s*(\*+)\s*([A-Za-z_]\w*)\s*(?:\[\w*\])?;", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"(?:^|[(,])\s*(?:const\s+)?([A-Za-z_][\w:<>]*?)\s*(\*+)\s*([A-Za-z_]\w*)\s*(?=[,)])", RegexOptions.CultureInvariant)]
    private static partial Regex Parameter();

    static readonly HashSet<string> Builtin = new(StringComparer.Ordinal)
    {
        "void", "char", "uchar", "byte", "short", "ushort", "int", "uint", "long", "ulong", "longlong", "ulonglong", "float", "double", "bool",
        "undefined", "undefined1", "undefined2", "undefined4", "undefined8", "code", "wchar_t", "wchar16", "char16_t", "word", "dword", "qword",
    };

    static Env Environment(string code, string masked, int body, int p, GuardNames names)
    {
        var component = new Dictionary<string, string>(StringComparer.Ordinal);
        var calls = new List<(int Pos, string Var, string Rhs)>();
        var has = new Dictionary<string, string>(StringComparer.Ordinal);
        var structs = new Dictionary<string, string>(StringComparer.Ordinal);
        void Pointer(string type, string var)
        {
            type = type.Replace("_const_", "", StringComparison.Ordinal).Replace("_const", "", StringComparison.Ordinal);
            // a template (ID<...>, Array<...>) is a container, not a structure of the record
            if (!Builtin.Contains(type) && !type.Contains('<', StringComparison.Ordinal)) structs[var] = names.FullName(type);
        }
        // the signature's parameters, then the declarations at the top of the body
        int sigOpen = masked.LastIndexOf('(', body);
        int sigClose = masked.LastIndexOf(')', body);
        if (sigOpen >= 0 && sigClose > sigOpen)
            foreach (Match m in Parameter().Matches(code[sigOpen..(sigClose + 1)]))
                Pointer(m.Groups[1].Value, m.Groups[3].Value);
        int declEnd = masked.IndexOf("\n  \n", body, StringComparison.Ordinal);
        if (declEnd < 0 || declEnd > p) declEnd = Math.Min(p, masked.Length);
        foreach (Match m in Declaration().Matches(code[body..declEnd]))
            Pointer(m.Groups[1].Value, m.Groups[3].Value);
        // the latest assignment from a helper before the call wins
        foreach (Match m in Assignment().Matches(masked[..p]))
        {
            string callee = Squash(code.Substring(m.Groups[2].Index, m.Groups[2].Length));
            if (!names.Helpers.TryGetValue(callee, out var h))
            {
                // a local that holds what a named function answered stands for the call
                if (!callee.StartsWith("FUN_", StringComparison.Ordinal) && !callee.StartsWith("thunk_", StringComparison.Ordinal) && callee.Contains("::", StringComparison.Ordinal))
                {
                    var parts = callee.Split("::");
                    calls.Add((m.Index, m.Groups[1].Value, string.Join("::", parts.TakeLast(2)) + "()"));
                }
                continue;
            }
            string var = m.Groups[1].Value;
            component.Remove(var);
            has.Remove(var);
            if (h.Family is "get" or "add") component[var] = h.Type;
            else if (h.Family == "has") has[var] = h.Type;
        }
        // a local that only copies a field of a known structure: the field stands for it
        var alias = new List<(int Pos, string Var, string? Rhs)>();
        foreach (Match m in SimpleAssignment().Matches(masked[body..p]))
        {
            string var = m.Groups[1].Value;
            string rhs = Squash(code.Substring(body + m.Groups[2].Index, m.Groups[2].Length));
            string bare = ValueCast().Replace(PointerCast().Replace(rhs, ""), "");
            if (bare.Length > 70 || CallIn().IsMatch(bare)) { alias.Add((body + m.Index, var, null)); continue; }
            bool known = Arrow().Matches(bare).Any(a => structs.ContainsKey(a.Groups[1].Value) || component.ContainsKey(a.Groups[1].Value))
                         || Deref().Matches(bare).Any(a => structs.ContainsKey(a.Groups[1].Value) || component.ContainsKey(a.Groups[1].Value));
            alias.Add((body + m.Index, var, known && !bare.Contains(var, StringComparison.Ordinal) ? bare : null));
        }
        foreach (var (pos, var, rhs) in calls) alias.Add((pos, var, rhs));
        alias.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        return new Env(component, has, structs, alias);
    }

    /// <summary>A condition over the names the pseudo-code and the record know.</summary>
    static string Render(string cond, int pos, Env env, GuardNames names)
    {
        string t = StripCommaAssignments(Squash(cond).Replace(":: ", "::", StringComparison.Ordinal));
        // what each local held where the condition stands: its last assignment before it
        var held = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var a in env.Alias)
            if (a.Pos < pos && !env.Closed.Any(c => c.Start < a.Pos && a.Pos < c.End && !(c.Start < pos && pos < c.End))) held[a.Var] = a.Rhs;
        foreach (var (var, rhs) in held.Where(h => h.Value != null).Select(h => (h.Key, h.Value!)))
            t = Regex.Replace(t, $@"(?<![\w.>])\b{Regex.Escape(var)}\b(?!\s*(?:->|\.|\[))", rhs.Contains(' ', StringComparison.Ordinal) ? "(" + rhs + ")" : rhs, RegexOptions.CultureInvariant);
        // the helpers called inline
        for (int from = 0; from < t.Length;)
        {
            var call = CallHead().Match(t, from);
            if (!call.Success) break;
            int open = call.Index + call.Length - 1;
            int close = Close(t, open);
            if (close < 0 || !names.Helpers.TryGetValue(call.Groups[1].Value, out var h))
            {
                from = call.Index + call.Length;
                continue;
            }
            string token = h.Family == "has" ? $"has({h.Type})" : h.Family is "get" or "add" ? $"get({h.Type})" : $"{h.Family}({h.Type})";
            t = t[..call.Index] + token + t[(close + 1)..];
            from = call.Index + token.Length;
        }
        t = TypedZero().Replace(t, "0");
        t = PointerCast().Replace(t, "");
        t = ValueCast().Replace(t, "");
        t = NulChar().Replace(t, "0");
        // a field of a component or a structure: by its name in the record, else by its offset
        t = Deref().Replace(t, m =>
        {
            string var = m.Groups[1].Value;
            long offset = m.Groups[2].Value.StartsWith("0x", StringComparison.Ordinal)
                ? long.Parse(m.Groups[2].Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            string? type = env.Component.GetValueOrDefault(var) ?? env.Struct.GetValueOrDefault(var);
            if (type == null) return $"[{var}+0x{offset:x}]";
            return names.Fields.TryGetValue(type, out var fields) && fields.TryGetValue(offset, out var field) ? $"{type}.{field}" : $"{type}+0x{offset:x}";
        });
        t = Arrow().Replace(t, m =>
        {
            string var = m.Groups[1].Value;
            string? type = env.Component.GetValueOrDefault(var) ?? env.Struct.GetValueOrDefault(var);
            return type == null ? m.Value : $"{type}.{m.Groups[2].Value}";
        });
        // a local that holds a helper's answer
        foreach (var (var, type) in env.Has)
            t = Regex.Replace(t, $@"\b{Regex.Escape(var)}\b(?!\s*\.)", $"has({type})", RegexOptions.CultureInvariant);
        foreach (var (var, type) in env.Component)
            t = Regex.Replace(t, $@"(?<![\w.])\b{Regex.Escape(var)}\b(?!\s*[.\[])", $"get({type})", RegexOptions.CultureInvariant);
        // presence: get(T) != 0, has(T) != 0, has(T) == 0, ...
        t = Regex.Replace(t, @"(get|has)\(([^()]+)\)\s*!=\s*0(x0)?\b", "has($2)", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"(get|has)\(([^()]+)\)\s*==\s*0(x0)?\b", "not has($2)", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"!\s*(?:has|get)\(", "not has(", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"^get\(([^()]+)\)$", "has($1)", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"^not \(has\(([^()]+)\)\)$", "not has($1)", RegexOptions.CultureInvariant);
        // an entity handle that is set: (h >> 0x36) < 0x41 and h != the null handle
        t = Regex.Replace(t, @"\(?\(?([\w.:\[\]+*]+) >> 0x36\)? < 0x41\)?", "valid($1)", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"\(?0x40 < \(?([\w.:\[\]+*]+) >> 0x36\)\)?", "not valid($1)", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"\(?([\w.:\[\]+*]+) != (?:0xffc0000000000000|-0x40000000000000)\)?", "valid($1)", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"\(?([\w.:\[\]+*]+) == (?:0xffc0000000000000|-0x40000000000000)\)?", "not valid($1)", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"\bvalid\(([^()]+)\) && valid\(\1\)", "valid($1)", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"not valid\(([^()]+)\) \|\| not valid\(\1\)", "not valid($1)", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"&&", "and", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"\|\|", "or", RegexOptions.CultureInvariant);
        while (t.StartsWith('(') && Close(t, 0) == t.Length - 1) t = t[1..^1].Trim();
        // parentheses around a single name, and double negations
        t = Regex.Replace(t, @"(?<![\w>])\(([\w.:\[\]+]+)\)", "$1", RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"(?<![\w>])\(((?:not )?[\w:]+\([^()]*\))\)", "$1", RegexOptions.CultureInvariant);
        for (string was = ""; was != t;)
        {
            was = t;
            t = Regex.Replace(t, @"not \(?not (valid|has)\(([^()]+)\)\)?", "$1($2)", RegexOptions.CultureInvariant);
            t = Regex.Replace(t, @"not \((\w+\([^()]+\))\)", "not $1", RegexOptions.CultureInvariant);
        }
        while (t.StartsWith('(') && Close(t, 0) == t.Length - 1) t = t[1..^1].Trim();
        if (t is "true" or "1") return "";
        if (t.Contains(" or ", StringComparison.Ordinal) && !(t.StartsWith('(') && Close(t, 0) == t.Length - 1)) t = $"({t})";
        return t.Length > MaxTerm ? t[..(MaxTerm - 3)] + "..." : t;
    }
}
