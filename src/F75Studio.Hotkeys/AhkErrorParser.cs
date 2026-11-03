using System.Text.RegularExpressions;

namespace F75Studio.Hotkeys;

public static partial class AhkErrorParser
{
    [GeneratedRegex(@"\((?<line>\d+)\)\s*:\s*==>\s*(?<msg>[^\r\n]+)")]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"Specifically:\s*(?<detail>[^\r\n]+)")]
    private static partial Regex Specifically();

    public static AhkValidationResult Parse(string output)
    {
        var match = ErrorLine().Match(output);
        if (!match.Success)
        {
            var raw = string.IsNullOrWhiteSpace(output) ? "AutoHotkey reported an unknown error." : output.Trim();
            return new AhkValidationResult(false, raw, null);
        }

        var message = match.Groups["msg"].Value.Trim();
        var detail = Specifically().Match(output);
        if (detail.Success) message += $" ({detail.Groups["detail"].Value.Trim()})";
        return new AhkValidationResult(false, message, int.Parse(match.Groups["line"].Value));
    }
}
