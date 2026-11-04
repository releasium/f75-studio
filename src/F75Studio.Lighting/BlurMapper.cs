namespace F75Studio.Lighting;

/// <summary>Maps a downscaled screen image onto the keys as a blurred picture: each key shows the average of its own area plus a margin.</summary>
/// <remarks>
/// A plain average of a large area is mostly white or grey on a typical desktop, so the average favours coloured pixels
/// and the result is then given more saturation, with greys dimmed: colours stand out and white areas stay in the background.
/// </remarks>
public static class BlurMapper
{
    /// <summary>Margin added on each side of a key, as a fraction of the key's own size.</summary>
    public const double Margin = 0.6;

    /// <summary>Extra weight of a fully saturated pixel in a key's average; a grey pixel weighs 1, a pure colour 1 + ColourWeight.</summary>
    public const double ColourWeight = 8;

    /// <summary>Brightness kept by a fully grey result (white shows as this fraction of full brightness); saturated colours keep all of it.</summary>
    public const double GreyLevel = 0.35;

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
            result[k.Led] = Enhance(ColourWeightedAverage(frame, (xs, ys, xe, ye)));
        }
        return result;
    }

    /// <summary>Average of the region in 0..1 per channel, each pixel weighted by 1 + ColourWeight * its chroma.</summary>
    private static (double R, double G, double B) ColourWeightedAverage(CapturedFrame f, (int X0, int Y0, int X1, int Y1) r)
    {
        double sr = 0, sg = 0, sb = 0, sw = 0;
        for (var y = r.Y0; y < r.Y1; y++)
        for (var x = r.X0; x < r.X1; x++)
        {
            var i = (y * f.Width + x) * 4;
            int b = f.Bgra[i], g = f.Bgra[i + 1], red = f.Bgra[i + 2];
            var chroma = (Math.Max(red, Math.Max(g, b)) - Math.Min(red, Math.Min(g, b))) / 255.0;
            var w = 1 + ColourWeight * chroma;
            sr += w * red; sg += w * g; sb += w * b; sw += w;
        }
        return (sr / sw / 255, sg / sw / 255, sb / sw / 255);
    }

    /// <summary>Raises saturation (s → 1 − (1 − s)², hue kept) and dims greys towards GreyLevel of their brightness.</summary>
    private static Rgb Enhance((double R, double G, double B) c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max <= 0) return new Rgb(0, 0, 0);

        var s = (max - min) / max;
        var boosted = 1 - (1 - s) * (1 - s);
        var stretch = s > 0 ? boosted / s : 0;
        var dim = GreyLevel + (1 - GreyLevel) * boosted;
        byte Ch(double v) => (byte)Math.Round(Math.Clamp((max - (max - v) * stretch) * dim, 0, 1) * 255);
        return new Rgb(Ch(c.R), Ch(c.G), Ch(c.B));
    }
}
