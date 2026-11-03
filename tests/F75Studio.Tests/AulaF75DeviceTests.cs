using F75Studio.Lighting;
using F75Studio.Lighting.Aula;

namespace F75Studio.Tests;

public class AulaF75DeviceTests
{
    private static readonly byte[] KnownId = [0x03, 0x00, 0x00, 0x00, 0x00, 0xCD];
    private static readonly byte[] OtherId = [0x09, 0x09, 0x09, 0x09, 0x09, 0x09];

    [Theory]
    [InlineData(true, true, DeviceStatus.Dongle)]
    [InlineData(true, false, DeviceStatus.Dongle)]
    [InlineData(false, true, DeviceStatus.Bluetooth)]
    [InlineData(false, false, DeviceStatus.NotFound)]
    public void Classifies_non_cable_connections(bool dongle, bool bluetooth, DeviceStatus expected)
    {
        var (status, text) = AulaF75Device.ClassifyWithoutCable(dongle, bluetooth);
        Assert.Equal(expected, status);
        Assert.Contains("USB cable", text);
    }

    // Hardware-dependent: runs against whatever is plugged in. It must never throw and never write.
    // Skipped (returns early) unless the environment variable F75_HARDWARE_TESTS=1 is set.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Detection_produces_a_status_without_throwing()
    {
        if (Environment.GetEnvironmentVariable("F75_HARDWARE_TESTS") != "1") return;
        using var device = new AulaF75Device();
        await device.RefreshAsync();
        Assert.False(string.IsNullOrWhiteSpace(device.StatusText));
    }

    [Fact]
    public async Task Known_board_is_connected()
    {
        var probe = new FakeProbe { Boards = { new FakeBoard(KnownId) } };
        using var device = new AulaF75Device(probe, 0);
        await device.RefreshAsync();
        Assert.Equal(DeviceStatus.Connected, device.Status);
    }

    [Fact]
    public async Task Picks_the_known_board_among_several()
    {
        var probe = new FakeProbe { Boards = { new FakeBoard(OtherId), new FakeBoard(KnownId) } };
        using var device = new AulaF75Device(probe, 0);
        await device.RefreshAsync();
        Assert.Equal(DeviceStatus.Connected, device.Status);
    }

    [Fact]
    public async Task Only_unknown_boards_are_unsupported_and_mention_the_identity()
    {
        var probe = new FakeProbe { Boards = { new FakeBoard(OtherId) } };
        using var device = new AulaF75Device(probe, 0);
        await device.RefreshAsync();
        Assert.Equal(DeviceStatus.Unsupported, device.Status);
        Assert.Contains("090909090909", device.StatusText);
    }

    [Fact]
    public async Task Unopenable_board_is_an_error()
    {
        var probe = new FakeProbe { Boards = { new FakeBoard(KnownId) { OpenFails = true } } };
        using var device = new AulaF75Device(probe, 0);
        await device.RefreshAsync();
        Assert.Equal(DeviceStatus.Error, device.Status);
        Assert.Contains("Close the AULA F75 app", device.StatusText);
    }

    [Fact]
    public async Task Refresh_failure_becomes_an_error_status_and_does_not_throw()
    {
        var probe = new FakeProbe { EnumerationFails = true };
        using var device = new AulaF75Device(probe, 0);
        await device.RefreshAsync();
        Assert.Equal(DeviceStatus.Error, device.Status);
        Assert.StartsWith("Couldn't check for the keyboard:", device.StatusText);

        probe.EnumerationFails = false;
        await device.RefreshAsync();
        Assert.Equal(DeviceStatus.NotFound, device.Status);
    }

    [Fact]
    public async Task Subscriber_exception_does_not_break_later_refreshes()
    {
        var probe = new FakeProbe();
        using var device = new AulaF75Device(probe, 0);
        await device.RefreshAsync();
        var seen = new List<DeviceStatus>();
        device.StatusChanged += (_, _) => throw new InvalidOperationException("boom");
        device.StatusChanged += (_, _) => seen.Add(device.Status);

        probe.Bluetooth = true;
        await device.RefreshAsync();
        probe.Bluetooth = false;
        await device.RefreshAsync();

        Assert.Equal([DeviceStatus.Bluetooth, DeviceStatus.NotFound], seen);
    }

