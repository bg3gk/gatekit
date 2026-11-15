namespace GateKit.Sweep;

/// <summary>
/// The time a sweep runs on: <see cref="Clock"/> times the steps, the budget and the watchdog;
/// <see cref="Wait"/> is every wait the runner makes of its own accord (settling, the capture's first
/// ticks, polling for a dummy). A test gives a clock it moves itself, so that nothing it checks
/// depends on how fast the machine is.
/// </summary>
public sealed record SweepTime(TimeProvider Clock, Func<TimeSpan, CancellationToken, Task> Wait)
{
    public static SweepTime Real { get; } = new(TimeProvider.System, (span, cancel) => Task.Delay(span, TimeProvider.System, cancel));
}

/// <summary>
/// The limit on one step: it counts only the time spent waiting for the game, so a long settle or a
/// slow dummy never reads as a hang. <see cref="Token"/> is cancelled when the limit is spent or the
/// run is cancelled.
/// </summary>
internal sealed class StepWatchdog : IDisposable
{
    readonly SweepTime _time;
    readonly TimeSpan _limit;
    readonly CancellationTokenSource _timer;
    readonly CancellationTokenSource _linked;
    readonly long _started;
    long _since;
    TimeSpan _spent;

    public StepWatchdog(SweepTime time, TimeSpan limit, CancellationToken cancel)
    {
        _time = time;
        _limit = limit;
        _timer = new CancellationTokenSource(limit, time.Clock);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _timer.Token);
        _started = _since = time.Clock.GetTimestamp();
    }

    public CancellationToken Token => _linked.Token;

    /// <summary>The whole time of the step, its own waits included.</summary>
    public TimeSpan Total => _time.Clock.GetElapsedTime(_started);

    /// <summary>A wait of the runner's own, with the limit held still while it lasts.</summary>
    public async Task WaitAsync(TimeSpan span)
    {
        _spent += _time.Clock.GetElapsedTime(_since);
        _timer.CancelAfter(Timeout.InfiniteTimeSpan);
        try { await _time.Wait(span, Token); }
        finally
        {
            _since = _time.Clock.GetTimestamp();
            var left = _limit - _spent;
            _timer.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }
    }

    public void Dispose()
    {
        _linked.Dispose();
        _timer.Dispose();
    }
}
