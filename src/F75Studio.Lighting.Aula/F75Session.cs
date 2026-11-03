namespace F75Studio.Lighting.Aula;

/// <summary>A HID feature-report endpoint (report 0x06, 520-byte buffers).</summary>
public interface IFeatureChannel : IDisposable
{
    void SetFeature(byte[] frame);

    /// <summary>Fills <paramref name="buffer"/> (buffer[0] = report id) with the device's reply.</summary>
    void GetFeature(byte[] buffer);
}

/// <summary>Paced read/write transactions with the driver's timings (protocol doc §2, §3.4).</summary>
public sealed class F75Session
{
    private const int MarkerReadAttempts = 5;
    private const int SetAttempts = 4; // first try + 3 retries

    private readonly IFeatureChannel _channel;
    private readonly Func<int, CancellationToken, Task> _delay;

    public F75Session(IFeatureChannel channel, Func<int, CancellationToken, Task>? delay = null)
    {
        _channel = channel;
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    public Task<byte[]> ReadIdentityAsync(CancellationToken ct) => ReadAsync(F75Protocol.CmdIdentity, 1, 6, ct);

    public async Task ApplyAsync(LightingState state, CancellationToken ct)
    {
        var s = state.Normalize();
        var definition = F75Effects.Get(s.EffectId);
        var settingsWait = 60;

        if (s.EffectId == EffectIds.PerKey)
        {
            await _delay(30, ct);
            await WriteAsync(F75Protocol.CmdPerKey, F75Protocol.BuildPerKey(s.KeyColors), ct);
            settingsWait = 100;
        }
        else if (definition.HasColor && !s.Colorful)
        {
            var table = await ReadWithMarkerAsync(F75Protocol.CmdColorTable, F75Protocol.ColorTableLength, F75Protocol.ColorTableMarkerOffset, ct);
            await _delay(120, ct);
            await WriteAsync(F75Protocol.CmdColorTable, F75Protocol.PatchColorTable(table, s.EffectId, s.Color), ct);
            settingsWait = 100;
        }

        var page = await ReadWithMarkerAsync(F75Protocol.CmdSettings, F75Protocol.SettingsLength, F75Protocol.SettingsMarkerOffset, ct);
        await _delay(settingsWait, ct);
        await WriteAsync(F75Protocol.CmdSettings, F75Protocol.PatchSettings(page, s), ct);
    }

    private async Task<byte[]> ReadWithMarkerAsync(byte cmd, int length, int markerOffset, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var data = await ReadAsync(cmd, 0, length, ct);
            if (F75Protocol.HasMarker(data, markerOffset)) return data;
            if (attempt == MarkerReadAttempts)
                throw new F75ProtocolException("The keyboard returned incomplete lighting data. Nothing was changed.");
            await _delay(100, ct);
        }
    }

    private async Task<byte[]> ReadAsync(byte writeCmd, byte param, int length, CancellationToken ct)
    {
        var request = F75Protocol.ReadRequest(writeCmd, param, length);
        await SendAsync(request, ct);
        await _delay(20, ct);
        var reply = new byte[F75Protocol.FrameLength];
        reply[0] = F75Protocol.ReportId;
        _channel.GetFeature(reply);
        return F75Protocol.ReplyPayload(reply, request, length);
    }

    private Task WriteAsync(byte cmd, byte[] payload, CancellationToken ct) =>
        SendAsync(F75Protocol.Frame(cmd, 0, payload.Length, payload), ct);

    private async Task SendAsync(byte[] frame, CancellationToken ct)
    {
        await _delay(20, ct);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _channel.SetFeature(frame);
                return;
            }
            catch (IOException) when (attempt < SetAttempts)
            {
                await _delay(70, ct);
            }
        }
    }
}
