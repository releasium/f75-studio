using System.Text.RegularExpressions;
using F75Studio.Hotkeys.Model;

namespace F75Studio.Hotkeys;

public sealed record ImportResult(IReadOnlyList<Hotkey> Hotkeys, IReadOnlyList<string> SkippedLines);

public static partial class AhkImporter
{
    private static readonly HashSet<string> IgnoredDirectives = new(StringComparer.OrdinalIgnoreCase) { "#Requires", "#SingleInstance" };

    [GeneratedRegex(@"^(?<trigger>[^:;\s""'(][^:""'(]*?)\s*::(?<rest>.*)$")]
    private static partial Regex HotkeyLine();

    [GeneratedRegex(@"^#HotIf\s+WinActive\(\s*[""']ahk_exe\s+(?<exe>[^""']+)[""']\s*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HotIfExe();

    [GeneratedRegex(@"^F(?:[1-9]|1[0-9]|2[0-4])$", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionKey();

    [GeneratedRegex(@"^(?:Numpad\w+|vk[0-9A-F]{2}(?:sc[0-9A-F]{3})?|sc[0-9A-F]{3}|(?:Media|Volume|Browser|Launch)_\w+)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyPattern();

    private static readonly HashSet<string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Escape", "Esc", "Enter", "Return", "Space", "Tab", "Backspace", "BS", "Delete", "Del", "Insert", "Ins",
        "Home", "End", "PgUp", "PgDn", "Up", "Down", "Left", "Right", "CapsLock", "ScrollLock", "NumLock",
        "Ctrl", "LCtrl", "RCtrl", "Control", "Alt", "LAlt", "RAlt", "Shift", "LShift", "RShift", "LWin", "RWin",
        "AppsKey", "PrintScreen", "Pause", "Break", "Sleep", "LButton", "RButton", "MButton", "XButton1", "XButton2",
        "WheelUp", "WheelDown", "WheelLeft", "WheelRight",
    };

    public static ImportResult Import(string scriptText, string defaultGroup)
    {
        var lines = scriptText.ReplaceLineEndings("\n").Split('\n');
        var hotkeys = new List<Hotkey>();
        var skipped = new List<string>();
        var group = defaultGroup;
        string? scope = null;
        var skipping = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith(';'))
            {
                var text = line.TrimStart(';').Trim();
                if (text.Length > 0 && !line.Equals(AhkGenerator.Header, StringComparison.Ordinal)) group = text;
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (IgnoredDirectives.Contains(line.Split(' ', 2)[0])) continue;
                if (line.Equals("#HotIf", StringComparison.OrdinalIgnoreCase)) { scope = null; skipping = false; continue; }
                var hotIf = HotIfExe().Match(line);
                if (hotIf.Success) { scope = hotIf.Groups["exe"].Value.Trim(); skipping = false; continue; }
                if (line.StartsWith("#HotIf", StringComparison.OrdinalIgnoreCase)) skipping = true;
                skipped.Add(line);
                continue;
            }

            var match = HotkeyLine().Match(line);
            if (!match.Success) { skipped.Add(line); continue; }

            var trigger = match.Groups["trigger"].Value.Trim();
            var rest = StripComment(match.Groups["rest"].Value).Trim();

            HotkeyAction action;
            if (rest.Length == 0 || rest == "{")
            {
                var body = ReadBlock(lines, ref i, openedOnSameLine: rest == "{");
                if (body is null || skipping) { skipped.Add(line); continue; }
                action = new CustomCodeAction(body);
            }
            else
            {
                if (skipping) { skipped.Add(line); continue; }
                action = ParseAction(rest);
            }

            hotkeys.Add(new Hotkey { Trigger = trigger, Group = group, AppScope = scope, Action = action });
        }

        return new ImportResult(hotkeys, skipped);
    }

    private static HotkeyAction ParseAction(string rest)
    {
        if (rest.Equals("Return", StringComparison.OrdinalIgnoreCase)) return new DisableKeyAction();
        if (TryCall(rest, "SendText", out var text)) return new TypeTextAction(text);
        if (TryCall(rest, "Send", out var keys))
        {
            if (keys.StartsWith("{Blind}", StringComparison.OrdinalIgnoreCase))
                return new SendKeysAction(keys["{Blind}".Length..], Blind: true);
            return MediaKeys.TryParse(keys, out var media) ? new MediaAction(media) : new SendKeysAction(keys);
        }
        if (TryCall(rest, "Run", out var command)) return ParseRun(command);
        if (IsKeyName(rest)) return new RemapAction(rest);
        return new CustomCodeAction(rest);
    }

    /// <summary>True for a single character or a recognised AutoHotkey key name; other single words are code, not keys.</summary>
    private static bool IsKeyName(string name) =>
        (name.Length == 1 && !char.IsWhiteSpace(name[0]))
        || NamedKeys.Contains(name)
        || FunctionKey().IsMatch(name)
        || KeyPattern().IsMatch(name);

    /// <summary>Matches <c>Name "literal"</c> or <c>Name("literal")</c> with nothing after it.</summary>
    private static bool TryCall(string rest, string name, out string argument)
    {
        argument = "";
        if (!rest.StartsWith(name, StringComparison.OrdinalIgnoreCase) || rest.Length == name.Length) return false;

        var after = rest[name.Length..];
        var paren = after[0] == '(';
        if (!paren && !char.IsWhiteSpace(after[0])) return false;

        after = (paren ? after[1..] : after).Trim();
        if (!AhkString.TryUnquote(after, out argument, out var tail)) return false;
        tail = tail.Trim();
        return paren ? tail == ")" : tail.Length == 0;
    }

    private static HotkeyAction ParseRun(string command)
    {
        string target, arguments;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0) return new RunAction(command);
            target = command[1..end];
            arguments = command[(end + 1)..].Trim();
        }
        else
        {
            var space = command.IndexOf(' ');
            target = space < 0 ? command : command[..space];
            arguments = space < 0 ? "" : command[(space + 1)..].Trim();
        }

        if (Path.GetFileName(target).Equals("F75Studio.exe", StringComparison.OrdinalIgnoreCase)
            && arguments.StartsWith("--profile ", StringComparison.Ordinal))
        {
            return new SwitchLightingProfileAction(arguments["--profile ".Length..].Trim().Trim('"'));
        }

        return new RunAction(target, arguments.Length == 0 ? null : arguments);
    }

    /// <summary>Reads a { ... } block after a hotkey line; returns the dedented body or null.</summary>
    private static string? ReadBlock(string[] lines, ref int index, bool openedOnSameLine)
    {
        var j = index + 1;
        if (!openedOnSameLine)
        {
            while (j < lines.Length && lines[j].Trim().Length == 0) j++;
            if (j >= lines.Length || lines[j].Trim() != "{") return null;
            j++;
        }

        var depth = 1;
        var body = new List<string>();
        for (; j < lines.Length; j++)
        {
            var raw = lines[j];
            depth += raw.Count(c => c == '{') - raw.Count(c => c == '}');
            if (depth <= 0)
            {
                index = j;
                return Dedent(body);
            }
            body.Add(raw.TrimEnd());
        }
        return null;
    }

    private static string Dedent(List<string> body)
    {
        var indent = body.Where(l => l.Trim().Length > 0)
            .Select(l => l.Length - l.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        return string.Join("\n", body.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())).Trim('\n');
    }

    /// <summary>Removes a trailing " ; comment" that is not inside a string literal.</summary>
    private static string StripComment(string text)
    {
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == '`') i++;
                else if (c == quote) quote = null;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ';' && i > 0 && char.IsWhiteSpace(text[i - 1]))
            {
                return text[..i];
            }
        }
        return text;
    }
}
