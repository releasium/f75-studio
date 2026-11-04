using F75Studio.App.ViewModels;
using F75Studio.Core;
using F75Studio.Lighting;
using F75Studio.Tests.TestSupport;
using Wpf.Ui.Controls;

namespace F75Studio.Tests;

public class KeyboardScreenSyncTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeKeyboardDevice _device = new();
    private readonly FakeScreenSync _sync = new();
    private JsonFileStore<LightingProfileSet> Store() => new(_temp.Combine("profiles.json"), () => new LightingProfileSet());

    public void Dispose() => _temp.Dispose();

    private KeyboardViewModel Create(bool perKeyFirst = false) =>
        new(_device, Store(), _ => Task.CompletedTask, _sync, perKeyFirst);

    private static async Task SettleAsync(KeyboardViewModel vm)
    {
        for (var i = 0; i < 3; i++)
        {
            await vm.PendingScreenSync;
            await vm.PendingApply;
        }
        await vm.PendingScreenSync;
    }

    private async Task<KeyboardViewModel> RunningAsync()
    {
        var vm = Create();
        await SettleAsync(vm);
        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSync);
        await SettleAsync(vm);
        Assert.Equal(1, _sync.Starts);
        return vm;
    }

    [Fact]
    public async Task Selecting_screen_sync_on_cable_starts_the_engine_and_keeps_the_base_effect()
    {
        var vm = Create();
        await SettleAsync(vm);

        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSync);
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Starts);
        Assert.True(vm.IsScreenSyncRunning);
        Assert.Equal("Screen sync is running.", vm.StatusMessage);
        var saved = Store().Load().Profiles.Single().State;
        Assert.Equal(EffectIds.ScreenSync, saved.EffectId);
        Assert.Equal(EffectIds.FixedOn, saved.BaseEffectId);
        Assert.All(_device.Applied, s => Assert.NotEqual(EffectIds.ScreenSync, s.EffectId));
    }

    private static CapturedFrame SyntheticFrame()
    {
        const int w = 64, h = 36;
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                bgra[i] = (byte)(x * 4);
                bgra[i + 1] = (byte)(y * 7);
                bgra[i + 2] = (byte)((x * y) % 256);
                bgra[i + 3] = 255;
            }
        return new CapturedFrame(w, h, bgra);
    }

    private static void AssertSameMap(IReadOnlyDictionary<int, Rgb> expected, IReadOnlyDictionary<int, Rgb> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (k, v) in expected) Assert.Equal(v, actual[k]);
    }

    private async Task<KeyboardViewModel> RunningBlurAsync()
    {
        var vm = Create();
        await SettleAsync(vm);
        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSyncBlur);
        await SettleAsync(vm);
        Assert.Equal(1, _sync.Starts);
        return vm;
    }

    [Fact]
    public async Task Selecting_blur_on_cable_starts_the_engine_with_the_blur_mapper()
    {
        var frame = SyntheticFrame();
        var edge = EdgeMapper.Map(frame);
        Assert.False(BlurMapper.Map(frame).All(kv => edge.TryGetValue(kv.Key, out var e) && e == kv.Value)); // frame distinguishes the mappers

        var vm = await RunningBlurAsync();

        Assert.True(vm.IsScreenSyncRunning);
        AssertSameMap(BlurMapper.Map(frame), _sync.LastMapper!(frame));
    }

    [Fact]
    public async Task Blur_to_accent_while_running_swaps_the_mapper_without_restart_or_write()
    {
        var frame = SyntheticFrame();
        var blur = BlurMapper.Map(frame);
        Assert.False(AccentMapper.Map(frame).All(kv => blur[kv.Key] == kv.Value)); // frame distinguishes the mappers
        var vm = await RunningBlurAsync();
        var count = _device.Applied.Count;

        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSyncAccent);
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Starts);
        Assert.Equal(0, _sync.Stops);
        Assert.Equal(count, _device.Applied.Count);
        AssertSameMap(AccentMapper.Map(frame), _sync.LastMapper!(frame));
        Assert.Equal(EffectIds.ScreenSyncAccent, Store().Load().Profiles.Single().State.EffectId);
    }

    [Fact]
    public async Task Edges_to_blur_while_running_swaps_the_mapper_without_restart_or_write()
    {
        var frame = SyntheticFrame();
        var vm = await RunningAsync();
        AssertSameMap(EdgeMapper.Map(frame), _sync.LastMapper!(frame));
        var count = _device.Applied.Count;

        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSyncBlur);
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Starts);
        Assert.Equal(0, _sync.Stops);
        Assert.Equal(count, _device.Applied.Count);
        Assert.True(vm.IsScreenSyncRunning);
        AssertSameMap(BlurMapper.Map(frame), _sync.LastMapper!(frame));
        var saved = Store().Load().Profiles.Single().State;
        Assert.Equal(EffectIds.ScreenSyncBlur, saved.EffectId);
        Assert.Equal(EffectIds.FixedOn, saved.BaseEffectId);
    }

    [Fact]
    public async Task Blur_to_static_stops_and_restores_the_stored_effect()
    {
        var vm = await RunningBlurAsync();

        vm.SelectedEffect = F75Effects.Get(2);
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Stops);
        Assert.False(vm.IsScreenSyncRunning);
        Assert.Equal(2, _device.Applied[^1].EffectId);
    }

    [Fact]
    public async Task Brightness_changes_while_blur_runs_do_not_write_flash()
    {
        var vm = await RunningBlurAsync();
        var count = _device.Applied.Count;

        foreach (var b in new[] { 3, 5, 7 })
        {
            vm.Brightness = b;
            await SettleAsync(vm);
        }

        Assert.Equal(count, _device.Applied.Count);
        Assert.Equal(7, _sync.Brightness!());
    }

    [Fact]
    public async Task Loading_a_blur_profile_starts_it_with_the_blur_mapper()
    {
        var frame = SyntheticFrame();
        Store().Save(new LightingProfileSet
        {
            Profiles = [new("Blur", new LightingState { EffectId = EffectIds.ScreenSyncBlur, BaseEffectId = 2 })],
            Active = "Blur",
        });

        var vm = Create();
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Starts);
        AssertSameMap(BlurMapper.Map(frame), _sync.LastMapper!(frame));
    }

    [Fact]
    public async Task On_bluetooth_it_waits_for_the_cable()
    {
        _device.SetStatus(DeviceStatus.Bluetooth, "Bluetooth");
        var vm = Create();
        await SettleAsync(vm);

        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSync);
        await SettleAsync(vm);

        Assert.Equal(0, _sync.Starts);
        Assert.Equal("Screen sync needs the USB cable.", vm.StatusMessage);
        Assert.Equal(InfoBarSeverity.Informational, vm.StatusSeverity);

        _device.SetStatus(DeviceStatus.Connected, "Connected");
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Starts);
        Assert.All(_device.Applied, s => Assert.NotEqual(EffectIds.ScreenSync, s.EffectId));
    }

    [Fact]
    public async Task Switching_away_stops_and_restores_the_stored_effect()
    {
        var vm = await RunningAsync();

        vm.SelectedEffect = F75Effects.Get(2);
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Stops);
        Assert.False(vm.IsScreenSyncRunning);
        Assert.Equal(2, _device.Applied[^1].EffectId);
    }

    [Fact]
    public async Task Brightness_changes_while_live_do_not_write_flash()
    {
        var vm = await RunningAsync();
        var count = _device.Applied.Count;

        foreach (var b in new[] { 3, 5, 7 })
        {
            vm.Brightness = b;
            await SettleAsync(vm);
        }

        Assert.Equal(count, _device.Applied.Count);
        Assert.Equal(7, Store().Load().Profiles.Single().State.Brightness);
        Assert.Equal(7, _sync.Brightness!());
    }

    [Fact]
    public async Task Fault_shows_reason_and_stops()
    {
        var vm = await RunningAsync();

        _sync.RaiseFaulted("unplugged");

        Assert.Equal(InfoBarSeverity.Warning, vm.StatusSeverity);
        Assert.Contains("unplugged", vm.StatusMessage);
        Assert.False(vm.IsScreenSyncRunning);
        await SettleAsync(vm);
        Assert.Equal(1, _sync.Starts); // no automatic restart
    }

    [Fact]
    public async Task Lock_stops_and_unlock_restarts()
    {
        var vm = await RunningAsync();
        var count = _device.Applied.Count;

        vm.SetScreenLocked(true);
        await SettleAsync(vm);
        Assert.Equal(1, _sync.Stops);
        Assert.Equal(count + 1, _device.Applied.Count);
        Assert.Equal(EffectIds.FixedOn, _device.Applied[^1].EffectId);

        vm.SetScreenLocked(false);
        await SettleAsync(vm);
        Assert.Equal(2, _sync.Starts);
    }

    [Fact]
    public async Task Suspend_stops_and_resume_restarts()
    {
        var vm = await RunningAsync();

        vm.SetSuspended(true);
        await SettleAsync(vm);
        Assert.Equal(1, _sync.Stops);

        vm.SetSuspended(false);
        await SettleAsync(vm);
        Assert.Equal(2, _sync.Starts);
    }

    [Fact]
    public async Task Resume_does_not_restart_while_the_screen_is_still_locked()
    {
        var vm = await RunningAsync();

        vm.SetScreenLocked(true);
        vm.SetSuspended(true);
        await SettleAsync(vm);
        vm.SetSuspended(false);
        await SettleAsync(vm);
        Assert.Equal(1, _sync.Starts);

        vm.SetScreenLocked(false);
        await SettleAsync(vm);
        Assert.Equal(2, _sync.Starts);
    }

    [Fact]
    public async Task Unlock_does_not_restart_while_still_suspended()
    {
        var vm = await RunningAsync();

        vm.SetSuspended(true);
        vm.SetScreenLocked(true);
        await SettleAsync(vm);
        vm.SetScreenLocked(false);
        await SettleAsync(vm);
        Assert.Equal(1, _sync.Starts);

        vm.SetSuspended(false);
        await SettleAsync(vm);
        Assert.Equal(2, _sync.Starts);
    }

    [Fact]
    public async Task Per_key_first_flag_writes_effect_21_before_streaming()
    {
        var vm = Create(perKeyFirst: true);
        await SettleAsync(vm);
        var appliedAtStart = -1;
        var perKeyWritten = false;
        _sync.OnStart = () =>
        {
            appliedAtStart = _device.Applied.Count;
            perKeyWritten = _device.Applied.Any(s => s.EffectId == EffectIds.PerKey);
        };

        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSync);
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Starts);
        Assert.True(perKeyWritten);
        Assert.True(appliedAtStart >= 1);
        Assert.Empty(_device.Applied.Where(s => s.EffectId == EffectIds.ScreenSync));
    }

    [Fact]
    public async Task Loading_a_screen_sync_profile_starts_it()
    {
        Store().Save(new LightingProfileSet
        {
            Profiles = [new("Sync", new LightingState { EffectId = EffectIds.ScreenSync, BaseEffectId = 2 })],
            Active = "Sync",
        });

        var vm = Create();
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Starts);
        Assert.Equal(2, _device.Applied.Single().EffectId);
    }

    [Fact]
    public async Task Frames_update_the_preview()
    {
        var vm = await RunningAsync();
        var esc = vm.Keys.First(k => k.Name == "Esc");

        _sync.RaiseFrame(new Dictionary<int, Rgb> { [esc.Led] = new Rgb(255, 0, 0) });

        Assert.Equal(new Rgb(255, 0, 0), esc.Color);
    }

    [Fact]
    public async Task Stop_for_exit_does_not_restore()
    {
        var vm = await RunningAsync();
        var count = _device.Applied.Count;

        await vm.StopScreenSyncAsync();
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Stops);
        Assert.Equal(count, _device.Applied.Count);
    }

    private async Task<KeyboardViewModel> RunningWithProfilesAsync()
    {
        LightingProfile P(string name, int baseId) => new(name, new LightingState { EffectId = EffectIds.ScreenSync, BaseEffectId = baseId });
        Store().Save(new LightingProfileSet { Profiles = [P("A", 1), P("B", 2), P("C", 1)], Active = "A" });
        var vm = Create();
        await SettleAsync(vm);
        Assert.Equal(1, _sync.Starts);
        Assert.True(vm.IsScreenSyncRunning);
        return vm;
    }

    [Fact]
    public async Task Switching_to_another_live_profile_restarts_around_the_stored_write()
    {
        var vm = await RunningWithProfilesAsync();

        vm.SelectedProfile = "B";
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Stops);
        Assert.Equal(2, _sync.Starts);
        Assert.True(vm.IsScreenSyncRunning);
        Assert.Equal(2, _device.Applied[^1].EffectId);
        Assert.DoesNotContain("stopped", vm.StatusMessage ?? "");
    }

    [Fact]
    public async Task Switching_to_a_live_profile_with_the_same_stored_part_keeps_streaming()
    {
        var vm = await RunningWithProfilesAsync();
        var count = _device.Applied.Count;

        vm.SelectedProfile = "C";
        await SettleAsync(vm);

        Assert.Equal(0, _sync.Stops);
        Assert.Equal(1, _sync.Starts);
        Assert.Equal(count, _device.Applied.Count);
        Assert.True(vm.IsScreenSyncRunning);
    }

    [Fact]
    public async Task Tray_reapply_of_the_active_live_profile_keeps_it_running()
    {
        var vm = await RunningWithProfilesAsync();

        Assert.True(vm.ApplyProfileByName("A"));
        await SettleAsync(vm);

        Assert.Equal(0, _sync.Stops);
        Assert.Equal(1, _sync.Starts);
        Assert.True(vm.IsScreenSyncRunning);
    }

    [Fact]
    public async Task Reapplying_after_a_fault_restarts_screen_sync()
    {
        var vm = await RunningWithProfilesAsync();
        _sync.RaiseFaulted("unplugged");
        await SettleAsync(vm);
        Assert.False(vm.IsScreenSyncRunning);
        Assert.Equal(1, _sync.Starts);

        Assert.True(vm.ApplyProfileByName("A"));
        await SettleAsync(vm);

        Assert.Equal(2, _sync.Starts);
        Assert.True(vm.IsScreenSyncRunning);
    }

    [Fact]
    public async Task A_fault_on_the_cable_rewrites_the_stored_effect()
    {
        var vm = await RunningAsync();
        var count = _device.Applied.Count;

        _sync.RaiseFaulted("capture died");
        await SettleAsync(vm);

        Assert.Equal(count + 1, _device.Applied.Count);
        Assert.Equal(EffectIds.FixedOn, _device.Applied[^1].EffectId);
        Assert.Contains("capture died", vm.StatusMessage);
    }

    [Fact]
    public async Task Start_failure_warns_and_does_not_run()
    {
        _sync.StartFailWith = new KeyboardUnavailableException("no cable");
        var vm = Create();
        await SettleAsync(vm);

        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSync);
        await SettleAsync(vm);

        Assert.False(vm.IsScreenSyncRunning);
        Assert.Equal(InfoBarSeverity.Warning, vm.StatusSeverity);
        Assert.Contains("no cable", vm.StatusMessage);
    }

    [Fact]
    public async Task Start_failure_after_the_per_key_write_restores_the_stored_effect()
    {
        _sync.StartFailWith = new KeyboardUnavailableException("no cable");
        var vm = Create(perKeyFirst: true);
        await SettleAsync(vm);

        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSync);
        await SettleAsync(vm);

        Assert.Contains(_device.Applied, s => s.EffectId == EffectIds.PerKey);
        Assert.Equal(EffectIds.FixedOn, _device.Applied[^1].EffectId);
        Assert.False(vm.IsScreenSyncRunning);
    }

    [Fact]
    public async Task Frames_within_100ms_update_the_preview_once()
    {
        var vm = await RunningAsync();
        var esc = vm.Keys.First(k => k.Name == "Esc");

        _sync.RaiseFrame(new Dictionary<int, Rgb> { [esc.Led] = new Rgb(255, 0, 0) });
        _sync.RaiseFrame(new Dictionary<int, Rgb> { [esc.Led] = new Rgb(0, 255, 0) });

        Assert.Equal(new Rgb(255, 0, 0), esc.Color);
    }

    [Fact]
    public async Task A_stale_fault_from_a_replaced_run_is_ignored()
    {
        var vm = await RunningAsync();

        // The engine is running again by the time the old run's fault reaches the UI.
        _sync.RaiseFaultedWhileRunning("old run");

        Assert.True(vm.IsScreenSyncRunning);
        Assert.NotEqual(InfoBarSeverity.Warning, vm.StatusSeverity);
    }

    [Fact]
    public async Task Paused_capture_shows_the_warning_instead_of_running()
    {
        var vm = await RunningAsync();

        _sync.RaiseCaptureStatus("Screen capture paused");
        Assert.Equal(InfoBarSeverity.Warning, vm.StatusSeverity);
        Assert.Equal("Screen capture paused", vm.StatusMessage);

        _sync.RaiseCaptureStatus(null);
        Assert.Equal("Screen sync is running.", vm.StatusMessage);
    }
}
