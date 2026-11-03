namespace F75Studio.Lighting;

public sealed record ScreenSyncTiming
{
    public int FramePeriodMs { get; init; } = 40;
    public int KeepAliveMs { get; init; } = 500;
    public int CaptureRetryMs { get; init; } = 500;
    public double Smoothing { get; init; } = 0.35;
    public int StopTimeoutMs { get; init; } = 1000;
}

/// <summary>Captures the screen, maps it onto the keys, smooths it and streams it to the keyboard on a background loop.</summary>
public sealed class ScreenSyncEngine : IScreenSync
{
    private readonly Func<IScreenCapture> _captureFactory;
    private readonly ILiveLighting _live;
    private readonly ScreenSyncTiming _timing;
    private readonly Func<long> _nowMs;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _running;
    private int _liveActive;

    public ScreenSyncEngine(Func<IScreenCapture> captureFactory, ILiveLighting live, ScreenSyncTiming? timing = null,
        Func<long>? nowMs = null, Func<int, CancellationToken, Task>? delay = null)
    {
        _captureFactory = captureFactory;
        _live = live;
        _timing = timing ?? new ScreenSyncTiming();
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _delay = delay ?? Task.Delay;
    }

    public bool IsRunning => _running;

    public event Action<IReadOnlyDictionary<int, Rgb>>? FrameSent;
    public event Action<string>? Faulted;
    public event Action<string?>? CaptureStatusChanged;

    public async Task StartAsync(Func<int> brightness, Func<CapturedFrame, IReadOnlyDictionary<int, Rgb>>? mapper = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_running) return;
            _cts?.Dispose();
            _cts = null;
            _loop = null;

