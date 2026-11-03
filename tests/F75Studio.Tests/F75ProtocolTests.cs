using F75Studio.Lighting;
using F75Studio.Lighting.Aula;

namespace F75Studio.Tests;

public class F75ProtocolTests
{
    public static byte[] FixedOnPage() => Convert.FromHexString(
        "00030301000004040700012001000000" +
        "00000200020100ff0a00000101000301" +
        "00000000000000000000000000000000" +
        "0000000000000000ffff094009470947" +
        "09470947094709470947094709470947" +
        "09470947094709470947094709470947" +
        "09370747074707440744074407440744" +
        "07440744040904040404040404045aa5");

    public static byte[] ColorTable()
    {
        var t = new byte[512];
        t[506] = 0x5A;
        t[507] = 0xA5;
        return t;
    }

    private static string Header(byte[] frame) => Convert.ToHexString(frame, 0, 8);

    [Fact]
    public void Test_vector_is_a_valid_page()
    {
        var page = FixedOnPage();
        Assert.Equal(128, page.Length);
        Assert.True(F75Protocol.HasMarker(page, F75Protocol.SettingsMarkerOffset));
        Assert.Equal(0x01, page[0x0A]);
    }

    [Fact]
    public void Frames_have_the_documented_headers_and_full_length()
    {
        Assert.Equal("0682010001000600", Header(F75Protocol.ReadRequest(F75Protocol.CmdIdentity, 1, 6)));
        Assert.Equal("0684000001008000", Header(F75Protocol.ReadRequest(F75Protocol.CmdSettings, 0, 128)));
        Assert.Equal("0604000001008000", Header(F75Protocol.Frame(F75Protocol.CmdSettings, 0, 128, FixedOnPage())));
        Assert.Equal("060A000001000002", Header(F75Protocol.Frame(F75Protocol.CmdColorTable, 0, 512, ColorTable())));
        Assert.Equal("0606000001007A01", Header(F75Protocol.Frame(F75Protocol.CmdPerKey, 0, 378, new byte[378])));
        Assert.Equal(520, F75Protocol.Frame(F75Protocol.CmdSettings, 0, 128, FixedOnPage()).Length);
    }

