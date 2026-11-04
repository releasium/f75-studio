namespace F75Studio.Lighting;

/// <summary>
/// Screen sync (accent): the frame keys (F-row, bottom row, side columns) show the screen like <see cref="BlurMapper"/>,
/// and the main keys show an accent colour that contrasts with the screen's main colours, such as red for a green and blue screen.
/// </summary>
public static class AccentMapper
{
    /// <summary>Pixels below this chroma (0..1) are white, grey or black and don't count as a screen colour.</summary>
    public const double MinChroma = 0.15;

    /// <summary>Below this share of colour (summed chroma per pixel) the screen counts as colourless and <see cref="FallbackAccent"/> is used.</summary>
    public const double MinColourShare = 0.02;

    /// <summary>A second screen colour counts only if its weight is at least this fraction of the main colour's.</summary>
    public const double SecondColourShare = 0.25;

    /// <summary>A second screen colour counts only if its hue is at least this far (degrees) from the main colour's.</summary>
    public const int MinHueGap = 60;

    /// <summary>Accent used when the screen has no colour to contrast with.</summary>
    public static readonly Rgb FallbackAccent = new(0, 160, 255);

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
        foreach (var k in F75Layout.Keys) result[k.Led] = FrameLeds.Contains(k.Led) ? screen[k.Led] : accent;
        return result;
    }

    /// <summary>
    /// The hue farthest from the screen's main colours, at full saturation and brightness: the complement of a single
    /// main colour, or the middle of the larger arc between two.
    /// </summary>
    /// <exception cref="ArgumentException">The frame is empty or its buffer is shorter than Width*Height*4.</exception>
    public static Rgb Accent(CapturedFrame frame)
    {
        FrameSampler.Validate(frame);

        // Chroma-weighted hue histogram; bin i is centred on i * BinDegrees.
        var raw = new double[Bins];
        double total = 0;
        var pixels = frame.Width * frame.Height;
        for (var p = 0; p < pixels; p++)
        {
            var i = p * 4;
            double b = frame.Bgra[i] / 255.0, g = frame.Bgra[i + 1] / 255.0, r = frame.Bgra[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
            if (chroma < MinChroma) continue;
            raw[(int)Math.Round(Hue(r, g, b, max, chroma) / BinDegrees) % Bins] += chroma;
            total += chroma;
        }
        if (total < MinColourShare * pixels) return FallbackAccent;

        var bins = new double[Bins];
        for (var i = 0; i < Bins; i++) bins[i] = raw[(i + Bins - 1) % Bins] + 2 * raw[i] + raw[(i + 1) % Bins];

        var first = 0;
        for (var i = 1; i < Bins; i++) if (bins[i] > bins[first]) first = i;
        var second = -1;
        for (var i = 0; i < Bins; i++)
        {
            if (Gap(i * BinDegrees, first * BinDegrees) < MinHueGap || bins[i] < SecondColourShare * bins[first]) continue;
            if (second < 0 || bins[i] > bins[second]) second = i;
        }

        double h1 = first * BinDegrees;
        double hue = second < 0 ? h1 + 180 : MiddleOfLargerArc(h1, second * BinDegrees);
        return FromHue(hue);
    }

    private static double Hue(double r, double g, double b, double max, double chroma)
    {
        double h = max == r ? (g - b) / chroma : max == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
        return (h * 60 + 360) % 360;
    }

    private static double Gap(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return Math.Min(d, 360 - d);
    }

    private static double MiddleOfLargerArc(double a, double b)
    {
        var forward = ((b - a) % 360 + 360) % 360; // arc from a to b going up
        return forward >= 180 ? a + forward / 2 : b + (360 - forward) / 2;
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
