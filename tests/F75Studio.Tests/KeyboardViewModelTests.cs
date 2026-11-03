using F75Studio.App.ViewModels;
using F75Studio.Core;
using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using F75Studio.Tests.TestSupport;
using Wpf.Ui.Controls;

namespace F75Studio.Tests;

public class KeyboardViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeKeyboardDevice _device = new();
    private JsonFileStore<LightingProfileSet> Store() => new(_temp.Combine("profiles.json"), () => new LightingProfileSet());

    public void Dispose() => _temp.Dispose();

    private KeyboardViewModel Create(JsonFileStore<LightingProfileSet>? store = null) =>
        new(_device, store ?? Store(), _ => Task.CompletedTask);

    [Fact]
    public async Task First_run_creates_a_default_profile_and_applies_it()
    {
        var vm = Create();
        await vm.PendingApply;

        Assert.Equal(new[] { "Default" }, vm.ProfileNames);
        Assert.Equal("Default", vm.SelectedProfile);
        Assert.Single(_device.Applied);
        Assert.Equal("Default", Store().Load().Active);
    }

    [Fact]
    public async Task Changing_brightness_saves_and_applies()
    {
        var vm = Create();
        await vm.PendingApply;

        vm.Brightness = 3;
        await vm.PendingApply;

        Assert.Equal(3, _device.Applied[^1].Brightness);
        Assert.Equal(3, Store().Load().Profiles.Single().State.Brightness);
        Assert.Equal(InfoBarSeverity.Success, vm.StatusSeverity);
    }

    [Fact]
    public async Task Rapid_changes_collapse_into_one_apply()
    {
        var gate = new TaskCompletionSource();
        var vm = new KeyboardViewModel(_device, Store(), _ => gate.Task);
        vm.Brightness = 1;
        vm.Brightness = 2;
        vm.Brightness = 4;
        gate.SetResult();
        await vm.PendingApply;

        Assert.Single(_device.Applied);
        Assert.Equal(4, _device.Applied[0].Brightness);
    }

    [Fact]
    public async Task Hex_hue_and_colour_stay_in_sync()
    {
        var vm = Create();
        vm.ColorHex = "#00FF00";
        Assert.Equal(new Rgb(0, 255, 0), vm.Color);

        vm.Hue = 240;
        Assert.Equal("#0000FF", vm.ColorHex);

        vm.ColorHex = "nope";
        Assert.Equal(new Rgb(0, 0, 255), vm.Color);
        await vm.PendingApply;
    }

    [Fact]
    public async Task Painting_a_group_switches_to_per_key()
    {
        var vm = Create();
        vm.Color = new Rgb(255, 0, 0);
        vm.SelectGroupCommand.Execute("WASD");
        vm.PaintSelectedCommand.Execute(null);
        await vm.PendingApply;

        var applied = _device.Applied[^1];
        Assert.Equal(EffectIds.PerKey, applied.EffectId);
        Assert.Equal(new Rgb(255, 0, 0), applied.KeyColors[14]);
        Assert.Equal(4, applied.KeyColors.Count);
        Assert.Equal(new Rgb(255, 0, 0).WithBrightness(9), vm.Keys.Single(k => k.Name == "W").Color);
    }

    [Fact]
    public void Click_selects_and_ctrl_click_adds()
    {
        var vm = Create();
        var q = vm.Keys.Single(k => k.Name == "Q");
        var w = vm.Keys.Single(k => k.Name == "W");

        vm.ClickKey(q, additive: false);
        vm.ClickKey(w, additive: false);
        Assert.False(q.IsSelected);
        Assert.True(w.IsSelected);

        vm.ClickKey(q, additive: true);
        vm.DragOverKey(vm.Keys.Single(k => k.Name == "E"));
        Assert.Equal(3, vm.Keys.Count(k => k.IsSelected));
    }

    [Fact]
    public async Task Unavailable_keyboard_is_a_warning_not_a_crash()
    {
        _device.FailWith = new KeyboardUnavailableException("Connected by Bluetooth.");
        var vm = Create();
        await vm.PendingApply;

        Assert.Equal(InfoBarSeverity.Warning, vm.StatusSeverity);
        Assert.Contains("Connected by Bluetooth.", vm.StatusMessage);
    }

    [Fact]
    public async Task Protocol_error_is_an_error_status()
    {
        _device.FailWith = new F75ProtocolException("bad page");
        var vm = Create();
        await vm.PendingApply;
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity);
    }

    [Fact]
    public async Task Unsupported_effect_from_a_hand_edited_profile_is_an_error_status_not_a_crash()
    {
        _device.FailWith = new ArgumentOutOfRangeException("effect");
        var vm = Create();
        await vm.PendingApply;
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity);
    }

    [Fact]
    public async Task Profiles_new_duplicate_rename_delete()
    {
        var vm = Create();
        vm.NewProfileCommand.Execute(null);
        Assert.Equal("Profile 1", vm.SelectedProfile);

        vm.DuplicateProfileCommand.Execute(null);
        Assert.Equal("Profile 1 copy", vm.SelectedProfile);

        vm.ProfileNameEdit = "Gaming";
        vm.RenameProfileCommand.Execute(null);
        Assert.Contains("Gaming", vm.ProfileNames);

        vm.ProfileNameEdit = "Default";
        vm.RenameProfileCommand.Execute(null);
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity); // duplicate name refused

        vm.DeleteProfileCommand.Execute(null);
        Assert.DoesNotContain("Gaming", vm.ProfileNames);
        await vm.PendingApply;
        Assert.Equal(vm.ProfileNames.Count, Store().Load().Profiles.Count);
    }

    [Fact]
    public void Last_profile_cannot_be_deleted()
    {
        var vm = Create();
        vm.DeleteProfileCommand.Execute(null);
        Assert.Single(vm.ProfileNames);
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity);
    }

    [Fact]
    public async Task Apply_profile_by_name_is_case_insensitive()
    {
        var store = Store();
        store.Save(new LightingProfileSet
        {
            Profiles = [new("Default", new LightingState()), new("Gaming", new LightingState { EffectId = 3 })],
            Active = "Default",
        });
        var vm = Create(store);
        await vm.PendingApply;

        Assert.True(vm.ApplyProfileByName("gaming"));
        await vm.PendingApply;
        Assert.Equal(3, _device.Applied[^1].EffectId);
        Assert.False(vm.ApplyProfileByName("nope"));
    }

    [Fact]
    public async Task Device_connecting_reapplies_the_active_profile()
    {
        _device.SetStatus(DeviceStatus.Bluetooth, "Bluetooth");
        var vm = Create();
        await vm.PendingApply;
        var before = _device.Applied.Count;

        _device.SetStatus(DeviceStatus.Connected, "Connected by USB cable");
        await vm.PendingApply;

        Assert.Equal("Connected by USB cable", vm.DeviceStatusText);
        Assert.Equal(before + 1, _device.Applied.Count);
    }

    [Fact]
    public void Effect_flags_drive_visible_controls()
    {
        var vm = Create();
        vm.SelectedEffect = F75Effects.Get(3);
        Assert.True(vm.ShowSpeed);
        Assert.False(vm.ShowColor);
        vm.SelectedEffect = F75Effects.Get(EffectIds.PerKey);
        Assert.True(vm.ShowPerKeyTools);
    }

    [Fact]
    public async Task General_device_exception_is_an_error_status()
    {
        _device.FailWith = new Exception("boom");
        var vm = Create();
        await vm.PendingApply;
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity);
        Assert.Contains("Couldn't apply lighting: boom", vm.StatusMessage);
    }

    [Fact]
    public async Task Changes_that_do_not_alter_the_wire_state_do_not_reach_the_device()
    {
        var vm = Create();
        await vm.PendingApply;

        vm.Speed = 4; // Static has no speed
        await vm.PendingApply;
        Assert.Single(_device.Applied);

        vm.SelectGroupCommand.Execute("WASD");
        vm.PaintSelectedCommand.Execute(null);
        await vm.PendingApply;
        var count = _device.Applied.Count;

        vm.Color = new Rgb(1, 2, 3); // colour is irrelevant in per-key mode
        vm.Hue = 100;
        vm.PickSwatchCommand.Execute(vm.Swatches[0]);
        await vm.PendingApply;
        Assert.Equal(count, _device.Applied.Count);
    }

    [Fact]
    public async Task Repainting_other_keys_in_per_key_mode_applies()
    {
        var vm = Create();
        vm.SelectGroupCommand.Execute("WASD");
        vm.PaintSelectedCommand.Execute(null);
        await vm.PendingApply;
        var count = _device.Applied.Count;

        vm.Color = new Rgb(0, 255, 0);
        vm.ClickKey(vm.Keys.Single(k => k.Name == "Q"), additive: false);
        vm.PaintSelectedCommand.Execute(null);
        await vm.PendingApply;
        Assert.Equal(count + 1, _device.Applied.Count);
    }

    [Fact]
    public async Task Profile_switch_applies_exactly_once_and_persists_active()
    {
        var store = Store();
        store.Save(new LightingProfileSet
        {
            Profiles = [new("Default", new LightingState()), new("Gaming", new LightingState { EffectId = 3 })],
            Active = "Default",
        });
        var vm = Create(store);
        await vm.PendingApply;
        var before = _device.Applied.Count;

        vm.SelectedProfile = "Gaming";
        await vm.PendingApply;

        Assert.Equal(before + 1, _device.Applied.Count);
        Assert.Equal("Gaming", Store().Load().Active);
    }

    [Fact]
    public async Task Active_profile_is_persisted_after_rename_and_delete()
    {
        var vm = Create();
        vm.NewProfileCommand.Execute(null);
        vm.ProfileNameEdit = "Gaming";
        vm.RenameProfileCommand.Execute(null);
        await vm.PendingApply;
        Assert.Equal("Gaming", Store().Load().Active);

        vm.DeleteProfileCommand.Execute(null);
        await vm.PendingApply;
        Assert.Equal("Default", Store().Load().Active);
    }

    [Fact]
    public async Task Save_failure_is_an_error_status_and_the_apply_still_happens()
    {
        var vm = Create();
        await vm.PendingApply;
        var path = _temp.Combine("profiles.json");
        File.Delete(path);
        Directory.CreateDirectory(path); // makes every save fail

        vm.Brightness = 3;
        await vm.PendingApply;

        Assert.Equal(3, _device.Applied[^1].Brightness);
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity);
        Assert.Contains("Couldn't save lighting profiles", vm.StatusMessage);
    }

    [Fact]
    public async Task Transient_null_profile_selection_does_not_break_saving()
    {
        var vm = Create();
        await vm.PendingApply;

        vm.SelectedProfile = null;
        vm.Brightness = 3;
        await vm.PendingApply;

        var saved = Store().Load();
        Assert.Equal("Default", saved.Active);
        Assert.Equal(3, saved.Profiles.Single().State.Brightness);
    }

    [Fact]
    public void Hue_follows_the_colour()
    {
        var vm = Create();
        vm.ColorHex = "#00FF00";
        Assert.Equal(120, vm.Hue, 1);
        vm.Color = new Rgb(128, 128, 128); // grey keeps the hue
        Assert.Equal(120, vm.Hue, 1);
    }

    private sealed class GatedDevice : IKeyboardDevice
    {
        public DeviceStatus Status => DeviceStatus.Connected;
        public string StatusText => "gated";
        public List<LightingState> Applied { get; } = [];
        public TaskCompletionSource? Gate { get; set; }
#pragma warning disable CS0067
        public event EventHandler? StatusChanged;
#pragma warning restore CS0067

        public Task ApplyAsync(LightingState state, CancellationToken ct = default)
        {
            Applied.Add(state);
            return Gate?.Task ?? Task.CompletedTask;
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task Reverting_during_an_in_flight_write_is_still_applied()
    {
        var gated = new GatedDevice();
        var vm = new KeyboardViewModel(gated, Store(), _ => Task.CompletedTask);
        await vm.PendingApply;

        gated.Gate = new TaskCompletionSource();
        vm.Brightness = 3;
        var first = vm.PendingApply;
        vm.Brightness = 9; // back to the previous value while the write for 3 is blocked
        var second = vm.PendingApply;
        gated.Gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(9, gated.Applied[^1].Brightness);
    }

    [Fact]
    public void Hue_echo_of_the_current_hue_does_not_change_the_colour()
    {
        var vm = Create();
        var before = vm.Color;
        vm.Hue = vm.Hue + 0.2; // a view echoing roughly the same value
        Assert.Equal(before, vm.Color);
        vm.Hue = 0; // a real change to red still works
        Assert.Equal(new Rgb(255, 0, 0), vm.Color);
    }

    [Fact]
    public void Hue_matches_the_default_colour_on_a_fresh_view_model()
    {
        var vm = Create();
        Assert.InRange(vm.Hue, 211, 213);
    }

    [Fact]
    public async Task Flush_saves_now_without_starting_a_device_write()
    {
        var gate = new TaskCompletionSource();
        var vm = new KeyboardViewModel(_device, Store(), _ => gate.Task);
        vm.Brightness = 3;

        await vm.FlushAsync();
        Assert.Equal(3, Store().Load().Profiles.Single().State.Brightness);

        gate.SetResult();
        await vm.PendingApply;
        Assert.Empty(_device.Applied);
    }

    [Fact]
    public async Task Forced_triggers_apply_an_identical_state()
    {
        var vm = Create();
        await vm.PendingApply;
        var n = _device.Applied.Count;

        Assert.True(vm.ApplyProfileByName("default"));
        await vm.PendingApply;
        Assert.Equal(n + 1, _device.Applied.Count);

        _device.SetStatus(DeviceStatus.Connected, "Connected by USB cable");
        await vm.PendingApply;
        Assert.Equal(n + 2, _device.Applied.Count);
    }

    [Fact]
    public async Task Same_state_is_retried_after_a_failed_apply()
    {
        _device.FailWith = new KeyboardUnavailableException("Bluetooth.");
        var vm = Create();
        await vm.PendingApply;
        Assert.Empty(_device.Applied);

        _device.FailWith = null;
        vm.Speed = 4; // no effect on the wire for Static, but the last apply failed
        await vm.PendingApply;

        Assert.Single(_device.Applied);
        Assert.Equal(InfoBarSeverity.Success, vm.StatusSeverity);
    }

    [Fact]
    public async Task New_and_duplicate_names_are_unique_case_insensitively()
    {
        var store = Store();
        store.Save(new LightingProfileSet
        {
            Profiles = [new("Default", new LightingState()), new("profile 1", new LightingState()), new("default copy", new LightingState())],
            Active = "Default",
        });
        var vm = Create(store);
        await vm.PendingApply;

        vm.NewProfileCommand.Execute(null);
        Assert.Equal("Profile 2", vm.SelectedProfile);

        vm.SelectedProfile = "Default";
        vm.DuplicateProfileCommand.Execute(null);
        Assert.Equal("Default copy 2", vm.SelectedProfile);
    }

    [Fact]
    public async Task Deleting_a_profile_loads_and_applies_the_first_remaining()
    {
        var store = Store();
        store.Save(new LightingProfileSet
        {
            Profiles = [new("Default", new LightingState { Brightness = 2 }), new("Gaming", new LightingState { Brightness = 5 })],
            Active = "Gaming",
        });
        var vm = Create(store);
        await vm.PendingApply;

        vm.DeleteProfileCommand.Execute(null);
        await vm.PendingApply;

        Assert.Equal("Default", vm.SelectedProfile);
        Assert.Equal(2, vm.Brightness);
        Assert.Equal(2, _device.Applied[^1].Brightness);
        Assert.Equal("Default", Store().Load().Active);
    }
}
