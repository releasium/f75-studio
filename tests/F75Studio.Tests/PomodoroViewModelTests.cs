using F75Studio.App.ViewModels;
using F75Studio.Lighting;

namespace F75Studio.Tests;

public class PomodoroViewModelTests
{
    private long _now = 5_000_000;

    [Fact]
    public void Shows_the_configured_focus_time_and_phase()
    {
        var vm = new PomodoroViewModel(new PomodoroTimer(() => _now), 25, 5, (_, _) => { });

        Assert.Equal("25:00", vm.TimeText);
        Assert.StartsWith("Focus", vm.PhaseText);
        Assert.True(vm.IsFocus);
        Assert.Equal(100, vm.Progress);
        Assert.Equal("Start", vm.StartPauseText);
    }

    [Fact]
    public void Start_pause_counts_down()
    {
        var vm = new PomodoroViewModel(new PomodoroTimer(() => _now), 15, 5, (_, _) => { });
        vm.StartPauseCommand.Execute(null);
        _now += 90_500;
        vm.Refresh();

        Assert.Equal("13:29", vm.TimeText);
        Assert.Equal("Pause", vm.StartPauseText);

        vm.StartPauseCommand.Execute(null);
        Assert.False(vm.IsRunning);
        Assert.EndsWith("paused", vm.PhaseText);
    }

    [Fact]
    public void Changing_minutes_saves_them_and_clamps_bad_values()
    {
        var saved = new List<(int, int)>();
        var vm = new PomodoroViewModel(new PomodoroTimer(() => _now), 15, 5, (f, r) => saved.Add((f, r)));

        vm.FocusMinutes = 30;
        vm.RestMinutes = 0;

        Assert.Equal("30:00", vm.TimeText);
        Assert.Equal(PomodoroViewModel.MinMinutes, vm.RestMinutes);
        Assert.Equal((30, 1), saved[^1]);
    }

    [Fact]
    public void Skip_goes_to_rest()
    {
        var vm = new PomodoroViewModel(new PomodoroTimer(() => _now), 15, 5, (_, _) => { });
        vm.SkipCommand.Execute(null);

        Assert.False(vm.IsFocus);
        Assert.Equal("05:00", vm.TimeText);
    }
}
