using System.Diagnostics;

namespace F75Studio.Lighting;

/// <summary>Shared frame validation and key-to-pixel geometry for the screen-sync mappers.</summary>
internal static class FrameSampler
{
    public static readonly double KbLeft = F75Layout.Keys.Min(k => k.X);
    public static readonly double KbRight = F75Layout.Keys.Max(k => k.X + k.Width);
    public static readonly double KbTop = F75Layout.Keys.Min(k => k.Y);
    public static readonly double KbBottom = F75Layout.Keys.Max(k => k.Y + k.Height);

    /// <exception cref="ArgumentException">The frame is empty or its buffer is shorter than Width*Height*4.</exception>
    public static void Validate(CapturedFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra.Length < (long)frame.Width * frame.Height * 4)
            throw new ArgumentException("Frame must be non-empty and hold Width*Height*4 bytes.", nameof(frame));
    }

    public static (int Start, int End) SpanX(KeyDef k, int w) =>
        Span((k.X - KbLeft) / (KbRight - KbLeft), (k.X + k.Width - KbLeft) / (KbRight - KbLeft), w);

    public static (int Start, int End) SpanY(KeyDef k, int h) =>
        Span((k.Y - KbTop) / (KbBottom - KbTop), (k.Y + k.Height - KbTop) / (KbBottom - KbTop), h);

    /// <summary>Pixel range for a normalized [from, to) interval: at least one pixel, inside [0, size).</summary>
    public static (int Start, int End) Span(double from, double to, int size)
    {
        var start = Math.Clamp((int)Math.Floor(from * size), 0, size - 1);
        var end = Math.Clamp((int)Math.Ceiling(to * size), start + 1, size);
        return (start, end);
    }

    public static Rgb Average(CapturedFrame f, (int X0, int Y0, int X1, int Y1) r)
    {
        long b = 0, g = 0, red = 0;
        long n = (long)(r.X1 - r.X0) * (r.Y1 - r.Y0);
        Debug.Assert(n > 0, "Regions are at least one pixel by construction.");
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
}
