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
}
