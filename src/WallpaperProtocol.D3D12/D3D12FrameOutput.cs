using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace WallpaperProtocol.D3D12;

/// <summary>
/// Direct3D 12 producer for a <see cref="Transport.Gpu"/> session: opens the host's shared
/// textures (as committed resources) and fences on your device. Per frame:
/// <code>
/// if (output.BeginFrame() is { } resource)   // null: the host still uses that slot, skip this tick
/// {
///     // record: barrier COMMON -> RENDER_TARGET, draw, barrier RENDER_TARGET -> COMMON
///     queue.ExecuteCommandList(list);
///     output.EndFrame();                     // queue.Signal(produced fence) + publish
/// }
/// </code>
/// Resources must be left in D3D12_RESOURCE_STATE_COMMON at the end of each frame so the host
/// (Direct3D 11) can read them. Create your device on <see cref="FindAdapter"/>'s adapter.
/// </summary>
public sealed class D3D12FrameOutput : IDisposable
{
    private readonly WallpaperSession _session;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12Fence _produced, _consumed;
    private int _slot = -1;

    public ID3D12Resource[] Textures { get; }

    public D3D12FrameOutput(WallpaperSession session, ID3D12Device device, ID3D12CommandQueue queue)
    {
        var gpu = session.Gpu ?? throw new ArgumentException("Session doesn't use the GPU transport");
        _session = session;
        _queue = queue;
        Textures = gpu.Textures.Select(h => Open(h, x => device.OpenSharedHandle<ID3D12Resource>(x))).ToArray();
        _produced = Open(gpu.ProducedFence, x => device.OpenSharedHandle<ID3D12Fence>(x));
        _consumed = Open(gpu.ConsumedFence, x => device.OpenSharedHandle<ID3D12Fence>(x));
    }

    [DllImport("kernel32")] private static extern bool CloseHandle(nint h);

    private static T Open<T>(long handle, Func<nint, T> open)
    {
        try { return open((nint)handle); }
        finally { CloseHandle((nint)handle); }
    }

    /// <summary>The resource to render the next frame into, or null if the host hasn't released that slot yet.</summary>
    public ID3D12Resource? BeginFrame()
    {
        _slot = _session.TryBeginGpuFrame(_consumed.CompletedValue);
        return _slot < 0 ? null : Textures[_slot];
    }

    /// <summary>Call after ExecuteCommandLists for this frame: queues the fence signal and publishes.</summary>
    public void EndFrame()
    {
        if (_slot < 0) throw new InvalidOperationException("EndFrame without a successful BeginFrame");
        _queue.Signal(_produced, _session.ProducedFenceValue);
        _session.PublishGpuFrame();
        _slot = -1;
    }

    public static IDXGIAdapter1 FindAdapter(GpuResources gpu)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            var luid = adapter.Description1.Luid;
            if (luid.LowPart == gpu.LuidLow && luid.HighPart == gpu.LuidHigh) return adapter;
            adapter.Dispose();
        }
        throw new InvalidOperationException("The host's GPU adapter wasn't found");
    }

    public void Dispose()
    {
        foreach (var t in Textures) t.Dispose();
        _produced.Dispose(); _consumed.Dispose();
    }
}
