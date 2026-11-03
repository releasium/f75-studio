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
    private static readonly HashSet<int> RealLeds = F75Layout.Keys.Select(k => k.Led).ToHashSet();

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
    private bool _initializing;
    private bool _loadFailed;

    private readonly IScreenSync? _screenSync;
    private readonly bool _selectPerKeyBeforeLive;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private int _baseEffectId = EffectIds.FixedOn;
    private int _brightnessForLive = 9;
    private bool _screenRunning;
    private bool _locked;
    private bool _faulted;
    private volatile bool _exiting;
    private bool _holdFaultStatus;
    private string? _capturePaused;
    private long _lastPreviewMs = long.MinValue;

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
    [ObservableProperty] private bool _isScreenSyncRunning;

    public KeyboardViewModel(IKeyboardDevice device, JsonFileStore<LightingProfileSet> store, Func<CancellationToken, Task>? debounce = null,
        IScreenSync? screenSync = null, bool selectPerKeyBeforeLive = false)
    {
        _device = device;
        _screenSync = screenSync;
        _selectPerKeyBeforeLive = selectPerKeyBeforeLive;
        _store = store;
        _debounce = debounce ?? (ct => Task.Delay(500, ct));
        Keys = F75Layout.Keys.Select(k => new KeyItemViewModel(k)).ToList();
        _initializing = true;
        if (screenSync is not null)
        {
            screenSync.FrameSent += OnFrameSent;
            screenSync.Faulted += reason => Ui.Run(() => OnScreenSyncFaulted(reason));
            screenSync.CaptureStatusChanged += paused => Ui.Run(() => OnCaptureStatusChanged(paused));
        }
        // Subscribe before reading the status so a change in between is not lost.
        device.StatusChanged += (_, _) => Ui.Run(OnDeviceStatusChanged);
        _deviceStatus = device.Status;
        _deviceStatusText = device.StatusText;

        LightingProfileSet set;
        string? loadError = null;
        try
        {
            set = store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable (locked, no access): run on defaults, and don't overwrite the file we couldn't read.
            set = new LightingProfileSet();
            _loadFailed = true;
            loadError = $"Couldn't read lighting profiles ({ex.Message}). Using defaults; your saved profiles were left untouched.";
        }

        _profiles = Sanitize(set.Profiles);
        var createdDefault = _profiles.Count == 0;
        if (createdDefault)
        {
            _profiles.Add(new LightingProfile("Default", new LightingState()));
            set = new LightingProfileSet { Profiles = _profiles.ToList(), Active = "Default" };
            if (!_loadFailed)
            {
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
        }
        foreach (var p in _profiles) ProfileNames.Add(p.Name);
        SelectedProfile = _profiles.Any(p => p.Name == set.Active) ? set.Active : _profiles[0].Name;
        _initializing = false;
        if (loadError is not null)
        {
            _lastSaveError = loadError; // keeps the error showing instead of "Applied" until a save succeeds
            SetStatus(loadError, InfoBarSeverity.Error);
        }
    }

    /// <summary>Drops unusable entries from a hand-edited file: null/blank names or state, duplicate names (first wins), non-key LEDs.</summary>
    private static List<LightingProfile> Sanitize(IReadOnlyList<LightingProfile>? profiles)
    {
        var result = new List<LightingProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles ?? [])
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Name) || p.State is null) continue;
            if (!seen.Add(p.Name)) continue;
            result.Add(p with { State = p.State with { KeyColors = RealKeyColors(p.State.KeyColors) } });
        }
        return result;
    }

    private static Dictionary<int, Rgb> RealKeyColors(IReadOnlyDictionary<int, Rgb>? colors) =>
        colors is null ? [] : colors.Where(kv => RealLeds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

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
        BaseEffectId = _baseEffectId,
    };

    public IReadOnlyList<string> ProfileNamesSnapshot() => ProfileNames.ToList();

    public bool ApplyProfileByName(string name)
    {
        var match = ProfileNames.FirstOrDefault(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        if (match == _activeProfileName)
        {
            _faulted = false;
            ScheduleApply(force: true);
            UpdateScreenSync();
        }
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
        _loadFailed = false;
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
        _loadFailed = false;
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
        UpdateScreenSync();
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
        // At startup, skip the forced apply while the keyboard is not on the cable (the Connected transition applies later).
        if (!_initializing || _device.Status == DeviceStatus.Connected) ScheduleApply(force: true);
        UpdateScreenSync();
    }

    partial void OnSelectedEffectChanged(EffectDefinition value)
    {
        if (!value.IsLive) _baseEffectId = value.Id;
        _faulted = false; // choosing an effect (or loading a profile) allows screen sync to start again
        OnPropertyChanged(nameof(ShowSpeed));
        OnPropertyChanged(nameof(ShowBrightness));
        OnPropertyChanged(nameof(ShowColor));
        OnPropertyChanged(nameof(ShowPerKeyTools));
        OnStateChanged();
    }

    partial void OnBrightnessChanged(int value)
    {
        Volatile.Write(ref _brightnessForLive, value);
        OnStateChanged();
    }
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
        _loadFailed = false;
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
            var effect = F75Effects.IsSupported(s.EffectId) ? F75Effects.Get(s.EffectId) : F75Effects.Get(EffectIds.FixedOn);
            var savedBase = F75Effects.IsSupported(s.BaseEffectId) && !F75Effects.Get(s.BaseEffectId).IsLive ? s.BaseEffectId : EffectIds.FixedOn;
            _baseEffectId = effect.IsLive ? savedBase : effect.Id;
            SelectedEffect = effect;
            Brightness = s.Brightness;
            Speed = s.Speed;
            Color = s.Color;
            SyncHueFrom(s.Color); // Color may be unchanged (no notification), so sync explicitly
            Colorful = s.Colorful;
            _keyColors = RealKeyColors(s.KeyColors);
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
        _loadFailed = false; // a real user edit: saving is allowed again
        RefreshPreview();
        var index = _profiles.FindIndex(p => p.Name == _activeProfileName);
        if (index >= 0) _profiles[index] = _profiles[index] with { State = CurrentState };
        // When leaving screen sync, the stop path schedules the (forced) restore itself: don't write twice.
        if (!(_screenRunning && !ShouldRunScreenSync())) ScheduleApply();
        UpdateScreenSync();
    }

    private void Save()
    {
        if (_loadFailed) return; // never overwrite a profiles file we couldn't read, until the user changes something
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
            // While a live effect is selected the keyboard only gets stored writes when forced (never on brightness drags).
            if (!_forcePending && state.EffectId == EffectIds.ScreenSync) return;
            var stored = state.StoredState();
            if (!_forcePending && _lastApplied is { } last && WireEqual(last, stored)) return;
            _forcePending = false;
            mine = ++_deviceSeq;

            // A stored write ends the device's live session, so it goes through the screen-sync gate:
            // stop the engine, write, then let UpdateScreenSync restart it if it should still run.
            var gateHeld = false;
            var restartSync = false;
            if (_screenSync is not null)
            {
                await _syncGate.WaitAsync(CancellationToken.None);
                gateHeld = true;
            }
            try
            {
                if (_screenRunning && _screenSync is not null)
                {
                    // Still streaming a live effect whose stored part is already on the keyboard: nothing to write.
                    if (state.EffectId == EffectIds.ScreenSync && _lastApplied is { } onKeyboard && WireEqual(onKeyboard, stored))
                        return;
                    try { await _screenSync.StopAsync(); }
                    catch { /* the engine cleans up after itself */ }
                    _screenRunning = false;
                    IsScreenSyncRunning = false;
                    _capturePaused = null;
                    RefreshPreview();
                    restartSync = true;
                }
                _lastApplied = null; // unknown until the write finishes, so a revert during the write is not skipped
                // HID I/O (even HidSharp's synchronous parts) runs on the pool; this method resumes on the UI context.
                await Task.Run(() => _device.ApplyAsync(stored, CancellationToken.None));
                if (mine == _deviceSeq) _lastApplied = stored;
            }
            finally
            {
                if (gateHeld) _syncGate.Release();
                if (restartSync) UpdateScreenSync();
            }
            if (_lastSaveError is { } saveError) Report(saveError, InfoBarSeverity.Error);
            else if (_holdFaultStatus) _holdFaultStatus = false; // keep the "Screen sync stopped" message visible
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
        var was = DeviceStatus;
        DeviceStatus = _device.Status;
        DeviceStatusText = _device.StatusText;
        if (DeviceStatus == DeviceStatus.Connected)
        {
            if (was != DeviceStatus.Connected) _faulted = false;
            // A repeated Connected notification must not write settings in the middle of a live stream.
            if (!(_screenRunning && was == DeviceStatus.Connected)) ScheduleApply(force: true);
        }
        UpdateScreenSync();
    }

    private void RefreshPreview()
    {
        if (SelectedEffect.IsLive && _screenRunning) return; // frames drive the preview
        var effect = SelectedEffect.IsLive ? F75Effects.Get(_baseEffectId) : SelectedEffect;
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

    // ---- Screen sync -------------------------------------------------------------------------------------------

    /// <summary>Completes when the most recent screen-sync start/stop decision has been carried out (tests await it).</summary>
    public Task PendingScreenSync { get; private set; } = Task.CompletedTask;

    /// <summary>Stops the engine without restoring the stored effect (used on exit).</summary>
    public async Task StopScreenSyncAsync()
    {
        _exiting = true; // any in-flight start/update bails out
        if (_screenSync is null) return;
        // Not through the gate (a long start must not block exit); the engine's StopAsync is idempotent.
        try { await _screenSync.StopAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch { /* shutting down anyway */ }
        _screenRunning = false;
    }

    /// <summary>Screen locked: stop streaming (and restore the stored effect); unlocked: start again if it should run.</summary>
    public void SetScreenLocked(bool locked)
    {
        _locked = locked;
        UpdateScreenSync();
    }

    private bool ShouldRunScreenSync() =>
        _screenSync is not null && SelectedEffect.IsLive && DeviceStatus == DeviceStatus.Connected
        && !_locked && !_faulted && !_exiting;

    private void UpdateScreenSync() => PendingScreenSync = UpdateScreenSyncAsync();

    private async Task UpdateScreenSyncAsync()
    {
        if (_screenSync is null) return;
        // Before starting, let queued stored writes (profile switch, Connected) finish. Done outside the gate:
        // those writes take the gate themselves.
        if (ShouldRunScreenSync() && !_screenRunning)
        {
            Task pending;
            do
            {
                pending = PendingApply;
                await pending;
            } while (!ReferenceEquals(pending, PendingApply));
        }
        await _syncGate.WaitAsync();
        try
        {
            if (ShouldRunScreenSync())
            {
                if (!_screenRunning) await StartScreenSyncAsync(_screenSync);
            }
            else if (_screenRunning)
            {
                await StopScreenSyncCoreAsync(_screenSync);
            }
            else if (SelectedEffect.IsLive && !_locked && DeviceStatus != DeviceStatus.Connected)
            {
                SetStatus("Screen sync needs the USB cable.", InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Screen sync error: {ex.Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task StartScreenSyncAsync(IScreenSync sync)
    {
        _capturePaused = null;
        Interlocked.Exchange(ref _lastPreviewMs, long.MinValue);
        var wroteStored = false;
        try
        {
            if (_selectPerKeyBeforeLive)
            {
                var perKey = CurrentState.StoredState() with { EffectId = EffectIds.PerKey, KeyColors = new Dictionary<int, Rgb>() };
                _lastApplied = null;
                wroteStored = true;
                await Task.Run(() => _device.ApplyAsync(perKey, CancellationToken.None));
            }
            await sync.StartAsync(() => Volatile.Read(ref _brightnessForLive));
        }
        catch (Exception ex)
        {
            SetStatus($"Screen sync couldn't start: {ex.Message}", InfoBarSeverity.Warning);
            if (wroteStored && DeviceStatus == DeviceStatus.Connected) ScheduleApply(force: true);
            return;
        }

        if (_exiting)
        {
            try { await sync.StopAsync(); } catch { /* shutting down anyway */ }
            return;
        }
        _screenRunning = true;
        IsScreenSyncRunning = true;
        ShowRunningStatus();
    }

    private async Task StopScreenSyncCoreAsync(IScreenSync sync)
    {
        try { await sync.StopAsync(); }
        catch { /* the engine cleans up after itself; carry on restoring */ }
        _screenRunning = false;
        IsScreenSyncRunning = false;
        _capturePaused = null;
        RefreshPreview();
        if (_exiting) return;
        if (DeviceStatus == DeviceStatus.Connected) ScheduleApply(force: true);
        else if (SelectedEffect.IsLive && !_locked) SetStatus("Screen sync needs the USB cable.", InfoBarSeverity.Informational);
    }

    private void ShowRunningStatus()
    {
        if (_capturePaused is { } text) SetStatus(text, InfoBarSeverity.Warning);
        else SetStatus("Screen sync is running.", InfoBarSeverity.Success);
    }

    private void OnCaptureStatusChanged(string? paused)
    {
        _capturePaused = paused;
        if (_screenRunning) ShowRunningStatus();
    }

    private void OnScreenSyncFaulted(string reason)
    {
        if (_exiting || _screenSync?.IsRunning == true) return; // exiting, or a stale fault from a run that was already replaced
        _faulted = true; // no automatic restart until the next Connected transition or effect selection
        _screenRunning = false;
        IsScreenSyncRunning = false;
        _capturePaused = null;
        RefreshPreview();
        SetStatus($"Screen sync stopped: {reason}", InfoBarSeverity.Warning);
        // The keyboard is left on the last live frame: write the stored effect again, unless the device left the cable
        // (the Connected transition applies it then).
        if (DeviceStatus == DeviceStatus.Connected)
        {
            _lastApplied = null;
            _holdFaultStatus = true;
            ScheduleApply(force: true);
        }
    }

    /// <summary>Engine thread: throttles to one preview update per 100 ms.</summary>
    private void OnFrameSent(IReadOnlyDictionary<int, Rgb> frame)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastPreviewMs);
        if (last != long.MinValue && now - last < 100) return;
        Interlocked.Exchange(ref _lastPreviewMs, now);
        Ui.Run(() =>
        {
            if (!_screenRunning) return;
            foreach (var key in Keys) key.Color = frame.TryGetValue(key.Led, out var c) ? c : Rgb.Black;
        });
    }

    private void SetStatus(string message, InfoBarSeverity severity) => Ui.Run(() =>
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    });
}
