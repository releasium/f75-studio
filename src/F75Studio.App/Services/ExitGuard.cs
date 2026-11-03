namespace F75Studio.App.Services;

/// <summary>Decides whether a user-initiated exit may proceed when there is unsaved work.</summary>
public static class ExitGuard
{
    /// <param name="confirm">Shown the warning text; true means "exit anyway".</param>
    public static bool ShouldExit(bool hotkeysDirty, bool scriptDirty, Func<string, bool> confirm)
    {
        if (!hotkeysDirty && !scriptDirty) return true;
        var what = (hotkeysDirty, scriptDirty) switch
        {
            (true, true) => "un-applied hotkeys and unsaved script changes",
            (true, false) => "un-applied hotkeys",
            _ => "unsaved script changes",
        };
        return confirm($"You have {what}. Exit anyway?");
    }
}
