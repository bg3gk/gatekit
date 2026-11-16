using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GateKit.Sweep;

/// <summary>What carries a step's thing into the game: a stats entry of some kind, or a direct call.</summary>
/// <param name="Kind">status (ApplyStatus), passive (AddPassive), item (created and equipped), spell (cast), direct (AddBoosts).</param>
/// <param name="Name">The stats entry, or for direct the boost text itself.</param>
/// <param name="Field">The field of the entry the thing was read from (Boosts, OnApplyFunctors, SpellFlags...).</param>
/// <param name="Shipping">The entry is not a test, debug or cheat entry by its name.</param>
public sealed record Carrier(string Kind, string Name, string? Field = null, bool Shipping = true);

/// <summary>A second way to the same thing, run when the first lit none of the expected types.</summary>
public sealed record Fallback(Carrier Carrier, string How, string? Undo, string ReachedBy);

/// <summary>
/// One step of a sweep plan. <see cref="How"/> and <see cref="Undo"/> are server Lua with two
/// locals set: <c>D</c>, the dummy the step is done to, and <c>T</c>, a second dummy for spells
/// that need a target (docs/contracts/cli.md, "sweep").
/// </summary>
public sealed record SweepStep
{
    public required string Id { get; init; }
    public required string What { get; init; }
    public required Carrier Carrier { get; init; }
    public required string How { get; init; }
    public string? Undo { get; init; }
    /// <summary>cheat (the cause is staged) or play_with_cheat_setup (staged setup, then a player's own action).</summary>
    public required string ReachedBy { get; init; }
    /// <summary>The component types this step should make appear, when the plan can tell.</summary>
    public IReadOnlyList<string>? Expects { get; init; }
    /// <summary>Seconds to let pass after <see cref="How"/> before asking what came alive.</summary>
    public double Settle { get; init; } = 1.0;
    /// <summary>The step changes its dummy beyond what <see cref="Undo"/> takes back: fresh dummies after it.</summary>
    public bool Fresh { get; init; }
    public Fallback? Fallback { get; init; }
    /// <summary>Server Lua run after the settle, answering true when what the step meant to happen did
    /// (a cast cast, a surface under the dummy), or a string saying what did not; null: no check.</summary>
    public string? Check { get; init; }
    /// <summary>The never-seen types whose leads this step tries (a `leads` plan); each is reported
    /// back with `leads report` when the run ends.</summary>
    public IReadOnlyList<string>? Leads { get; init; }
    /// <summary>The lead's instrument the try is reported under (osiris, stage, sweep).</summary>
    public string? Instrument { get; init; }
    /// <summary>Why a step that cannot be tried is in the plan at all: it is reported blocked, not run.</summary>
    public string? Blocked { get; init; }
}

/// <summary>A plan: the steps of one kind, in a fixed order, built from the data index alone.</summary>
public sealed record SweepPlan
{
    public required string Kind { get; init; }
    /// <summary>When the data index the plan was built from was built (its meta table), so two plans compare.</summary>
    public string? Data { get; init; }
    /// <summary>How many things of the kind the data names, and how many steps carry them.</summary>
    public JsonObject? Counts { get; init; }
    public required IReadOnlyList<SweepStep> Steps { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IndentSize = 1,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json).ReplaceLineEndings("\n") + "\n";

    public static SweepPlan Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<SweepPlan>(File.ReadAllText(path), Json)
                ?? throw new SweepException($"{path} holds no plan");
        }
        catch (JsonException e)
        {
            throw new SweepException($"{path} is not a sweep plan ({e.Message}); `gatekit sweep plan <kind> --out {path}` writes one");
        }
    }
}

/// <summary>An error of the sweep that is the user's to fix, said as one line.</summary>
public sealed class SweepException(string message) : Exception(message);
