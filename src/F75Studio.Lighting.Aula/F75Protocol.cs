namespace F75Studio.Lighting.Aula;

public sealed class F75ProtocolException(string message) : Exception(message);

/// <summary>Wired AULA F75 lighting protocol (docs/protocol/aula-f75.md). Pure functions only.</summary>
public static class F75Protocol
{
    public const byte ReportId = 0x06;
    public const int FrameLength = 520;
    public const int HeaderLength = 8;
    public const int SettingsLength = 128;
    public const int ColorTableLength = 512;
    public const int PerKeyLength = 378;
    public const int PlaneSize = 126;
    public const int SettingsMarkerOffset = 126;
    public const int ColorTableMarkerOffset = 506;

    public const byte CmdIdentity = 0x02;   // only ever sent as a read (0x82)
    public const byte CmdSettings = 0x04;
    public const byte CmdColorTable = 0x0A;
    public const byte CmdPerKey = 0x06;
    public const byte ReadFlag = 0x80;

    // (command, param, length) — everything else is refused (protocol doc §10).
    private static readonly HashSet<(byte Cmd, byte Param, int Length)> Allowed =
    [
        (0x82, 1, 6),
        (0x84, 0, SettingsLength), (0x04, 0, SettingsLength),
        (0x8A, 0, ColorTableLength), (0x0A, 0, ColorTableLength),
        (0x86, 0, PerKeyLength), (0x06, 0, PerKeyLength),
    ];

    private static readonly byte[][] KnownIdentities =
    [
        [0x03, 0x00, 0x00, 0x00, 0x00, 0xCD], // AULA F75 (tri-mode)
        [0x03, 0x00, 0x00, 0x00, 0x00, 0xE1], // AULA F75 Wired
        [0x03, 0x00, 0x00, 0x00, 0x01, 0x91], // AULA F75 KR
    ];

