using F75Studio.App.Services;

namespace F75Studio.Tests;

public class ExitGuardTests
{
    [Fact]
    public void Exits_without_asking_when_nothing_is_dirty()
    {
        var asked = false;
        Assert.True(ExitGuard.ShouldExit(false, false, _ => { asked = true; return false; }));
        Assert.False(asked);
    }

    [Theory]
    [InlineData(true, false, "un-applied hotkeys")]
    [InlineData(false, true, "unsaved script changes")]
    [InlineData(true, true, "un-applied hotkeys and unsaved script changes")]
    public void Asks_naming_what_is_dirty_and_follows_the_answer(bool hotkeys, bool script, string expected)
    {
        string? message = null;
        Assert.False(ExitGuard.ShouldExit(hotkeys, script, m => { message = m; return false; }));
        Assert.Equal($"You have {expected}. Exit anyway?", message);
        Assert.True(ExitGuard.ShouldExit(hotkeys, script, _ => true));
    }
}
