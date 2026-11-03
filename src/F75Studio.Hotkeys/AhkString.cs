using System.Text;

namespace F75Studio.Hotkeys;

/// <summary>AutoHotkey v2 quoted string literals (escape character is the backtick).</summary>
public static class AhkString
{
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '`' => "``",
                '"' => "`\"",
                ';' => "`;",
                '\n' => "`n",
                '\r' => "`r",
                '\t' => "`t",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Parses a "..." or '...' literal at the start of <paramref name="text"/>.</summary>
    public static bool TryUnquote(string text, out string value, out string rest)
    {
        value = "";
        rest = text;
        if (text.Length == 0 || (text[0] != '"' && text[0] != '\'')) return false;

        var quote = text[0];
        var sb = new StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '`' && i + 1 < text.Length)
            {
                var next = text[++i];
                sb.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => next });
                continue;
            }
            if (c == quote)
            {
                value = sb.ToString();
                rest = text[(i + 1)..];
                return true;
            }
            sb.Append(c);
        }
        return false;
    }
}
