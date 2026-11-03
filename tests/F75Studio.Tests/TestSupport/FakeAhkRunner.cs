using F75Studio.Hotkeys;

namespace F75Studio.Tests.TestSupport;

public sealed class FakeAhkRunner : IAhkRunner
{
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

    public string? AhkExePath { get; set; } = @"C:\fake\AutoHotkey64.exe";
    public AhkValidationResult NextValidation { get; set; } = AhkValidationResult.Ok;
    public Exception? ValidateException { get; set; }
    public Exception? StartException { get; set; }
    public List<string> Started { get; } = [];
    public List<string> Stopped { get; } = [];
    public List<string> Validated { get; } = [];
    public IReadOnlyCollection<string> RunningScripts => _running.ToList();

    public event EventHandler<string>? RunningChanged;

    public Task<AhkValidationResult> ValidateAsync(string scriptPath, CancellationToken ct = default)
    {
        if (AhkExePath is null) throw new AhkNotFoundException();
        Validated.Add(scriptPath);
        if (ValidateException is not null) throw ValidateException;
        return Task.FromResult(NextValidation);
    }

    public void Start(string scriptPath)
    {
        if (AhkExePath is null) throw new AhkNotFoundException();
        if (StartException is not null) throw StartException;
        Started.Add(scriptPath);
        _running.Add(scriptPath);
        RunningChanged?.Invoke(this, scriptPath);
    }

    public void Stop(string scriptPath)
    {
        Stopped.Add(scriptPath);
        if (_running.Remove(scriptPath)) RunningChanged?.Invoke(this, scriptPath);
    }

    public bool IsRunning(string scriptPath) => _running.Contains(scriptPath);

    public void StopAll()
    {
        foreach (var path in _running.ToList()) Stop(path);
    }
}
