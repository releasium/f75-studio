using F75Studio.Lighting;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class ScreenSyncEngineTests
{
    private static readonly int EscLed = F75Layout.Keys.Single(k => k.Name == "Esc").Led;
    private static readonly int InteriorLed = F75Layout.Keys.Single(k => k.Name == "G").Led;

    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Blue = new(0, 0, 255);

    private static CapturedFrame Solid(Rgb c, int w = 64, int h = 36)
    {
        var data = new byte[w * h * 4];
        for (var i = 0; i < data.Length; i += 4) { data[i] = c.B; data[i + 1] = c.G; data[i + 2] = c.R; data[i + 3] = 255; }
        return new CapturedFrame(w, h, data);
    }

    private sealed class Harness
    {
        private long _now;
        private int _delayCount;
        private readonly object _lock = new();
        private readonly List<int> _delays = [];

        public FakeKeyboardDevice Device { get; } = new();
        public FakeScreenCaptureFactory Captures { get; } = new();
        public long Now { get => Interlocked.Read(ref _now); set => Interlocked.Exchange(ref _now, value); }

        /// <summary>After this many delay calls, the next delay parks until cancelled (freezes the loop deterministically).</summary>
        public int ParkAfter { get; set; } = int.MaxValue;

        public IReadOnlyList<int> Delays { get { lock (_lock) return _delays.ToList(); } }
        public int DelayCount => Volatile.Read(ref _delayCount);

        public ScreenSyncEngine Engine { get; }

        public Harness()
        {
            Engine = new ScreenSyncEngine(Captures.Create, Device, null, () => Now, Delay);
        }

        private async Task Delay(int ms, CancellationToken ct)
        {
            lock (_lock) _delays.Add(ms);
            var n = Interlocked.Increment(ref _delayCount);
            Interlocked.Add(ref _now, ms);
            if (n > ParkAfter) await Task.Delay(Timeout.Infinite, ct);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
        }
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!cond())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task Streams_mapped_edge_colours()
    {
        var h = new Harness();
        h.Captures.Enqueue(Solid(Red));
        await h.Engine.StartAsync(() => 9);
        await WaitUntil(() => h.Device.LiveFramesSnapshot().Count >= 1);
        await h.Engine.StopAsync();

        var frame = h.Device.LiveFramesSnapshot()[0];
        Assert.Equal(new Rgb(255, 0, 0), frame[EscLed]);
        Assert.Equal(new Rgb(76, 0, 0), frame[InteriorLed]);
    }

    [Fact]
    public async Task Smoothing_moves_35_percent_per_frame()
    {
        var h = new Harness();
        h.Captures.Enqueue(Solid(Red)).Enqueue(Solid(Blue));
        await h.Engine.StartAsync(() => 9);
        await WaitUntil(() => h.Device.LiveFramesSnapshot().Count >= 2);
        await h.Engine.StopAsync();

        Assert.Equal(new Rgb(166, 0, 89), h.Device.LiveFramesSnapshot()[1][EscLed]);
    }

    [Fact]
    public async Task Identical_output_is_only_resent_as_keep_alive()
    {
        var h = new Harness { ParkAfter = 30 };
        h.Captures.Enqueue(Solid(Red));
        await h.Engine.StartAsync(() => 9);
        await WaitUntil(() => h.DelayCount >= 31);
        await h.Engine.StopAsync();

        Assert.Equal(3, h.Device.LiveFramesSnapshot().Count);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 128)]
    public async Task Brightness_scales_output_and_zero_is_black(int brightness, int expectedEscRed)
    {
        var h = new Harness();
        h.Captures.Enqueue(Solid(Red));
        await h.Engine.StartAsync(() => brightness);
        await WaitUntil(() => h.Device.LiveFramesSnapshot().Count >= 1);
        await h.Engine.StopAsync();

        var frame = h.Device.LiveFramesSnapshot()[0];
        Assert.Equal(new Rgb((byte)expectedEscRed, 0, 0), frame[EscLed]);
        if (brightness == 0) Assert.All(frame.Values, c => Assert.Equal(new Rgb(0, 0, 0), c));
    }

    [Fact]
    public async Task Send_failure_faults_and_stops()
    {
        var h = new Harness();
        h.Captures.Enqueue(Solid(Red));
        string? fault = null;
        bool? runningWhenFaulted = null;
        h.Engine.Faulted += m => { runningWhenFaulted = h.Engine.IsRunning; fault = m; };
        await h.Engine.StartAsync(() => 9);
        h.Device.LiveFailWith = new KeyboardUnavailableException("unplugged");
        await WaitUntil(() => fault is not null);

        Assert.Equal("unplugged", fault);
        Assert.False(runningWhenFaulted);
        Assert.False(h.Engine.IsRunning);
        Assert.Equal(1, h.Device.LiveStops);
        Assert.Equal(1, h.Captures.Disposed);

        await h.Engine.StopAsync();
        Assert.Equal(1, h.Device.LiveStops);
    }

    [Fact]
    public async Task Capture_failure_pauses_dims_and_recovers()
    {
        var h = new Harness();
        h.Captures.Enqueue(Solid(Red)).Enqueue(new ScreenCaptureException("locked")).Enqueue(Solid(Red));
        var statuses = new List<string?>();
        h.Engine.CaptureStatusChanged += s => { lock (statuses) statuses.Add(s); };
        await h.Engine.StartAsync(() => 9);
        await WaitUntil(() => { lock (statuses) return statuses.Count >= 2; });
        await h.Engine.StopAsync();

        Assert.Equal("Screen capture paused: locked", statuses[0]);
        Assert.Null(statuses[1]);
        Assert.Equal(2, h.Captures.Created);
        var frames = h.Device.LiveFramesSnapshot();
        Assert.Equal(255, frames[0][EscLed].R);
        Assert.True(frames[1][EscLed].R <= 128);
    }

    [Fact]
    public async Task Overrun_drops_instead_of_queueing()
    {
        var h = new Harness();
        for (var i = 0; i < 6; i++) h.Captures.Enqueue(Solid(i % 2 == 0 ? Red : Blue));
        h.Captures.OnCapture = () => h.Now += 100;
        await h.Engine.StartAsync(() => 9);
        await WaitUntil(() => h.Captures.Calls >= 6);
        await h.Engine.StopAsync();

        Assert.Empty(h.Delays);
        Assert.True(h.Device.LiveFramesSnapshot().Count >= 6); // every one of the first 6 (changing) cycles was sent, none skipped
    }

    [Fact]
    public async Task Start_failure_propagates_and_leaves_engine_stopped()
    {
        var h = new Harness();
        h.Device.SetStatus(DeviceStatus.Bluetooth, "Bluetooth");

        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => h.Engine.StartAsync(() => 9));

        Assert.False(h.Engine.IsRunning);
        Assert.Equal(0, h.Captures.Created);
    }

    [Fact]
    public async Task Stop_is_idempotent_and_releases_everything()
    {
        var h = new Harness();
        var faulted = false;
        h.Engine.Faulted += _ => faulted = true;
        h.Captures.Enqueue(Solid(Red));
        await h.Engine.StartAsync(() => 9);
        await WaitUntil(() => h.Device.LiveFramesSnapshot().Count >= 1);
        await h.Engine.StopAsync();
        await h.Engine.StopAsync();

        Assert.False(h.Engine.IsRunning);
        Assert.Equal(1, h.Device.LiveStops);
        Assert.Equal(1, h.Captures.Disposed);
        Assert.False(faulted);
    }
}
