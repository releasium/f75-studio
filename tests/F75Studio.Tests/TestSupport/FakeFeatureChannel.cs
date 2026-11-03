using F75Studio.Lighting.Aula;

namespace F75Studio.Tests.TestSupport;

/// <summary>Simulates the keyboard's feature-report endpoint: answers reads, records writes, applies them.</summary>
public sealed class FakeFeatureChannel : IFeatureChannel
{
    private byte[]? _pendingRead;

    public byte[] Identity { get; set; } = [3, 0, 0, 0, 0, 0xCD];
    public byte[] SettingsPage { get; set; } = F75ProtocolTests.FixedOnPage();
    public byte[] ColorTable { get; set; } = F75ProtocolTests.ColorTable();
    public byte[] PerKey { get; private set; } = new byte[378];
    public List<byte[]> Sent { get; } = [];
    public int FailNextSets { get; set; }

    /// <summary>Like FailNextSets but only for write frames (reads are unaffected).</summary>
    public int FailNextWriteSets { get; set; }
    public bool Disposed { get; private set; }

    /// <summary>Shared ordered log; the session tests also add delays so ordering can be asserted.</summary>
    public List<string> Events { get; } = [];

    /// <summary>Number of settings-page reads answered so far.</summary>
    public int SettingsReads { get; private set; }

    /// <summary>Optionally replaces the settings page returned for the Nth (1-based) read.</summary>
    public Func<int, byte[], byte[]>? OnSettingsRead { get; set; }

    public IEnumerable<byte[]> Writes => Sent.Where(f => (f[1] & 0x80) == 0);

    public void SetFeature(byte[] frame)
    {
        if (FailNextSets > 0)
        {
            FailNextSets--;
            throw new IOException("simulated transfer failure");
        }
        if (FailNextWriteSets > 0 && (frame[1] & 0x80) == 0)
        {
            FailNextWriteSets--;
            throw new IOException("simulated write failure");
        }
        Sent.Add((byte[])frame.Clone());
        Events.Add($"set:{frame[1]:X2}");
        var length = frame[6] | (frame[7] << 8);
        var payload = frame.AsSpan(8, length).ToArray();
        switch (frame[1])
        {
            case 0x82: _pendingRead = Identity; break;
            case 0x84:
                SettingsReads++;
                _pendingRead = OnSettingsRead?.Invoke(SettingsReads, SettingsPage) ?? SettingsPage;
                break;
            case 0x8A: _pendingRead = ColorTable; break;
            case 0x04: SettingsPage = payload; break;
            case 0x0A: ColorTable = payload; break;
            case 0x06: PerKey = payload; break;
        }
        if ((frame[1] & 0x80) != 0) _lastReadHeader = frame[..8];
    }

    private byte[] _lastReadHeader = new byte[8];

    public void GetFeature(byte[] buffer)
    {
        Array.Clear(buffer);
        _lastReadHeader.CopyTo(buffer, 0);
        _pendingRead?.CopyTo(buffer, 8);
    }

    public void Dispose() => Disposed = true;
}
