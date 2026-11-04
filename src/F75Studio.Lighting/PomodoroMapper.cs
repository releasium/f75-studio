namespace F75Studio.Lighting;

/// <summary>
/// Pomodoro lighting: the keyboard is red while focusing and blue while resting, and F1–F12 are a progress bar of
/// the time left (all twelve lit at the start, draining from F12 towards F1). While paused everything is dimmed.
/// </summary>
public static class PomodoroMapper
{
    public static readonly Rgb FocusColor = new(255, 0, 0);
    public static readonly Rgb RestColor = new(0, 64, 255);

    /// <summary>Brightness of the whole board while the timer is paused or not started.</summary>
    public const double PausedLevel = 0.35;

    public static IReadOnlyList<string> BarKeys { get; } = ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"];

    private static readonly int[] BarLeds = BarKeys.Select(n => F75Layout.Keys.Single(k => k.Name == n).Led).ToArray();

    public static IReadOnlyDictionary<int, Rgb> Map(PomodoroSnapshot snapshot)
    {
        var color = snapshot.Phase == PomodoroPhase.Focus ? FocusColor : RestColor;
        var level = snapshot.Running ? 1.0 : PausedLevel;

        var result = new Dictionary<int, Rgb>(F75Layout.Keys.Count);
        foreach (var k in F75Layout.Keys) result[k.Led] = Scale(color, level);

        // Key i is fully lit while more than i+1 twelfths are left and fades out over its own twelfth.
        var lit = snapshot.RemainingFraction * BarLeds.Length;
        for (var i = 0; i < BarLeds.Length; i++) result[BarLeds[i]] = Scale(color, level * Math.Clamp(lit - i, 0, 1));
        return result;
    }

    private static Rgb Scale(Rgb c, double f) =>
        new((byte)Math.Round(c.R * f), (byte)Math.Round(c.G * f), (byte)Math.Round(c.B * f));
}
