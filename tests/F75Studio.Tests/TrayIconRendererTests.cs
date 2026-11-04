using System.Drawing;
using F75Studio.App.Services;

namespace F75Studio.Tests;

public class TrayIconRendererTests
{
    [Theory]
    [InlineData(15 * 60, "15")]
    [InlineData(14 * 60 + 1, "15")]  // rounded up: 14:01 left still shows 15
    [InlineData(14 * 60, "14")]
    [InlineData(30, "1")]           // the last minute
    [InlineData(0, "0")]
    [InlineData(120 * 60, "120")]
    public void Shows_whole_minutes_left_rounded_up(int seconds, string expected)
    {
        Assert.Equal(expected, TrayIconRenderer.MinutesText(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Renders_a_coloured_tile_with_white_text()
    {
        var red = Color.FromArgb(0xE5, 0x38, 0x3B);
        using var icon = TrayIconRenderer.Render("14", red);
        {
            using var bitmap = icon.ToBitmap();
            Assert.Equal(32, bitmap.Width);
            Assert.Equal(0, bitmap.GetPixel(0, 0).A);          // rounded corner stays transparent
            var edge = bitmap.GetPixel(16, 2);                  // tile background above the digits
            Assert.Equal((red.R, red.G, red.B), (edge.R, edge.G, edge.B));
            var white = 0;
            for (var y = 8; y < 24; y++)
            for (var x = 6; x < 26; x++)
                if (bitmap.GetPixel(x, y) is { R: > 240, G: > 240, B: > 240 }) white++;
            Assert.True(white > 20, $"only {white} white pixels: the digits are missing");
        }
    }
}
