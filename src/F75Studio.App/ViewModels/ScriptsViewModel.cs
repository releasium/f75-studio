using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F75Studio.App.Services;
using F75Studio.Hotkeys;
using ICSharpCode.AvalonEdit.Document;
using Wpf.Ui.Controls;

namespace F75Studio.App.ViewModels;

public sealed partial class ScriptsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly HotkeysViewModel _hotkeys;
    private readonly Action<object> _navigate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(SaveCommand),
        nameof(ImportIntoBuilderCommand), nameof(OpenInVsCodeCommand))]
    private ScriptItemViewModel? _selectedScript;

    [ObservableProperty] private TextDocument _document = new();
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _isEditorDirty;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isStatusOpen;
    [ObservableProperty] private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public ScriptsViewModel(AppServices services, HotkeysViewModel hotkeys, Action<object> navigate)
    {
        _services = services;
        _hotkeys = hotkeys;
        _navigate = navigate;
        services.Runner.RunningChanged += (_, _) => Ui.Run(UpdateRunningState);
        Refresh();
    }

    public ObservableCollection<ScriptItemViewModel> Scripts { get; } = [];

    [RelayCommand]
    private void Refresh()
    {
        var selectedPath = SelectedScript?.Path;
        var generated = Path.GetFullPath(_services.Paths.GeneratedHotkeysScript);
        Scripts.Clear();
        foreach (var info in ScriptDiscovery.Discover(_services.Paths.ScriptSearchFolders))
        {
            Scripts.Add(new ScriptItemViewModel(
                info,
                isGenerated: string.Equals(info.Path, generated, StringComparison.OrdinalIgnoreCase),
                isRunning: _services.Runner.IsRunning(info.Path),
                runAtStartup: _services.Settings.StartupScripts.Contains(info.Path, StringComparer.OrdinalIgnoreCase),
                onRunAtStartupChanged: SetRunAtStartup));
        }
        SelectedScript = Scripts.FirstOrDefault(s => s.Path == selectedPath) ?? Scripts.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Start() => Guard(() =>
    {
        _services.Runner.Start(SelectedScript!.Path);
        SetStatus($"Started {SelectedScript.Name}.", InfoBarSeverity.Success);
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Stop() => Guard(() =>
    {
        _services.Runner.Stop(SelectedScript!.Path);
        SetStatus($"Stopped {SelectedScript.Name}.", InfoBarSeverity.Informational);
    });

    // AhkRunner.Start stops a running instance first, so Restart is Start.
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Restart() => Start();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var script = SelectedScript!;
        try
        {
            ScriptFile.SaveWithBackup(script.Path, Document.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save {script.Name}: {ex.Message}", InfoBarSeverity.Error);
            return;
        }
        IsEditorDirty = false;
        if (_services.Runner.IsRunning(script.Path)) Guard(() => _services.Runner.Start(script.Path));
        SetStatus($"Saved {script.Name}. The previous version is kept as {script.Name}.bak.", InfoBarSeverity.Success);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ImportIntoBuilder()
    {
        _hotkeys.ImportFromScript(Document.Text, Path.GetFileNameWithoutExtension(SelectedScript!.Name));
        _navigate(_hotkeys);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenInVsCode()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c code \"{SelectedScript!.Path}\"") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not start VS Code: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    partial void OnSelectedScriptChanged(ScriptItemViewModel? value)
    {
        var document = new TextDocument(value is null ? "" : File.ReadAllText(value.Path));
        document.TextChanged += (_, _) => IsEditorDirty = true;
        Document = document;
        IsEditorDirty = false;
    }

    private bool HasSelection() => SelectedScript is not null;

    private bool CanSave() => SelectedScript is { IsGenerated: false } && IsEditorDirty;

    private void SetRunAtStartup(ScriptItemViewModel item, bool enabled) =>
        _services.UpdateSettings(s => s with
        {
            StartupScripts = enabled
                ? s.StartupScripts.Append(item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : s.StartupScripts.Where(p => !p.Equals(item.Path, StringComparison.OrdinalIgnoreCase)).ToList(),
        });

    private void UpdateRunningState()
    {
        foreach (var script in Scripts) script.IsRunning = _services.Runner.IsRunning(script.Path);
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (AhkNotFoundException) { SetStatus("AutoHotkey v2 was not found. Set its path in Settings.", InfoBarSeverity.Error); }
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}