    [Theory]
    [InlineData(0x05, 0, 6)]      // macro / bootloader family
    [InlineData(0x11, 0, 1)]      // factory reset
    [InlineData(0x03, 0, 512)]    // key matrix
    [InlineData(0x88, 0, 378)]    // memory dump read
    [InlineData(0x04, 0, 512)]    // allowed command, wrong length
    [InlineData(0x04, 1, 128)]    // allowed command, wrong param
    public void Commands_outside_the_allowlist_are_blocked(byte cmd, byte param, int length) =>
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame(cmd, param, length));

    [Fact]
    public void Markerless_settings_write_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame(F75Protocol.CmdSettings, 0, 128, new byte[128]));
    }

    [Fact]
    public void Markerless_colour_table_write_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame(F75Protocol.CmdColorTable, 0, 512, new byte[512]));
    }

    [Fact]
    public void Short_write_payload_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame(F75Protocol.CmdPerKey, 0, 378, new byte[100]));
    }

    [Fact]
    public void Oversized_write_payload_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame(F75Protocol.CmdPerKey, 0, 378, new byte[379]));
    }

    [Fact]
    public void Read_request_with_a_payload_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame((byte)(F75Protocol.CmdSettings | F75Protocol.ReadFlag), 0, 128, new byte[128]));
    }

    [Fact]
    public void Identity_accepts_only_known_f75_models()
    {
        Assert.True(F75Protocol.IsKnownIdentity([3, 0, 0, 0, 0, 0xCD]));
        Assert.True(F75Protocol.IsKnownIdentity([3, 0, 0, 0, 0, 0xE1]));
        Assert.True(F75Protocol.IsKnownIdentity([3, 0, 0, 0, 1, 0x91]));
        Assert.False(F75Protocol.IsKnownIdentity([3, 0, 0, 0, 0, 0x8F])); // F87
        Assert.False(F75Protocol.IsKnownIdentity([0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void Reply_payload_requires_the_echoed_header()
    {
        var request = F75Protocol.ReadRequest(F75Protocol.CmdSettings, 0, 128);
        var reply = new byte[520];
        request.AsSpan(0, 8).CopyTo(reply);
        reply[8] = 0xAB;

        Assert.Equal(0xAB, F75Protocol.ReplyPayload(reply, request, 128)[0]);
        Assert.Throws<F75ProtocolException>(() =>
            F75Protocol.ReplyPayload(reply, F75Protocol.ReadRequest(F75Protocol.CmdColorTable, 0, 512), 512));
    }

    [Fact]
    public void Reply_with_different_length_bytes_is_refused()
    {
        var request = F75Protocol.ReadRequest(F75Protocol.CmdSettings, 0, 128);
        var reply = new byte[520];
        request.AsSpan(0, 8).CopyTo(reply);
        reply[6] = 0x00;
        reply[7] = 0x01; // 256 bytes instead of 128

        Assert.Throws<F75ProtocolException>(() => F75Protocol.ReplyPayload(reply, request, 128));
    }

    [Fact]
    public void Breathing_patches_only_its_bytes()
    {
        var original = FixedOnPage();
        var state = new LightingState { EffectId = 2, Brightness = 5, Speed = 1, Colorful = false };

        var page = F75Protocol.PatchSettings(original, state);

        Assert.Equal(2, page[0x0A]);
        Assert.Equal(0, page[0x09]);
        Assert.Equal(5, page[0x3C]);
        Assert.Equal(0x10, page[0x3D]);
        var changed = Enumerable.Range(0, 128).Where(i => page[i] != original[i]).ToArray();
        Assert.Equal(new[] { 0x0A, 0x3C, 0x3D }, changed);
    }

    [Fact]
    public void Colorful_sets_nibble_seven()
    {
        var page = F75Protocol.PatchSettings(FixedOnPage(), new LightingState { EffectId = 4, Speed = 3, Colorful = true });
        Assert.Equal(0x37, page[0x3B + 2 * 3]);
    }

    [Fact]
    public void Effect_without_colour_keeps_the_stored_nibble()
    {
        var page = F75Protocol.PatchSettings(FixedOnPage(), new LightingState { EffectId = 3, Speed = 2 });
        Assert.Equal(0x27, page[0x3F]); // slot 2 was 09 47 → speed 2, nibble 7 kept
    }

    [Fact]
    public void Effect_17_writes_slot_16_at_0x5A_and_0x5B()
    {
        var page = F75Protocol.PatchSettings(FixedOnPage(), new LightingState { EffectId = 17, Brightness = 4, Speed = 2 });
        Assert.Equal(4, page[0x5A]);
        Assert.Equal(0x27, page[0x5B]); // speed 2, slot nibble 7 kept (Blossoming has no colour)
    }

    [Fact]
    public void Static_keeps_the_stored_speed()
    {
        var page = F75Protocol.PatchSettings(FixedOnPage(), new LightingState { EffectId = 1, Brightness = 3, Speed = 0 });
        Assert.Equal(3, page[0x3A]);
        Assert.Equal(0x40, page[0x3B]); // 09 40 → speed 4 kept, colour slot 0
    }

    [Fact]
    public void Per_key_sets_mode_flag_group_and_slot_18_brightness()
    {
        var original = FixedOnPage();
        var page = F75Protocol.PatchSettings(original, new LightingState { EffectId = EffectIds.PerKey, Brightness = 6 });

        Assert.Equal(0x15, page[0x0A]);
        Assert.Equal(1, page[0x09]);
        Assert.Equal(0x20, page[0x0B]);
        Assert.Equal(6, page[0x5E]);
        Assert.Equal(original[0x5F], page[0x5F]);
    }

    [Fact]
    public void Per_key_patch_changes_exactly_the_mode_bytes_and_slot_18_brightness()
    {
        var original = FixedOnPage();
        var page = F75Protocol.PatchSettings(original, new LightingState { EffectId = EffectIds.PerKey, Brightness = 6 });
        var changed = Enumerable.Range(0, 128).Where(i => page[i] != original[i]).ToArray();
        Assert.Equal(new[] { 0x09, 0x0A, 0x5E }, changed);
    }

    [Fact]
    public void Off_changes_only_the_effect_and_mode_bytes()
    {
        var original = FixedOnPage();
        var page = F75Protocol.PatchSettings(original, new LightingState { EffectId = EffectIds.Off });
        Assert.Equal(new[] { 0x0A }, Enumerable.Range(0, 128).Where(i => page[i] != original[i]).ToArray());
    }

    [Fact]
    public void Patching_refuses_pages_without_marker_and_unknown_effects()
    {
        var broken = FixedOnPage();
        broken[127] = 0;
        Assert.Throws<F75ProtocolException>(() => F75Protocol.PatchSettings(broken, new LightingState()));
        Assert.Throws<ArgumentOutOfRangeException>(() => F75Protocol.PatchSettings(FixedOnPage(), new LightingState { EffectId = 9 }));
    }

    [Fact]
    public void Colour_table_row_is_effect_id_slot_zero()
    {
        var table = F75Protocol.PatchColorTable(ColorTable(), 2, new Rgb(1, 2, 3));
        Assert.Equal(new byte[] { 1, 2, 3 }, table[42..45]);
        Assert.Equal(0x5A, table[506]);
        Assert.Throws<F75ProtocolException>(() => F75Protocol.PatchColorTable(new byte[512], 2, Rgb.White));
    }

    [Theory]
    [InlineData(3)]   // Rainbow: no colour
    [InlineData(9)]   // hidden by the official driver, not in the catalogue
    [InlineData(0)]   // Off
    [InlineData(21)]  // per-key has no colour row
    public void Colour_table_refuses_effects_without_a_colour_row(int effectId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => F75Protocol.PatchColorTable(ColorTable(), effectId, Rgb.White));
    }

    [Fact]
    public void Per_key_is_planar_by_led_index()
    {
        var data = F75Protocol.BuildPerKey(new Dictionary<int, Rgb>
        {
            [0] = new(255, 0, 0),
            [9] = new(0, 255, 0),
            [89] = new(0, 0, 255),
            [200] = Rgb.White, // out of range: ignored
            [84] = Rgb.White, // volume knob LED: not a key, ignored
        });

        Assert.Equal(378, data.Length);
        Assert.Equal(255, data[0]);
        Assert.Equal(255, data[126 + 9]);
        Assert.Equal(255, data[252 + 89]);
        Assert.Equal(3 * 255, data.Sum(b => b));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    public void Marker_check_refuses_negative_offsets(int offset)
    {
        Assert.False(F75Protocol.HasMarker(new byte[] { 0x5A, 0xA5, 0x5A, 0xA5 }, offset));
    }
}
