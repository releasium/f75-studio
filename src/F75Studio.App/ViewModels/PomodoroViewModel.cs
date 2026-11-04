using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F75Studio.Lighting;

namespace F75Studio.App.ViewModels;

/// <summary>The Pomodoro timer window: countdown, controls and the configurable focus/rest lengths.</summary>
public sealed partial class PomodoroViewModel : ObservableObject
{
    public const int MinMinutes = 1;
    public const int MaxMinutes = 180;

    private readonly PomodoroTimer _timer;
    private readonly Action<int, int> _saveMinutes;

    [ObservableProperty] private string _phaseText = "Focus";
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private double _progress = 100;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFocus = true;
    [ObservableProperty] private int _completedFocus;
    [ObservableProperty] private int _focusMinutes;
    [ObservableProperty] private int _restMinutes;

    /// <param name="saveMinutes">Persists (focus, rest) minutes when the user changes them.</param>
    public PomodoroViewModel(PomodoroTimer timer, int focusMinutes, int restMinutes, Action<int, int> saveMinutes)
    {
        _timer = timer;
        _saveMinutes = saveMinutes;
        _focusMinutes = Clamp(focusMinutes);
        _restMinutes = Clamp(restMinutes);
        _timer.SetDurations(TimeSpan.FromMinutes(_focusMinutes), TimeSpan.FromMinutes(_restMinutes));
        Refresh();
    }

    public string StartPauseText => IsRunning ? "Pause" : "Start";

    /// <summary>Re-reads the timer; the window calls it a few times a second.</summary>
    public void Refresh()
    {
        var s = _timer.Snapshot();
        IsFocus = s.Phase == PomodoroPhase.Focus;
        PhaseText = (IsFocus ? "Focus" : "Rest") + (s.Running ? "" : s.Remaining == s.Duration ? " · ready" : " · paused");
        var left = s.Remaining;
        TimeText = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
        Progress = s.RemainingFraction * 100;
        IsRunning = s.Running;
        CompletedFocus = s.CompletedFocus;
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StartPauseText));

    [RelayCommand]
    private void StartPause()
    {
        if (_timer.Snapshot().Running) _timer.Pause();
        else _timer.Start();
        Refresh();
    }

    [RelayCommand]
    private void Skip()
    {
        _timer.Skip();
        Refresh();
    }

    [RelayCommand]
    private void Reset()
    {
        _timer.Reset();
        Refresh();
    }

    partial void OnFocusMinutesChanged(int value) => ApplyMinutes();
    partial void OnRestMinutesChanged(int value) => ApplyMinutes();

    private void ApplyMinutes()
    {
        var focus = Clamp(FocusMinutes);
        var rest = Clamp(RestMinutes);
        if (focus != FocusMinutes) { FocusMinutes = focus; return; } // re-enters with the clamped value
        if (rest != RestMinutes) { RestMinutes = rest; return; }
        _timer.SetDurations(TimeSpan.FromMinutes(focus), TimeSpan.FromMinutes(rest));
        _saveMinutes(focus, rest);
        Refresh();
    }

    private static int Clamp(int minutes) => Math.Clamp(minutes, MinMinutes, MaxMinutes);
}
