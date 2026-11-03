namespace F75Studio.Lighting;

/// <summary>In-memory device: used by tests and when designing profiles without hardware.</summary>
public sealed class FakeKeyboardDevice : IKeyboardDevice, ILiveLighting
{
    private readonly object _liveLock = new();
    private volatile bool _isLive;
    private int _liveStarts;
    private int _liveStops;

    public DeviceStatus Status { get; private set; } = DeviceStatus.Connected;
    public string StatusText { get; private set; } = "Simulated keyboard";
    public List<LightingState> Applied { get; } = [];
    public Exception? FailWith { get; set; }

    private readonly List<IReadOnlyDictionary<int, Rgb>> _liveFrames = [];

    /// <summary>Copy of the frames passed to <see cref="SendLiveAsync"/> so far; safe to call while streaming.</summary>
    public IReadOnlyList<IReadOnlyDictionary<int, Rgb>> LiveFramesSnapshot()
    {
        lock (_liveLock) return _liveFrames.ToList();
    }
    public Exception? LiveFailWith { get; set; }
    public bool IsLive => _isLive;
    public int LiveStarts => Volatile.Read(ref _liveStarts);
    public int LiveStops => Volatile.Read(ref _liveStops);

    public event EventHandler? StatusChanged;

    public void SetStatus(DeviceStatus status, string text)
    {
        Status = status;
        StatusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task ApplyAsync(LightingState state, CancellationToken ct = default)
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        Applied.Add(state);
        return Task.CompletedTask;
    }

    public Task StartLiveAsync(CancellationToken ct = default)
    {
        if (LiveFailWith is { } failure) return Task.FromException(failure);
        if (Status != DeviceStatus.Connected) return Task.FromException(new KeyboardUnavailableException(StatusText));
        _isLive = true;
        Interlocked.Increment(ref _liveStarts);
        return Task.CompletedTask;
    }

    public Task SendLiveAsync(IReadOnlyDictionary<int, Rgb> colors, CancellationToken ct = default)
    {
        if (LiveFailWith is { } failure) return Task.FromException(failure);
        if (!_isLive) return Task.FromException(new KeyboardUnavailableException("Screen sync isn't running."));
        lock (_liveLock) _liveFrames.Add(new Dictionary<int, Rgb>(colors));
        return Task.CompletedTask;
    }

    public Task StopLiveAsync()
    {
        _isLive = false;
        Interlocked.Increment(ref _liveStops);
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
