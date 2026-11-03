using F75Studio.App.Services;
using F75Studio.Lighting;

namespace F75Studio.Tests;

public class DxgiScreenCaptureTests
{
    // Real GPU/desktop: runs only when F75_CAPTURE_TESTS=1 (set it manually on a desktop session).
    [Fact]
    [Trait("Category", "Integration")]
    public void Captures_a_small_bgra_frame_of_the_primary_screen()
    {
        if (Environment.GetEnvironmentVariable("F75_CAPTURE_TESTS") != "1") return;

        using var capture = new DxgiScreenCapture();
        CapturedFrame? frame = null;
        for (var i = 0; i < 20 && frame is null; i++) frame = capture.TryCapture(100);

        Assert.NotNull(frame);
        Assert.InRange(frame!.Width, 1, DxgiScreenCapture.TargetWidth);
        Assert.Equal(frame.Width * frame.Height * 4, frame.Bgra.Length);
        // Size only; never the pixel content (private).
        Console.Error.WriteLine($"CAPTURE_SIZE {frame.Width}x{frame.Height}");
    }
}
