namespace F75Studio.Lighting;

/// <summary>
/// Screen sync (accent): the main keys glow in the screen's accent, its strongest colour (red for a red film poster
/// on a black page), and the frame keys (F-row, bottom row, side columns) show the surrounding screen like <see cref="BlurMapper"/>.
/// </summary>
public static class AccentMapper
{
    /// <summary>Pixels below this chroma (0..1) are white, grey or black and don't count towards the accent.</summary>
    public const double MinChroma = 0.15;

    /// <summary>Below this share of colour (summed chroma per pixel) the screen has no accent and the main keys show <see cref="BlurMapper"/> colours too.</summary>
    public const double MinColourShare = 0.02;

    /// <summary>Hues within this many degrees of the strongest hue are averaged into the accent.</summary>
    public const int AccentSpread = 20;

    private const int BinDegrees = 10;
    private const int Bins = 360 / BinDegrees;

    private static readonly HashSet<int> FrameLeds =
        EdgeMapper.TopKeys.Concat(EdgeMapper.BottomKeys).Concat(EdgeMapper.LeftKeys).Concat(EdgeMapper.RightKeys)
            .Select(n => F75Layout.Keys.Single(k => k.Name == n).Led)
            .ToHashSet();

    /// <exception cref="ArgumentException">The frame is empty or its buffer is shorter than Width*Height*4.</exception>
    public static IReadOnlyDictionary<int, Rgb> Map(CapturedFrame frame)
    {
        var screen = BlurMapper.Map(frame); // validates the frame
        var accent = Accent(frame);
        var result = new Dictionary<int, Rgb>(F75Layout.Keys.Count);
        foreach (var k in F75Layout.Keys)
            result[k.Led] = accent is { } a && !FrameLeds.Contains(k.Led) ? a : screen[k.Led];
        return result;
    }

    /// <summary>
    /// The screen's strongest colour at full saturation and brightness: the hue with the most colour (summed chroma,
    /// so black, white and grey don't count), refined by averaging the hues around it; null when the screen has almost no colour.
    /// </summary>
    /// <exception cref="ArgumentException">The frame is empty or its buffer is shorter than Width*Height*4.</exception>
    public static Rgb? Accent(CapturedFrame frame)
    {
        FrameSampler.Validate(frame);

        // Chroma-weighted hue histogram; bin i is centred on i * BinDegrees. Unit vectors keep the hue average circular.
        var weight = new double[Bins];
        var sumX = new double[Bins];
        var sumY = new double[Bins];
        double total = 0;
        var pixels = frame.Width * frame.Height;
        for (var p = 0; p < pixels; p++)
        {
            var i = p * 4;
            double b = frame.Bgra[i] / 255.0, g = frame.Bgra[i + 1] / 255.0, r = frame.Bgra[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
            if (chroma < MinChroma) continue;
            var hue = Hue(r, g, b, max, chroma);
            var bin = (int)Math.Round(hue / BinDegrees) % Bins;
            weight[bin] += chroma;
            sumX[bin] += chroma * Math.Cos(hue * Math.PI / 180);
            sumY[bin] += chroma * Math.Sin(hue * Math.PI / 180);
            total += chroma;
        }
        if (total < MinColourShare * pixels) return null;

        // Strongest hue region: the bin whose neighbourhood (±AccentSpread) holds the most colour.
        var reach = AccentSpread / BinDegrees;
        int best = 0;
        double bestWeight = -1;
        for (var c = 0; c < Bins; c++)
        {
            double w = 0;
            for (var d = -reach; d <= reach; d++) w += weight[(c + d + Bins) % Bins];
            if (w > bestWeight) { bestWeight = w; best = c; }
        }
        double x = 0, y = 0;
        for (var d = -reach; d <= reach; d++) { x += sumX[(best + d + Bins) % Bins]; y += sumY[(best + d + Bins) % Bins]; }
        return FromHue(Math.Atan2(y, x) * 180 / Math.PI);
    }

    private static double Hue(double r, double g, double b, double max, double chroma)
    {
        double h = max == r ? (g - b) / chroma : max == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
        return (h * 60 + 360) % 360;
    }

    private static Rgb FromHue(double hue)
    {
        var h = (hue % 360 + 360) % 360 / 60;
        var x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };
        static byte B(double v) => (byte)Math.Round(v * 255, MidpointRounding.AwayFromZero);
        return new Rgb(B(r), B(g), B(b));
    }
}