    [Fact]
    public async Task Status_changed_is_not_raised_when_nothing_changed()
    {
        var probe = new FakeProbe();
        using var device = new AulaF75Device(probe, 0);
        await device.RefreshAsync();
        var raised = 0;
        device.StatusChanged += (_, _) => raised++;
        await device.RefreshAsync();
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Hot_plug_bursts_are_coalesced_but_the_last_event_is_honoured()
    {
        var probe = new FakeProbe { BlockFirstEnumeration = new ManualResetEventSlim(false) };
        using var device = new AulaF75Device(probe, 0);
        Assert.True(SpinWait.SpinUntil(() => probe.EnumerationCount >= 1, 5000));

        for (var i = 0; i < 20; i++) probe.RaiseChanged();
        probe.Bluetooth = true;
        probe.BlockFirstEnumeration.Set();

        Assert.True(SpinWait.SpinUntil(() => device.Status == DeviceStatus.Bluetooth, 5000));
        await Task.Delay(200);
        Assert.Equal(2, probe.EnumerationCount); // one run in flight + one follow-up for the 20 events
    }

    [Fact]
    public async Task Apply_after_dispose_is_refused_and_refresh_stays_quiet()
    {
        var probe = new FakeProbe();
        var device = new AulaF75Device(probe, 0);
        await device.RefreshAsync();
        var raised = 0;
        device.StatusChanged += (_, _) => raised++;
        device.Dispose();

        var ex = await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.ApplyAsync(new LightingState()));
        Assert.Equal("Lighting is shutting down.", ex.Message);

        probe.Bluetooth = true;
        await device.RefreshAsync();
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Apply_when_not_connected_throws_with_the_status_text()
    {
        using var device = new AulaF75Device(new FakeProbe { Bluetooth = true }, 0);
        await device.RefreshAsync();
        var ex = await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.ApplyAsync(new LightingState()));
        Assert.Equal(device.StatusText, ex.Message);
    }

