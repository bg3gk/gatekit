using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>The Ghidra scripts in tools/ghidra, read as text: no Java compiler is needed. A raw
/// tab or line break inside a character or string literal makes javac refuse the script, and
/// Ghidra then skips it and the scripts after it while still ending with 0.</summary>
public sealed class GhidraScriptsTests
{
    static string[] Scripts()
    {
        var files = Directory.GetFiles(GhidraCommand.FindScripts(), "*.java");
        Assert.NotEmpty(files);
        return files;
    }

    /// <summary>Raw control characters inside Java literals: (line, what). Comments and text
    /// blocks (""" ... """, where a line break is allowed) are skipped.</summary>
    internal static List<(int Line, string What)> RawControlCharacters(string text)
    {
        var found = new List<(int, string)>();
        int line = 1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                line++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i + 1 < text.Length && text[i + 1] != '\n')
                    i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length - 2 : end;
                line += text.AsSpan(i, end + 2 - i).Count('\n');
                i = end + 1;
                continue;
            }
            if (c == '"' && string.CompareOrdinal(text, i, "\"\"\"", 0, 3) == 0)
            {
                int end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                end = end < 0 ? text.Length - 3 : end;
                line += text.AsSpan(i, end + 3 - i).Count('\n');
                i = end + 2;
                continue;
            }
            if (c is '"' or '\'')
            {
                char quote = c;
                for (i++; i < text.Length && text[i] != quote; i++)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                        continue;
                    }
                    if (text[i] < 0x20)
                    {
                        found.Add((line, $"U+{(int)text[i]:X4} inside a {(quote == '"' ? "string" : "character")} literal"));
                        if (text[i] == '\n')
                        {
                            line++;
                            // the literal ends here for javac; its closing quote on the next line is no new one
                            if (i + 1 < text.Length && text[i + 1] == quote)
                                i++;
                            break;
                        }
                    }
                }
            }
        }
        return found;
    }

    [Fact]
    public void NoScriptHoldsARawControlCharacterInALiteral()
    {
        var bad = new List<string>();
        foreach (var file in Scripts())
            foreach (var (line, what) in RawControlCharacters(File.ReadAllText(file)))
                bad.Add($"{Path.GetFileName(file)}:{line}: {what}");
        Assert.True(bad.Count == 0, "write \\t and \\n as escapes:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void TheScannerSeesTheMistakesOfApplyNames()
    {
        Assert.Equal([(2, "U+0009 inside a character literal")], RawControlCharacters("class A {\n  char t = '\t';\n}\n"));
        Assert.Equal([(1, "U+000A inside a character literal")], RawControlCharacters("char n = '\n';\nint x;"));
        Assert.Equal([(1, "U+0009 inside a string literal")], RawControlCharacters("String s = \"a\tb\";"));
        // escapes, comments, text blocks and a quote inside the other kind are fine
        Assert.Empty(RawControlCharacters("char t = '\\t'; char q = '\"'; String s = \"it's\\n\"; // a 'tab\there\n/* \"\t\" */ String b = \"\"\"\n  x\t\n  \"\"\";"));
    }

    [Fact]
    public void EveryScriptEndsWithTheLineTheCommandChecks()
    {
        foreach (var file in Scripts())
        {
            string name = Path.GetFileNameWithoutExtension(file);
            Assert.Contains($"println(\"{ScriptCheck.Marker}{name} done", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AScriptThatDidNotCompileOrNeverFinishedIsNamed()
    {
        const string ok = "INFO  ApplyNames.java> gatekit-script: ApplyNames done applied=1 failed=0 touched=2 (GhidraScript)\n"
                          + "INFO  ExportDecomp.java> gatekit-script: ExportDecomp done written=2 failed=0 (GhidraScript)\n";
        Assert.Empty(ScriptCheck.Problems(ok, ["ApplyNames", "ExportDecomp"]));
        // a javac warning is printed as ERROR and is no failure
        Assert.Empty(ScriptCheck.Problems("ERROR ApplyNames.java:72: warning: [removal] X has been deprecated\n" + ok, ["ApplyNames", "ExportDecomp"]));

        var problems = ScriptCheck.Problems("ApplyNames.java:31: error: unclosed character literal\n"
                                            + "ERROR REPORT SCRIPT ERROR:  ApplyNames.java : The class could not be found. It must be the public class\n",
                                            ["ApplyNames", "ApplyTypes", "ExportDecomp"]);
        Assert.Contains(problems, p => p.StartsWith("ApplyNames.java did not load or compile", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("ApplyTypes.java never printed its last line", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("ExportDecomp.java never printed its last line", StringComparison.Ordinal));
        Assert.Single(problems, p => p.StartsWith("ApplyNames", StringComparison.Ordinal));

        Assert.Equal(["ApplyNames", "ExportDecomp"], ScriptCheck.Asked(["-process", "x", "-preScript", "ApplyNames.java", "n.tsv", "-postScript", "ExportDecomp.java", "o"]));
    }
}
