using System.Diagnostics;
using HidSharp;

namespace F75Studio.Lighting.Aula;

public sealed class AulaF75Device : IKeyboardDevice
{
    public const int WiredVid = 0x258A;
    public const int WiredPid = 0x010C;
    public const int WirelessVid = 0x3554;
    public const int DonglePid = 0xFA09;
    private static readonly int[] BluetoothPids = [0xFA07, 0xFA08];
    private const uint ConfigUsage = 0xFF000001;
    private const int TransactionGapMs = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastTransactionEnd; // Stopwatch timestamp; 0 = no transaction yet
    private DeviceStatus _status = DeviceStatus.NotFound;
    private string _statusText = "Looking for the keyboard…";

    public AulaF75Device()
    {
        DeviceList.Local.Changed += OnDevicesChanged;
        _ = RefreshAsync();
    }

    public DeviceStatus Status => _status;
    public string StatusText => _statusText;
    public event EventHandler? StatusChanged;

    public static (DeviceStatus Status, string Text) ClassifyWithoutCable(bool donglePresent, bool bluetoothPresent) =>
        donglePresent
            ? (DeviceStatus.Dongle, "Connected through the 2.4 GHz receiver. Lighting over the receiver isn't supported yet — connect the USB cable.")
            : bluetoothPresent
                ? (DeviceStatus.Bluetooth, "Connected by Bluetooth. Lighting can only be changed over the USB cable.")
                : (DeviceStatus.NotFound, "Keyboard not found. Connect the F75 with its USB cable.");

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var (status, text) = await DetectAsync();
            SetStatus(status, text);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyAsync(LightingState state, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_status != DeviceStatus.Connected) throw new KeyboardUnavailableException(_statusText);
            var hid = FindWiredConfigDevice() ?? throw new KeyboardUnavailableException("The keyboard was disconnected.");
            try
            {
                await WaitForTransactionGapAsync(ct);
                try
                {
                    using var channel = HidSharpFeatureChannel.Open(hid);
                    var session = new F75Session(channel);
                    if (!F75Protocol.IsKnownIdentity(await session.ReadIdentityAsync(ct)))
                        throw new KeyboardUnavailableException("The keyboard no longer identifies as an AULA F75; nothing was sent.");
                    await session.ApplyAsync(state, ct);
                }
                finally
                {
                    _lastTransactionEnd = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                throw new KeyboardUnavailableException($"Couldn't send lighting to the keyboard: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => DeviceList.Local.Changed -= OnDevicesChanged;

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => _ = RefreshAsync();

    // Protocol doc §3.4 step 4: the driver leaves 500 ms between one transaction and the next.
    private async Task WaitForTransactionGapAsync(CancellationToken ct)
    {
        if (_lastTransactionEnd == 0) return;
        var remaining = TransactionGapMs - (int)Stopwatch.GetElapsedTime(_lastTransactionEnd).TotalMilliseconds;
        if (remaining > 0) await Task.Delay(remaining, ct);
    }

    private async Task<(DeviceStatus, string)> DetectAsync()
    {
        var wired = FindWiredConfigDevice();
        if (wired is not null)
        {
            try
            {
                await WaitForTransactionGapAsync(CancellationToken.None);
                try
                {
                    using var channel = HidSharpFeatureChannel.Open(wired);
                    var identity = await new F75Session(channel).ReadIdentityAsync(CancellationToken.None);
                    return F75Protocol.IsKnownIdentity(identity)
                        ? (DeviceStatus.Connected, "Connected by USB cable")
                        : (DeviceStatus.Unsupported,
                            $"This keyboard identified itself as {Convert.ToHexString(identity)}; F75 Studio only controls the AULA F75 and won't send it lighting commands.");
                }
                finally
                {
                    _lastTransactionEnd = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or F75ProtocolException)
            {
                return (DeviceStatus.Error, $"Found the F75 but couldn't talk to it ({ex.Message}). Close the AULA F75 app and try again.");
            }
        }

        var list = DeviceList.Local;
        var dongle = list.GetHidDevices(WirelessVid, DonglePid).Any();
        var bluetooth = BluetoothPids.Any(pid => list.GetHidDevices(WirelessVid, pid).Any());
        return ClassifyWithoutCable(dongle, bluetooth);
    }

    private static HidDevice? FindWiredConfigDevice() =>
        DeviceList.Local.GetHidDevices(WiredVid, WiredPid).FirstOrDefault(IsConfigCollection);

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

    private void SetStatus(DeviceStatus status, string text)
    {
        if (status == _status && text == _statusText) return;
        _status = status;
        _statusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