    /// <summary>
    /// Builds a 520-byte feature report. Reads carry no payload. Writes must carry exactly
    /// <paramref name="length"/> bytes, and settings/colour-table writes must carry their 5A A5 marker.
    /// </summary>
    public static byte[] Frame(byte cmd, byte param, int length, ReadOnlySpan<byte> payload = default)
    {
        if (!Allowed.Contains((cmd, param, length)))
            throw new InvalidOperationException($"Blocked F75 command 0x{cmd:X2} (param {param}, length {length}).");

        if ((cmd & ReadFlag) != 0)
        {
            if (payload.Length != 0)
                throw new InvalidOperationException($"Read request 0x{cmd:X2} must not carry a payload.");
        }
        else
        {
            if (payload.Length != length)
                throw new InvalidOperationException($"Write 0x{cmd:X2} must carry exactly {length} payload bytes (got {payload.Length}).");
            if (cmd == CmdSettings && !HasMarker(payload, SettingsMarkerOffset))
                throw new InvalidOperationException("Refusing to write a settings page without its 5A A5 marker.");
            if (cmd == CmdColorTable && !HasMarker(payload, ColorTableMarkerOffset))
                throw new InvalidOperationException("Refusing to write a colour table without its 5A A5 marker.");
        }

        var frame = new byte[FrameLength];
        frame[0] = ReportId;
        frame[1] = cmd;
        frame[2] = param;
        frame[4] = 1; // one package
        frame[6] = (byte)(length & 0xFF);
        frame[7] = (byte)(length >> 8);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static byte[] ReadRequest(byte writeCmd, byte param, int length) => Frame((byte)(writeCmd | ReadFlag), param, length);

    /// <summary>Returns the payload of <paramref name="reply"/> after checking its header against <paramref name="request"/>.</summary>
    public static byte[] ReplyPayload(byte[] reply, byte[] request, int length)
    {
        if (reply.Length < HeaderLength + length
            || !reply.AsSpan(0, HeaderLength).SequenceEqual(request.AsSpan(0, HeaderLength)))
            throw new F75ProtocolException($"Unexpected reply from the keyboard (expected header {Convert.ToHexString(request, 0, HeaderLength)}).");
        return reply.AsSpan(HeaderLength, length).ToArray();
    }

    public static bool IsKnownIdentity(ReadOnlySpan<byte> identity)
    {
        foreach (var known in KnownIdentities)
            if (identity.SequenceEqual(known)) return true;
        return false;
    }

    public static bool HasMarker(ReadOnlySpan<byte> region, int offset) =>
        offset >= 0 && region.Length >= offset + 2 && region[offset] == 0x5A && region[offset + 1] == 0xA5;

    /// <summary>Settings-page slot of an effect (protocol doc §4).</summary>
    public static int SlotFor(int effectId) => effectId switch
    {
        >= 1 and <= 18 => effectId - 1,
        EffectIds.PerKey => 18,
        _ => throw new ArgumentOutOfRangeException(nameof(effectId), effectId, "Effect has no settings slot."),
    };

    /// <summary>Read-modify-write of the 128-byte settings page; only the documented bytes change.</summary>
    public static byte[] PatchSettings(ReadOnlySpan<byte> page, LightingState state)
    {
        if (page.Length != SettingsLength || !HasMarker(page, SettingsMarkerOffset))
            throw new F75ProtocolException("The keyboard's settings page is missing its 5A A5 marker.");
        var definition = F75Effects.Get(state.EffectId);
        var s = state.Normalize();
        var p = page.ToArray();

        p[0x0A] = (byte)s.EffectId;
        p[0x09] = (byte)(s.EffectId == EffectIds.PerKey ? 1 : 0);
        if (s.EffectId == EffectIds.PerKey) p[0x0B] = 0x20;
        p[0x38] = 0xFF;
        p[0x39] = 0xFF;

        if (s.EffectId != EffectIds.Off)
        {
            var slot = SlotFor(s.EffectId);
            var stored = p[0x3B + 2 * slot];
            var speed = definition.HasSpeed ? s.Speed : stored >> 4;
            var colour = definition.HasColor ? (s.Colorful ? 7 : 0) : stored & 0x0F;
            p[0x3A + 2 * slot] = (byte)s.Brightness;
            p[0x3B + 2 * slot] = (byte)((speed << 4) | colour);
        }
        return p;
    }

    /// <summary>Puts <paramref name="color"/> into slot 0 of the effect's colour-table row (protocol doc §6).</summary>
    public static byte[] PatchColorTable(ReadOnlySpan<byte> table, int effectId, Rgb color)
    {
        if (table.Length != ColorTableLength || !HasMarker(table, ColorTableMarkerOffset))
            throw new F75ProtocolException("The keyboard's colour table is missing its 5A A5 marker.");
        if (!F75Effects.IsSupported(effectId) || !F75Effects.Get(effectId).HasColor)
            throw new ArgumentOutOfRangeException(nameof(effectId), effectId, "Effect has no colour row.");
        var t = table.ToArray();
        var offset = effectId * 21;
        t[offset] = color.R;
        t[offset + 1] = color.G;
        t[offset + 2] = color.B;
        return t;
    }

    private static readonly HashSet<int> RealLeds = F75Layout.Keys.Select(k => k.Led).ToHashSet();

    /// <summary>378 bytes, planar: R[126], G[126], B[126] by LED index (protocol doc §7.1).</summary>
    public static byte[] BuildPerKey(IReadOnlyDictionary<int, Rgb> colors)
    {
        var data = new byte[PerKeyLength];
        foreach (var (led, color) in colors)
        {
            if (led is < 0 or >= PlaneSize || !RealLeds.Contains(led)) continue; // knob, unused and out-of-range LEDs are not keys
            data[led] = color.R;
            data[PlaneSize + led] = color.G;
            data[2 * PlaneSize + led] = color.B;
        }
        return data;
    }
}
