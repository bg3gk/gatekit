namespace GateKit.Json;

/// <summary>
/// One thing wrong with a file: where (the file, its 1-based line and column, and the JSON pointer
/// of the value when the text parsed), which rule it broke (a short stable word, such as
/// <c>syntax</c>, <c>duplicate</c>, <c>type</c>, <c>required</c>, <c>enum</c>), and what was
/// expected, in a sentence. Printed as <c>file:line:column: what</c>.
/// </summary>
public sealed record JsonProblem(string Source, int Line, int Column, string Pointer, string Rule, string What)
{
    public override string ToString() => Line > 0 ? $"{Source}:{Line}:{Column}: {What}" : $"{Source}: {What}";
}

/// <summary>A file that cannot be taken as it is: the text does not parse, or the document does not
/// fit its schema. Nothing was read into the program and nothing was written.</summary>
public sealed class JsonProblemException : Exception
{
    /// <summary>How many problems the message lists before it says how many more there are.</summary>
    public const int Shown = 20;

    public IReadOnlyList<JsonProblem> Problems { get; }

    public JsonProblemException(IReadOnlyList<JsonProblem> problems)
        : base(Describe(problems)) => Problems = problems;

    public JsonProblemException(JsonProblem problem) : this([problem]) { }

    static string Describe(IReadOnlyList<JsonProblem> problems)
    {
        if (problems.Count == 0) return "no problems";
        var lines = problems.Take(Shown).Select(p => p.ToString()).ToList();
        if (problems.Count > Shown) lines.Add($"... and {problems.Count - Shown} more");
        return string.Join("\n", lines);
    }
}
