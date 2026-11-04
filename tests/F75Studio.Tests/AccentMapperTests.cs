using F75Studio.Lighting;

namespace F75Studio.Tests;

public class AccentMapperTests
{
    private static readonly Rgb Green = new(0, 255, 0);
    private static readonly Rgb Blue = new(0, 0, 255);
    private static readonly Rgb White = new(255, 255, 255);

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

    [Fact]
    public void Mostly_green_with_blue_gives_a_red_accent()
    {
        var frame = Make(64, 36, (x, _) => x < 44 ? Green : Blue);

        Assert.Equal(new Rgb(255, 0, 0), AccentMapper.Accent(frame));
    }

    [Fact]
    public void Main_keys_show_the_accent_and_frame_keys_the_screen()
    {
        var frame = Make(64, 36, (x, _) => x < 44 ? Green : Blue);
        var map = AccentMapper.Map(frame);
        var blur = BlurMapper.Map(frame);

        Assert.Equal(80, map.Count);
        foreach (var k in F75Layout.Keys)
        {
            if (FrameKeys.Contains(k.Name)) Assert.Equal(blur[k.Led], map[k.Led]);
            else Assert.Equal(new Rgb(255, 0, 0), map[k.Led]);
        }
        Assert.Equal(new Rgb(255, 0, 0), map[Led("J")]);
    }

    [Fact]
    public void One_colour_gives_its_complement()
    {
        Assert.Equal(new Rgb(255, 0, 255), AccentMapper.Accent(Make(64, 36, (_, _) => Green)));
    }

    [Fact]
    public void A_small_second_colour_is_ignored()
    {
        // Blue is under a quarter of green's weight, so green alone decides: its complement, magenta.
        var frame = Make(64, 36, (x, _) => x < 58 ? Green : Blue);

        Assert.Equal(new Rgb(255, 0, 255), AccentMapper.Accent(frame));
    }

    [Fact]
    public void Accent_is_opposite_the_gap_between_two_close_colours()
    {
        // Yellow (60°) and green (120°): the accent is the middle of the larger arc, 270° (violet).
        var frame = Make(64, 36, (x, _) => x < 32 ? new Rgb(255, 255, 0) : Green);

        Assert.Equal(new Rgb(128, 0, 255), AccentMapper.Accent(frame));
    }

    [Fact]
    public void White_on_a_coloured_page_does_not_count()
    {
        var frame = Make(64, 36, (x, _) => x < 48 ? White : Green);

        Assert.Equal(new Rgb(255, 0, 255), AccentMapper.Accent(frame));
    }

    [Theory]
    [InlineData(255, 255, 255)]
    [InlineData(40, 40, 40)]
    [InlineData(200, 215, 200)] // too pale to count as a colour
    public void Colourless_screen_uses_the_fallback_accent(byte r, byte g, byte b)
    {
        Assert.Equal(AccentMapper.FallbackAccent, AccentMapper.Accent(Make(64, 36, (_, _) => new Rgb(r, g, b))));
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
