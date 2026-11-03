namespace F75Studio.Hotkeys;

public sealed record AhkValidationResult(bool Success, string? Message, int? Line)
{
    public static readonly AhkValidationResult Ok = new(true, null, null);
}

public sealed class AhkNotFoundException : Exception
{
    public AhkNotFoundException() : base("AutoHotkey v2 executable was not found.") { }
}

public interface IAhkRunner
{
    string? AhkExePath { get; set; }
    IReadOnlyCollection<string> RunningScripts { get; }

    /// <summary>Raised with the script's full path whenever it starts or stops (may be raised on a background thread).</summary>
    event EventHandler<string>? RunningChanged;

    Task<AhkValidationResult> ValidateAsync(string scriptPath, CancellationToken ct = default);
    void Start(string scriptPath);
    void Stop(string scriptPath);
    bool IsRunning(string scriptPath);
    void StopAll();
}
