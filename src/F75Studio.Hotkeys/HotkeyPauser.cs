namespace F75Studio.Hotkeys;

/// <summary>Stops every running script and remembers which ones to start again.</summary>
public sealed class HotkeyPauser
{
    private readonly IAhkRunner _runner;
    private List<string> _paused = [];

    public HotkeyPauser(IAhkRunner runner) => _runner = runner;

    public bool IsPaused { get; private set; }

    public void Pause()
    {
        if (IsPaused) return;
        _paused = _runner.RunningScripts.ToList();
        _runner.StopAll();
        IsPaused = true;
    }

    public void Resume()
    {
        if (!IsPaused) return;
        foreach (var script in _paused.Where(File.Exists)) _runner.Start(script);
        _paused = [];
        IsPaused = false;
    }
}
