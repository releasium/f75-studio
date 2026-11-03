using F75Studio.Hotkeys.Model;

namespace F75Studio.Hotkeys;

public static class MediaKeys
{
    private static readonly Dictionary<MediaCommand, string> Map = new()
    {
        [MediaCommand.PlayPause] = "{Media_Play_Pause}",
        [MediaCommand.Next] = "{Media_Next}",
        [MediaCommand.Previous] = "{Media_Prev}",
        [MediaCommand.Stop] = "{Media_Stop}",
        [MediaCommand.VolumeUp] = "{Volume_Up}",
        [MediaCommand.VolumeDown] = "{Volume_Down}",
        [MediaCommand.Mute] = "{Volume_Mute}",
    };

    public static string ToSendKey(MediaCommand command) => Map[command];

    public static bool TryParse(string sendKeys, out MediaCommand command)
    {
        foreach (var (key, value) in Map)
        {
            if (string.Equals(value, sendKeys.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                command = key;
                return true;
            }
        }
        command = default;
        return false;
    }
}
