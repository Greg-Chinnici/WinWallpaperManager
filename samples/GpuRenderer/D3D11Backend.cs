using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WallpaperProtocol;
using WallpaperProtocol.D3D11;

namespace GpuRenderer;

/// <summary>Direct3D 11: clear the shared texture, then copy a pre-filled bar texture into it.</summary>
internal sealed class D3D11Backend : IBackend
{
    private readonly Look _look;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly D3D11FrameOutput _output;
    private readonly ID3D11RenderTargetView[] _rtv;
    private readonly ID3D11Texture2D _bar;
    private readonly int _w;

    public D3D11Backend(WallpaperSession session, Look look)
    {
        _look = look;
        _w = session.Descriptor.Width;
        var h = session.Descriptor.Height;
        using var adapter = D3D11FrameOutput.FindAdapter(session.Gpu!); // must match the host's GPU
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1 }, out ID3D11Device device, out ID3D11DeviceContext ctx).CheckError();
        _device = device; _ctx = ctx;
        _output = new D3D11FrameOutput(session, device);
        _rtv = _output.Textures.Select(t => device.CreateRenderTargetView(t)).ToArray();

        _bar = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)look.BarWidth(_w), Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        });
        using var barRtv = device.CreateRenderTargetView(_bar);
        var c = look.Bar;
        ctx.ClearRenderTargetView(barRtv, new Color4(c.R, c.G, c.B, c.A));
    }

    public bool RenderFrame(double t)
    {
        if (_output.BeginFrame() is not { } texture) return false;
        var slot = Array.IndexOf(_output.Textures, texture);
        var bg = _look.Background;
        _ctx.ClearRenderTargetView(_rtv[slot], new Color4(bg.R, bg.G, bg.B, bg.A));
        _ctx.CopySubresourceRegion(texture, 0, (uint)_look.BarX(_w, t), 0, 0, _bar, 0, null);
        _output.EndFrame();
        return true;
    }

    public void Dispose()
    {
        foreach (var r in _rtv) r.Dispose();
        _bar.Dispose(); _output.Dispose(); _ctx.Dispose(); _device.Dispose();
    }
}
