using HidSharp;

namespace F75Studio.Lighting.Aula;

/// <summary>One wired 258A:010C configuration collection that can be opened for feature reports.</summary>
internal interface IWiredBoard
{
    IFeatureChannel Open();
}

/// <summary>Enumeration of HID devices; a seam so device logic can be tested without hardware.</summary>
internal interface IHidProbe
{
    IReadOnlyList<IWiredBoard> FindWiredBoards();
    bool DonglePresent();
    bool BluetoothPresent();

    /// <summary>Calls <paramref name="onChanged"/> whenever the HID device list changes; dispose to unsubscribe.</summary>
    IDisposable SubscribeChanged(Action onChanged);
}

internal sealed class HidSharpProbe : IHidProbe
{
    private static readonly int[] BluetoothPids = [0xFA07, 0xFA08];
    private const uint ConfigUsage = 0xFF000001;

    public IReadOnlyList<IWiredBoard> FindWiredBoards() =>
        DeviceList.Local.GetHidDevices(AulaF75Device.WiredVid, AulaF75Device.WiredPid)
            .Where(IsConfigCollection)
            .Select(d => (IWiredBoard)new Board(d))
            .ToList();

    public bool DonglePresent() => DeviceList.Local.GetHidDevices(AulaF75Device.WirelessVid, AulaF75Device.DonglePid).Any();

    public bool BluetoothPresent() =>
        BluetoothPids.Any(pid => DeviceList.Local.GetHidDevices(AulaF75Device.WirelessVid, pid).Any());

    public IDisposable SubscribeChanged(Action onChanged)
    {
        void Handler(object? sender, DeviceListChangedEventArgs e) => onChanged();
        DeviceList.Local.Changed += Handler;
        return new Unsubscriber(() => DeviceList.Local.Changed -= Handler);
    }

    private static bool IsConfigCollection(HidDevice device)
    {
        try
        {
            if (device.GetMaxFeatureReportLength() != F75Protocol.FrameLength) return false;
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            return device.GetReportDescriptor().DeviceItems.Any(item => item.Usages.GetAllValues().Contains(ConfigUsage));
        }
        catch (Exception)
        {
            return true; // descriptor unavailable: the 520-byte feature report alone identifies the config collection
        }
    }

    private sealed class Board(HidDevice device) : IWiredBoard
    {
        public IFeatureChannel Open() => HidSharpFeatureChannel.Open(device);
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
