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

    [Fact]
    public void Resume_skips_script_that_is_already_running()
    {
        var a = _temp.WriteFile("a.ahk", "");
        var runner = new FakeAhkRunner();
        runner.Start(a);
        var pauser = new HotkeyPauser(runner);
        pauser.Pause();
        runner.Start(a);
        runner.Started.Clear();

        pauser.Resume();

        Assert.Empty(runner.Started);
        Assert.False(pauser.IsPaused);
    }

    [Fact]
    public void Resume_failure_keeps_paused_and_retry_starts_only_remaining()
    {
        var a = _temp.WriteFile("a.ahk", "");
        var b = _temp.WriteFile("b.ahk", "");
        var c = _temp.WriteFile("c.ahk", "");
        var runner = new FakeAhkRunner();
        runner.Start(a);
        runner.Start(b);
        runner.Start(c);
        var pauser = new HotkeyPauser(runner);
        pauser.Pause();
        runner.Started.Clear();
        var order = runner.Started;

        // Fail on whichever script is started second.
        var calls = 0;
        runner.StartFailure = _ => ++calls == 2 ? new InvalidOperationException("boom") : null;
        Assert.Throws<InvalidOperationException>(pauser.Resume);
        Assert.True(pauser.IsPaused);
        Assert.Single(order);

        runner.StartFailure = null;
        pauser.Resume();

        Assert.False(pauser.IsPaused);
        Assert.Equal(3, runner.Started.Count);
        Assert.Equal(3, runner.Started.Distinct().Count());
    }

    [Fact]
    public void Pause_with_nothing_running_then_Resume_is_noop_and_clears()
    {
        var runner = new FakeAhkRunner();
        var pauser = new HotkeyPauser(runner);
        pauser.Pause();
        Assert.True(pauser.IsPaused);
        pauser.Resume();
        Assert.False(pauser.IsPaused);
        Assert.Empty(runner.Started);
    }

    [Fact]
    public void Resume_skips_deleted_script()
    {
        var a = _temp.WriteFile("a.ahk", "");
        var runner = new FakeAhkRunner();
        runner.Start(a);
        var pauser = new HotkeyPauser(runner);
        pauser.Pause();
        File.Delete(a);
        runner.Started.Clear();

        pauser.Resume();

        Assert.Empty(runner.Started);
        Assert.False(pauser.IsPaused);
    }
}