            await _live.StartLiveAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _liveActive, 1);

            // No capture right now (secure desktop after unlock, UAC...) is not a start failure: begin paused and retry.
            IScreenCapture? capture = null;
            string? pausedReason = null;
            try
            {
                capture = _captureFactory();
            }
            catch (ScreenCaptureException ex)
            {
                pausedReason = ex.Message;
            }
            catch
            {
                await StopLiveOnceAsync().ConfigureAwait(false);
                throw;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _running = true;
            _loop = Task.Run(() => RunAsync(capture, pausedReason, brightness, mapper ?? EdgeMapper.Map, cts.Token));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cts = _cts;
            var loop = _loop;
            _cts = null;
            _loop = null;
            if (cts is null || loop is null) return;

            cts.Cancel();
            try { await loop.WaitAsync(TimeSpan.FromMilliseconds(_timing.StopTimeoutMs)).ConfigureAwait(false); }
            catch { /* timeout or a loop failure: cleanup below must always run */ }
            await StopLiveOnceAsync().ConfigureAwait(false);
            _running = false;
            cts.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopLiveOnceAsync()
    {
        if (Interlocked.Exchange(ref _liveActive, 0) == 1)
        {
            try { await _live.StopLiveAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
        }
    }

    private async Task RunAsync(IScreenCapture? capture, string? startPausedReason, Func<int> brightness,
        Func<CapturedFrame, IReadOnlyDictionary<int, Rgb>> mapper, CancellationToken ct)
    {
        IReadOnlyDictionary<int, Rgb>? lastTarget = null;
        CapturedFrame? lastFrame = null;
        Dictionary<int, double[]>? smoothed = null;
        Dictionary<int, Rgb>? lastSent = null;
        var lastSendAt = long.MinValue;
        var paused = false;

        try
        {
            if (startPausedReason is not null)
            {
                paused = true;
                Raise(CaptureStatusChanged, "Screen capture paused: " + startPausedReason);
                await _delay(_timing.CaptureRetryMs, ct).ConfigureAwait(false);
            }

            while (!ct.IsCancellationRequested)
            {
                var start = _nowMs();
                var target = lastTarget;

                try
                {
                    if (capture is null)
                    {
                        try { capture = _captureFactory(); }
                        catch (Exception factoryEx) when (factoryEx is not OperationCanceledException)
                        {
                            throw new ScreenCaptureException(factoryEx.Message, factoryEx);
                        }
                    }
                    var frame = capture.TryCapture(_timing.FramePeriodMs);
                    if (frame is not null) lastFrame = frame;
                    // Re-map every tick (not only on new frames) so a mapper switch shows up on a static screen.
                    // While paused only a real new frame resumes; until then the dimmed last good image stays.
                    if (lastFrame is not null && (frame is not null || !paused))
                    {
                        target = mapper(lastFrame);
                        lastTarget = target;
                        if (paused)
                        {
                            paused = false;
                            Raise(CaptureStatusChanged, (string?)null);
                        }
                    }
                }
                catch (Exception ex) when (ex is ScreenCaptureException or ArgumentException)
                {
                    lastFrame = null;
                    if (!paused)
                    {
                        paused = true;
                        Raise(CaptureStatusChanged, "Screen capture paused: " + ex.Message);
                    }
                    try { capture?.Dispose(); } catch { /* best effort */ }
                    capture = null;
                    await _delay(_timing.CaptureRetryMs, ct).ConfigureAwait(false);
                }

                if (target is not null)
                {
                    // While paused the keyboard shows the last good image at half strength, applied immediately.
                    var snap = paused;
                    if (snap) target = Dim(target);
                    smoothed = Smooth(smoothed, target, snap);

                    var output = Output(smoothed, Math.Clamp(brightness(), 0, 9));
                    var now = _nowMs();
                    if (lastSent is null || !Same(output, lastSent) || now - lastSendAt >= _timing.KeepAliveMs)
                    {
                        await _live.SendLiveAsync(output, ct).ConfigureAwait(false);
                        lastSent = output;
                        lastSendAt = now;
                        Raise(FrameSent, (IReadOnlyDictionary<int, Rgb>)output);
                    }
                }

                var remaining = _timing.FramePeriodMs - (_nowMs() - start);
                if (remaining > 0) await _delay((int)remaining, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A stop in progress (or a run orphaned by a stop timeout) must not touch the next session.
            if (ct.IsCancellationRequested) return;
            try { capture?.Dispose(); } catch { /* best effort */ }
            capture = null;
            await StopLiveOnceAsync().ConfigureAwait(false);
            _running = false;
            Raise(Faulted, ex.Message);
        }
        finally
        {
            try { capture?.Dispose(); } catch { /* best effort */ }
        }
    }

    private Dictionary<int, double[]> Smooth(Dictionary<int, double[]>? state, IReadOnlyDictionary<int, Rgb> target, bool snap)
    {
        if (state is null || snap)
        {
            state ??= new Dictionary<int, double[]>(target.Count);
            foreach (var (led, c) in target) state[led] = [c.R, c.G, c.B];
            return state;
        }
        foreach (var (led, c) in target)
        {
            if (!state.TryGetValue(led, out var s)) { state[led] = [c.R, c.G, c.B]; continue; }
            s[0] += (c.R - s[0]) * _timing.Smoothing;
            s[1] += (c.G - s[1]) * _timing.Smoothing;
            s[2] += (c.B - s[2]) * _timing.Smoothing;
        }
        return state;
    }

    private static IReadOnlyDictionary<int, Rgb> Dim(IReadOnlyDictionary<int, Rgb> target)
    {
        var dim = new Dictionary<int, Rgb>(target.Count);
        foreach (var (led, c) in target)
            dim[led] = new Rgb((byte)Math.Round(c.R * 0.5), (byte)Math.Round(c.G * 0.5), (byte)Math.Round(c.B * 0.5));
        return dim;
    }

    private static Dictionary<int, Rgb> Output(Dictionary<int, double[]> smoothed, int b)
    {
        var factor = b == 0 ? 0 : 0.25 + 0.75 * b / 9;
        var output = new Dictionary<int, Rgb>(smoothed.Count);
        foreach (var (led, s) in smoothed) output[led] = new Rgb(Channel(s[0], factor), Channel(s[1], factor), Channel(s[2], factor));
        return output;
    }

    private static byte Channel(double value, double factor) => (byte)Math.Clamp(Math.Round(value * factor), 0, 255);

    private static bool Same(Dictionary<int, Rgb> a, Dictionary<int, Rgb> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (led, c) in a)
            if (!b.TryGetValue(led, out var other) || other != c) return false;
        return true;
    }

    private static void Raise<T>(Action<T>? handler, T arg)
    {
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<T>)d)(arg); }
            catch { /* a throwing subscriber must not kill the loop */ }
        }
    }
}
