using System.Globalization;
using System.Text.Json.Nodes;
using GateKit.Bridge;
using GateKit.Platform;

namespace GateKit.Stand;

/// <summary>How a copy answered when it was asked to cap itself.</summary>
public enum CapOutcome
{
    /// <summary>The game took the cap.</summary>
    Applied,

    /// <summary>The game answered, and said no: GateKit's Script Extender build is not loaded, or
    /// the engine's layout has moved and the frame cap refuses to write somewhere it does not
    /// recognise. This copy will not take a cap until something changes.</summary>
    Refused,

    /// <summary>Nobody answered: the copy is loading, closing, or has no bridge at all. Worth
    /// trying again rather than giving up on.</summary>
    Unreachable,
}

/// <summary>What one copy's frame cap is, as the game reports it.</summary>
/// <param name="Asked">Frames a second asked for; 0 is no cap.</param>
/// <param name="InForce">Frames a second actually in force.</param>
/// <param name="InBackground">The cap that applies while the window is not in front.</param>
/// <param name="HasFocus">Whether the engine believes its window is the one in front.</param>
/// <param name="Yields">Whether the engine's own background throttle is switched on.</param>
public sealed record FrameCapState(int Asked, int InForce, int InBackground, bool HasFocus, bool Yields)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Describe(InForce)} in force ({Describe(Asked)} asked for, {Describe(InBackground)} in the background"
        + $"; the window is {(HasFocus ? "in front" : "not in front")}, the engine's own throttle is "
        + $"{(Yields ? "on" : "off")})");

    static string Describe(int fps) => fps == 0 ? "no cap" : fps + " a second";
}

/// <summary>
/// How fast a copy of the game is allowed to draw. A copy that is not the window you are looking at
/// throttles itself to thirty frames a second - the engine does that on purpose, and GateKit feels
/// it more than anyone, because at most one of several copies can ever be in front and a mirrored
/// copy is never in front at all.
///
/// The same knob turned the other way is the thing that makes several copies affordable: the copy
/// being watched takes what it needs and the rest draw at a floor they do not care about. The deck
/// spends its budget through here; <c>gatekit window fps</c> is a person or an agent doing the same
/// by hand.
///
/// It goes through the bridge like everything else, so it needs no window and works on a copy that
/// is hidden, behind another, or in no deck at all. It needs GateKit's Script Extender build.
/// </summary>
public static class FrameCap
{
    /// <summary>Asking a game about itself should never hold anyone up for long; a copy drawing at
    /// a few frames a second answers on its own tick, so this is generous rather than tight.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>What that copy's cap is now, or null when the copy cannot say (no bridge, the stock
    /// Script Extender, or a build that cannot reach the engine's caps).</summary>
    public static FrameCapState? Read(StandSettings settings, IGamePlatform platform, string instance)
    {
        var answer = Ask(settings, platform, instance, "return GK.FrameCap()");
        return answer is JsonObject cap
            ? new FrameCapState(
                Number(cap, "frameCap"), Number(cap, "effectiveFrameCap"), Number(cap, "lostFocusFrameCap"),
                cap["hasFocus"]?.GetValue<bool>() ?? false, cap["yieldOnLostFocus"]?.GetValue<bool>() ?? true)
            : null;
    }

    /// <summary>Caps that copy at <paramref name="fps"/> frames a second whether its window is in
    /// front or not; 0 lifts the cap and the engine's background throttle with it. True when the
    /// game agreed.</summary>
    public static bool Set(StandSettings settings, IGamePlatform platform, string instance, int fps)
    {
        if (fps < 0) throw new StandException($"a frame cap cannot be negative: {fps}");
        var answer = Ask(settings, platform, instance,
            string.Create(CultureInfo.InvariantCulture, $"return GK.SetFrameCap({fps})"));
        return answer is JsonValue value && value.TryGetValue<bool>(out var ok) && ok;
    }

    /// <summary>Gives a copy back the frame rate it had before anyone capped it: our cap lifted,
    /// and the engine's own background throttle - whatever it was when the copy was first read -
    /// put back. A deck does this when a copy leaves it and when it closes, because a copy left
    /// uncapped in the background would draw flat out for nobody.</summary>
    public static bool Lift(StandSettings settings, IGamePlatform platform, string instance, int background)
    {
        var answer = Ask(settings, platform, instance, string.Create(CultureInfo.InvariantCulture,
            $"local ok = GK.SetFrameCap(0) and Ext.ClientRender.SetLostFocusFrameCap({background}); return ok"));
        return answer is JsonValue value && value.TryGetValue<bool>(out var ok) && ok;
    }

    /// <summary>The same as <see cref="Lift"/>, without caring whether it worked.</summary>
    public static void TryLift(StandSettings settings, IGamePlatform platform, string instance, int background)
    {
        try
        {
            Lift(settings, platform, instance, background);
        }
        catch (Exception e) when (e is StandException or IOException or TimeoutException or BridgeTimeoutException or BridgeLuaException)
        {
            // The copy is going anyway, or has no bridge: nothing left to give back.
        }
    }

    /// <summary>What that copy's cap is, or null when it cannot say - without throwing, for the
    /// deck's worker threads.</summary>
    public static FrameCapState? TryRead(StandSettings settings, IGamePlatform platform, string instance)
    {
        try
        {
            return Read(settings, platform, instance);
        }
        catch (Exception e) when (e is StandException or IOException or TimeoutException or BridgeTimeoutException or BridgeLuaException)
        {
            return null;
        }
    }

    /// <summary>Sets the cap without throwing, and says what happened. A deck asks every time its
    /// layout changes, where one copy busy starting up must not hold the others up - but a copy
    /// that will never take a cap is worth hearing about, because a budget that silently does
    /// nothing is the kind of thing nobody notices until their machine is slow.</summary>
    public static CapOutcome TrySet(StandSettings settings, IGamePlatform platform, string instance, int fps)
    {
        try
        {
            return Set(settings, platform, instance, fps) ? CapOutcome.Applied : CapOutcome.Refused;
        }
        catch (Exception e) when (e is StandException or IOException or TimeoutException or BridgeTimeoutException or BridgeLuaException)
        {
            return CapOutcome.Unreachable;
        }
    }

    static int Number(JsonObject cap, string key) =>
        cap[key] is JsonValue value && value.TryGetValue<double>(out var number) ? (int)number : 0;

    static JsonNode? Ask(StandSettings settings, IGamePlatform platform, string instance, string code)
    {
        var session = new Stand(settings with
        {
            Instance = string.Equals(instance, DeckRecord.InstalledGame, StringComparison.OrdinalIgnoreCase) ? null : instance,
        }, platform, TextWriter.Null).Session();
        if (session.State(Side.Client) is null)
            throw new StandException($"{instance}: the bridge is not answering in this copy, so its frame rate cannot be read or set");
        return session.ClientAsync(code, Timeout).GetAwaiter().GetResult();
    }
}