    [Fact]
    public async Task Apply_io_failure_maps_to_unavailable_and_refreshes_status()
    {
        var board = new FakeBoard(KnownId);
        var probe = new FakeProbe { Boards = { board } };
        using var device = new AulaF75Device(probe, 0);
        await Settle(device);
        Assert.Equal(DeviceStatus.Connected, device.Status);

        board.SetFeatureFails = true; // identity request can't even be sent
        var ex = await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.ApplyAsync(new LightingState()));
        Assert.StartsWith("Couldn't send lighting to the keyboard:", ex.Message);
        Assert.True(SpinWait.SpinUntil(() => device.Status == DeviceStatus.Error, 5000));
    }

    [Fact]
    public async Task Apply_never_writes_when_the_identity_is_not_known()
    {
        var board = new FakeBoard(KnownId);
        var probe = new FakeProbe { Boards = { board } };
        using var device = new AulaF75Device(probe, 0);
        await Settle(device);

        board.Identity = OtherId; // swapped after detection
        var ex = await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.ApplyAsync(new LightingState()));
        Assert.Contains("no longer identifies", ex.Message);
        Assert.All(board.Frames, f => Assert.Equal(0x82, f[1] & 0xFF)); // only identity reads were sent
    }

    [Fact]
    public async Task Official_app_running_is_an_error_and_nothing_is_sent()
    {
        var board = new FakeBoard(KnownId);
        var probe = new FakeProbe { Boards = { board }, OfficialApp = true };
        using var device = new AulaF75Device(probe, 0);
        await Settle(device);

        Assert.Equal(DeviceStatus.Error, device.Status);
        Assert.Equal(AulaF75Device.OfficialAppRunningText, device.StatusText);
        Assert.Empty(board.Frames); // not even an identity read
    }

    [Fact]
    public async Task Apply_refuses_and_sends_nothing_when_the_official_app_starts_after_detection()
    {
        var board = new FakeBoard(KnownId);
        var probe = new FakeProbe { Boards = { board } };
        using var device = new AulaF75Device(probe, 0);
        await Settle(device);
        Assert.Equal(DeviceStatus.Connected, device.Status);
        var framesBefore = board.Frames.Count;

        probe.OfficialApp = true;
        var ex = await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.ApplyAsync(new LightingState()));

        Assert.Equal(AulaF75Device.OfficialAppRunningText, ex.Message);
        Assert.Equal(framesBefore, board.Frames.Count);
        Assert.True(SpinWait.SpinUntil(() => device.Status == DeviceStatus.Error, 5000));
    }

    [Fact]
    public async Task Dispose_waits_for_an_in_flight_apply()
    {
        var board = new FakeBoard(KnownId);
        var probe = new FakeProbe { Boards = { board } };
        var device = new AulaF75Device(probe, 0);
        await Settle(device);

        board.BlockReads = new ManualResetEventSlim(false);
        var apply = Task.Run(() => device.ApplyAsync(new LightingState()));
        Assert.True(SpinWait.SpinUntil(() => board.ReadsBlocked, 5000));

        var disposed = Task.Run(device.Dispose);
        await Task.Delay(300);
        Assert.False(disposed.IsCompleted); // still waiting for the transaction on the wire

        board.BlockReads.Set();
        await disposed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(apply.IsCompleted); // the fake keyboard has no settings page, so the apply itself ends in a protocol error
        await Assert.ThrowsAsync<F75ProtocolException>(() => apply);
    }

    private static async Task Settle(AulaF75Device device)
    {
        Assert.True(SpinWait.SpinUntil(() => !device.IsRefreshing, 5000));
        await device.RefreshAsync();
    }

    private sealed class FakeBoard(byte[] identity) : IWiredBoard
    {
        public byte[] Identity { get; set; } = identity;
        public bool OpenFails { get; set; }
        public bool SetFeatureFails { get; set; }
        public ManualResetEventSlim? BlockReads { get; set; }
        public volatile bool ReadsBlocked;
        public List<byte[]> Frames { get; } = [];

        public IFeatureChannel Open() => OpenFails ? throw new IOException("busy") : new Channel(this);

        private sealed class Channel(FakeBoard board) : IFeatureChannel
        {
            private byte[]? _last;

            public void SetFeature(byte[] frame)
            {
                if (board.SetFeatureFails) throw new IOException("write failed");
                _last = (byte[])frame.Clone();
                board.Frames.Add(_last);
            }

            public void GetFeature(byte[] buffer)
            {
                if (board.BlockReads is { } gate) { board.ReadsBlocked = true; gate.Wait(); }
                Array.Copy(_last!, buffer, 8);
                Array.Copy(board.Identity, 0, buffer, 8, board.Identity.Length);
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeProbe : IHidProbe
    {
        private Action? _onChanged;
        private int _count;

        public List<FakeBoard> Boards { get; } = [];
        public bool Bluetooth { get; set; }
        public bool EnumerationFails { get; set; }
        public ManualResetEventSlim? BlockFirstEnumeration { get; set; }
        public int EnumerationCount => Volatile.Read(ref _count);

        public IReadOnlyList<IWiredBoard> FindWiredBoards()
        {
            if (Interlocked.Increment(ref _count) == 1) BlockFirstEnumeration?.Wait();
            if (EnumerationFails) throw new InvalidOperationException("enumeration failed");
            return Boards.ToList<IWiredBoard>();
        }

        public bool DonglePresent() => false;
        public bool BluetoothPresent() => Bluetooth;
        public bool OfficialApp { get; set; }
        public bool OfficialAppRunning() => OfficialApp;

        public IDisposable SubscribeChanged(Action onChanged)
        {
            _onChanged = onChanged;
            return new Noop();
        }

        public void RaiseChanged() => _onChanged?.Invoke();

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
