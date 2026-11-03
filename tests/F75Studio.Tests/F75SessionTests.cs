using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class F75SessionTests
{
    private readonly FakeFeatureChannel _channel = new();
    private readonly List<int> _delays = [];

    private F75Session Session() => new(_channel, (ms, _) => { _delays.Add(ms); return Task.CompletedTask; });

    private static byte Cmd(byte[] frame) => frame[1];

    [Fact]
    public async Task Identity_is_read_with_the_documented_request()
    {
        var id = await Session().ReadIdentityAsync(CancellationToken.None);

        Assert.Equal(new byte[] { 3, 0, 0, 0, 0, 0xCD }, id);
        Assert.Equal("0682010001000600", Convert.ToHexString(_channel.Sent[0], 0, 8));
    }

    [Fact]
    public async Task Static_colour_writes_colour_table_then_settings()
    {
        await Session().ApplyAsync(new LightingState { EffectId = 1, Color = new Rgb(255, 0, 0) }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x8A, 0x0A, 0x84, 0x04 }, _channel.Sent.Select(Cmd).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0 }, _channel.ColorTable[21..24]);
        Assert.Equal(1, _channel.SettingsPage[0x0A]);
        Assert.Contains(120, _delays);
    }

    [Fact]
    public async Task Colorful_and_colourless_effects_skip_the_colour_table()
    {
        await Session().ApplyAsync(new LightingState { EffectId = 2, Colorful = true }, CancellationToken.None);
        await Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None);

        Assert.DoesNotContain(_channel.Sent, f => Cmd(f) is 0x8A or 0x0A);
        Assert.Equal(3, _channel.SettingsPage[0x0A]);
    }

    [Fact]
    public async Task Per_key_writes_colours_first_then_selects_effect_21()
    {
        var state = new LightingState
        {
            EffectId = EffectIds.PerKey,
            KeyColors = new Dictionary<int, Rgb> { [14] = new(255, 0, 0) },
        };

        await Session().ApplyAsync(state, CancellationToken.None);

        Assert.Equal(new byte[] { 0x06, 0x84, 0x04 }, _channel.Sent.Select(Cmd).ToArray());
        Assert.Equal(255, _channel.PerKey[14]);
        Assert.Equal(0x15, _channel.SettingsPage[0x0A]);
        Assert.Equal(1, _channel.SettingsPage[0x09]);
        Assert.Equal(30, _delays[0]);
    }

    [Fact]
    public async Task Settings_page_without_marker_is_retried_then_refused_without_writing()
    {
        var broken = F75ProtocolTests.FixedOnPage();
        broken[126] = 0;
        _channel.SettingsPage = broken;

        await Assert.ThrowsAsync<F75ProtocolException>(() => Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None));

        Assert.Equal(5, _channel.Sent.Count(f => Cmd(f) == 0x84));
        Assert.Empty(_channel.Writes);
    }

    [Fact]
    public async Task Transfer_failures_are_retried()
    {
        _channel.FailNextSets = 2;

        await Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None);

        Assert.Equal(3, _channel.SettingsPage[0x0A]);
        Assert.Equal(2, _delays.Count(d => d == 70));
    }

    [Fact]
    public async Task Every_transfer_waits_20ms_first()
    {
        await Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None);
        Assert.True(_delays.Count(d => d == 20) >= _channel.Sent.Count);
    }
}
