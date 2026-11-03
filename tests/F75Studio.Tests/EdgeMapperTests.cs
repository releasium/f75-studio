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

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(40, 22)]
    [InlineData(64, 36)]
    [InlineData(1920, 1080)]
    public void Uniform_frame_maps_edges_to_colour_and_interior_to_scaled_colour(int w, int h)
    {
        var map = EdgeMapper.Map(Frame(w, h, (_, _) => new Rgb(10, 20, 30)));

        Assert.Equal(80, map.Count);
        var edgeNames = EdgeMapper.TopKeys.Concat(EdgeMapper.BottomKeys).Concat(EdgeMapper.LeftKeys).Concat(EdgeMapper.RightKeys).ToList();
        Assert.All(edgeNames, k => Assert.Equal(new Rgb(10, 20, 30), Led(map, k)));
        var interior = F75Layout.Keys.Where(k => !edgeNames.Contains(k.Name));
        Assert.All(interior, k => Assert.Equal(new Rgb(3, 6, 9), map[k.Led]));
    }

    [Fact]
    public void Vertical_split_drives_top_and_bottom_of_each_side_column()
    {
        var map = EdgeMapper.Map(Frame(64, 36, (_, y) => y < 18 ? new Rgb(255, 0, 0) : new Rgb(0, 0, 255)));

        Assert.Equal(new Rgb(255, 0, 0), Led(map, "`"));
        Assert.Equal(new Rgb(0, 0, 255), Led(map, "LShift"));
        Assert.Equal(new Rgb(255, 0, 0), Led(map, "Delete"));
        Assert.Equal(new Rgb(0, 0, 255), Led(map, "End"));
    }

    [Fact]
    public void Bright_block_in_bottom_strip_lights_only_the_keys_it_overlaps()
    {
        var map = EdgeMapper.Map(Frame(64, 36, (x, y) => y >= 34 && x >= 24 && x < 40 ? Rgb.White : Rgb.Black));

        Assert.NotEqual(Rgb.Black, Led(map, "Space"));
        Assert.Equal(Rgb.Black, Led(map, "LCtrl"));
        Assert.Equal(Rgb.Black, Led(map, "Right"));
    }

    [Fact]
    public void Interior_key_copies_its_nearest_edge_key_scaled()
    {
        // Top two rows red, bottom two rows blue, everything else black (edge depth is 2 rows on 36 px).
        var map = EdgeMapper.Map(Frame(64, 36, (_, y) => y < 2 ? new Rgb(255, 0, 0) : y >= 34 ? new Rgb(0, 0, 255) : Rgb.Black));

        var nearest = NearestEdgeKey("B");
        Assert.Equal("Space", nearest);
        var source = EdgeMapper.TopKeys.Contains(nearest) ? new Rgb(255, 0, 0)
                   : EdgeMapper.BottomKeys.Contains(nearest) ? new Rgb(0, 0, 255)
                   : Rgb.Black;
        var expected = new Rgb(
            (byte)Math.Round(source.R * EdgeMapper.InteriorScale),
            (byte)Math.Round(source.G * EdgeMapper.InteriorScale),
            (byte)Math.Round(source.B * EdgeMapper.InteriorScale));
        Assert.Equal(expected, Led(map, "B"));
    }

    private static string NearestEdgeKey(string interiorName)
    {
        var k = F75Layout.Keys.Single(x => x.Name == interiorName);
        var edgeNames = EdgeMapper.TopKeys.Concat(EdgeMapper.BottomKeys).Concat(EdgeMapper.LeftKeys).Concat(EdgeMapper.RightKeys);
        return edgeNames.MinBy(n =>
        {
            var e = F75Layout.Keys.Single(x => x.Name == n);
            double dx = (k.X + k.Width / 2.0) - (e.X + e.Width / 2.0);
            double dy = (k.Y + k.Height / 2.0) - (e.Y + e.Height / 2.0);
            return dx * dx + dy * dy;
        })!;
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 2)]
    public void Map_rejects_non_positive_dimensions(int w, int h)
    {
        Assert.Throws<ArgumentException>(() => EdgeMapper.Map(new CapturedFrame(w, h, [])));
    }

    [Fact]
    public void Map_rejects_a_buffer_shorter_than_the_frame()
    {
        Assert.Throws<ArgumentException>(() => EdgeMapper.Map(new CapturedFrame(2, 2, new byte[15])));
    }
}
