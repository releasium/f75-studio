using F75Studio.Lighting;

namespace F75Studio.Tests;

/// <summary>Pomodoro effect on the Keyboard page; shares the screen-sync harness.</summary>
public partial class KeyboardScreenSyncTests
{
    [Fact]
    public async Task Pomodoro_streams_the_timer_without_screen_capture()
    {
        var vm = Create();
        await SettleAsync(vm);
        var applied = _device.Applied.Count;

        vm.BeginPomodoro();
        await SettleAsync(vm);

        Assert.Equal(1, _sync.Starts);
        Assert.False(_sync.LastNeedsCapture!());
        Assert.Equal(applied, _device.Applied.Count); // nothing stored is written
        var map = _sync.LastMapper!(new CapturedFrame(1, 1, [0, 0, 0, 255]));
        Assert.Equal(PomodoroMapper.Map(vm.Pomodoro.Snapshot()).Count, map.Count);
        Assert.All(map.Values, c => Assert.Equal(PomodoroMapper.Map(vm.Pomodoro.Snapshot())[F75Layout.Keys[0].Led], c));
    }

    [Fact]
    public async Task EndPomodoro_restores_the_effect_selected_before()
    {
        var vm = Create();
        await SettleAsync(vm);
        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSyncBlur);
        await SettleAsync(vm);

        vm.BeginPomodoro();
        await SettleAsync(vm);
        Assert.True(_sync.LastNeedsCapture is not null);
        vm.EndPomodoro();
        await SettleAsync(vm);

        Assert.Equal(EffectIds.ScreenSyncBlur, vm.SelectedEffect.Id);
        Assert.True(_sync.LastNeedsCapture!()); // the same running engine captures again
        Assert.Equal(1, _sync.Starts);
    }

    [Fact]
    public async Task EndPomodoro_from_a_stored_effect_stops_streaming_and_restores_it()
    {
        var vm = Create();
        await SettleAsync(vm);
        vm.SelectedEffect = F75Effects.Get(2);
        await SettleAsync(vm);

        vm.BeginPomodoro();
        await SettleAsync(vm);
        vm.EndPomodoro();
        await SettleAsync(vm);

        Assert.Equal(2, vm.SelectedEffect.Id);
        Assert.False(_sync.IsRunning);
        Assert.Equal(2, _device.Applied[^1].EffectId);
    }

    [Fact]
    public async Task Pomodoro_is_never_saved_into_the_profile()
    {
        var vm = Create();
        await SettleAsync(vm);
        vm.SelectedEffect = F75Effects.Get(2);
        await SettleAsync(vm);

        vm.BeginPomodoro();
        vm.Brightness = 4; // an edit while the timer runs is still saved, with the profile's own effect
        await SettleAsync(vm);

        var saved = Store().Load().Profiles.Single().State;
        Assert.Equal(2, saved.EffectId);
        Assert.Equal(4, saved.Brightness);
    }

    [Fact]
    public async Task A_profile_that_holds_pomodoro_loads_its_base_effect_instead()
    {
        var store = Store();
        store.Save(new LightingProfileSet
        {
            Profiles = [new("Old", new LightingState { EffectId = EffectIds.Pomodoro, BaseEffectId = 2 })],
            Active = "Old",
        });
        var vm = Create();
        await SettleAsync(vm);

        Assert.Equal(2, vm.SelectedEffect.Id);
        Assert.Equal(0, _sync.Starts);
    }

    [Fact]
    public async Task Locking_the_PC_keeps_the_pomodoro_lights_but_stops_screen_sync()
    {
        var vm = Create();
        await SettleAsync(vm);
        vm.BeginPomodoro();
        await SettleAsync(vm);

        vm.SetScreenLocked(true);
        await SettleAsync(vm);
        Assert.True(_sync.IsRunning);

        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSyncBlur);
        await SettleAsync(vm);
        Assert.False(_sync.IsRunning);
    }

    [Fact]
    public async Task Selecting_pomodoro_raises_PomodoroSelected_and_EndPomodoro_leaves_other_effects_alone()
    {
        var vm = Create();
        await SettleAsync(vm);
        var raised = 0;
        vm.PomodoroSelected += () => raised++;

        vm.SelectedEffect = F75Effects.Get(EffectIds.Pomodoro);
        await SettleAsync(vm);
        Assert.Equal(1, raised);

        vm.SelectedEffect = F75Effects.Get(EffectIds.ScreenSync);
        await SettleAsync(vm);
        vm.EndPomodoro(); // the user already left Pomodoro: nothing changes
        Assert.Equal(EffectIds.ScreenSync, vm.SelectedEffect.Id);
    }
}
