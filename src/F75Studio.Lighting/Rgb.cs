using System.Globalization;

namespace F75Studio.Lighting;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParseHex(string? text, out Rgb rgb)
    {
        rgb = Black;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        rgb = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <param name="hue">0–360</param>
    /// <param name="saturation">0–1</param>
    /// <param name="value">0–1</param>
    public static Rgb FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}
