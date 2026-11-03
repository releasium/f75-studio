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
    public bool Disposed { get; private set; }

    public IEnumerable<byte[]> Writes => Sent.Where(f => (f[1] & 0x80) == 0);

    public void SetFeature(byte[] frame)
    {
        if (FailNextSets > 0)
        {
            FailNextSets--;
            throw new IOException("simulated transfer failure");
        }
        Sent.Add((byte[])frame.Clone());
        var length = frame[6] | (frame[7] << 8);
        var payload = frame.AsSpan(8, length).ToArray();
        switch (frame[1])
        {
            case 0x82: _pendingRead = Identity; break;
            case 0x84: _pendingRead = SettingsPage; break;
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
