namespace F75Studio.Lighting;

/// <summary>Maps a downscaled screen image onto the keys as a blurred picture: each key shows the average of its own area plus a margin.</summary>
public static class BlurMapper
{
    /// <summary>Margin added on each side of a key, as a fraction of the key's own size.</summary>
    public const double Margin = 0.6;

    /// <exception cref="ArgumentException">The frame is empty or its buffer is shorter than Width*Height*4.</exception>
    public static IReadOnlyDictionary<int, Rgb> Map(CapturedFrame frame)
    {
        FrameSampler.Validate(frame);

        var width = FrameSampler.KbRight - FrameSampler.KbLeft;
        var height = FrameSampler.KbBottom - FrameSampler.KbTop;
        var result = new Dictionary<int, Rgb>(F75Layout.Keys.Count);
        foreach (var k in F75Layout.Keys)
        {
            double nx0 = (k.X - FrameSampler.KbLeft) / width, nx1 = (k.X + k.Width - FrameSampler.KbLeft) / width;
            double ny0 = (k.Y - FrameSampler.KbTop) / height, ny1 = (k.Y + k.Height - FrameSampler.KbTop) / height;
            double mx = Margin * (nx1 - nx0), my = Margin * (ny1 - ny0);
            var (xs, xe) = FrameSampler.Span(Math.Clamp(nx0 - mx, 0, 1), Math.Clamp(nx1 + mx, 0, 1), frame.Width);
            var (ys, ye) = FrameSampler.Span(Math.Clamp(ny0 - my, 0, 1), Math.Clamp(ny1 + my, 0, 1), frame.Height);
            result[k.Led] = FrameSampler.Average(frame, (xs, ys, xe, ye));
        }
        return result;
    }
}
