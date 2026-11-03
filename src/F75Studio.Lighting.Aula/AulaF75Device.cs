using System.Diagnostics;

namespace F75Studio.Lighting.Aula;

public sealed class AulaF75Device : IKeyboardDevice, ILiveLighting
{
    public const int WiredVid = 0x258A;
    public const int WiredPid = 0x010C;
    public const int WirelessVid = 0x3554;
    public const int DonglePid = 0xFA09;

    internal const string OfficialAppRunningText = "The AULA F75 app is running. Close it so F75 Studio can control the lighting.";
    private const int DisposeWaitMs = 3000;

    private sealed record Snapshot(DeviceStatus Status, string Text);

    private readonly IHidProbe _probe;
    private readonly int _transactionGapMs;
    private readonly IDisposable _subscription;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastTransactionEnd; // Stopwatch timestamp; 0 = no transaction yet
    private volatile Snapshot _snapshot = new(DeviceStatus.NotFound, "Looking for the keyboard…");
    private volatile bool _disposed;
    private int _dirty;     // a hot-plug event arrived since the running refresh last started
    private int _scheduled; // a background refresh loop is queued or running
    private IFeatureChannel? _liveChannel; // guarded by _gate
    private F75Session? _liveSession;      // guarded by _gate
    private volatile bool _isLive;

    public AulaF75Device() : this(new HidSharpProbe(), 500)
    {
    }

    internal AulaF75Device(IHidProbe probe, int transactionGapMs)
    {
        _probe = probe;
        _transactionGapMs = transactionGapMs;
        _subscription = probe.SubscribeChanged(ScheduleRefresh);
        ScheduleRefresh();
    }

    /// <summary>True while a background (hot-plug or startup) refresh is queued or running.</summary>
    internal bool IsRefreshing => Volatile.Read(ref _scheduled) != 0;

    public DeviceStatus Status => _snapshot.Status;
    public string StatusText => _snapshot.Text;
    public event EventHandler? StatusChanged;

    public static (DeviceStatus Status, string Text) ClassifyWithoutCable(bool donglePresent, bool bluetoothPresent) =>
        donglePresent
            ? (DeviceStatus.Dongle, "Connected through the 2.4 GHz receiver. Lighting over the receiver isn't supported yet — connect the USB cable.")
            : bluetoothPresent
                ? (DeviceStatus.Bluetooth, "Connected by Bluetooth. Lighting can only be changed over the USB cable.")
                : (DeviceStatus.NotFound, "Keyboard not found. Connect the F75 with its USB cable.");

    /// <summary>Re-detects the keyboard. Never throws.</summary>
    public async Task RefreshAsync()
    {
        var changed = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            (DeviceStatus Status, string Text) result;
            try
            {
                result = await DetectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = (DeviceStatus.Error, $"Couldn't check for the keyboard: {ex.Message}");
            }
            changed = SetStatus(result.Status, result.Text);
            if (result.Status != DeviceStatus.Connected) EndLiveSession();
        }
        finally
        {
            _gate.Release();
        }

