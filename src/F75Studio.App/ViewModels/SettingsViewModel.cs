using CommunityToolkit.Mvvm.ComponentModel;
using F75Studio.App.Services;
using F75Studio.Core;

namespace F75Studio.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IStartupRegistration _startup;

    [ObservableProperty] private string _ahkPath;
    [ObservableProperty] private string _ahkStatus;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private AppTheme _theme;

    public SettingsViewModel(AppServices services, IStartupRegistration startup)
    {
        _services = services;
        _startup = startup;
        _ahkPath = services.Runner.AhkExePath ?? "";
        _ahkStatus = services.Runner.AhkExePath is null
            ? "AutoHotkey v2 not found. Install it from autohotkey.com or browse to AutoHotkey64.exe."
            : "AutoHotkey found.";
        _startWithWindows = startup.IsEnabled;
        _theme = services.Settings.Theme;
    }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    public string ScriptsFolder => _services.Paths.GeneratedScriptsFolder;

    partial void OnAhkPathChanged(string value)
    {
        if (!File.Exists(value))
        {
            AhkStatus = "File not found.";
            return;
        }
        _services.Runner.AhkExePath = value;
        _services.UpdateSettings(s => s with { AhkPath = value });
        AhkStatus = "AutoHotkey found.";
    }

    partial void OnStartWithWindowsChanged(bool value) => _startup.SetEnabled(value);

    partial void OnThemeChanged(AppTheme value) => _services.UpdateSettings(s => s with { Theme = value });
}
