namespace F75Studio.Lighting;

/// <summary>In-memory device: used by tests and when designing profiles without hardware.</summary>
public sealed class FakeKeyboardDevice : IKeyboardDevice
{
    public DeviceStatus Status { get; private set; } = DeviceStatus.Connected;
    public string StatusText { get; private set; } = "Simulated keyboard";
    public List<LightingState> Applied { get; } = [];
    public Exception? FailWith { get; set; }

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

    public void Dispose() { }
}
