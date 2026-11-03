using F75Studio.Lighting;

namespace F75Studio.Tests;

public class EdgeMapperTests
{
    private static CapturedFrame Frame(int w, int h, Func<int, int, Rgb> pixel)
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

    private static Rgb Led(IReadOnlyDictionary<int, Rgb> map, string key) => map[F75Layout.Keys.Single(k => k.Name == key).Led];

    [Fact]
    public void Top_half_red_bottom_half_blue()
    {
        var map = EdgeMapper.Map(Frame(64, 36, (_, y) => y < 18 ? new Rgb(255, 0, 0) : new Rgb(0, 0, 255)));

        Assert.All(EdgeMapper.TopKeys, k => Assert.Equal(new Rgb(255, 0, 0), Led(map, k)));
        Assert.All(EdgeMapper.BottomKeys, k => Assert.Equal(new Rgb(0, 0, 255), Led(map, k)));
        Assert.Equal(80, map.Count);
    }

    [Fact]
    public void Left_and_right_stripes_drive_the_side_columns()
    {
        var map = EdgeMapper.Map(Frame(64, 36, (x, _) => x < 8 ? new Rgb(0, 255, 0) : x >= 56 ? new Rgb(255, 255, 0) : Rgb.Black));

        Assert.All(EdgeMapper.LeftKeys, k => Assert.Equal(new Rgb(0, 255, 0), Led(map, k)));
        Assert.All(EdgeMapper.RightKeys, k => Assert.Equal(new Rgb(255, 255, 0), Led(map, k)));
    }

    [Fact]
    public void Top_edge_follows_horizontal_position()
    {
        var map = EdgeMapper.Map(Frame(64, 36, (x, _) => x < 32 ? new Rgb(255, 0, 0) : new Rgb(0, 0, 255)));

        Assert.Equal(new Rgb(255, 0, 0), Led(map, "Esc"));
        Assert.Equal(new Rgb(0, 0, 255), Led(map, "F12"));
    }

    [Fact]
    public void Interior_keys_take_the_nearest_edge_dimmed()
    {
        var map = EdgeMapper.Map(Frame(64, 36, (_, _) => new Rgb(200, 100, 50)));

        Assert.Equal(new Rgb(60, 30, 15), Led(map, "G"));
        Assert.Equal(new Rgb(200, 100, 50), Led(map, "Esc"));
    }

    [Fact]
    public void Edge_lists_cover_the_documented_keys()
    {
        Assert.Equal(13, EdgeMapper.TopKeys.Count);
        Assert.Equal(new[] { "`", "Tab", "CapsLock", "LShift" }, EdgeMapper.LeftKeys);
        Assert.Equal(new[] { "Delete", "PgUp", "PgDn", "End" }, EdgeMapper.RightKeys);
        Assert.Contains("Space", EdgeMapper.BottomKeys);
    }

    [Fact]
    public void Tiny_frames_still_map_every_key()
    {
        var map = EdgeMapper.Map(Frame(2, 2, (_, _) => Rgb.White));
        Assert.Equal(80, map.Count);
    }
}
