using F75Studio.Lighting;

namespace F75Studio.Tests;

public class AccentMapperTests
{
    private static readonly Rgb Green = new(0, 255, 0);
    private static readonly Rgb Blue = new(0, 0, 255);
    private static readonly Rgb White = new(255, 255, 255);
    private static readonly Rgb Black = new(0, 0, 0);

    private static int Led(string name) => F75Layout.Keys.Single(k => k.Name == name).Led;

    private static readonly HashSet<string> FrameKeys =
        [.. EdgeMapper.TopKeys, .. EdgeMapper.BottomKeys, .. EdgeMapper.LeftKeys, .. EdgeMapper.RightKeys];

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

    // A red and orange poster in the middle of a black page, like a film poster opened in the browser.
    private static CapturedFrame Poster() => Make(64, 36, (x, y) =>
        x is < 23 or >= 41 || y is < 4 or >= 32 ? Black
        : (x + y) % 3 == 0 ? new Rgb(240, 110, 20) // orange fire
        : (x + y) % 3 == 1 ? new Rgb(230, 30, 35)  // red title
        : new Rgb(60, 40, 35));                    // dark faces and coats

    [Fact]
    public void Red_poster_on_black_gives_a_red_accent()
    {
        var a = AccentMapper.Accent(Poster())!.Value;

        Assert.Equal(255, a.R);
        Assert.True(a.G < 128 && a.B < 40, a.ToString()); // red to red-orange, never blue
    }

    [Fact]
    public void Main_keys_show_the_accent_and_frame_keys_the_screen()
    {
        var frame = Poster();
        var map = AccentMapper.Map(frame);
        var blur = BlurMapper.Map(frame);
        var accent = AccentMapper.Accent(frame)!.Value;

        Assert.Equal(80, map.Count);
        foreach (var k in F75Layout.Keys)
            Assert.Equal(FrameKeys.Contains(k.Name) ? blur[k.Led] : accent, map[k.Led]);
        Assert.Equal(accent, map[Led("J")]);
    }

    [Fact]
    public void The_colour_with_the_most_screen_wins()
    {
        Assert.Equal(Green, AccentMapper.Accent(Make(64, 36, (x, _) => x < 44 ? Green : Blue)));
        Assert.Equal(Blue, AccentMapper.Accent(Make(64, 36, (x, _) => x < 20 ? Green : Blue)));
    }

    [Fact]
    public void Black_and_white_do_not_count()
    {
        // Green covers only a quarter of the screen but is its only colour.
        Assert.Equal(Green, AccentMapper.Accent(Make(64, 36, (x, _) => x < 24 ? White : x < 48 ? Black : Green)));
    }

    [Fact]
    public void Accent_is_full_strength_even_for_a_dark_colour()
    {
        Assert.Equal(new Rgb(255, 0, 0), AccentMapper.Accent(Make(64, 36, (_, _) => new Rgb(90, 0, 0))));
    }

    [Theory]
    [InlineData(255, 255, 255)]
    [InlineData(40, 40, 40)]
    [InlineData(200, 215, 200)] // too pale to count as a colour
    public void Colourless_screen_has_no_accent_and_every_key_follows_the_screen(byte r, byte g, byte b)
    {
        var frame = Make(64, 36, (_, _) => new Rgb(r, g, b));

        Assert.Null(AccentMapper.Accent(frame));
        var blur = BlurMapper.Map(frame);
        Assert.All(AccentMapper.Map(frame), kv => Assert.Equal(blur[kv.Key], kv.Value));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    [InlineData(40, 22)]
    public void Small_frames_map_every_key(int w, int h)
    {
        Assert.Equal(80, AccentMapper.Map(Make(w, h, (_, _) => Green)).Count);
    }

    [Fact]
    public void Invalid_frames_throw()
    {
        Assert.Throws<ArgumentException>(() => AccentMapper.Map(new CapturedFrame(0, 1, [])));
        Assert.Throws<ArgumentException>(() => AccentMapper.Map(new CapturedFrame(4, 4, new byte[10])));
    }
}
