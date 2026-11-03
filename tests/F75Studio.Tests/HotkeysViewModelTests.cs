using F75Studio.App.Services;
using F75Studio.App.ViewModels;
using F75Studio.Core;
using F75Studio.Hotkeys;
using F75Studio.Hotkeys.Model;
using F75Studio.Tests.TestSupport;
using Wpf.Ui.Controls;

namespace F75Studio.Tests;

public class HotkeysViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeAhkRunner _runner = new();

    public void Dispose() => _temp.Dispose();

    private AppServices CreateServices() => new(new AppPaths(_temp.Combine("data"), _temp.Combine("docs")), _runner);

    [Fact]
    public void Item_round_trips_every_action_kind()
    {
        foreach (var hotkey in HotkeyModelTests.AllActionKinds())
            Assert.Equal(hotkey, HotkeyItemViewModel.FromHotkey(hotkey).ToHotkey());
    }

    [Fact]
    public void Loads_saved_hotkeys_and_previews_them()
    {
        var services = CreateServices();
        services.HotkeyStore.Save([new Hotkey { Trigger = "^k", Action = new SendKeysAction("{Up}") }]);

        var vm = new HotkeysViewModel(services);

        var item = Assert.Single(vm.Items);
        Assert.Same(item, vm.SelectedItem);
        Assert.False(vm.IsDirty);
        Assert.Contains("^k::Send \"{Up}\"", vm.Preview);
    }

    [Fact]
    public void Editing_an_item_updates_preview_and_marks_dirty()
    {
        var vm = new HotkeysViewModel(CreateServices());
        vm.AddHotkeyCommand.Execute(null);

        vm.SelectedItem!.Trigger = "F13";
        vm.SelectedItem.Kind = ActionKind.Media;
        vm.SelectedItem.Media = MediaCommand.Next;

        Assert.True(vm.IsDirty);
        Assert.Contains("F13::Send \"{Media_Next}\"", vm.Preview);
        Assert.Equal("Media: Next", vm.SelectedItem.Summary);
    }

    [Fact]
    public async Task Apply_saves_writes_validates_and_starts_the_script()
    {
        var services = CreateServices();
        var vm = new HotkeysViewModel(services);
        vm.AddHotkeyCommand.Execute(null);
        vm.SelectedItem!.Trigger = "^!t";
        vm.SelectedItem.Keys = "{Home}";

        await vm.ApplyCommand.ExecuteAsync(null);

        var script = services.Paths.GeneratedHotkeysScript;
        Assert.Contains("^!t::Send \"{Home}\"", File.ReadAllText(script));
        Assert.Equal(new[] { Path.ChangeExtension(script, ".pending.ahk") }, _runner.Validated);
        Assert.Equal(new[] { script }, _runner.Started);
        Assert.Single(services.HotkeyStore.Load());
        Assert.False(vm.IsDirty);
        Assert.Equal(InfoBarSeverity.Success, vm.StatusSeverity);
        Assert.False(File.Exists(Path.ChangeExtension(script, ".pending.ahk")));
    }

    [Fact]
    public async Task Apply_marks_the_hotkey_that_AutoHotkey_rejected()
    {
        var services = CreateServices();
        var vm = new HotkeysViewModel(services);
        vm.AddHotkeyCommand.Execute(null);
        vm.SelectedItem!.Trigger = "^1";
        vm.SelectedItem.Keys = "a";
        vm.AddHotkeyCommand.Execute(null);
        var bad = vm.SelectedItem!;
        bad.Trigger = "^2";
        bad.Kind = ActionKind.CustomCode;
        bad.Code = "Sendd x";
        var badLine = services.Generator.Generate(vm.Items.Select(i => i.ToHotkey())).LineOwners.First(kv => kv.Value == bad.Id).Key;
        _runner.NextValidation = new AhkValidationResult(false, "This line does not contain a recognized action.", badLine);
        vm.SelectedItem = vm.Items[0];

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("This line does not contain a recognized action.", bad.Error);
        Assert.Same(bad, vm.SelectedItem);
        Assert.Empty(_runner.Started);
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity);
        Assert.False(File.Exists(services.Paths.GeneratedHotkeysScript));
    }

    [Fact]
    public async Task Apply_refuses_hotkeys_without_a_trigger()
    {
        var vm = new HotkeysViewModel(CreateServices());
        vm.AddHotkeyCommand.Execute(null);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("Set a trigger first.", vm.Items[0].Error);
        Assert.Empty(_runner.Validated);
    }

    [Fact]
    public async Task Apply_without_AutoHotkey_saves_cards_and_explains()
    {
        var services = CreateServices();
        _runner.AhkExePath = null;
        var vm = new HotkeysViewModel(services);
        vm.AddHotkeyCommand.Execute(null);
        vm.SelectedItem!.Trigger = "^k";

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Single(services.HotkeyStore.Load());
        Assert.Contains("AutoHotkey v2 was not found", vm.StatusMessage);
    }

    [Fact]
    public void Recording_sets_the_trigger_and_pauses_the_running_hotkeys_script()
    {
        var services = CreateServices();
        var vm = new HotkeysViewModel(services);
        vm.AddHotkeyCommand.Execute(null);
        _runner.Start(services.Paths.GeneratedHotkeysScript);

        vm.ToggleRecordingCommand.Execute(null);
        Assert.True(vm.IsRecording);
        Assert.False(_runner.IsRunning(services.Paths.GeneratedHotkeysScript));

        vm.RecordKeyDown("LCtrl");
        vm.RecordKeyDown("k");
        vm.RecordKeyUp("k");
        vm.RecordKeyUp("LCtrl");

        Assert.Equal("^k", vm.SelectedItem!.Trigger);
        Assert.False(vm.IsRecording);
        Assert.True(_runner.IsRunning(services.Paths.GeneratedHotkeysScript));
    }

    [Fact]
    public void ImportFromScript_adds_items_and_reports_skipped_lines()
    {
        var vm = new HotkeysViewModel(CreateServices());

        vm.ImportFromScript("^j::Send \"{Down}\"\n::btw::by the way\n", "Imported");

        var item = Assert.Single(vm.Items);
        Assert.Equal("Imported", item.Group);
        Assert.Same(item, vm.SelectedItem);
        Assert.True(vm.IsDirty);
        Assert.Contains("1 line(s) could not be imported", vm.StatusMessage);
        Assert.Equal(InfoBarSeverity.Warning, vm.StatusSeverity);
    }

    [Fact]
    public void Delete_removes_selected_item()
    {
        var vm = new HotkeysViewModel(CreateServices());
        vm.AddHotkeyCommand.Execute(null);
        vm.AddHotkeyCommand.Execute(null);

        vm.DeleteCommand.Execute(null);

        Assert.Single(vm.Items);
        Assert.Same(vm.Items[0], vm.SelectedItem);
    }
}
