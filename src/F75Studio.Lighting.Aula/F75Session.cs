using System.Diagnostics;

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

    private readonly Func<long>? _clockMs; // test hook; null = Stopwatch

    public F75Session(IFeatureChannel channel, Func<int, CancellationToken, Task>? delay = null, Func<long>? clockMs = null)
    {
        _clockMs = clockMs;
        _channel = channel;
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    public Task<byte[]> ReadIdentityAsync(CancellationToken ct) => ReadAsync(F75Protocol.CmdIdentity, 1, 6, ct);

    public async Task ApplyAsync(LightingState state, CancellationToken ct)
    {
        var s = state.Normalize();
        var definition = F75Effects.Get(s.EffectId);
        if (definition.IsLive) throw new ArgumentOutOfRangeException(nameof(state), state.EffectId, "Live effects are streamed, not stored.");
        var settingsWait = 60;
        var wroteFirst = false;

        // Validate the settings page before anything is written, so a bad read changes nothing.
        var page = await ReadSettingsAsync(ct,
            "The keyboard returned incomplete lighting data. Nothing was changed.").ConfigureAwait(false);

        if (s.EffectId == EffectIds.PerKey)
        {
            await _delay(30, ct).ConfigureAwait(false);
            await WriteAsync(F75Protocol.CmdPerKey, F75Protocol.BuildPerKey(s.KeyColors), ct).ConfigureAwait(false);
            wroteFirst = true;
            settingsWait = 100;
        }
        else if (definition.HasColor && !s.Colorful)
        {
            var table = await ReadWithMarkerAsync(F75Protocol.CmdColorTable, F75Protocol.ColorTableLength, F75Protocol.ColorTableMarkerOffset, ct).ConfigureAwait(false);
            var patchedTable = F75Protocol.PatchColorTable(table, s.EffectId, s.Color);
            // Flash has limited write cycles: don't rewrite a table that already holds this colour.
            if (!patchedTable.AsSpan().SequenceEqual(table))
            {
                await _delay(120, ct).ConfigureAwait(false);
                await WriteAsync(F75Protocol.CmdColorTable, patchedTable, ct).ConfigureAwait(false);
                wroteFirst = true;
                settingsWait = 100;
            }
        }

        if (wroteFirst)
        {
            // Past the first write: finish the sequence regardless of cancellation so the keyboard is not left half-applied.
            ct = CancellationToken.None;
            page = await ReadSettingsAsync(ct,
                "The keyboard returned incomplete lighting data after the colours were written; the effect was not switched. Try again.").ConfigureAwait(false);
        }

        var patchedPage = F75Protocol.PatchSettings(page, s);
        if (patchedPage.AsSpan().SequenceEqual(page)) return; // already what the keyboard holds: skip the flash write
        await _delay(settingsWait, ct).ConfigureAwait(false);
        await WriteAsync(F75Protocol.CmdSettings, patchedPage, ct).ConfigureAwait(false);
    }

    private long _lastLiveFrame;

    /// <summary>Sends one volatile live frame (protocol doc §8). At least 7 ms between frames; one retry on a transfer error.</summary>
    public async Task SendLiveFrameAsync(byte[] payload, CancellationToken ct)
    {
        var frame = F75Protocol.Frame(F75Protocol.CmdLive, 0, payload.Length, payload);
        if (_lastLiveFrame != 0)
        {
            var since = _clockMs is null ? (int)Stopwatch.GetElapsedTime(_lastLiveFrame).TotalMilliseconds : (int)(_clockMs() - _lastLiveFrame);
            if (since < 7) await _delay(7 - since, ct).ConfigureAwait(false);
        }
        try
        {
            _channel.SetFeature(frame);
        }
        catch (IOException)
        {
            await _delay(7, ct).ConfigureAwait(false);
            _channel.SetFeature(frame);
        }
        _lastLiveFrame = _clockMs?.Invoke() ?? Stopwatch.GetTimestamp();
    }

    private async Task<byte[]> ReadSettingsAsync(CancellationToken ct, string failureMessage)
    {
        await _delay(30, ct).ConfigureAwait(false);
        return await ReadWithMarkerAsync(F75Protocol.CmdSettings, F75Protocol.SettingsLength, F75Protocol.SettingsMarkerOffset, ct, failureMessage).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadWithMarkerAsync(byte cmd, int length, int markerOffset, CancellationToken ct, string? failureMessage = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var data = await ReadAsync(cmd, 0, length, ct).ConfigureAwait(false);
            if (F75Protocol.HasMarker(data, markerOffset)) return data;
            if (attempt == MarkerReadAttempts)
                throw new F75ProtocolException(failureMessage ?? "The keyboard returned incomplete lighting data. Nothing was changed.");
            await _delay(100, ct).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> ReadAsync(byte writeCmd, byte param, int length, CancellationToken ct)
    {
        var request = F75Protocol.ReadRequest(writeCmd, param, length);
        await SendAsync(request, ct).ConfigureAwait(false);
        await _delay(20, ct).ConfigureAwait(false);
        var reply = new byte[F75Protocol.FrameLength];
        reply[0] = F75Protocol.ReportId;
        _channel.GetFeature(reply);
        return F75Protocol.ReplyPayload(reply, request, length);
    }

    private Task WriteAsync(byte cmd, byte[] payload, CancellationToken ct) =>
        SendAsync(F75Protocol.Frame(cmd, 0, payload.Length, payload), ct);

    private async Task SendAsync(byte[] frame, CancellationToken ct)
    {
        await _delay(20, ct).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _channel.SetFeature(frame);
                return;
            }
            catch (IOException) when (attempt < SetAttempts)
            {
                await _delay(70, ct).ConfigureAwait(false);
            }
        }
    }
}
