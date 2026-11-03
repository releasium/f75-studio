using F75Studio.Lighting.Aula;

namespace F75Studio.Tests.TestSupport;

internal sealed class FakeBoard(byte[] identity) : IWiredBoard
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

internal sealed class FakeProbe : IHidProbe
{
    private Action? _onChanged;
    private int _count;

    public List<IWiredBoard> Boards { get; } = [];
    public bool Bluetooth { get; set; }
    public bool EnumerationFails { get; set; }
    public ManualResetEventSlim? BlockFirstEnumeration { get; set; }
    public int EnumerationCount => Volatile.Read(ref _count);

    public IReadOnlyList<IWiredBoard> FindWiredBoards()
    {
        if (Interlocked.Increment(ref _count) == 1) BlockFirstEnumeration?.Wait();
        if (EnumerationFails) throw new InvalidOperationException("enumeration failed");
        return Boards.ToList();
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

/// <summary>A board that always hands out the same <see cref="FakeFeatureChannel"/>, so tests can inspect every frame sent.</summary>
internal sealed class FakeChannelBoard(FakeFeatureChannel channel) : IWiredBoard
{
    public IFeatureChannel Open() => channel;
}

/// <summary>A board that returns a NEW channel per Open() and records every channel, to detect leaked handles.</summary>
internal sealed class FakeTrackedBoard : IWiredBoard
{
    private readonly object _lock = new();
    private readonly List<FakeFeatureChannel> _opened = [];

    public IReadOnlyList<FakeFeatureChannel> Opened
    {
        get { lock (_lock) return _opened.ToList(); }
    }

    public FakeFeatureChannel Last => Opened[^1];

    public IFeatureChannel Open()
    {
        var channel = new FakeFeatureChannel();
        lock (_lock) _opened.Add(channel);
        return channel;
    }
}
