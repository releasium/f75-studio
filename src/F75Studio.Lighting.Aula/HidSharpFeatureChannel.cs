using HidSharp;

namespace F75Studio.Lighting.Aula;

public sealed class HidSharpFeatureChannel : IFeatureChannel
{
    private readonly HidStream _stream;

    private HidSharpFeatureChannel(HidStream stream)
    {
        _stream = stream;
        _stream.ReadTimeout = 2000;
        _stream.WriteTimeout = 2000;
    }

    public static HidSharpFeatureChannel Open(HidDevice device) =>
        device.TryOpen(out var stream)
            ? new HidSharpFeatureChannel(stream)
            : throw new IOException("Couldn't open the keyboard's lighting interface.");

    public void SetFeature(byte[] frame) => _stream.SetFeature(frame);

    public void GetFeature(byte[] buffer) => _stream.GetFeature(buffer);

    public void Dispose() => _stream.Dispose();
}
