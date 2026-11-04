using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace F75Studio.App.Services;

/// <summary>Draws the tray icon used while the Pomodoro timer is open: a coloured tile with the minutes left.</summary>
internal static class TrayIconRenderer
{
    private const int Size = 32;

    /// <summary>Text for the tile: whole minutes left, rounded up (so "1" is the last minute).</summary>
    public static string MinutesText(TimeSpan remaining) =>
        Math.Max(0, (int)Math.Ceiling(remaining.TotalMinutes)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Returns a new icon that owns its handle (disposing it frees it).</summary>
    public static Icon Render(string text, Color background)
    {
        using var bitmap = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (var path = RoundedRect(new RectangleF(0, 0, Size, Size), 7))
            using (var fill = new SolidBrush(background))
                g.FillPath(fill, path);

            // Two digits fill the tile; three ("120") need a smaller font.
            var fontSize = text.Length <= 2 ? 20f : 14f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, Size, Size), format);
        }
        // Icon.FromHandle doesn't own the handle: keep an owning copy and free the raw handle now.
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
