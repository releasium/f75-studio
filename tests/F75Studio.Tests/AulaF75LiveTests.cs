using F75Studio.Lighting;
using F75Studio.Lighting.Aula;
using F75Studio.Tests.TestSupport;

namespace F75Studio.Tests;

public class AulaF75LiveTests
{
    private static AulaF75Device CreateConnectedDevice(out FakeFeatureChannel channel)
    {
        channel = new FakeFeatureChannel { Identity = [3, 0, 0, 0, 0, 0xCD] };
        var probe = new FakeProbe { Boards = { new FakeChannelBoard(channel) } };
        var device = new AulaF75Device(probe, 0);
        Assert.True(SpinWait.SpinUntil(() => !device.IsRefreshing, 5000));
        Assert.Equal(DeviceStatus.Connected, device.Status);
        return device;
    }

    private static AulaF75Device CreateBluetoothDevice()
    {
        var device = new AulaF75Device(new FakeProbe { Bluetooth = true }, 0);
        Assert.True(SpinWait.SpinUntil(() => !device.IsRefreshing, 5000));
        Assert.Equal(DeviceStatus.Bluetooth, device.Status);
        return device;
    }

    [Fact]
    public async Task Live_session_checks_identity_then_streams_0x08_frames_only()
    {
        using var device = CreateConnectedDevice(out var channel);
        channel.Sent.Clear();

        await device.StartLiveAsync();
        await device.SendLiveAsync(new Dictionary<int, Rgb> { [0] = new(255, 0, 0) });
        await device.SendLiveAsync(new Dictionary<int, Rgb> { [0] = new(0, 255, 0) });
        await device.StopLiveAsync();

        Assert.Equal(new byte[] { 0x82, 0x08, 0x08 }, channel.Sent.Select(f => f[1]).ToArray());
        Assert.Equal(0, channel.Sent[1][9]);
        Assert.Equal(255, channel.Sent[2][9]);
        Assert.False(device.IsLive);
    }

    [Fact]
    public async Task Live_start_refused_when_not_on_the_cable()
    {
        using var device = CreateBluetoothDevice();
        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.StartLiveAsync());
        Assert.False(device.IsLive);
    }

    [Fact]
    public async Task Live_start_refused_for_unknown_identity()
    {
        using var device = CreateConnectedDevice(out var channel);
        channel.Identity = [3, 0, 0, 0, 0, 0x8F];
        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.StartLiveAsync());
        Assert.DoesNotContain(channel.Sent, f => f[1] == 0x08);
    }

    [Fact]
    public async Task Sending_without_a_session_is_refused()
    {
        using var device = CreateConnectedDevice(out _);
        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.SendLiveAsync(new Dictionary<int, Rgb>()));
    }

    [Fact]
    public async Task Stored_apply_ends_the_live_session_first()
    {
        using var device = CreateConnectedDevice(out _);
        await device.StartLiveAsync();
        await device.ApplyAsync(new LightingState { EffectId = 3, Brightness = 5 });
        Assert.False(device.IsLive);
    }

    [Fact]
    public async Task Transfer_failure_ends_the_session_with_a_clear_error()
    {
        using var device = CreateConnectedDevice(out var channel);
        await device.StartLiveAsync();
        channel.FailNextWriteSets = 2; // initial try + the single retry

        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => device.SendLiveAsync(new Dictionary<int, Rgb> { [0] = Rgb.White }));
        Assert.False(device.IsLive);
    }

    [Fact]
    public async Task Fake_device_records_live_frames()
    {
        var fake = new FakeKeyboardDevice();
        await fake.StartLiveAsync();
        await fake.SendLiveAsync(new Dictionary<int, Rgb> { [0] = Rgb.White });
        await fake.StopLiveAsync();
        Assert.Equal(1, fake.LiveStarts);
        Assert.Single(fake.LiveFrames);
        Assert.Equal(1, fake.LiveStops);

        fake.SetStatus(DeviceStatus.Bluetooth, "Bluetooth");
        await Assert.ThrowsAsync<KeyboardUnavailableException>(() => fake.StartLiveAsync());
    }
}
