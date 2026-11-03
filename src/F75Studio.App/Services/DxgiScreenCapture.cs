using F75Studio.Lighting;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace F75Studio.App.Services;

/// <summary>
/// Captures the primary output with DXGI Desktop Duplication and downsamples it on the GPU
/// (mip chain) to roughly <see cref="TargetWidth"/> pixels wide. Not thread-safe: use from one thread.
/// </summary>
public sealed class DxgiScreenCapture : IScreenCapture
{
    public const int TargetWidth = 64;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipView;
    private ID3D11Texture2D? _staging;
    private int _mipLevel;
    private int _outWidth;
    private int _outHeight;
    private bool _disposed;

    public DxgiScreenCapture()
    {
        try
        {
            Initialize();
        }
        catch
        {
            ReleaseAll();
            throw;
        }
    }

    private void Initialize()
    {
        try
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out _device, out _context);
            if (hr.Failure || _device is null || _context is null)
                throw new ScreenCaptureException($"Could not create a Direct3D device (0x{hr.Code:X8}).");

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var outHr = adapter.EnumOutputs(0, out var output);
            if (outHr.Failure || output is null)
                throw new ScreenCaptureException("No display output is available for capture.");
            using (output)
            {
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                _duplication = output1.DuplicateOutput(_device);
            }

            var desc = _duplication.Description;
            if (desc.ModeDescription.Format != Format.B8G8R8A8_UNorm)
                throw new ScreenCaptureException("HDR or non-standard desktop format isn't supported yet.");

            var width = (int)desc.ModeDescription.Width;
            var height = (int)desc.ModeDescription.Height;
            if (width < 1 || height < 1)
                throw new ScreenCaptureException("The desktop reported an empty size.");

            _mipLevel = 0;
            while ((width >> _mipLevel) > TargetWidth) _mipLevel++;
            _outWidth = Math.Max(1, width >> _mipLevel);
            _outHeight = Math.Max(1, height >> _mipLevel);

            _mipTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = (uint)(_mipLevel + 1),
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });
            _mipView = _device.CreateShaderResourceView(_mipTexture);

            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_outWidth,
                Height = (uint)_outHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
        }
        catch (SharpGenException ex)
        {
            throw new ScreenCaptureException("Screen capture could not be initialized: " + ex.Message, ex);
        }
    }

    public CapturedFrame? TryCapture(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_duplication is null) throw new ScreenCaptureException("Screen capture is not available.");

        var frameAcquired = false;
        try
        {
            var hr = _duplication.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out _, out var resource);
            if (hr == Vortice.DXGI.ResultCode.WaitTimeout) return null;
            if (hr == Vortice.DXGI.ResultCode.AccessLost)
            {
                Recreate();
                return null;
            }
            if (hr.Failure)
                throw new ScreenCaptureException($"Acquiring a desktop frame failed (0x{hr.Code:X8}).");
            frameAcquired = true;

            using (resource)
            using (var desktop = resource.QueryInterface<ID3D11Texture2D>())
            {
                var ctx = _context!;
                ctx.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, desktop, 0);
                ctx.GenerateMips(_mipView!);
                ctx.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mipTexture!, (uint)_mipLevel);
            }

            _duplication.ReleaseFrame();
            frameAcquired = false;

            return ReadStaging();
        }
        catch (SharpGenException ex)
        {
            throw new ScreenCaptureException("Screen capture failed: " + ex.Message, ex);
        }
        finally
        {
            if (frameAcquired)
            {
                try { _duplication?.ReleaseFrame(); } catch (SharpGenException) { }
            }
        }
    }

    private CapturedFrame ReadStaging()
    {
        var ctx = _context!;
        var mapHr = ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped);
        if (mapHr.Failure)
            throw new ScreenCaptureException($"Reading the captured frame failed (0x{mapHr.Code:X8}).");
        try
        {
            var rowBytes = _outWidth * 4;
            var bytes = new byte[rowBytes * _outHeight];
            var pitch = (int)mapped.RowPitch;
            for (var y = 0; y < _outHeight; y++)
                System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + y * pitch, bytes, y * rowBytes, rowBytes);
            return new CapturedFrame(_outWidth, _outHeight, bytes);
        }
        finally
        {
            ctx.Unmap(_staging!, 0);
        }
    }

    private void Recreate()
    {
        ReleaseAll();
        try
        {
            Initialize();
        }
        catch (ScreenCaptureException)
        {
            ReleaseAll();
            throw;
        }
        catch (Exception ex)
        {
            ReleaseAll();
            throw new ScreenCaptureException("Screen capture could not be re-established: " + ex.Message, ex);
        }
    }

    private void ReleaseAll()
    {
        _staging?.Dispose(); _staging = null;
        _mipView?.Dispose(); _mipView = null;
        _mipTexture?.Dispose(); _mipTexture = null;
        _duplication?.Dispose(); _duplication = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseAll();
    }
}
