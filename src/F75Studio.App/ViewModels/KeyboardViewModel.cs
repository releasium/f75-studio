using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F75Studio.App.Services;
using F75Studio.Core;
using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using Wpf.Ui.Controls;

namespace F75Studio.App.ViewModels;

public sealed partial class KeyboardViewModel : ObservableObject
{
    private static readonly Rgb OffPreview = new(0x20, 0x20, 0x20);
    private static readonly Rgb UnsetKeyPreview = new(0x10, 0x10, 0x10);

    private readonly IKeyboardDevice _device;
    private readonly JsonFileStore<LightingProfileSet> _store;
    private readonly Func<CancellationToken, Task> _debounce;
    private readonly List<LightingProfile> _profiles;
    private Dictionary<int, Rgb> _keyColors = [];
    private CancellationTokenSource? _pendingCts;
    private bool _loading;
    private bool _syncingColor;
    private bool _syncingHue;
    private string? _activeProfileName;
    private LightingState? _lastApplied;
    private bool _forcePending;
    private int _deviceSeq;
    private string? _lastSaveError;

    [ObservableProperty] private EffectDefinition _selectedEffect = F75Effects.Get(EffectIds.FixedOn);
    [ObservableProperty] private int _brightness = 9;
    [ObservableProperty] private int _speed = 2;
    [ObservableProperty] private Rgb _color = new(0, 120, 255);
    [ObservableProperty] private string _colorHex = "#0078FF";
    [ObservableProperty] private double _hue;
    [ObservableProperty] private bool _colorful;
    [ObservableProperty] private string _deviceStatusText;
    [ObservableProperty] private DeviceStatus _deviceStatus;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isStatusOpen;
    [ObservableProperty] private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private string _profileNameEdit = "";

    public KeyboardViewModel(IKeyboardDevice device, JsonFileStore<LightingProfileSet> store, Func<CancellationToken, Task>? debounce = null)
    {
        _device = device;
        _store = store;
        _debounce = debounce ?? (ct => Task.Delay(500, ct));
        Keys = F75Layout.Keys.Select(k => new KeyItemViewModel(k)).ToList();
        _deviceStatus = device.Status;
        _deviceStatusText = device.StatusText;
        device.StatusChanged += (_, _) => Ui.Run(OnDeviceStatusChanged);

        var set = store.Load();
        _profiles = set.Profiles.ToList();
        if (_profiles.Count == 0)
        {
            _profiles.Add(new LightingProfile("Default", new LightingState()));
            set = new LightingProfileSet { Profiles = _profiles.ToList(), Active = "Default" };
            try
            {
                store.Save(set);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _lastSaveError = $"Couldn't save lighting profiles: {ex.Message}";
                SetStatus(_lastSaveError, InfoBarSeverity.Error);
            }
        }
        foreach (var p in _profiles) ProfileNames.Add(p.Name);
        SelectedProfile = _profiles.Any(p => p.Name == set.Active) ? set.Active : _profiles[0].Name;
    }

    public IReadOnlyList<EffectDefinition> Effects => F75Effects.All;
    public IReadOnlyList<KeyItemViewModel> Keys { get; }
    public IReadOnlyList<string> GroupNames => F75Layout.GroupNames;
    public ObservableCollection<string> ProfileNames { get; } = [];

    public IReadOnlyList<Rgb> Swatches { get; } =
    [
        new(255, 0, 0), new(255, 128, 0), new(255, 220, 0), new(0, 255, 0), new(0, 255, 200), new(0, 120, 255),
        new(0, 0, 255), new(140, 0, 255), new(255, 0, 200), new(255, 255, 255),
    ];

    public bool ShowSpeed => SelectedEffect.HasSpeed;
    public bool ShowBrightness => SelectedEffect.HasBrightness;
    public bool ShowColor => SelectedEffect.HasColor;
    public bool ShowPerKeyTools => SelectedEffect.Id == EffectIds.PerKey;

    /// <summary>Completes when the most recently scheduled apply has finished (tests await it).</summary>
    public Task PendingApply { get; private set; } = Task.CompletedTask;

    public LightingState CurrentState => new()
    {
        EffectId = SelectedEffect.Id,
        Brightness = Brightness,
        Speed = Speed,
        Color = Color,
        Colorful = Colorful,
        KeyColors = new Dictionary<int, Rgb>(_keyColors),
    };

    public IReadOnlyList<string> ProfileNamesSnapshot() => ProfileNames.ToList();

    public bool ApplyProfileByName(string name)
    {
        var match = ProfileNames.FirstOrDefault(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        if (match == _activeProfileName) ScheduleApply(force: true);
        else SelectedProfile = match;
        return true;
    }

    public void ClickKey(KeyItemViewModel key, bool additive)
    {
        if (!additive)
            foreach (var k in Keys) k.IsSelected = false;
        key.IsSelected = !additive || !key.IsSelected;
    }

    public void DragOverKey(KeyItemViewModel key) => key.IsSelected = true;

    [RelayCommand]
    private void SelectGroup(string name)
    {
        var leds = F75Layout.Group(name).ToHashSet();
        foreach (var k in Keys) k.IsSelected = leds.Contains(k.Led);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var k in Keys) k.IsSelected = false;
    }

