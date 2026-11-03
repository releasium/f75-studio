namespace F75Studio.Lighting;

/// <summary>A small, downscaled screen image. <see cref="Bgra"/> is Width*Height*4 bytes, row-major, B,G,R,A.</summary>
public sealed record CapturedFrame(int Width, int Height, byte[] Bgra);

public sealed class ScreenCaptureException(string message, Exception? inner = null) : Exception(message, inner);

public interface IScreenCapture : IDisposable
{
    /// <summary>Returns the latest frame, or null if the screen didn't change within <paramref name="timeoutMs"/>.</summary>
    /// <exception cref="ScreenCaptureException">Capture is temporarily impossible (lock screen, GPU reset, unsupported format).</exception>
    CapturedFrame? TryCapture(int timeoutMs);
}

/// <summary>Volatile per-LED streaming (protocol doc §8). Implemented by AulaF75Device and FakeKeyboardDevice.</summary>
public interface ILiveLighting
{
    bool IsLive { get; }

    /// <exception cref="KeyboardUnavailableException">Not on the USB cable, or identity check failed.</exception>
    Task StartLiveAsync(CancellationToken ct = default);

    /// <remarks>Implementations serialize live frames with other device commands (one HID transaction at a time).</remarks>
    /// <exception cref="KeyboardUnavailableException">Live session ended (unplugged, send failed).</exception>
    Task SendLiveAsync(IReadOnlyDictionary<int, Rgb> colors, CancellationToken ct = default);

    /// <remarks>Idempotent: calling it when not live is a no-op. Never throws.</remarks>
    Task StopLiveAsync();
}

/// <summary>What the view model needs from the screen-sync engine.</summary>
public interface IScreenSync
{
    bool IsRunning { get; }

    /// <summary>Raised on a background thread with each frame actually sent.</summary>
    /// <remarks>The dictionary is a snapshot; the engine never mutates it after raising the event.</remarks>
    event Action<IReadOnlyDictionary<int, Rgb>>? FrameSent;

    /// <summary>Raised on a background thread when streaming stops by itself (reason text).</summary>
    /// <remarks>Raised after <see cref="IsRunning"/> has become false.</remarks>
    event Action<string>? Faulted;

    /// <summary>Raised on a background thread when capture pauses/resumes (null = resumed).</summary>
    event Action<string?>? CaptureStatusChanged;

    /// <param name="brightness">Called on the engine's background thread every frame; it must be thread-safe.</param>
    /// <param name="mapper">Maps each captured frame to key colours, called every captured frame (may switch result between frames); null means <see cref="EdgeMapper.Map"/>.</param>
    Task StartAsync(Func<int> brightness, Func<CapturedFrame, IReadOnlyDictionary<int, Rgb>>? mapper = null, CancellationToken ct = default);
    Task StopAsync();
}
