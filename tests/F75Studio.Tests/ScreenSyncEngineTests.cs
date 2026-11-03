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
    public async Task A_custom_mapper_replaces_the_edge_mapper()
    {
        var h = new Harness();
        h.Captures.Enqueue(Solid(Red));
        var green = new Rgb(0, 255, 0);
        await h.Engine.StartAsync(() => 9, _ => F75Layout.Keys.ToDictionary(k => k.Led, _ => green));
        await WaitUntil(() => h.Device.LiveFramesSnapshot().Count >= 1);
        await h.Engine.StopAsync();

        Assert.All(h.Device.LiveFramesSnapshot()[0].Values, c => Assert.Equal(green, c));
    }

    [Fact]
    public async Task The_mapper_is_consulted_every_frame_so_it_can_switch_without_a_restart()
    {
        var h = new Harness();
        var green = new Rgb(0, 255, 0);
        var useBlue = 0;
        IReadOnlyDictionary<int, Rgb> Map(CapturedFrame _) =>
            F75Layout.Keys.ToDictionary(k => k.Led, _ => Volatile.Read(ref useBlue) == 1 ? Blue : green);
        h.Captures.Enqueue(Solid(Red));
        await h.Engine.StartAsync(() => 9, Map);
        await WaitUntil(() => h.Device.LiveFramesSnapshot().Count >= 1);
        Assert.Equal(green, h.Device.LiveFramesSnapshot()[0][EscLed]);

        Volatile.Write(ref useBlue, 1);
        for (var i = 0; i < 40; i++) h.Captures.Enqueue(Solid(Red));
        await WaitUntil(() => h.Device.LiveFramesSnapshot().Any(f => f[EscLed].B > 200));
        await h.Engine.StopAsync();

        Assert.Equal(1, h.Device.LiveStarts);
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

    [Fact]
    public async Task Unexpected_exception_faults_and_engine_can_restart()
    {
        var h = new Harness();
        h.Captures.Enqueue(Solid(Red));
        string? fault = null;
        h.Engine.Faulted += m => fault = m;
        await h.Engine.StartAsync(() => throw new InvalidOperationException("boom"));
        await WaitUntil(() => fault is not null);

        Assert.Equal("boom", fault);
        Assert.False(h.Engine.IsRunning);
        Assert.Equal(1, h.Device.LiveStops);

        h.Captures.Enqueue(Solid(Red));
        await h.Engine.StartAsync(() => 9);
        Assert.True(h.Engine.IsRunning);
        await WaitUntil(() => h.Device.LiveFramesSnapshot().Count >= 1);
        await h.Engine.StopAsync();
        Assert.Equal(2, h.Device.LiveStarts);
        Assert.Equal(2, h.Device.LiveStops);
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_stop_streaming()
    {
        var h = new Harness();
        h.Captures.Enqueue(Solid(Red)).Enqueue(Solid(Blue));
        h.Engine.FrameSent += _ => throw new InvalidOperationException("subscriber");
        await h.Engine.StartAsync(() => 9);
        await WaitUntil(() => h.Device.LiveFramesSnapshot().Count >= 2);
        Assert.True(h.Engine.IsRunning);
        await h.Engine.StopAsync();
    }

    [Fact]
    public async Task Factory_failure_during_pause_retries_on_a_delay_and_recovers()
    {
        var h = new Harness();
        h.Captures.Enqueue(Solid(Red)).Enqueue(new ScreenCaptureException("locked")).Enqueue(Solid(Red));
        h.Captures.OnCapture = () => { if (h.Captures.Calls == 2) h.Captures.CreateFailure = new IOException("gpu reset"); };
        var statuses = new List<string?>();
        h.Engine.CaptureStatusChanged += s => { lock (statuses) statuses.Add(s); };
        await h.Engine.StartAsync(() => 9);
        await WaitUntil(() => h.DelayCount >= 4);
        h.Captures.CreateFailure = null;
        await WaitUntil(() => { lock (statuses) return statuses.Count >= 2; });
        await h.Engine.StopAsync();

        Assert.All(h.Delays.Skip(1).Take(3), d => Assert.Equal(500, d));
        lock (statuses) Assert.Equal(new string?[] { "Screen capture paused: locked", null }, statuses.Take(2));
    }

    [Fact]
    public async Task Capture_failure_at_start_begins_paused_then_resumes_and_sends()
    {
        var h = new Harness();
        h.Captures.CreateFailure = new ScreenCaptureException("secure desktop");
        var statuses = new List<string?>();
        h.Engine.CaptureStatusChanged += s => { lock (statuses) statuses.Add(s); };

        await h.Engine.StartAsync(() => 9);

        Assert.True(h.Engine.IsRunning);
        await WaitUntil(() => { lock (statuses) return statuses.Count >= 1; });
        lock (statuses) Assert.Equal("Screen capture paused: secure desktop", statuses[0]);
        Assert.Empty(h.Device.LiveFramesSnapshot());

        h.Captures.Enqueue(Solid(Red));
        h.Captures.CreateFailure = null;
        await WaitUntil(() => h.Device.LiveFramesSnapshot().Count >= 1);
        await h.Engine.StopAsync();

        lock (statuses) Assert.Equal(new string?[] { "Screen capture paused: secure desktop", null }, statuses.Take(2));
        Assert.Equal(1, h.Device.LiveStarts);
        Assert.Equal(1, h.Device.LiveStops);
    }

    [Fact]
    public async Task Concurrent_start_and_stop_leave_a_consistent_state()
    {
        var h = new Harness();
        h.Captures.Enqueue(Solid(Red));
        var start = Task.Run(() => h.Engine.StartAsync(() => 9));
        var stop = Task.Run(() => h.Engine.StopAsync());
        await Task.WhenAll(start, stop);

        if (h.Engine.IsRunning)
        {
            Assert.Equal(1, h.Device.LiveStarts);
            Assert.Equal(0, h.Device.LiveStops);
        }
        else
        {
            Assert.Equal(h.Device.LiveStarts, h.Device.LiveStops);
        }
        await h.Engine.StopAsync();
        Assert.False(h.Engine.IsRunning);
        Assert.Equal(h.Device.LiveStarts, h.Device.LiveStops);
    }
}
