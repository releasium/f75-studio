using F75Studio.Hotkeys;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class HotkeyPauserTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Pause_stops_running_scripts_and_Resume_restarts_exactly_those()
    {
        var a = _temp.WriteFile("a.ahk", "");
        var b = _temp.WriteFile("b.ahk", "");
        var runner = new FakeAhkRunner();
        runner.Start(a);
        runner.Start(b);
        runner.Stop(b);
        var pauser = new HotkeyPauser(runner);

        pauser.Pause();
        Assert.True(pauser.IsPaused);
        Assert.Empty(runner.RunningScripts);

        runner.Started.Clear();
        pauser.Resume();
        Assert.False(pauser.IsPaused);
        Assert.Equal(new[] { a }, runner.Started);
    }

    [Fact]
    public void Resume_without_Pause_does_nothing()
    {
        var runner = new FakeAhkRunner();
        new HotkeyPauser(runner).Resume();
        Assert.Empty(runner.Started);
    }
}
