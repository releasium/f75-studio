namespace F75Studio.Lighting;

/// <summary>
/// Pomodoro lighting: the keyboard is red while focusing and blue while resting. F1–F12 are a progress bar in the
/// other colour (blue during focus, red during rest): dark at the start of a phase, filling up from F1 to F12 as
/// the time passes. While the timer isn't running (not started or paused) the board is green, and the bar keeps
/// showing how far the phase got.
/// </summary>
public static class PomodoroMapper
{
    public static readonly Rgb FocusColor = new(255, 0, 0);
    public static readonly Rgb RestColor = new(0, 64, 255);
    public static readonly Rgb IdleColor = new(0, 255, 0);

    public static IReadOnlyList<string> BarKeys { get; } = ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"];

    private static readonly int[] BarLeds = BarKeys.Select(n => F75Layout.Keys.Single(k => k.Name == n).Led).ToArray();

    public static IReadOnlyDictionary<int, Rgb> Map(PomodoroSnapshot snapshot)
    {
        var focus = snapshot.Phase == PomodoroPhase.Focus;
        var board = !snapshot.Running ? IdleColor : focus ? FocusColor : RestColor;
        var bar = focus ? RestColor : FocusColor;

        var result = new Dictionary<int, Rgb>(F75Layout.Keys.Count);
        foreach (var k in F75Layout.Keys) result[k.Led] = board;

        // Key i lights up over its own twelfth of the phase: F1 first, F12 when the phase ends.
        var filled = (1 - snapshot.RemainingFraction) * BarLeds.Length;
        for (var i = 0; i < BarLeds.Length; i++) result[BarLeds[i]] = Scale(bar, Math.Clamp(filled - i, 0, 1));
        return result;
    }

    private static Rgb Scale(Rgb c, double f) =>
        new((byte)Math.Round(c.R * f), (byte)Math.Round(c.G * f), (byte)Math.Round(c.B * f));
}
