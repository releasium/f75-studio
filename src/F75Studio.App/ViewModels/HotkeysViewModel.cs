using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F75Studio.App.Services;
using F75Studio.Hotkeys;
using Wpf.Ui.Controls;

namespace F75Studio.App.ViewModels;

public sealed partial class HotkeysViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly string _scriptPath;
    private TriggerRecorder? _recorder;
    private bool _resumeAfterRecording;
    private int _editCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(ToggleRecordingCommand))]
    private HotkeyItemViewModel? _selectedItem;

    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isStatusOpen;
    [ObservableProperty] private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public HotkeysViewModel(AppServices services)
    {
        _services = services;
        _scriptPath = services.Paths.GeneratedHotkeysScript;
        foreach (var hotkey in services.HotkeyStore.Load()) AddItem(HotkeyItemViewModel.FromHotkey(hotkey));
        Items.CollectionChanged += (_, _) => { _editCount++; IsDirty = true; RefreshPreview(); };
        SelectedItem = Items.FirstOrDefault();
        RefreshPreview();
        IsDirty = false;
    }

    partial void OnSelectedItemChanged(HotkeyItemViewModel? value) => CancelRecording();

    public ObservableCollection<HotkeyItemViewModel> Items { get; } = [];

    [RelayCommand]
    private void AddHotkey()
    {
        var item = new HotkeyItemViewModel { Group = SelectedItem?.Group ?? "General" };
        AddItem(item);
        SelectedItem = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        CancelRecording();
        var index = Items.IndexOf(SelectedItem!);
        Items.RemoveAt(index);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleRecording()
    {
        if (IsRecording) StopRecording();
        else BeginRecording();
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        CancelRecording();
        foreach (var item in Items) item.Error = null;
        var missing = Items.Where(i => string.IsNullOrWhiteSpace(i.Trigger)).ToList();
        if (missing.Count > 0)
        {
            foreach (var item in missing) item.Error = "Set a trigger first.";
            SelectedItem = missing[0];
            SetStatus($"{missing.Count} hotkey(s) have no trigger.", InfoBarSeverity.Error);
            return;
        }

        var editsAtStart = _editCount;
        var hotkeys = Items.Select(i => i.ToHotkey()).ToList();
        var pending = Path.ChangeExtension(_scriptPath, ".pending.ahk");
        try
        {
            _services.HotkeyStore.Save(hotkeys);
            var script = _services.Generator.Generate(hotkeys);
            ScriptFile.Write(pending, script.Text);

            var result = await _services.Runner.ValidateAsync(pending);
            if (!result.Success)
            {
                var owner = result.Line is int line && script.LineOwners.TryGetValue(line, out var id)
                    ? Items.FirstOrDefault(i => i.Id == id)
                    : null;
                if (owner is not null)
                {
                    owner.Error = result.Message;
                    SelectedItem = owner;
                }
                SetStatus($"AutoHotkey rejected the script: {result.Message}", InfoBarSeverity.Error);
                return;
            }

            File.Move(pending, _scriptPath, overwrite: true);
            _services.Runner.Start(_scriptPath);
            if (_editCount == editsAtStart) IsDirty = false;
            SetStatus($"Applied {hotkeys.Count(h => h.Enabled)} hotkey(s).", InfoBarSeverity.Success);
        }
        catch (AhkNotFoundException)
        {
            SetStatus("AutoHotkey v2 was not found. Set its path in Settings. Your hotkeys are saved.", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            try { _services.Log.Write(ex, "Applying hotkeys"); } catch { /* logging must not throw */ }
            SetStatus($"Could not apply hotkeys: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); } catch { /* best effort */ }
        }
    }

    /// <summary>Stops an in-progress recording, resuming the script paused for it. Leaves the trigger as last recorded.</summary>
    /// <param name="resume">False while the app is exiting, so the paused script is not restarted.</param>
    public void CancelRecording(bool resume = true)
    {
        if (IsRecording) StopRecording(resume);
    }

    /// <summary>Writes the current cards to hotkeys.json without generating or applying anything. Returns false if it could not.</summary>
    public bool SaveDrafts()
    {
        try
        {
            _services.HotkeyStore.Save(Items.Select(i => i.ToHotkey()).ToList());
            return true;
        }
        catch (Exception ex)
        {
            try { _services.Log.Write(ex, "Saving hotkey drafts"); } catch { /* logging must not throw */ }
            return false;
        }
    }

    public void RecordKeyDown(string ahkKey)
    {
        if (_recorder is null) return;
        _recorder.KeyDown(ahkKey);
        ShowRecordedTrigger();
    }

    public void RecordKeyUp(string ahkKey)
    {
        if (_recorder is null) return;
        _recorder.KeyUp(ahkKey);
        ShowRecordedTrigger();
        if (_recorder.IsComplete) StopRecording();
    }

    public ImportResult ImportFromScript(string scriptText, string defaultGroup)
    {
        var result = AhkImporter.Import(scriptText, defaultGroup);
        HotkeyItemViewModel? first = null;
        foreach (var hotkey in result.Hotkeys)
        {
            var item = HotkeyItemViewModel.FromHotkey(hotkey with { Id = Guid.NewGuid() });
            AddItem(item);
            first ??= item;
        }
        if (first is not null) SelectedItem = first;

        var message = $"Imported {result.Hotkeys.Count} hotkey(s). Stop the original script, then click Apply to avoid duplicate hotkeys.";
        if (result.SkippedLines.Count > 0)
        {
            message += $" {result.SkippedLines.Count} line(s) could not be imported: {string.Join(" | ", result.SkippedLines.Take(3))}";
        }
        SetStatus(message, result.SkippedLines.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        return result;
    }

    private bool HasSelection() => SelectedItem is not null;

    private void AddItem(HotkeyItemViewModel item)
    {
        item.PropertyChanged += OnItemChanged;
        Items.Add(item);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HotkeyItemViewModel.Error) or nameof(HotkeyItemViewModel.Summary)) return;
        _editCount++;
        if (sender is HotkeyItemViewModel changed) changed.Error = null;
        IsDirty = true;
        RefreshPreview();
    }

    private void RefreshPreview() => Preview = _services.Generator.Generate(Items.Select(i => i.ToHotkey())).Text;

    private void BeginRecording()
    {
        _recorder = new TriggerRecorder();
        // AutoHotkey intercepts keys before this window sees them, so pause our own script while recording.
        _resumeAfterRecording = _services.Runner.IsRunning(_scriptPath);
        if (_resumeAfterRecording) _services.Runner.Stop(_scriptPath);
        IsRecording = true;
    }

    private void StopRecording(bool resume = true)
    {
        _recorder = null;
        IsRecording = false;
        if (!_resumeAfterRecording) return;
        _resumeAfterRecording = false;
        if (!resume) return;
        try { _services.Runner.Start(_scriptPath); }
        catch (AhkNotFoundException) { SetStatus("AutoHotkey v2 was not found. Set its path in Settings.", InfoBarSeverity.Error); }
    }

    private void ShowRecordedTrigger()
    {
        if (_recorder?.Trigger is { } trigger && SelectedItem is not null) SelectedItem.Trigger = trigger;
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}
