using F75Studio.Lighting;

namespace F75Studio.Tests;

public class BlurMapperTests
{
    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Blue = new(0, 0, 255);

    private static int Led(string name) => F75Layout.Keys.Single(k => k.Name == name).Led;

    private static CapturedFrame Make(int w, int h, Func<int, int, Rgb> pixel)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = pixel(x, y);
            var i = (y * w + x) * 4;
            data[i] = c.B; data[i + 1] = c.G; data[i + 2] = c.R; data[i + 3] = 255;
        }
        return new CapturedFrame(w, h, data);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(40, 22)]
    [InlineData(64, 36)]
    [InlineData(1920, 1080)]
    public void Uniform_frame_gives_that_colour_on_every_key(int w, int h)
    {
        var map = BlurMapper.Map(Make(w, h, (_, _) => new Rgb(10, 20, 30)));

        Assert.Equal(80, map.Count);
        Assert.All(F75Layout.Keys, k => Assert.Equal(new Rgb(10, 20, 30), map[k.Led]));
    }

    [Fact]
    public void Left_red_right_blue_blurs_across_the_split()
    {
        var map = BlurMapper.Map(Make(64, 36, (x, _) => x < 32 ? Red : Blue));

        Assert.Equal(Red, map[Led("Esc")]);
        Assert.Equal(Red, map[Led("CapsLock")]);
        Assert.Equal(Blue, map[Led("PgUp")]);
        Assert.Equal(Blue, map[Led("Right")]);
        Assert.True(map[Led("J")].R > 0);
        Assert.True(map[Led("J")].B > 0);
    }

    [Fact]
    public void White_block_lights_only_keys_whose_blur_region_overlaps_it()
    {
        const int w = 64, h = 36;
        var white = new Rgb(255, 255, 255);
        var map = BlurMapper.Map(Make(w, h, (x, y) => x is >= 28 and <= 35 && y is >= 14 and <= 21 ? white : new Rgb(0, 0, 0)));

        var overlapping = 0;
        foreach (var k in F75Layout.Keys)
        {
            var (x0, y0, x1, y1) = TestRegion(k, w, h);
            var overlaps = x0 <= 35 && x1 > 28 && y0 <= 21 && y1 > 14;
            if (overlaps) { overlapping++; Assert.NotEqual(new Rgb(0, 0, 0), map[k.Led]); }
            else Assert.Equal(new Rgb(0, 0, 0), map[k.Led]);
        }
        Assert.True(overlapping > 0);
        foreach (var n in new[] { "Esc", "PgUp", "LCtrl", "Right" }) Assert.Equal(new Rgb(0, 0, 0), map[Led(n)]);
    }

    [Fact]
    public void Top_red_bottom_blue()
    {
        var map = BlurMapper.Map(Make(64, 36, (_, y) => y < 18 ? Red : Blue));

        Assert.Equal(Red, map[Led("F5")]);
        Assert.Equal(Blue, map[Led("Space")]);
    }

    [Fact]
    public void Invalid_frames_throw()
    {
        Assert.Throws<ArgumentException>(() => BlurMapper.Map(new CapturedFrame(0, 1, [])));
        Assert.Throws<ArgumentException>(() => BlurMapper.Map(new CapturedFrame(4, 4, new byte[10])));
    }

    // Same formula as the spec, computed independently of the production helper.
    private static (int X0, int Y0, int X1, int Y1) TestRegion(KeyDef k, int w, int h)
    {
        double left = F75Layout.Keys.Min(q => q.X);
        double right = F75Layout.Keys.Max(q => q.X + q.Width);
        double top = F75Layout.Keys.Min(q => q.Y);
        double bottom = F75Layout.Keys.Max(q => q.Y + q.Height);
        double nx0 = (k.X - left) / (right - left), nx1 = (k.X + k.Width - left) / (right - left);
        double ny0 = (k.Y - top) / (bottom - top), ny1 = (k.Y + k.Height - top) / (bottom - top);
        double mx = BlurMapper.Margin * (nx1 - nx0), my = BlurMapper.Margin * (ny1 - ny0);
        (int, int) S(double a, double b, int size)
        {
            var s = Math.Clamp((int)Math.Floor(Math.Clamp(a, 0, 1) * size), 0, size - 1);
            var e = Math.Clamp((int)Math.Ceiling(Math.Clamp(b, 0, 1) * size), s + 1, size);
            return (s, e);
        }
        var (xs, xe) = S(nx0 - mx, nx1 + mx, w);
        var (ys, ye) = S(ny0 - my, ny1 + my, h);
        return (xs, ys, xe, ye);
    }
}
