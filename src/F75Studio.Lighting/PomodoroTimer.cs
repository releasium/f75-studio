namespace F75Studio.Lighting;

public enum PomodoroPhase { Focus, Rest }

/// <param name="CompletedFocus">Focus periods finished since the last reset.</param>
public sealed record PomodoroSnapshot(PomodoroPhase Phase, TimeSpan Remaining, TimeSpan Duration, bool Running, int CompletedFocus)
{
    /// <summary>Time left as a fraction of the phase, 1 at the start and 0 at the end.</summary>
    public double RemainingFraction => Duration <= TimeSpan.Zero ? 0 : Math.Clamp(Remaining / Duration, 0, 1);
}

/// <summary>
/// Focus/rest timer that alternates phases by itself while running. Thread-safe: the UI drives it and the
/// screen-sync engine reads <see cref="Snapshot"/> on its own thread.
/// </summary>
public sealed class PomodoroTimer
{
    private readonly Func<long> _nowMs;
    private readonly object _lock = new();
    private TimeSpan _focus = TimeSpan.FromMinutes(15);
    private TimeSpan _rest = TimeSpan.FromMinutes(5);
    private PomodoroPhase _phase = PomodoroPhase.Focus;
    private TimeSpan _duration = TimeSpan.FromMinutes(15);
    private TimeSpan _elapsedBeforeRun;  // time spent in this phase before the current run
    private long? _runStartedAt;         // null while paused or stopped
    private int _completedFocus;

    public PomodoroTimer(Func<long>? nowMs = null) => _nowMs = nowMs ?? (() => Environment.TickCount64);

    /// <summary>Raised (from whichever thread observed it) when a phase ends by itself or is skipped.</summary>
    public event Action<PomodoroPhase>? PhaseChanged;

    /// <summary>Sets the phase lengths. The current phase takes its new length only if it hasn't started yet.</summary>
    public void SetDurations(TimeSpan focus, TimeSpan rest)
    {
        if (focus <= TimeSpan.Zero || rest <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(focus), "Durations must be positive.");
        lock (_lock)
        {
            _focus = focus;
            _rest = rest;
            if (_runStartedAt is null && _elapsedBeforeRun == TimeSpan.Zero) _duration = PhaseLength(_phase);
        }
    }

    public void Start()
    {
        lock (_lock) _runStartedAt ??= _nowMs();
    }

    public void Pause()
    {
        PomodoroPhase? changed;
        lock (_lock)
        {
            changed = AdvanceLocked();
            if (_runStartedAt is { } started)
            {
                _elapsedBeforeRun += TimeSpan.FromMilliseconds(_nowMs() - started);
                _runStartedAt = null;
            }
        }
        Raise(changed);
    }

    /// <summary>Back to a full, stopped focus period.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _phase = PomodoroPhase.Focus;
            _duration = _focus;
            _elapsedBeforeRun = TimeSpan.Zero;
            _runStartedAt = null;
            _completedFocus = 0;
        }
    }

    /// <summary>Ends the current phase now; keeps running if it was running. A skipped focus period doesn't count as done,
    /// and <see cref="PhaseChanged"/> is not raised (the user asked for it).</summary>
    public void Skip()
    {
        PomodoroPhase? changed;
        lock (_lock)
        {
            changed = AdvanceLocked();
            NextPhaseLocked(_runStartedAt is null ? null : _nowMs(), completed: false);
        }
        Raise(changed);
    }

    public PomodoroSnapshot Snapshot()
    {
        PomodoroSnapshot snap;
        PomodoroPhase? changed;
        lock (_lock)
        {
            changed = AdvanceLocked();
            var remaining = _duration - ElapsedLocked();
            snap = new PomodoroSnapshot(_phase, remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, _duration, _runStartedAt is not null, _completedFocus);
        }
        Raise(changed);
        return snap;
    }

    private TimeSpan ElapsedLocked() =>
        _elapsedBeforeRun + (_runStartedAt is { } started ? TimeSpan.FromMilliseconds(_nowMs() - started) : TimeSpan.Zero);

    /// <summary>While running, moves through every phase that has ended; returns the last phase entered, if any.</summary>
    private PomodoroPhase? AdvanceLocked()
    {
        PomodoroPhase? entered = null;
        while (_runStartedAt is { } started)
        {
            var overrun = ElapsedLocked() - _duration;
            if (overrun < TimeSpan.Zero) break;
            // The next phase started when this one ended, so a late check doesn't lose time.
            entered = NextPhaseLocked(_nowMs() - (long)overrun.TotalMilliseconds);
        }
        return entered;
    }

    private PomodoroPhase NextPhaseLocked(long? startedAt, bool completed = true)
    {
        if (_phase == PomodoroPhase.Focus && completed) _completedFocus++;
        _phase = _phase == PomodoroPhase.Focus ? PomodoroPhase.Rest : PomodoroPhase.Focus;
        _duration = PhaseLength(_phase);
        _elapsedBeforeRun = TimeSpan.Zero;
        _runStartedAt = startedAt;
        return _phase;
    }

    private TimeSpan PhaseLength(PomodoroPhase phase) => phase == PomodoroPhase.Focus ? _focus : _rest;

    private void Raise(PomodoroPhase? phase)
    {
        if (phase is not { } p || PhaseChanged is not { } handler) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<PomodoroPhase>)d)(p); }
            catch { /* a throwing subscriber must not break the timer */ }
        }
    }
}
