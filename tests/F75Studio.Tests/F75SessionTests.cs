using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class F75SessionTests
{
    private readonly FakeFeatureChannel _channel = new();
    private readonly List<int> _delays = [];

    private F75Session Session() => new(_channel, (ms, _) =>
    {
        _delays.Add(ms);
        _channel.Events.Add($"d:{ms}");
        return Task.CompletedTask;
    });

    private static byte Cmd(byte[] frame) => frame[1];

    private static byte[] BrokenPage()
    {
        var broken = F75ProtocolTests.FixedOnPage();
        broken[126] = 0;
        return broken;
    }

    [Fact]
    public async Task Identity_is_read_with_the_documented_request()
    {
        var id = await Session().ReadIdentityAsync(CancellationToken.None);

        Assert.Equal(new byte[] { 3, 0, 0, 0, 0, 0xCD }, id);
        Assert.Equal("0682010001000600", Convert.ToHexString(_channel.Sent[0], 0, 8));
    }

    [Fact]
    public async Task Static_colour_validates_settings_then_writes_colour_table_then_settings()
    {
        await Session().ApplyAsync(new LightingState { EffectId = 1, Color = new Rgb(255, 0, 0) }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x84, 0x8A, 0x0A, 0x84, 0x04 }, _channel.Sent.Select(Cmd).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0 }, _channel.ColorTable[21..24]);
        Assert.Equal(1, _channel.SettingsPage[0x0A]);
        // 30 + read(20,20) | read table(20,20) | 120 | write(20) | 30 + read(20,20) | 100 | write(20)
        Assert.Equal(new[] { 30, 20, 20, 20, 20, 120, 20, 30, 20, 20, 100, 20 }, _delays);
    }

    [Fact]
    public async Task Colourless_effect_skips_the_colour_table()
    {
        await Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x84, 0x04 }, _channel.Sent.Select(Cmd).ToArray());
        Assert.Equal(3, _channel.SettingsPage[0x0A]);
        // 30 + read(20,20) | 60 | write(20)
        Assert.Equal(new[] { 30, 20, 20, 60, 20 }, _delays);
    }

    [Fact]
    public async Task Colorful_effect_skips_the_colour_table()
    {
        await Session().ApplyAsync(new LightingState { EffectId = 2, Colorful = true }, CancellationToken.None);

        Assert.Equal(new byte[] { 0x84, 0x04 }, _channel.Sent.Select(Cmd).ToArray());
        Assert.Equal(2, _channel.SettingsPage[0x0A]);
        Assert.Equal(new[] { 30, 20, 20, 60, 20 }, _delays);
    }

    [Fact]
    public async Task Per_key_validates_settings_then_writes_colours_then_selects_effect_21()
    {
        var state = new LightingState
        {
            EffectId = EffectIds.PerKey,
            KeyColors = new Dictionary<int, Rgb> { [14] = new(255, 0, 0) },
        };

        await Session().ApplyAsync(state, CancellationToken.None);

        Assert.Equal(new byte[] { 0x84, 0x06, 0x84, 0x04 }, _channel.Sent.Select(Cmd).ToArray());
        Assert.Equal(255, _channel.PerKey[14]);
        Assert.Equal(0x15, _channel.SettingsPage[0x0A]);
        Assert.Equal(1, _channel.SettingsPage[0x09]);
        // 30 + read(20,20) | 30 | write(20) | 30 + read(20,20) | 100 | write(20)
        Assert.Equal(new[] { 30, 20, 20, 30, 20, 30, 20, 20, 100, 20 }, _delays);
    }

    [Fact]
    public async Task Settings_page_without_marker_is_retried_then_refused_without_writing()
    {
        _channel.SettingsPage = BrokenPage();

        var ex = await Assert.ThrowsAsync<F75ProtocolException>(() => Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None));

        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Equal(5, _channel.Sent.Count(f => Cmd(f) == 0x84));
        Assert.Empty(_channel.Writes);
    }

    [Fact]
    public async Task Broken_settings_page_blocks_the_colour_write_too()
    {
        _channel.SettingsPage = BrokenPage();

        var ex = await Assert.ThrowsAsync<F75ProtocolException>(() =>
            Session().ApplyAsync(new LightingState { EffectId = 1, Color = new Rgb(1, 2, 3) }, CancellationToken.None));

        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Empty(_channel.Writes);
    }

    [Fact]
    public async Task Settings_page_breaking_after_the_colour_write_reports_a_partial_apply()
    {
        _channel.OnSettingsRead = (n, page) => n >= 2 ? BrokenPage() : page;

        var ex = await Assert.ThrowsAsync<F75ProtocolException>(() =>
            Session().ApplyAsync(new LightingState { EffectId = 1, Color = new Rgb(9, 8, 7) }, CancellationToken.None));

        Assert.DoesNotContain("Nothing was changed", ex.Message);
        Assert.Contains("after the colours were written", ex.Message);
        Assert.Equal(new byte[] { 0x0A }, _channel.Writes.Select(Cmd).ToArray());
        Assert.Equal(new byte[] { 9, 8, 7 }, _channel.ColorTable[21..24]);
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
    public async Task A_write_that_fails_twice_then_succeeds_lands_once()
    {
        _channel.FailNextWriteSets = 2;

        await Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None);

        Assert.Single(_channel.Writes);
        Assert.Equal(3, _channel.SettingsPage[0x0A]);
        Assert.Equal(2, _delays.Count(d => d == 70));
    }

    [Fact]
    public async Task Four_consecutive_write_failures_throw_and_write_nothing()
    {
        _channel.FailNextWriteSets = 4;

        await Assert.ThrowsAsync<IOException>(() => Session().ApplyAsync(new LightingState { EffectId = 3 }, CancellationToken.None));

        Assert.Empty(_channel.Writes);
        Assert.Equal(3, _delays.Count(d => d == 70));
    }

    [Fact]
    public async Task Every_transfer_waits_20ms_first()
    {
        await Session().ApplyAsync(new LightingState { EffectId = 1, Color = new Rgb(1, 2, 3) }, CancellationToken.None);

        var events = _channel.Events;
        var sets = 0;
        for (var i = 0; i < events.Count; i++)
        {
            if (!events[i].StartsWith("set:")) continue;
            sets++;
            Assert.True(i > 0 && events[i - 1] == "d:20", $"{events[i]} at {i} was not preceded by a 20 ms delay");
        }
        Assert.Equal(_channel.Sent.Count, sets);
    }

    [Fact]
    public async Task Cancellation_after_the_first_write_still_completes_the_settings_write()
    {
        using var cts = new CancellationTokenSource();
        var session = new F75Session(_channel, (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (_channel.Writes.Any()) cts.Cancel();
            return Task.CompletedTask;
        });

        await session.ApplyAsync(new LightingState { EffectId = 1, Color = new Rgb(5, 6, 7) }, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(new byte[] { 0x0A, 0x04 }, _channel.Writes.Select(Cmd).ToArray());
        Assert.Equal(1, _channel.SettingsPage[0x0A]);
    }

    [Fact]
    public async Task Cancellation_before_any_write_aborts_without_writing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var session = new F75Session(_channel, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ApplyAsync(new LightingState { EffectId = 3 }, cts.Token));

        Assert.Empty(_channel.Writes);
    }
}
