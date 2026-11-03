namespace F75Studio.Lighting;

public enum DeviceStatus { NotFound, Connected, Bluetooth, Dongle, Unsupported, Error }

public interface IKeyboardDevice : IDisposable
{
    DeviceStatus Status { get; }

    /// <summary>Human-readable explanation of <see cref="Status"/>.</summary>
    string StatusText { get; }

    /// <summary>May be raised on a background thread.</summary>
    event EventHandler? StatusChanged;

    /// <exception cref="KeyboardUnavailableException">The keyboard can't take lighting commands right now.</exception>
    Task ApplyAsync(LightingState state, CancellationToken ct = default);
}

public sealed class KeyboardUnavailableException(string message) : Exception(message);
