using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Copies;

namespace GateKit.Testing;

/// <summary>
/// `gatekit test --trace`: one trace of the entity system a step (GK.Trace, docs/contracts/lua-api.md
/// 16.1), on both sides of the game, begun when the step begins and ended when it ends. Each step's
/// trace is written beside the test's results, as <c>trace/&lt;nn&gt;-&lt;step&gt;.json</c> in its
/// output folder: { step, case, outcome, server, client }, each side GK.Trace.End's answer (its
/// counts and the changes of the step); the event log holds each step's trace_step row (the trace
/// family), not the changes, so that a whole world's record does not push a test's own rows out.
/// A side without the tracer answers why, and the step runs as it would without --trace.
/// </summary>
public sealed class StepTraces(GameSession session, string outFolder)
{
    static readonly Side[] Sides = [Side.Server, Side.Client];
    int _count;

    /// <summary>The traces written so far, by path.</summary>
    public List<string> Written { get; } = [];

    /// <summary>A step begins: a trace on each side that answers, named after the step.</summary>
    public void Begin(string step)
    {
        foreach (var side in Sides)
        {
            if (session.State(side, GameSession.Fresh) is null) continue;
            try
            {
                session.LuaAsync(side.Name(), "local GK = Mods.GateKit.GK\nif GK.Trace.Status().on then GK.Trace.End() end\nreturn GK.Trace.Begin{ step = " + Quote(step) + ", quiet = true }",
                    TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is CopyException or BridgeLuaException or BridgeTimeoutException) { }
        }
    }

    /// <summary>A step ended: each side's trace ended and written to the step's file.</summary>
    public string? End(string step, string? inCase, string outcome)
    {
        var record = new JsonObject { ["step"] = step, ["case"] = inCase, ["outcome"] = outcome };
        foreach (var side in Sides)
        {
            if (session.State(side, GameSession.Fresh) is null) continue;
            try
            {
                record[side.Name()] = session.LuaAsync(side.Name(), "local GK = Mods.GateKit.GK\nreturn GK.Trace.End()", TimeSpan.FromSeconds(10))
                    .GetAwaiter().GetResult()?.DeepClone();
            }
            catch (Exception e) when (e is CopyException or BridgeLuaException or BridgeTimeoutException)
            {
                record[side.Name()] = new JsonObject { ["ok"] = false, ["error"] = e.Message };
            }
        }
        _count++;
        var path = Path.Combine(outFolder, "trace", FileName(_count, inCase, step));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, GateKit.Json.JsonText.Inline(record) + "\n", new UTF8Encoding(false));
            Written.Add(path);
            return path;
        }
        catch (IOException) { return null; }
    }

    /// <summary>The file of the n-th step's trace: its number, then its case and name as a slug.</summary>
    public static string FileName(int n, string? inCase, string step)
    {
        var name = (inCase is null ? "" : inCase + "-") + step;
        var slug = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            slug.Append(char.IsLetterOrDigit(c) ? c : slug.Length > 0 && slug[^1] != '-' ? '-' : '\0');
        var text = slug.ToString().Replace("\0", "", StringComparison.Ordinal).Trim('-');
        if (text.Length > 60) text = text[..60].TrimEnd('-');
        return n.ToString("00", CultureInfo.InvariantCulture) + "-" + (text.Length == 0 ? "step" : text) + ".json";
    }

    static string Quote(string text)
    {
        var level = 1;
        while (text.Contains("]" + new string('=', level) + "]", StringComparison.Ordinal)) level++;
        return "[" + new string('=', level) + "[" + text + "]" + new string('=', level) + "]";
    }
}
