using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Vortice.MediaFoundation;
using WallpaperProtocol;
using WallpaperProtocol.D3D11;

namespace WallpaperDaemon.Sources;

/// <summary>
/// The daemon's decode device: D3D11 on the host's GPU (shared textures can't cross adapters), with
/// video support and multithread protection (Media Foundation uses it from its own threads), wrapped
/// in a DXGI device manager for the source reader.
/// </summary>
internal sealed class VideoGpu : IDisposable
{
    public ID3D11Device Device { get; }
    public IMFDXGIDeviceManager Manager { get; }

    private VideoGpu(ID3D11Device device, IMFDXGIDeviceManager manager) { Device = device; Manager = manager; }

    public static VideoGpu Create(long adapterLuid)
    {
        using var adapter = D3D11FrameOutput.FindAdapter(adapterLuid);
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext ctx).CheckError();
        ctx.Dispose();
        using (var mt = device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();
        Log.Write($"GPU video decode device ready ({adapter.Description1.Description})");
        return new VideoGpu(device, manager);
    }

    public void Dispose() { Manager.Dispose(); Device.Dispose(); }
}

/// <summary>Where GPU-decoded frames go: the daemon's own GPU session, filled with texture copies.</summary>
internal sealed class GpuSink : IDisposable
{
    private readonly D3D11FrameOutput _output;
    private readonly ID3D11DeviceContext _ctx;
    private readonly SessionMap _map;

    public GpuSink(SessionMap map, GpuResources gpu, ID3D11Device device)
    {
        _map = map;
        _output = new D3D11FrameOutput(map, gpu, device);
        _ctx = device.ImmediateContext; // cached by Vortice and owned by the device: never dispose it
    }

    /// <summary>Copy the decoder's texture (one array slice of it) into the next slot and publish. False if the host is behind.</summary>
    public bool TryPublish(ID3D11Texture2D source, uint subresource, int width, int height)
    {
        if (_output.BeginFrame() is not { } slot) return false;
        _ctx.CopySubresourceRegion(slot, 0, 0, 0, 0, source, subresource, new Box(0, 0, 0, width, height, 1));
        _output.EndFrame();
        return true;
    }

    /// <summary>Wake the host to re-read the latest frame (it is still in its texture).</summary>
    public void Poke() => _map.Ready.Set();

    public void Dispose() => _output.Dispose();
}
