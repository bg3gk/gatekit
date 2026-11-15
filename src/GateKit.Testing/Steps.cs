using System.Diagnostics;
using System.Globalization;
using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.Testing;

/// <summary>One named step of a test: its class, how long it took, how it ended.</summary>
public sealed record StepRecord(string Name, string Class)
{
    /// <summary>pass, fail or unmet; null while it runs.</summary>
    public string? Outcome { get; set; }
    public TimeSpan Took { get; set; }
    public string? Error { get; set; }
    /// <summary>Seconds of the step spent inside waits of `game` (wait, wait_row, wait_attention,
    /// answer, wait_state, sleep).</summary>
    public double Waits { get; set; }
    /// <summary>Seconds of the step spent in the game's own answers to its chunks, outside any wait.</summary>
    public double Answers { get; set; }
    /// <summary>The case it ran in; null in a test without cases.</summary>
    public string? Case { get; init; }
}

/// <summary>
/// The named steps and preconditions of one test run (docs/contracts/lua-api.md, "Steps"):
/// <c>step(name, class, fn)</c> runs fn as a step of class fair, cheat or observation, and every
/// command it sends to the game carries the class, so a verb the class does not allow is refused
/// there; <c>need(condition, why)</c> ends the test as unmet - neither a pass nor a failure. A
/// step's begin and end are rows of the game's event log (kinds step and step_ended), so whatever
/// else the log holds between them belongs to the step.
/// </summary>
public sealed class Steps
{
    public static readonly IReadOnlySet<string> Classes = new HashSet<string>(StringComparer.Ordinal) { "fair", "cheat", "observation" };

    readonly List<StepRecord> _steps = [];
    StepRecord? _current;

    /// <summary>The steps run so far, in order.</summary>
    public IReadOnlyList<StepRecord> All => _steps;

    /// <summary>The class of the step running now, or null outside any step.</summary>
    public string? Class => _current?.Class;

    /// <summary>Why the test (or the case running) cannot run here, once a need() was not met.</summary>
    public string? Unmet { get; private set; }

    /// <summary>The cases of the test (empty: it has none). A step outside a case, and a step named as
    /// a case, are refused in a test that has them.</summary>
    public IReadOnlySet<string> CaseNames { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The case running now, or null.</summary>
    public string? Case { get; private set; }

    /// <summary>Enters a case: its steps carry its name, and its unmet is its own.</summary>
    public void BeginCase(string name)
    {
        if (Case is not null) throw new ScriptRuntimeException($"case {name} inside case {Case}: cases do not nest");
        Case = name;
        Unmet = null;
    }

    /// <summary>Leaves the case; answers its unmet, if any.</summary>
    public string? EndCase()
    {
        var unmet = Unmet;
        Case = null;
        Unmet = null;
        return unmet;
    }

    /// <summary>Writes a step's row into the game's event log: (name, class, outcome or null for its
    /// begin, ms, error). A row that cannot be written leaves the step as it is.</summary>
    public Action<string, string, string?, double, string?>? Mark { get; set; }

    /// <summary>The test's time in waits and in the game's answers so far (GameApi.Spent): read at a
    /// step's begin and end, the difference is the step's.</summary>
    public Func<(double Waits, double Answers)>? Spent { get; set; }

    /// <summary>Refuses a host function of class <paramref name="verbClass"/> that the step running now
    /// does not allow, as the game refuses a verb: "a fair step called the cheat game.reset".</summary>
    public void Check(string verb, string verbClass)
    {
        if (Class is not { } scope || scope == "cheat") return;
        var allowed = scope == "fair" ? verbClass is "fair" or "observation" : verbClass == "observation";
        if (!allowed) throw new ScriptRuntimeException($"{Article(scope)} {scope} step called the {verbClass} {verb}");
    }

    static string Article(string word) => word == "observation" ? "an" : "a";

    /// <summary>Puts <c>step</c> and <c>need</c> into a test's globals.</summary>
    public void Install(Script script)
    {
        script.Globals["step"] = DynValue.NewCallback(Step, "step");
        script.Globals["need"] = DynValue.NewCallback(Need, "need");
    }

    DynValue Need(ScriptExecutionContext context, CallbackArguments args)
    {
        var condition = args[0];
        if (condition.CastToBool()) return condition;
        var why = args[1].Type == DataType.String ? args[1].String : "a precondition";
        Unmet ??= why;
        throw new ScriptRuntimeException("unmet: " + why);
    }

    DynValue Step(ScriptExecutionContext context, CallbackArguments args)
    {
        if (args[0].Type != DataType.String || args[0].String.Length == 0)
            throw new ScriptRuntimeException("step(name, class, fn): a step has a name");
        var name = args[0].String;
        var cls = args[1].Type == DataType.String ? args[1].String : null;
        if (cls is null || !Classes.Contains(cls))
            throw new ScriptRuntimeException($"step {name}: the class is fair, cheat or observation, not {args[1].ToPrintString()}");
        if (args[2].Type is not (DataType.Function or DataType.ClrFunction))
            throw new ScriptRuntimeException($"step {name}: its body is a function");
        if (_current is not null) throw new ScriptRuntimeException($"step {name} inside step {_current.Name}: steps do not nest");
        if (CaseNames.Count > 0 && Case is null)
            throw new ScriptRuntimeException($"step {name}: a test with cases runs its steps inside them (case(name, fn), or the `before` its header names)");
        if (CaseNames.Contains(name)) throw new ScriptRuntimeException($"step {name}: a step is not named as a case of its test");
        var record = new StepRecord(name, cls) { Case = Case };
        _steps.Add(record);
        TryMark(name, cls, null, 0, null);
        _current = record;
        var before = Spent?.Invoke() ?? (0, 0);
        var watch = Stopwatch.StartNew();
        try
        {
            var result = context.Call(args[2]);
            record.Outcome = "pass";
            return result;
        }
        catch (InterpreterException problem)
        {
            var message = Flatten(problem.DecoratedMessage ?? problem.Message);
            if (Unmet is not null)
            {
                record.Outcome = "unmet";
                record.Error = Unmet;
                throw;
            }
            record.Outcome = "fail";
            record.Error = message;
            throw new ScriptRuntimeException($"step {name} ({cls}) failed: {message}");
        }
        finally
        {
            record.Took = watch.Elapsed;
            var after = Spent?.Invoke() ?? (0, 0);
            record.Waits = Math.Max(0, after.Waits - before.Waits);
            record.Answers = Math.Max(0, after.Answers - before.Answers);
            _current = null;
            TryMark(name, cls, record.Outcome ?? "fail", Math.Round(record.Took.TotalMilliseconds), record.Error);
        }
    }

    void TryMark(string name, string cls, string? outcome, double ms, string? error)
    {
        try { Mark?.Invoke(name, cls, outcome, ms, error); }
        catch (Exception e) when (e is InterpreterException or CopyException or IOException) { }
    }

    static string Flatten(string text) => text.Replace("\r", "").Replace('\n', ' ');

    /// <summary>The steps as the report gives them: "  step <name> (<class>) <outcome> <s> s".</summary>
    public static IEnumerable<string> Lines(IReadOnlyList<StepRecord> steps) => steps.Select(s => string.Create(CultureInfo.InvariantCulture,
        $"      step {s.Name} ({s.Class}) {s.Outcome ?? "not ended"} {s.Took.TotalSeconds:0.0} s")
        + (s.Waits >= 0.05 ? string.Create(CultureInfo.InvariantCulture, $" (waits {s.Waits:0.0} s)") : ""));
}