        if (changed && !_disposed) RaiseStatusChanged();
    }

    public bool IsLive => _isLive;

    public async Task ApplyAsync(LightingState state, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EndLiveSession(); // a stored apply replaces the live overlay; the transaction gap below is measured from here
            var (channel, session, firstError) = await OpenKnownBoardAsync(ct).ConfigureAwait(false);
            if (channel is null)
            {
                if (firstError is not null)
                {
                    ScheduleRefresh();
                    throw new KeyboardUnavailableException($"Couldn't send lighting to the keyboard: {firstError.Message}");
                }
                throw new KeyboardUnavailableException("The keyboard no longer identifies as an AULA F75; nothing was sent.");
            }

            using (channel)
            {
                try
                {
                    await session!.ApplyAsync(state, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsDeviceFailure(ex))
                {
                    ScheduleRefresh();
                    throw new KeyboardUnavailableException($"Couldn't send lighting to the keyboard: {ex.Message}");
                }
                finally
                {
                    _lastTransactionEnd = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartLiveAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_isLive) return;
            var (channel, session, firstError) = await OpenKnownBoardAsync(ct).ConfigureAwait(false);
            if (channel is null)
            {
                if (firstError is not null)
                {
                    ScheduleRefresh();
                    throw new KeyboardUnavailableException($"Couldn't start screen sync: {firstError.Message}");
                }
                throw new KeyboardUnavailableException("The keyboard no longer identifies as an AULA F75; nothing was sent.");
            }

            _liveChannel = channel;
            _liveSession = session;
            _isLive = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendLiveAsync(IReadOnlyDictionary<int, Rgb> colors, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_liveSession is null) throw new KeyboardUnavailableException("Screen sync isn't running.");
            try
            {
                await _liveSession.SendLiveFrameAsync(F75Protocol.BuildLiveFrame(colors), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDeviceFailure(ex))
            {
                EndLiveSession();
                ScheduleRefresh();
                throw new KeyboardUnavailableException($"Lost the keyboard while streaming: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopLiveAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                EndLiveSession();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // Stopping is best effort and must never throw.
        }
    }

    /// <summary>Closes the live channel and clears the live state. Caller holds <c>_gate</c>. Never throws.</summary>
    private void EndLiveSession()
    {
        var channel = _liveChannel;
        _liveChannel = null;
        _liveSession = null;
        _isLive = false;
        if (channel is null) return;
        try
        {
            channel.Dispose();
        }
        catch (Exception)
        {
            // Already gone.
        }
        _lastTransactionEnd = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Shared by apply and live start (caller holds <c>_gate</c>): checks the status, the official app and each wired board's
    /// identity on the same channel. Returns the open channel of the first known board (the caller owns it and stamps the
    /// transaction end), or a null channel with the first device error (if any) when none qualified.
    /// </summary>
    private async Task<(IFeatureChannel? Channel, F75Session? Session, Exception? FirstError)> OpenKnownBoardAsync(CancellationToken ct)
    {
        var snapshot = _snapshot;
        if (snapshot.Status != DeviceStatus.Connected) throw new KeyboardUnavailableException(snapshot.Text);
        var boards = _probe.FindWiredBoards();
        if (boards.Count == 0) throw new KeyboardUnavailableException("The keyboard was disconnected.");
        if (_probe.OfficialAppRunning())
        {
            ScheduleRefresh(); // moves the status to the "close the AULA app" error
            throw new KeyboardUnavailableException(OfficialAppRunningText);
        }

        Exception? firstError = null;
        foreach (var board in boards)
        {
            await WaitForTransactionGapAsync(ct).ConfigureAwait(false);
            IFeatureChannel? channel = null;
            var keep = false;
            try
            {
                channel = board.Open();
                var session = new F75Session(channel);
                var identity = await session.ReadIdentityAsync(ct).ConfigureAwait(false);
                if (!F75Protocol.IsKnownIdentity(identity)) continue;
                keep = true;
                return (channel, session, firstError);
            }
            catch (Exception ex) when (IsDeviceFailure(ex) || ex is F75ProtocolException)
            {
                firstError ??= ex; // not talking to us; try the next candidate
            }
            finally
            {
                if (!keep)
                {
                    channel?.Dispose();
                    _lastTransactionEnd = Stopwatch.GetTimestamp();
                }
            }
        }

        return (null, null, firstError);
    }

    public void Dispose()
    {
        _disposed = true;
        _subscription.Dispose();
        // Let a transaction that is already on the wire finish (a half-applied sequence would leave the keyboard
        // inconsistent), but never hang the exit: give up after 3 s. A blocking wait is fine because all HID I/O
        // runs off the UI thread (ConfigureAwait(false) here, Task.Run in the view model).
        if (_gate.Wait(DisposeWaitMs))
        {
            try
            {
                EndLiveSession();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private static bool IsDeviceFailure(Exception ex) => ex is IOException or TimeoutException or UnauthorizedAccessException;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new KeyboardUnavailableException("Lighting is shutting down.");
    }

    /// <summary>Queues a background refresh; bursts of calls collapse into at most one running plus one follow-up.</summary>
    private void ScheduleRefresh()
    {
        if (_disposed) return;
        Volatile.Write(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0)
            _ = Task.Run(RefreshLoopAsync);
    }

    private async Task RefreshLoopAsync()
    {
        while (true)
        {
            Interlocked.Exchange(ref _dirty, 0);
            await RefreshAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _dirty) != 0) continue;
            Interlocked.Exchange(ref _scheduled, 0);
            // An event may have landed between the check and the reset; reclaim the loop if so.
            if (Volatile.Read(ref _dirty) != 0 && Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0) continue;
            return;
        }
    }

    // Protocol doc §3.4 step 4: the driver leaves 500 ms between one transaction and the next.
    private async Task WaitForTransactionGapAsync(CancellationToken ct)
    {
        if (_lastTransactionEnd == 0) return;
        var remaining = _transactionGapMs - (int)Stopwatch.GetElapsedTime(_lastTransactionEnd).TotalMilliseconds;
        if (remaining > 0) await Task.Delay(remaining, ct).ConfigureAwait(false);
    }

    private async Task<(DeviceStatus, string)> DetectAsync()
    {
        var boards = _probe.FindWiredBoards();
        if (boards.Count > 0)
        {
            // The official driver talks to the same collection; don't fight it (and send nothing, not even a read).
            if (_probe.OfficialAppRunning()) return (DeviceStatus.Error, OfficialAppRunningText);
            Exception? firstError = null;
            byte[]? unknownIdentity = null;
            foreach (var board in boards)
            {
                try
                {
                    await WaitForTransactionGapAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        using var channel = board.Open();
                        var identity = await new F75Session(channel).ReadIdentityAsync(CancellationToken.None).ConfigureAwait(false);
                        if (F75Protocol.IsKnownIdentity(identity)) return (DeviceStatus.Connected, "Connected by USB cable");
                        unknownIdentity ??= identity;
                    }
                    finally
                    {
                        _lastTransactionEnd = Stopwatch.GetTimestamp();
                    }
                }
                catch (Exception ex) when (IsDeviceFailure(ex) || ex is F75ProtocolException)
                {
                    firstError ??= ex;
                }
            }

            if (unknownIdentity is not null)
                return (DeviceStatus.Unsupported,
                    $"This keyboard identified itself as {Convert.ToHexString(unknownIdentity)}; F75 Studio only controls the AULA F75 and won't send it lighting commands.");
            return (DeviceStatus.Error, $"Found the F75 but couldn't talk to it ({firstError!.Message}). Close the AULA F75 app and try again.");
        }

        return ClassifyWithoutCable(_probe.DonglePresent(), _probe.BluetoothPresent());
    }

    private bool SetStatus(DeviceStatus status, string text)
    {
        var current = _snapshot;
        if (status == current.Status && text == current.Text) return false;
        _snapshot = new Snapshot(status, text);
        return true;
    }

    private void RaiseStatusChanged()
    {
        var handlers = StatusChanged;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A faulty subscriber must not break detection.
            }
        }
    }
}
