using System.Diagnostics;

namespace GateKit.Bridge;

/// <summary>
/// A limit counted in the game's own ticks rather than the host's clock: how far the game has come
/// since the guard began. The progress is the server's ticks (its event-log head and heartbeat),
/// the game's own logic tick; without a server, the client's clock from its heartbeat (its frames
/// counted by their time, docs/contracts/bridge-protocol.md) - never the client's frames themselves,
/// which run at the renderer's rate. A side whose Lua state started again counts from zero
/// on, so a reload neither eats nor resets the limit. A game that stands still eats nothing of it;
/// a game that shows no progress for <see cref="BridgeClient.StallGuard"/> (or the limit, if
/// longer) is given up as hung - the one real-time guard. Where no side counts ticks (a bridge that
/// does not say them, no game) the limit is the host's clock.
/// </summary>
public sealed class TickGuard
{
    static readonly Side[] Sides = [Side.Server, Side.Client];

    readonly BridgeClient _bridge;
    readonly EventLogReader? _log;
    readonly Func<bool>? _alive;
    readonly Stopwatch _since = Stopwatch.StartNew();
    readonly Stopwatch _still = Stopwatch.StartNew();
    readonly Stopwatch _asked = Stopwatch.StartNew();
    readonly Stopwatch _looked = Stopwatch.StartNew();
    readonly bool _capToClock;
    readonly Dictionary<Side, double> _last = [];
    readonly Dictionary<Side, long> _ran = [];

    /// <param name="capToClock">progress counts at most the ticks the nominal rate gives the real time
    /// between two looks (a game whose tick period is lifted cannot eat the limit early); false counts
    /// every tick, however fast they came.</param>
    public TickGuard(BridgeClient bridge, TimeSpan limit, Func<bool>? alive = null, EventLogReader? log = null,
        bool capToClock = true)
    {
        _capToClock = capToClock;
        _bridge = bridge;
        _log = log;
        _alive = alive;
        Limit = limit;
        LimitTicks = BridgeClient.LimitTicks(limit);
        foreach (var side in Sides)
            if (TickOf(side) is { } tick) { _last[side] = tick; _ran[side] = 0; }
    }

    /// <summary>The limit as given, in seconds of a running game.</summary>
    public TimeSpan Limit { get; }

    /// <summary>The limit in ticks at the bridge's nominal rate.</summary>
    public long LimitTicks { get; }

    /// <summary>Ticks the game ran since the guard began: the server's, else the client's clock.</summary>
    public long Ticks => _ran.TryGetValue(Side.Server, out var server) ? server : _ran.GetValueOrDefault(Side.Client);

    /// <summary>The side's newest tick count: the server's head or heartbeat, whichever is later; the
    /// client's heartbeat (its head counts frames).</summary>
    double? TickOf(Side side)
    {
        double? beat = _bridge.State(side)?.Tick;
        if (side == Side.Client) return beat;
        double? head = _log?.Head(side)?.Tick;
        return beat is null ? head : head is null ? beat : Math.Max(beat.Value, head.Value);
    }

    /// <summary>Why the limit is over (the game ran its ticks, stood still too long, or closed), or
    /// null while the wait goes on.</summary>
    public string? Over()
    {
        if (_alive is not null && _asked.Elapsed >= TimeSpan.FromSeconds(1))
        {
            _asked.Restart();
            if (!_alive()) return "the game process is gone";
        }
        var moved = false;
        // A game whose ticks run faster than its nominal rate (the fast load lifts the server's tick
        // period to 0 while it loads: thousands of ticks a second, each a sliver of game time) would
        // eat the limit in a fraction of its time: progress counts at most the ticks the nominal
        // rate gives the real time since the last look (the night's W3, round 6: a 120 s wait over
        // a headless level change ran out in ~10 s).
        var seen = _looked.Elapsed;
        _looked.Restart();
        var most = _capToClock ? (long)Math.Ceiling(seen / BridgeClient.NominalTick) + 1 : long.MaxValue;
        foreach (var side in Sides)
        {
            if (TickOf(side) is not { } tick) continue;
            if (!_last.TryGetValue(side, out var last)) { _last[side] = tick; _ran[side] = 0; continue; }
            if (tick == last) continue;
            // a new Lua state counts from zero: what it ran since is progress too
            _ran[side] = _ran.GetValueOrDefault(side) + Math.Min(most, (long)(tick > last ? tick - last : tick));
            _last[side] = tick;
            moved = true;
        }
        if (moved) _still.Restart();
        if (_last.Count == 0) return _since.Elapsed >= Limit ? $"nothing came in {Limit.TotalSeconds:0.#} s (no side counts its ticks)" : null;
        if (Ticks >= LimitTicks) return $"nothing came in {Ticks} ticks of the game ({Limit.TotalSeconds:0.#} s of a running game)";
        var guard = Limit > BridgeClient.StallGuard ? Limit : BridgeClient.StallGuard;
        return _still.Elapsed >= guard ? $"the game made no progress for {_still.Elapsed.TotalSeconds:0} s (a hang guard)" : null;
    }
}
