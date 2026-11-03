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
        Items.CollectionChanged += (_, _) => { IsDirty = true; RefreshPreview(); };
        SelectedItem = Items.FirstOrDefault();
        RefreshPreview();
        IsDirty = false;
    }

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
        foreach (var item in Items) item.Error = null;
        var missing = Items.Where(i => string.IsNullOrWhiteSpace(i.Trigger)).ToList();
        if (missing.Count > 0)
        {
            foreach (var item in missing) item.Error = "Set a trigger first.";
            SelectedItem = missing[0];
            SetStatus($"{missing.Count} hotkey(s) have no trigger.", InfoBarSeverity.Error);
            return;
        }

        var hotkeys = Items.Select(i => i.ToHotkey()).ToList();
        _services.HotkeyStore.Save(hotkeys);
        var script = _services.Generator.Generate(hotkeys);
        var pending = Path.ChangeExtension(_scriptPath, ".pending.ahk");
        ScriptFile.Write(pending, script.Text);

        try
        {
            var result = await _services.Runner.ValidateAsync(pending);
            if (!result.Success)
            {
                File.Delete(pending);
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
            IsDirty = false;
            SetStatus($"Applied {hotkeys.Count(h => h.Enabled)} hotkey(s).", InfoBarSeverity.Success);
        }
        catch (AhkNotFoundException)
        {
            File.Delete(pending);
            SetStatus("AutoHotkey v2 was not found. Set its path in Settings. Your hotkeys are saved.", InfoBarSeverity.Error);
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

    private void StopRecording()
    {
        _recorder = null;
        IsRecording = false;
        if (!_resumeAfterRecording) return;
        _resumeAfterRecording = false;
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
