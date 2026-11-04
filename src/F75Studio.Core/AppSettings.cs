namespace F75Studio.Core;

public enum AppTheme { System, Light, Dark }

public sealed record AppSettings
{
    public string? AhkPath { get; init; }
    public AppTheme Theme { get; init; } = AppTheme.System;
    public IReadOnlyList<string> StartupScripts { get; init; } = [];
    public int PomodoroFocusMinutes { get; init; } = 15;
    public int PomodoroRestMinutes { get; init; } = 5;
}
