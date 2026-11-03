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
            store.Save(set);
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
        if (match == SelectedProfile) ScheduleApply();
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
        while (ProfileNames.Contains($"Profile {n}")) n++;
        AddProfile($"Profile {n}");
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        var name = $"{SelectedProfile} copy";
        var n = 2;
        while (ProfileNames.Contains(name)) name = $"{SelectedProfile} copy {n++}";
        AddProfile(name);
    }

    [RelayCommand]
    private void RenameProfile()
    {
        var newName = ProfileNameEdit.Trim();
        if (newName.Length == 0 || SelectedProfile is null) return;
        if (ProfileNames.Any(n => n != SelectedProfile && string.Equals(n, newName, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"A profile named \"{newName}\" already exists.", InfoBarSeverity.Error);
            return;
        }
        var index = _profiles.FindIndex(p => p.Name == SelectedProfile);
        _profiles[index] = _profiles[index] with { Name = newName };
        _loading = true;
        ProfileNames[index] = newName;
        SelectedProfile = newName;
        _loading = false;
        Save();
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (_profiles.Count <= 1 || SelectedProfile is null)
        {
            SetStatus("You need at least one lighting profile.", InfoBarSeverity.Error);
            return;
        }
        var index = _profiles.FindIndex(p => p.Name == SelectedProfile);
        _profiles.RemoveAt(index);
        ProfileNames.RemoveAt(index);
        SelectedProfile = _profiles[0].Name;
        Save();
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (_loading || value is null) return;
        var profile = _profiles.FirstOrDefault(p => p.Name == value);
        if (profile is null) return;
        Load(profile.State);
        ProfileNameEdit = value;
        Save();
        ScheduleApply();
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
            ColorHex = value.ToHex();
            _syncingColor = false;
        }
        OnStateChanged();
    }

    partial void OnColorHexChanged(string value)
    {
        if (_syncingColor || !Rgb.TryParseHex(value, out var rgb)) return;
        _syncingColor = true;
        Color = rgb;
        _syncingColor = false;
    }

    partial void OnHueChanged(double value)
    {
        if (_loading) return;
        Color = Rgb.FromHsv(value, 1, 1);
    }

    private void AddProfile(string name)
    {
        _profiles.Add(new LightingProfile(name, CurrentState));
        ProfileNames.Add(name);
        _loading = true;
        SelectedProfile = name;
        ProfileNameEdit = name;
        _loading = false;
        Save();
    }

    private void Load(LightingState state)
    {
        _loading = true;
        var s = state.Normalize();
        SelectedEffect = F75Effects.IsSupported(s.EffectId) ? F75Effects.Get(s.EffectId) : F75Effects.Get(EffectIds.FixedOn);
        Brightness = s.Brightness;
        Speed = s.Speed;
        Color = s.Color;
        Colorful = s.Colorful;
        _keyColors = new Dictionary<int, Rgb>(s.KeyColors);
        _loading = false;
        RefreshPreview();
    }

    private void OnStateChanged()
    {
        if (_loading) return;
        RefreshPreview();
        var index = _profiles.FindIndex(p => p.Name == SelectedProfile);
        if (index >= 0) _profiles[index] = _profiles[index] with { State = CurrentState };
        Save();
        ScheduleApply();
    }

    private void Save() => _store.Save(new LightingProfileSet { Profiles = _profiles.ToList(), Active = SelectedProfile });

    private void ScheduleApply()
    {
        _pendingCts?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingCts = cts;
        var state = CurrentState;
        PendingApply = ApplyAfterDebounceAsync(state, cts.Token);
    }

    private async Task ApplyAfterDebounceAsync(LightingState state, CancellationToken ct)
    {
        try
        {
            await _debounce(ct);
            if (ct.IsCancellationRequested) return;
            await _device.ApplyAsync(state, ct);
            SetStatus("Applied to keyboard.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer change
        }
        catch (KeyboardUnavailableException ex)
        {
            SetStatus($"Saved in F75 Studio. {ex.Message}", InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is F75ProtocolException or IOException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            SetStatus($"The keyboard rejected the change: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnDeviceStatusChanged()
    {
        DeviceStatus = _device.Status;
        DeviceStatusText = _device.StatusText;
        if (DeviceStatus == DeviceStatus.Connected) ScheduleApply();
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
