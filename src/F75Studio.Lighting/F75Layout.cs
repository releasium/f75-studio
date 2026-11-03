namespace F75Studio.Lighting;

/// <param name="Led">Index into the 126-entry per-key colour planes.</param>
public sealed record KeyDef(string Name, int Led, int X, int Y, int Width, int Height);

public static class F75Layout
{
    public const int CanvasWidth = 660;
    public const int CanvasHeight = 290;

    public static IReadOnlyList<KeyDef> Keys { get; } =
    [
        new("Esc", 0, 30, 30, 25, 27),
        new("F1", 12, 107, 30, 25, 27),
        new("F2", 18, 144, 30, 25, 27),
        new("F3", 24, 183, 30, 25, 27),
        new("F4", 30, 222, 30, 25, 27),
        new("F5", 36, 269, 30, 25, 27),
        new("F6", 42, 305, 30, 25, 27),
        new("F7", 48, 343, 30, 25, 27),
        new("F8", 54, 388, 30, 25, 27),
        new("F9", 60, 439, 30, 25, 27),
        new("F10", 66, 477, 30, 25, 27),
        new("F11", 72, 516, 30, 25, 27),
        new("F12", 78, 555, 30, 25, 27),
        new("`", 1, 31, 81, 25, 27),
        new("1", 7, 70, 81, 25, 27),
        new("2", 13, 108, 81, 25, 27),
        new("3", 19, 147, 81, 25, 27),
        new("4", 25, 184, 81, 25, 27),
        new("5", 31, 223, 81, 25, 27),
        new("6", 37, 262, 81, 25, 27),
        new("7", 43, 300, 80, 25, 27),
        new("8", 49, 338, 81, 25, 27),
        new("9", 55, 377, 81, 25, 27),
        new("0", 61, 416, 81, 25, 27),
        new("-", 67, 453, 81, 25, 27),
        new("=", 73, 491, 81, 25, 27),
        new("Backspace", 79, 531, 81, 60, 27),
        new("Delete", 85, 604, 81, 25, 27),
        new("Tab", 2, 34, 120, 44, 27),
        new("Q", 8, 89, 119, 25, 27),
        new("W", 14, 127, 119, 25, 27),
        new("E", 20, 165, 119, 25, 27),
        new("R", 26, 204, 119, 25, 27),
        new("T", 32, 242, 119, 25, 27),
        new("Y", 38, 280, 119, 25, 27),
        new("U", 44, 318, 119, 25, 27),
        new("I", 50, 356, 119, 25, 27),
        new("O", 56, 395, 119, 25, 27),
        new("P", 62, 433, 119, 25, 27),
        new("[", 68, 471, 120, 25, 27),
        new("]", 74, 509, 119, 25, 27),
        new("\\", 80, 549, 119, 44, 27),
        new("PgUp", 86, 605, 120, 25, 27),
        new("CapsLock", 3, 33, 158, 52, 27),
        new("A", 9, 98, 158, 25, 27),
        new("S", 15, 136, 158, 25, 27),
        new("D", 21, 174, 158, 25, 27),
        new("F", 27, 213, 158, 25, 27),
        new("G", 33, 252, 158, 25, 27),
        new("H", 39, 290, 158, 25, 27),
        new("J", 45, 327, 158, 25, 27),
        new("K", 51, 366, 158, 25, 27),
        new("L", 57, 405, 158, 25, 27),
        new(";", 63, 443, 158, 25, 27),
        new("'", 69, 481, 158, 25, 27),
        new("Enter", 81, 521, 158, 70, 27),
        new("PgDn", 87, 604, 159, 25, 27),
        new("LShift", 4, 33, 195, 72, 27),
        new("Z", 10, 116, 195, 25, 27),
        new("X", 16, 155, 195, 25, 27),
        new("C", 22, 193, 195, 25, 27),
        new("V", 28, 232, 195, 25, 27),
        new("B", 34, 270, 196, 25, 27),
        new("N", 40, 309, 195, 25, 27),
        new("M", 46, 347, 195, 25, 27),
        new(",", 52, 384, 195, 25, 27),
        new(".", 58, 424, 195, 25, 27),
        new("/", 64, 463, 195, 25, 27),
        new("RShift", 70, 502, 195, 56, 27),
        new("Up", 82, 567, 195, 25, 27),
        new("End", 88, 605, 195, 25, 27),
        new("LCtrl", 5, 34, 234, 35, 27),
        new("LWin", 11, 79, 234, 35, 27),
        new("LAlt", 17, 122, 235, 35, 27),
        new("Space", 35, 174, 235, 226, 27),
        new("Fn", 53, 414, 235, 35, 27),
        new("RCtrl", 59, 463, 236, 34, 27),
        new("Left", 77, 529, 234, 25, 27),
        new("Down", 83, 568, 234, 25, 27),
        new("Right", 89, 605, 234, 25, 27),
    ];

    private static readonly Dictionary<string, string[]> Groups = new()
    {
        ["All"] = [],
        ["WASD"] = ["W", "A", "S", "D"],
        ["Arrows"] = ["Up", "Down", "Left", "Right"],
        ["F-row"] = ["Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"],
        ["Numbers"] = ["`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "="],
        ["Modifiers"] = ["Tab", "CapsLock", "LShift", "RShift", "LCtrl", "RCtrl", "LWin", "LAlt", "Fn"],
    };

    public static IReadOnlyList<string> GroupNames { get; } = Groups.Keys.ToList();

    public static IReadOnlyList<int> Group(string name) =>
        name == "All"
            ? Keys.Select(k => k.Led).ToList()
            : Groups[name].Select(n => Keys.Single(k => k.Name == n).Led).ToList();
}
