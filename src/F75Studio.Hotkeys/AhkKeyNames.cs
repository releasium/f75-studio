namespace F75Studio.Hotkeys;

/// <summary>Maps WPF <c>System.Windows.Input.Key</c> names (as strings, so this library has no WPF dependency) to AHK key names.</summary>
public static class AhkKeyNames
{
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "LCtrl", "RCtrl", "LAlt", "RAlt", "LShift", "RShift", "LWin", "RWin",
    };

    // WPF Key has duplicate names for one value (Capital/CapsLock, Next/PageDown, Oem1/OemSemicolon...),
    // and Enum.ToString may return either, so both are listed.
    private static readonly Dictionary<string, string> Special = new(StringComparer.Ordinal)
    {
        ["Capital"] = "CapsLock", ["CapsLock"] = "CapsLock",
        ["Return"] = "Enter", ["Enter"] = "Enter",
        ["Escape"] = "Escape", ["Space"] = "Space", ["Tab"] = "Tab", ["Back"] = "Backspace",
        ["Delete"] = "Delete", ["Insert"] = "Insert", ["Home"] = "Home", ["End"] = "End",
        ["PageUp"] = "PgUp", ["Prior"] = "PgUp", ["PageDown"] = "PgDn", ["Next"] = "PgDn",
        ["Left"] = "Left", ["Right"] = "Right", ["Up"] = "Up", ["Down"] = "Down",
        ["LeftCtrl"] = "LCtrl", ["RightCtrl"] = "RCtrl", ["LeftAlt"] = "LAlt", ["RightAlt"] = "RAlt",
        ["LeftShift"] = "LShift", ["RightShift"] = "RShift", ["LWin"] = "LWin", ["RWin"] = "RWin",
        ["Apps"] = "AppsKey", ["PrintScreen"] = "PrintScreen", ["Snapshot"] = "PrintScreen",
        ["Scroll"] = "ScrollLock", ["Pause"] = "Pause", ["NumLock"] = "NumLock",
        ["Multiply"] = "NumpadMult", ["Add"] = "NumpadAdd", ["Subtract"] = "NumpadSub",
        ["Divide"] = "NumpadDiv", ["Decimal"] = "NumpadDot",
        ["OemComma"] = ",", ["OemPeriod"] = ".", ["OemMinus"] = "-", ["OemPlus"] = "=",
        ["OemOpenBrackets"] = "[", ["Oem4"] = "[", ["OemCloseBrackets"] = "]", ["Oem6"] = "]",
        ["OemQuestion"] = "/", ["Oem2"] = "/", ["OemPipe"] = "\\", ["Oem5"] = "\\",
        ["OemQuotes"] = "'", ["Oem7"] = "'", ["OemSemicolon"] = "`;", ["Oem1"] = "`;",
        ["OemTilde"] = "``", ["Oem3"] = "``",
        ["MediaPlayPause"] = "Media_Play_Pause", ["MediaNextTrack"] = "Media_Next",
        ["MediaPreviousTrack"] = "Media_Prev", ["MediaStop"] = "Media_Stop",
        ["VolumeUp"] = "Volume_Up", ["VolumeDown"] = "Volume_Down", ["VolumeMute"] = "Volume_Mute",
    };

    public static bool IsModifier(string ahkKey) => Modifiers.Contains(ahkKey);

    public static string? FromWpfKeyName(string wpfKeyName)
    {
        if (Special.TryGetValue(wpfKeyName, out var name)) return name;
        if (wpfKeyName.Length == 1 && char.IsAsciiLetter(wpfKeyName[0])) return wpfKeyName.ToLowerInvariant();
        if (wpfKeyName.Length == 2 && wpfKeyName[0] == 'D' && char.IsAsciiDigit(wpfKeyName[1])) return wpfKeyName[1..];
        if (wpfKeyName.Length == 7 && wpfKeyName.StartsWith("NumPad", StringComparison.Ordinal) && char.IsAsciiDigit(wpfKeyName[6]))
            return "Numpad" + wpfKeyName[6];
        if (wpfKeyName.Length >= 2 && wpfKeyName[0] == 'F' && int.TryParse(wpfKeyName[1..], out var n) && n is >= 1 and <= 24)
            return wpfKeyName;
        return null;
    }
}
