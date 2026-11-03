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
        await vm.PendingScreenSync;
        await vm.PendingApply;
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

        vm.SetScreenLocked(true);
        await SettleAsync(vm);
        Assert.Equal(1, _sync.Stops);

        vm.SetScreenLocked(false);
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
