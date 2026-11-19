namespace WindowsDriver.Testing;

/// <summary>
/// A controllable <see cref="System.TimeProvider"/> for deterministic tests.
/// Timers created through it only fire when <see cref="Advance"/>/
/// <see cref="AdvanceTo"/> crosses their due time — callbacks run synchronously
/// inside the advance call, so tests never wait on real wall-clock time.
/// </summary>
public sealed class FakeTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _utcNow = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private bool _advancing;

    public override DateTimeOffset GetUtcNow() { lock (_gate) { return _utcNow; } }

    /// <summary>Number of timers currently scheduled (periodic timers count once).</summary>
    public int ActiveTimers { get { lock (_gate) { return _timers.Count; } } }

    /// <summary>Advances time; fires all timers that become due, in due-time order.</summary>
    public void Advance(TimeSpan delta) => AdvanceTo(_utcNow + delta);

    /// <summary>Moves the clock to an absolute timestamp, firing due timers along the way.</summary>
    public void AdvanceTo(DateTimeOffset target)
    {
        lock (_gate)
        {
            if (_advancing)
            {
                throw new InvalidOperationException("AdvanceTo called re-entrantly from a timer callback.");
            }
            if (target < _utcNow)
            {
                throw new ArgumentOutOfRangeException(nameof(target), "Cannot advance time backwards.");
            }
            _advancing = true;
        }

        try
        {
            // Fire timers until none is due before the target.
            while (true)
            {
                FakeTimer? due;
                DateTimeOffset dueTime;
                lock (_gate)
                {
                    due = _timers
                        .Where(t => t.NextDue.HasValue && t.NextDue.Value <= target)
                        .OrderBy(t => t.NextDue!.Value)
                        .FirstOrDefault();
                    if (due is null)
                    {
                        _utcNow = target;
                        return;
                    }
                    dueTime = due.NextDue!.Value;
                    _utcNow = dueTime;
                }
                due.Fire(); // callback runs synchronously; may re-arm itself
            }
        }
        finally
        {
            lock (_gate)
            {
                _advancing = false;
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state, period);
        lock (_gate)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.NextDue = _utcNow + dueTime;
                _timers.Add(timer);
            }
        }
        return timer;
    }

    private void RemoveTimer(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        private bool _disposed;

        public DateTimeOffset? NextDue { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    NextDue = null;
                    return true;
                }
                NextDue = owner._utcNow + dueTime;
                return true;
            }
        }

        public void Fire()
        {
            if (_disposed)
            {
                return;
            }
            callback(state);
            lock (owner._gate)
            {
                if (period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero)
                {
                    NextDue = null;
                    owner._timers.Remove(this);
                }
                else
                {
                    NextDue = owner._utcNow + period;
                }
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