    [RelayCommand]
    private void PaintSelected()
    {
        var selected = Keys.Where(k => k.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("Select keys first: click, Ctrl+click, drag, or pick a group.", InfoBarSeverity.Informational);
            return;
        }
        foreach (var k in selected) _keyColors[k.Led] = Color;
        if (SelectedEffect.Id != EffectIds.PerKey) SelectedEffect = F75Effects.Get(EffectIds.PerKey);
        else OnStateChanged();
    }

    [RelayCommand]
    private void PickSwatch(Rgb color) => Color = color;

    [RelayCommand]
    private void NewProfile()
    {
        var n = 1;
        while (NameTaken($"Profile {n}")) n++;
        AddProfile($"Profile {n}");
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (_activeProfileName is null) return;
        var name = $"{_activeProfileName} copy";
        var n = 2;
        while (NameTaken(name)) name = $"{_activeProfileName} copy {n++}";
        AddProfile(name);
    }

    [RelayCommand]
    private void RenameProfile()
    {
        var newName = ProfileNameEdit.Trim();
        if (newName.Length == 0 || _activeProfileName is null) return;
        if (ProfileNames.Any(n => n != _activeProfileName && string.Equals(n, newName, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"A profile named \"{newName}\" already exists.", InfoBarSeverity.Error);
            return;
        }
        var index = _profiles.FindIndex(p => p.Name == _activeProfileName);
        if (index < 0) return;
        _profiles[index] = _profiles[index] with { Name = newName };
        _activeProfileName = newName;
        _loading = true;
        try
        {
            ProfileNames.RemoveAt(index);
            ProfileNames.Insert(index, newName);
            SelectedProfile = newName;
        }
        finally
        {
            _loading = false;
        }
        Save();
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (_profiles.Count <= 1 || _activeProfileName is null)
        {
            SetStatus("You need at least one lighting profile.", InfoBarSeverity.Error);
            return;
        }
        var index = _profiles.FindIndex(p => p.Name == _activeProfileName);
        if (index < 0) return;
        _profiles.RemoveAt(index);
        var next = _profiles[0];
        _activeProfileName = next.Name;
        _loading = true;
        try
        {
            ProfileNames.RemoveAt(index);
            SelectedProfile = next.Name;
            ProfileNameEdit = next.Name;
        }
        finally
        {
            _loading = false;
        }
        Load(next.State);
        Save();
        ScheduleApply(force: true);
    }

    /// <summary>Cancels the pending debounce and saves the current state now (call on exit). Starts no new device write.</summary>
    public Task FlushAsync()
    {
        _pendingCts?.Cancel();
        Save();
        return Task.CompletedTask;
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        // A bound ComboBox can push null transiently (e.g. while the item list changes): ignore it.
        if (_loading || value is null || value == _activeProfileName) return;
        var profile = _profiles.FirstOrDefault(p => p.Name == value);
        if (profile is null) return;
        _activeProfileName = value;
        Load(profile.State);
        ProfileNameEdit = value;
        Save();
        ScheduleApply(force: true);
    }

    partial void OnSelectedEffectChanged(EffectDefinition value)
    {
        OnPropertyChanged(nameof(ShowSpeed));
        OnPropertyChanged(nameof(ShowBrightness));
        OnPropertyChanged(nameof(ShowColor));
        OnPropertyChanged(nameof(ShowPerKeyTools));
        OnStateChanged();
    }

    partial void OnBrightnessChanged(int value) => OnStateChanged();
    partial void OnSpeedChanged(int value) => OnStateChanged();
    partial void OnColorfulChanged(bool value) => OnStateChanged();

    partial void OnColorChanged(Rgb value)
    {
        if (!_syncingColor)
        {
            _syncingColor = true;
            try { ColorHex = value.ToHex(); }
            finally { _syncingColor = false; }
        }
        if (!_syncingHue) SyncHueFrom(value);
        OnStateChanged();
    }

    partial void OnColorHexChanged(string value)
    {
        if (_syncingColor || !Rgb.TryParseHex(value, out var rgb)) return;
        _syncingColor = true;
        try { Color = rgb; }
        finally { _syncingColor = false; }
    }

    partial void OnHueChanged(double value)
    {
        if (_loading || _syncingHue) return;
        // A hue that matches the current colour's hue (e.g. a slider echoing its initial value) is not a user edit.
        if (TryGetHue(Color, out var current) && Math.Abs(value - current) < 0.5) return;
        _syncingHue = true;
        try { Color = Rgb.FromHsv(value, 1, 1); }
        finally { _syncingHue = false; }
    }

    private bool NameTaken(string name) =>
        ProfileNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private void AddProfile(string name)
    {
        _profiles.Add(new LightingProfile(name, CurrentState));
        ProfileNames.Add(name);
        _activeProfileName = name;
        _loading = true;
        try
        {
            SelectedProfile = name;
            ProfileNameEdit = name;
        }
        finally
        {
            _loading = false;
        }
        Save();
    }

    private void Load(LightingState state)
    {
        _loading = true;
        try
        {
            var s = state.Normalize();
            SelectedEffect = F75Effects.IsSupported(s.EffectId) ? F75Effects.Get(s.EffectId) : F75Effects.Get(EffectIds.FixedOn);
            Brightness = s.Brightness;
            Speed = s.Speed;
            Color = s.Color;
            SyncHueFrom(s.Color); // Color may be unchanged (no notification), so sync explicitly
            Colorful = s.Colorful;
            _keyColors = new Dictionary<int, Rgb>(s.KeyColors);
        }
        finally
        {
            _loading = false;
        }
        RefreshPreview();
    }

    private void SyncHueFrom(Rgb color)
    {
        if (!TryGetHue(color, out var hue)) return;
        _syncingHue = true;
        try { Hue = hue; }
        finally { _syncingHue = false; }
    }

    private static bool TryGetHue(Rgb c, out double hue)
    {
        hue = 0;
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var d = max - Math.Min(r, Math.Min(g, b));
        if (d <= 0) return false; // grey: keep the current hue
        hue = max == r ? 60 * (((g - b) / d % 6 + 6) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return true;
    }

    /// <summary>True when both states produce the same bytes on the keyboard.</summary>
    private static bool WireEqual(LightingState a, LightingState b)
    {
        if (a.EffectId != b.EffectId || a.Brightness != b.Brightness) return false;
        if (!F75Effects.IsSupported(a.EffectId)) return false;
        if (a.EffectId == EffectIds.PerKey)
            return a.KeyColors.Count == b.KeyColors.Count
                && a.KeyColors.All(kv => b.KeyColors.TryGetValue(kv.Key, out var v) && v == kv.Value);
        var effect = F75Effects.Get(a.EffectId);
        if (effect.HasSpeed && a.Speed != b.Speed) return false;
        if (effect.HasColor)
        {
            if (a.Colorful != b.Colorful) return false;
            if (!a.Colorful && a.Color != b.Color) return false;
        }
        return true;
    }

    private void OnStateChanged()
    {
        if (_loading) return;
        RefreshPreview();
        var index = _profiles.FindIndex(p => p.Name == _activeProfileName);
        if (index >= 0) _profiles[index] = _profiles[index] with { State = CurrentState };
        ScheduleApply();
    }

    private void Save()
    {
        try
        {
            _store.Save(new LightingProfileSet { Profiles = _profiles.ToList(), Active = _activeProfileName });
            _lastSaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastSaveError = $"Couldn't save lighting profiles: {ex.Message}";
            SetStatus(_lastSaveError, InfoBarSeverity.Error);
        }
    }

    private void ScheduleApply(bool force = false)
    {
        _pendingCts?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingCts = cts;
        if (force) _forcePending = true;
        PendingApply = ApplyAfterDebounceAsync(CurrentState, cts.Token);
    }

    private async Task ApplyAfterDebounceAsync(LightingState state, CancellationToken ct)
    {
        var mine = -1;
        void Report(string message, InfoBarSeverity severity)
        {
            if (mine == -1 || mine == _deviceSeq) SetStatus(message, severity);
        }

        try
        {
            // The token cancels only the debounce wait; a device write that has started is never interrupted.
            await _debounce(ct);
            if (ct.IsCancellationRequested) return;
            Save();
            if (!_forcePending && _lastApplied is { } last && WireEqual(last, state)) return;
            _forcePending = false;
            mine = ++_deviceSeq;
            _lastApplied = null; // unknown until the write finishes, so a revert during the write is not skipped
            await _device.ApplyAsync(state, CancellationToken.None);
            if (mine == _deviceSeq) _lastApplied = state;
            if (_lastSaveError is { } saveError) Report(saveError, InfoBarSeverity.Error);
            else Report("Applied to keyboard.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer change
        }
        catch (KeyboardUnavailableException ex)
        {
            _lastApplied = null;
            Report($"Saved in F75 Studio. {ex.Message}", InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is F75ProtocolException or IOException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            _lastApplied = null;
            Report($"The keyboard rejected the change: {ex.Message}", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            _lastApplied = null;
            Report($"Couldn't apply lighting: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnDeviceStatusChanged()
    {
        DeviceStatus = _device.Status;
        DeviceStatusText = _device.StatusText;
        if (DeviceStatus == DeviceStatus.Connected) ScheduleApply(force: true);
    }

    private void RefreshPreview()
    {
        var effect = SelectedEffect;
        foreach (var key in Keys)
        {
            key.Color = effect.Id switch
            {
                EffectIds.Off => OffPreview,
                EffectIds.PerKey => (_keyColors.TryGetValue(key.Led, out var c) ? c : UnsetKeyPreview).WithBrightness(Brightness),
                _ when !effect.HasColor || Colorful => Rgb.FromHsv(360.0 * key.X / F75Layout.CanvasWidth, 1, 1).WithBrightness(Brightness),
                _ => Color.WithBrightness(Brightness),
            };
        }
    }

    private void SetStatus(string message, InfoBarSeverity severity) => Ui.Run(() =>
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    });
}
