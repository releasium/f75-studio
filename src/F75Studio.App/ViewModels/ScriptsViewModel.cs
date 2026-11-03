using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
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
    private readonly Func<string, bool> _confirmDiscard;
    private readonly Func<string, bool> _confirmOverwrite;
    private DateTime _loadedWriteTimeUtc;
    private bool _savedFirstTime;

    // The file the current Document was loaded from; null when nothing (or a failed load) is shown.
    private string? _documentPath;
    private bool _refreshing;
    private bool _restoring;
    private bool _revertingStartup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(SaveCommand),
        nameof(ImportIntoBuilderCommand), nameof(OpenInVsCodeCommand))]
    private ScriptItemViewModel? _selectedScript;

    [ObservableProperty] private TextDocument _document = new();
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _isEditorDirty;
    [ObservableProperty] private bool _isEditorReadOnly = true;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isStatusOpen;
    [ObservableProperty] private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    /// <param name="confirmOverwrite">Asked when the file changed on disk since it was loaded; true overwrites. Default: never overwrite.</param>
    /// <param name="confirmDiscard">Asked (with a message) before unsaved edits are dropped; true discards. Default: never discard.</param>
    public ScriptsViewModel(AppServices services, HotkeysViewModel hotkeys, Action<object> navigate,
        Func<string, bool>? confirmDiscard = null, Func<string, bool>? confirmOverwrite = null)
    {
        _services = services;
        _hotkeys = hotkeys;
        _navigate = navigate;
        _confirmDiscard = confirmDiscard ?? (_ => false);
        _confirmOverwrite = confirmOverwrite ?? (_ => false);
        services.Runner.RunningChanged += (_, _) => Ui.Run(UpdateRunningState);
        Refresh();
    }

    public ObservableCollection<ScriptItemViewModel> Scripts { get; } = [];

    [RelayCommand]
    private void Refresh()
    {
        var selectedPath = _documentPath ?? SelectedScript?.Path;
        var generated = Path.GetFullPath(_services.Paths.GeneratedHotkeysScript);
        _refreshing = true;
        try
        {
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
        }
        finally { _refreshing = false; }

        var same = selectedPath is null ? null : Scripts.FirstOrDefault(s => s.Path == selectedPath);
        if (same is not null)
        {
            // Same path: keep the editor (and any unsaved edits) as they are, just re-point at the new item.
            var keep = IsEditorDirty && _documentPath == same.Path;
            if (keep) _restoring = true;
            try { SelectedScript = same; }
            finally { _restoring = false; }
            if (keep) NotifySelectionDependents();
            return;
        }

        if (IsEditorDirty && !_confirmDiscard("You have unsaved changes to the selected script, which no longer exists. Discard them?"))
        {
            // Keep the edits visible; nothing is selected so Save stays disabled.
            _restoring = true;
            try { SelectedScript = null; }
            finally { _restoring = false; }
            return;
        }
        IsEditorDirty = false;
        SelectedScript = Scripts.FirstOrDefault();
        if (SelectedScript is null) LoadSelected();
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
        if (!CanSave()) return;
        var script = SelectedScript!;
        var path = _documentPath!;
        try
        {
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > _loadedWriteTimeUtc
                && !_confirmOverwrite($"{script.Name} was changed outside F75 Studio. Overwrite those changes?"))
            {
                SetStatus("Not saved: file changed on disk. Refresh to reload it.", InfoBarSeverity.Warning);
                return;
            }
            _savedFirstTime = !File.Exists(path + ".orig");
            ScriptFile.SaveWithBackup(path, Document.Text);
            _loadedWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save {script.Name}: {ex.Message}", InfoBarSeverity.Error);
            return;
        }
        IsEditorDirty = false;
        SetStatus(_savedFirstTime
            ? $"Saved {script.Name}. The previous version is kept as {script.Name}.bak and the original as {script.Name}.orig."
            : $"Saved {script.Name}. The previous version is kept as {script.Name}.bak.", InfoBarSeverity.Success);
        if (_services.Runner.IsRunning(path)) Guard(() => _services.Runner.Start(path));
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
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
        if (_refreshing || _restoring) return;

        if (IsEditorDirty && _documentPath is not null)
        {
            if (value?.Path == _documentPath) return;
            var name = Path.GetFileName(_documentPath);
            if (!_confirmDiscard($"You have unsaved changes to {name}. Discard them?"))
            {
                var previous = Scripts.FirstOrDefault(s => s.Path == _documentPath);
                RestoreSelection(previous);
                return;
            }
        }
        LoadSelected();
    }

    /// <summary>Puts the selection back; deferred on the UI thread so the ListBox reflects it after its own selection change.</summary>
    private void RestoreSelection(ScriptItemViewModel? target)
    {
        void Apply()
        {
            _restoring = true;
            try { SelectedScript = target; }
            finally { _restoring = false; }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) Apply();
        else dispatcher.BeginInvoke(Apply);
    }

    private void LoadSelected()
    {
        var script = SelectedScript;
        string text = "";
        string? loadedFrom = null;
        if (script is not null)
        {
            try
            {
                _loadedWriteTimeUtc = File.GetLastWriteTimeUtc(script.Path);
                text = File.ReadAllText(script.Path);
                loadedFrom = script.Path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus($"Could not open {script.Name}: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        var document = new TextDocument(text);
        document.TextChanged += (_, _) => IsEditorDirty = true;
        _documentPath = loadedFrom;
        Document = document;
        IsEditorDirty = false;
        NotifySelectionDependents();
    }

    private void NotifySelectionDependents()
    {
        IsEditorReadOnly = SelectedScript is null || SelectedScript.IsGenerated || _documentPath != SelectedScript.Path;
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection() => SelectedScript is not null;

    private bool CanImport() => SelectedScript is { IsGenerated: false };

    private bool CanSave() =>
        SelectedScript is { IsGenerated: false } script && _documentPath == script.Path && IsEditorDirty;

    private void SetRunAtStartup(ScriptItemViewModel item, bool enabled)
    {
        try
        {
            _services.UpdateSettings(s => s with
            {
                StartupScripts = enabled
                    ? s.StartupScripts.Append(item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : s.StartupScripts.Where(p => !p.Equals(item.Path, StringComparison.OrdinalIgnoreCase)).ToList(),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save the startup setting: {ex.Message}", InfoBarSeverity.Error);
            if (_revertingStartup) return;
            _revertingStartup = true;
            try { item.RunAtStartup = !enabled; }
            finally { _revertingStartup = false; }
        }
    }

    private void UpdateRunningState()
    {
        foreach (var script in Scripts) script.IsRunning = _services.Runner.IsRunning(script.Path);
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (AhkNotFoundException) { SetStatus("AutoHotkey v2 was not found. Set its path in Settings.", InfoBarSeverity.Error); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            SetStatus($"Could not run the script: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}
