using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class ScreenSyncModelTests
{
    [Fact]
    public void Screen_sync_is_a_live_app_side_effect()
    {
        var def = F75Effects.Get(EffectIds.ScreenSync);
        Assert.True(def.IsLive);
        Assert.True(def.HasBrightness);
        Assert.False(def.HasSpeed);
        Assert.False(def.HasColor);
        Assert.Equal(EffectIds.ScreenSyncAccent, F75Effects.All[^1].Id);
        int[] live = [EffectIds.ScreenSync, EffectIds.ScreenSyncBlur, EffectIds.ScreenSyncAccent];
        Assert.All(F75Effects.All.Where(e => !live.Contains(e.Id)), e => Assert.False(e.IsLive));
    }

    [Fact]
    public void Screen_sync_accent_is_a_live_app_side_effect()
    {
        var def = F75Effects.Get(EffectIds.ScreenSyncAccent);
        Assert.Equal(102, def.Id);
        Assert.Equal("Screen sync (accent)", def.Name);
        Assert.True(def.IsLive);
        Assert.True(def.HasBrightness);
        Assert.False(def.HasSpeed);
        Assert.False(def.HasColor);

        var state = new LightingState { EffectId = EffectIds.ScreenSyncAccent, BaseEffectId = 2 };
        Assert.Equal(2, state.StoredState().EffectId);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            F75Protocol.PatchSettings(F75ProtocolTests.FixedOnPage(), new LightingState { EffectId = EffectIds.ScreenSyncAccent }));
    }

    [Fact]
    public void Screen_sync_blur_is_a_live_app_side_effect()
    {
        var def = F75Effects.Get(EffectIds.ScreenSyncBlur);
        Assert.Equal(101, def.Id);
        Assert.Equal("Screen sync (blur)", def.Name);
        Assert.True(def.IsLive);
        Assert.True(def.HasBrightness);
        Assert.False(def.HasSpeed);
        Assert.False(def.HasColor);

        var live = new LightingState { EffectId = EffectIds.ScreenSyncBlur, BaseEffectId = 2, Brightness = 4 };
        Assert.Equal(2, live.StoredState().EffectId);
        Assert.Equal(4, live.StoredState().Brightness);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            F75Protocol.PatchSettings(F75ProtocolTests.FixedOnPage(), new LightingState { EffectId = EffectIds.ScreenSyncBlur }));
    }

    [Fact]
    public void Stored_state_uses_the_base_effect_for_live_effects()
    {
        var live = new LightingState { EffectId = EffectIds.ScreenSync, BaseEffectId = 2, Brightness = 4 };
        Assert.Equal(2, live.StoredState().EffectId);
        Assert.Equal(4, live.StoredState().Brightness);

        var bogusBase = live with { BaseEffectId = EffectIds.ScreenSync };
        Assert.Equal(EffectIds.FixedOn, bogusBase.StoredState().EffectId);

        var normal = new LightingState { EffectId = 3 };
        Assert.Same(normal, normal.StoredState());
    }

    [Fact]
    public void Live_frame_is_interleaved_by_led_index_for_real_keys_only()
    {
        var frame = F75Protocol.BuildLiveFrame(new Dictionary<int, Rgb>
        {
            [0] = new(1, 2, 3),
            [89] = new(4, 5, 6),
            [84] = new(9, 9, 9),   // knob: no LED
            [200] = new(9, 9, 9),  // out of range
        });

        Assert.Equal(378, frame.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame[0..3]);
        Assert.Equal(new byte[] { 4, 5, 6 }, frame[267..270]);
        Assert.Equal(0, frame[252]);
        Assert.Equal(21, frame.Sum(b => b));
    }

    [Fact]
    public void Live_frame_header_and_allowlist()
    {
        var frame = F75Protocol.Frame(F75Protocol.CmdLive, 0, 378, new byte[378]);
        Assert.Equal("0608000001007A01", Convert.ToHexString(frame, 0, 8));
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame(F75Protocol.CmdLive, 0, 384, new byte[384]));
        Assert.Throws<InvalidOperationException>(() => F75Protocol.Frame(F75Protocol.CmdLive, 0, 378, new byte[100]));
        Assert.Throws<InvalidOperationException>(() => F75Protocol.ReadRequest(F75Protocol.CmdLive, 0, 378)); // 0x88 is never allowed
    }

    [Fact]
    public void Live_effect_never_reaches_the_settings_page()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            F75Protocol.PatchSettings(F75ProtocolTests.FixedOnPage(), new LightingState { EffectId = EffectIds.ScreenSync }));
    }

    [Fact]
    public async Task Session_refuses_live_effect_before_any_io()
    {
        var channel = new FakeFeatureChannel();
        var session = new F75Session(channel, (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.ApplyAsync(new LightingState { EffectId = EffectIds.ScreenSync }, CancellationToken.None));
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task Session_sends_one_live_frame_per_call()
    {
        var channel = new FakeFeatureChannel();
        var delays = new List<int>();
        var session = new F75Session(channel, (ms, _) => { delays.Add(ms); return Task.CompletedTask; });
        var payload = F75Protocol.BuildLiveFrame(new Dictionary<int, Rgb> { [0] = new(255, 0, 0) });

        await session.SendLiveFrameAsync(payload, CancellationToken.None);
        await session.SendLiveFrameAsync(payload, CancellationToken.None);

        Assert.Equal(2, channel.Sent.Count);
        Assert.All(channel.Sent, f => Assert.Equal(0x08, f[1]));
        Assert.Equal(255, channel.Sent[0][8]);
        Assert.DoesNotContain(delays, d => d > 7);
    }

    [Fact]
    public async Task First_live_frame_needs_no_delay_but_a_back_to_back_one_waits()
    {
        var channel = new FakeFeatureChannel();
        var delays = new List<int>();
        long now = 1000;
        var session = new F75Session(channel, (ms, _) => { delays.Add(ms); return Task.CompletedTask; }, () => now);
        var payload = F75Protocol.BuildLiveFrame(new Dictionary<int, Rgb> { [0] = new(255, 0, 0) });

        await session.SendLiveFrameAsync(payload, CancellationToken.None);
        Assert.Empty(delays);

        now += 3; // 3 ms later: only the remaining 4 ms are waited
        await session.SendLiveFrameAsync(payload, CancellationToken.None);
        Assert.Equal(new[] { 4 }, delays);

        now += 20; // well past 7 ms: no delay
        await session.SendLiveFrameAsync(payload, CancellationToken.None);
        Assert.Equal(new[] { 4 }, delays);
    }
}
