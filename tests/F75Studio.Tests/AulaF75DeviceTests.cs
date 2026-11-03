using F75Studio.Lighting;
using F75Studio.Lighting.Aula;

namespace F75Studio.Tests;

public class AulaF75DeviceTests
{
    [Theory]
    [InlineData(true, true, DeviceStatus.Dongle)]
    [InlineData(true, false, DeviceStatus.Dongle)]
    [InlineData(false, true, DeviceStatus.Bluetooth)]
    [InlineData(false, false, DeviceStatus.NotFound)]
    public void Classifies_non_cable_connections(bool dongle, bool bluetooth, DeviceStatus expected)
    {
        var (status, text) = AulaF75Device.ClassifyWithoutCable(dongle, bluetooth);
        Assert.Equal(expected, status);
        Assert.Contains("USB cable", text);
    }

    // Hardware-dependent: runs against whatever is plugged in. It must never throw and never write.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Detection_produces_a_status_without_throwing()
    {
        using var device = new AulaF75Device();
        await device.RefreshAsync();
        Assert.False(string.IsNullOrWhiteSpace(device.StatusText));
    }
}
