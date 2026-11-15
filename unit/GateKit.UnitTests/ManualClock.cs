namespace GateKit.UnitTests;

/// <summary>
/// A clock that moves only when a test moves it: timers made on it fire inside
/// <see cref="Advance"/>, on the caller's thread, and never because the machine was slow.
/// </summary>
public sealed class ManualClock : TimeProvider
{
    readonly Lock _gate = new();
    readonly List<Timer> _timers = [];
    long _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _now; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on and runs every timer that has come due.</summary>
    public void Advance(TimeSpan span)
    {
        List<Timer> due;
        lock (_gate)
        {
            _now += span.Ticks;
            due = _timers.Where(t => t.Due <= _now).ToList();
            foreach (var timer in due) timer.Fired();
        }
        foreach (var timer in due) timer.Run();
    }

    /// <summary>A wait that lets its time pass on this clock and returns at once.</summary>
    public Task Wait(TimeSpan span, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        Advance(span);
        return Task.CompletedTask;
    }

    sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due;
        long _period;

        public void Run() => callback(state);

        /// <summary>Under the clock's lock: the next due time, or off the list.</summary>
        public void Fired()
        {
            if (_period > 0) Due += _period;
            else clock._timers.Remove(this);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            bool now;
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                now = dueTime == TimeSpan.Zero;
                if (dueTime != Timeout.InfiniteTimeSpan && !now)
                {
                    Due = clock._now + dueTime.Ticks;
                    clock._timers.Add(this);
                }
                else if (now && _period > 0)
                {
                    Due = clock._now + _period;
                    clock._timers.Add(this);
                }
            }
            if (now) Run();
            return true;
        }

        public void Dispose()
        {
            lock (clock._gate) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
