using System.Diagnostics;

namespace F75Studio.Lighting;

/// <summary>Maps a downscaled screen image onto the F75's keys, TV-ambilight style (screen-sync spec).</summary>
public static class EdgeMapper
{
    public const double EdgeDepth = 0.06;
    public const double InteriorScale = 0.30;

    public static IReadOnlyList<string> TopKeys { get; } = ["Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"];
    public static IReadOnlyList<string> BottomKeys { get; } = ["LCtrl", "LWin", "LAlt", "Space", "Fn", "RCtrl", "Left", "Down", "Right"];
    public static IReadOnlyList<string> LeftKeys { get; } = ["`", "Tab", "CapsLock", "LShift"];
    public static IReadOnlyList<string> RightKeys { get; } = ["Delete", "PgUp", "PgDn", "End"];

    private static readonly double KbLeft = F75Layout.Keys.Min(k => k.X);
    private static readonly double KbRight = F75Layout.Keys.Max(k => k.X + k.Width);
    private static readonly double KbTop = F75Layout.Keys.Min(k => k.Y);
    private static readonly double KbBottom = F75Layout.Keys.Max(k => k.Y + k.Height);

    private enum Edge { Top, Bottom, Left, Right }

    private static readonly IReadOnlyList<(KeyDef Key, Edge Edge)> EdgeKeys =
        TopKeys.Select(n => (Find(n), Edge.Top))
            .Concat(BottomKeys.Select(n => (Find(n), Edge.Bottom)))
            .Concat(LeftKeys.Select(n => (Find(n), Edge.Left)))
            .Concat(RightKeys.Select(n => (Find(n), Edge.Right)))
            .ToList();

    // For each interior key, the edge key it copies (fixed: depends only on the layout).
    private static readonly IReadOnlyList<(KeyDef Interior, KeyDef Source)> InteriorKeys =
        F75Layout.Keys
            .Where(k => EdgeKeys.All(e => e.Key.Led != k.Led))
            .Select(k => (k, EdgeKeys.MinBy(e => DistanceSquared(k, e.Key)).Key))
            .ToList();

    /// <exception cref="ArgumentException">The frame is empty or its buffer is shorter than Width*Height*4.</exception>
    public static IReadOnlyDictionary<int, Rgb> Map(CapturedFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra.Length < (long)frame.Width * frame.Height * 4)
            throw new ArgumentException("Frame must be non-empty and hold Width*Height*4 bytes.", nameof(frame));

        var result = new Dictionary<int, Rgb>(F75Layout.Keys.Count);
        foreach (var (key, edge) in EdgeKeys) result[key.Led] = Average(frame, Region(frame, key, edge));
        foreach (var (interior, source) in InteriorKeys)
        {
            var c = result[source.Led];
            result[interior.Led] = new Rgb(Scale(c.R), Scale(c.G), Scale(c.B));
        }
        return result;
    }

    private static (int X0, int Y0, int X1, int Y1) Region(CapturedFrame f, KeyDef key, Edge edge)
    {
        int w = f.Width, h = f.Height;
        var (xStart, xEnd) = SpanX(key, w);
        var (yStart, yEnd) = SpanY(key, h);
        return edge switch
        {
            Edge.Top => (xStart, 0, xEnd, Math.Max(1, (int)Math.Round(EdgeDepth * h))),
            Edge.Bottom => (xStart, h - Math.Max(1, (int)Math.Round(EdgeDepth * h)), xEnd, h),
            Edge.Left => (0, yStart, Math.Max(1, (int)Math.Round(EdgeDepth * w)), yEnd),
            _ => (w - Math.Max(1, (int)Math.Round(EdgeDepth * w)), yStart, w, yEnd),
        };
    }

    private static (int Start, int End) SpanX(KeyDef k, int w) =>
        Span((k.X - KbLeft) / (KbRight - KbLeft), (k.X + k.Width - KbLeft) / (KbRight - KbLeft), w);

    private static (int Start, int End) SpanY(KeyDef k, int h) =>
        Span((k.Y - KbTop) / (KbBottom - KbTop), (k.Y + k.Height - KbTop) / (KbBottom - KbTop), h);

    private static (int Start, int End) Span(double from, double to, int size)
    {
        var start = Math.Clamp((int)Math.Floor(from * size), 0, size - 1);
        var end = Math.Clamp((int)Math.Ceiling(to * size), start + 1, size);
        return (start, end);
    }

    private static Rgb Average(CapturedFrame f, (int X0, int Y0, int X1, int Y1) r)
    {
        long b = 0, g = 0, red = 0;
        long n = (long)(r.X1 - r.X0) * (r.Y1 - r.Y0);
        Debug.Assert(n > 0, "Edge regions are at least one pixel by construction.");
        for (var y = r.Y0; y < r.Y1; y++)
        for (var x = r.X0; x < r.X1; x++)
        {
            var i = (y * f.Width + x) * 4;
            b += f.Bgra[i];
            g += f.Bgra[i + 1];
            red += f.Bgra[i + 2];
        }
        return new Rgb((byte)Math.Round((double)red / n), (byte)Math.Round((double)g / n), (byte)Math.Round((double)b / n));
    }

    private static byte Scale(byte v) => (byte)Math.Round(v * InteriorScale);

    private static double DistanceSquared(KeyDef a, KeyDef b)
    {
        double dx = (a.X + a.Width / 2.0) - (b.X + b.Width / 2.0);
        double dy = (a.Y + a.Height / 2.0) - (b.Y + b.Height / 2.0);
        return dx * dx + dy * dy;
    }

    private static KeyDef Find(string name) => F75Layout.Keys.Single(k => k.Name == name);
}
