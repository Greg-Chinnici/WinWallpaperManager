using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WallpaperProtocol.D3D11;

/// <summary>
/// Direct3D 11 producer for a <see cref="Transport.Gpu"/> session: opens the host's shared
/// textures and fences on your device. Per frame:
/// <code>
/// if (output.BeginFrame() is { } texture)   // null: the host is still using that slot, skip this tick
/// {
///     /* render into texture (it has RenderTarget + ShaderResource bind flags) */
///     output.EndFrame();                    // signals the produced fence and publishes
/// }
/// </code>
/// Create your device on <see cref="FindAdapter"/>'s adapter: shared textures don't cross GPUs.
/// </summary>
public sealed class D3D11FrameOutput : IDisposable
{
    private readonly SessionMap _map;
    private readonly ID3D11DeviceContext4 _ctx;
    private readonly ID3D11Fence _produced, _consumed;
    private int _slot = -1;

    public ID3D11Texture2D[] Textures { get; }

    public D3D11FrameOutput(WallpaperSession session, ID3D11Device device)
        : this(session.Map, session.Gpu ?? throw new ArgumentException("Session doesn't use the GPU transport"), device) { }

    /// <summary>Lower level: for producers that got the map + handles some other way (e.g. the daemon itself).</summary>
    public D3D11FrameOutput(SessionMap map, GpuResources gpu, ID3D11Device device)
    {
        _map = map;
        using var dev1 = device.QueryInterface<ID3D11Device1>();
        using var dev5 = device.QueryInterface<ID3D11Device5>();
        Textures = gpu.Textures.Select(h => Open(h, x => dev1.OpenSharedResource1<ID3D11Texture2D>(x))).ToArray();
        _produced = Open(gpu.ProducedFence, x => dev5.OpenSharedFence<ID3D11Fence>(x));
        _consumed = Open(gpu.ConsumedFence, x => dev5.OpenSharedFence<ID3D11Fence>(x));
        // Don't dispose device.ImmediateContext: Vortice caches that wrapper on the device, so
        // disposing it would break every other user of the device's immediate context.
        _ctx = device.ImmediateContext.QueryInterface<ID3D11DeviceContext4>();
    }

    [DllImport("kernel32")] private static extern bool CloseHandle(nint h);

    /// <summary>The handle was duplicated into this process for us; once opened we no longer need it.</summary>
    private static T Open<T>(long handle, Func<nint, T> open)
    {
        try { return open((nint)handle); }
        finally { CloseHandle((nint)handle); }
    }

    /// <summary>The texture to render the next frame into, or null if the host hasn't released that slot yet.</summary>
    public ID3D11Texture2D? BeginFrame()
    {
        _slot = _map.TryBeginGpuFrame(_consumed.CompletedValue);
        return _slot < 0 ? null : Textures[_slot];
    }

    /// <summary>Signal the produced fence after your rendering and tell the host the frame is ready.</summary>
    public void EndFrame()
    {
        if (_slot < 0) throw new InvalidOperationException("EndFrame without a successful BeginFrame");
        _ctx.Signal(_produced, _map.ProducedFenceValue);
        _ctx.Flush();
        _map.Publish();
        _slot = -1;
    }

    /// <summary>The DXGI adapter the host renders on (match it when creating your device).</summary>
    public static IDXGIAdapter1 FindAdapter(GpuResources gpu) => FindAdapter(gpu.AdapterLuid);

    /// <summary>The DXGI adapter with this LUID (LowPart | HighPart &lt;&lt; 32).</summary>
    public static IDXGIAdapter1 FindAdapter(long adapterLuid)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            var luid = adapter.Description1.Luid;
            if ((((long)luid.HighPart << 32) | luid.LowPart) == adapterLuid) return adapter;
            adapter.Dispose();
        }
        throw new InvalidOperationException("The host's GPU adapter wasn't found");
    }

    public void Dispose()
    {
        foreach (var t in Textures) t.Dispose();
        _produced.Dispose(); _consumed.Dispose(); _ctx.Dispose();
    }
}
