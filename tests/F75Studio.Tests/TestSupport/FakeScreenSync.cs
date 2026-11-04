using F75Studio.Lighting;

namespace F75Studio.Tests.TestSupport;

public sealed class FakeScreenSync : IScreenSync
{
    private int _starts, _stops;

    public bool IsRunning { get; private set; }
    public int Starts => Volatile.Read(ref _starts);
    public int Stops => Volatile.Read(ref _stops);

    /// <summary>When set, StartAsync throws it.</summary>
    public Exception? StartFailWith { get; set; }

    /// <summary>Invoked at the start of every StartAsync (before it counts).</summary>
    public Action? OnStart { get; set; }

    /// <summary>The brightness callback passed to the latest StartAsync.</summary>
    public Func<int>? Brightness { get; private set; }

    /// <summary>The mapper passed to the latest StartAsync (null = default edge mapper).</summary>
    public Func<CapturedFrame, IReadOnlyDictionary<int, Rgb>>? LastMapper { get; private set; }

    /// <summary>The needsCapture callback passed to the latest StartAsync (null = always capture).</summary>
    public Func<bool>? LastNeedsCapture { get; private set; }

    public event Action<IReadOnlyDictionary<int, Rgb>>? FrameSent;
    public event Action<string>? Faulted;
    public event Action<string?>? CaptureStatusChanged;

    public Task StartAsync(Func<int> brightness, Func<CapturedFrame, IReadOnlyDictionary<int, Rgb>>? mapper = null,
        Func<bool>? needsCapture = null, CancellationToken ct = default)
    {
        OnStart?.Invoke();
        if (StartFailWith is { } failure) return Task.FromException(failure);
        Brightness = brightness;
        LastMapper = mapper;
        LastNeedsCapture = needsCapture;
        IsRunning = true;
        Interlocked.Increment(ref _starts);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsRunning = false;
        Interlocked.Increment(ref _stops);
        return Task.CompletedTask;
    }

    public void RaiseFrame(IReadOnlyDictionary<int, Rgb> frame) => FrameSent?.Invoke(frame);

    public void RaiseFaulted(string reason)
    {
        IsRunning = false;
        Faulted?.Invoke(reason);
    }

    /// <summary>Raises Faulted without touching IsRunning (a stale fault from an earlier run).</summary>
    public void RaiseFaultedWhileRunning(string reason) => Faulted?.Invoke(reason);

    public void RaiseCaptureStatus(string? text) => CaptureStatusChanged?.Invoke(text);
}
