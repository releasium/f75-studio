using System.Runtime.InteropServices;
using System.Text;

namespace F75Studio.Hotkeys;

/// <summary>Asks a running AutoHotkey process to exit like its tray "Exit" item, so it removes its tray icon.</summary>
internal static class AhkWindow
{
    private const uint WmCommand = 0x0111;
    private const nint IdTrayExit = 65307;

    public static bool RequestExit(int processId)
    {
        var posted = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != (uint)processId) return true;
            var className = new StringBuilder(64);
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() != "AutoHotkey") return true;
            posted = PostMessage(hwnd, WmCommand, IdTrayExit, 0);
            return false;
        }, 0);
        return posted;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
}
