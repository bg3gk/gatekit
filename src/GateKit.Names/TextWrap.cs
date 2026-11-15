using System.Text.RegularExpressions;

namespace GateKit.Names;

/// <summary>
/// Breaks a paragraph into lines no longer than a width, the way the generated header has always
/// been wrapped: at spaces and after the hyphen of a hyphenated word, a word longer than the line
/// cut where the line ends. The header is regenerated whole, so an unchanged field must come out
/// as the same lines every time or every regeneration is a diff of comments.
/// </summary>
static partial class TextWrap
{
    // whitespace | an em-dash between words | a word, up to its hyphen, its end, or an em-dash
    [GeneratedRegex("""([\t\n\x0b\x0c\r ]+|(?<=[\w!"'&.,?])-{2,}(?=\w)|[^\t\n\x0b\x0c\r ]+?(?:-(?:(?<=[^\d\W]{2}-)|(?<=[^\d\W]-[^\d\W]-))(?=[^\d\W]-?[^\d\W])|(?=[\t\n\x0b\x0c\r ]|\z)|(?<=[\w!"'&.,?])(?=-{2,}\w)))""")]
    private static partial Regex Chunks();

    [GeneratedRegex(@"[\t\n\x0b\x0c\r]")]
    private static partial Regex Blank();

    public static List<string> Wrap(string text, int width)
    {
        text = Blank().Replace(text.Replace("\t", "        "), " ");
        var chunks = Chunks().Split(text).Where(c => c.Length > 0).Reverse().ToList();   // a stack: the next chunk is last
        var lines = new List<string>();
        while (chunks.Count > 0)
        {
            var line = new List<string>();
            int length = 0;
            if (chunks[^1].Trim().Length == 0 && lines.Count > 0)
                chunks.RemoveAt(chunks.Count - 1);
            while (chunks.Count > 0 && length + chunks[^1].Length <= width)
            {
                length += chunks[^1].Length;
                line.Add(chunks[^1]);
                chunks.RemoveAt(chunks.Count - 1);
            }
            if (chunks.Count > 0 && chunks[^1].Length > width)
            {
                int left = width < 1 ? 1 : width - length;
                string chunk = chunks[^1];
                int end = left;
                if (chunk.Length > left && left > 0)
                {
                    int hyphen = chunk.LastIndexOf('-', left - 1);
                    if (hyphen > 0 && chunk[..hyphen].Any(c => c != '-'))
                        end = hyphen + 1;
                }
                line.Add(chunk[..end]);
                chunks[^1] = chunk[end..];
            }
            if (line.Count > 0 && line[^1].Trim().Length == 0)
                line.RemoveAt(line.Count - 1);
            if (line.Count > 0)
                lines.Add(string.Concat(line));
        }
        return lines;
    }
}
