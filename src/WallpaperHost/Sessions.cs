using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WallpaperProtocol;
using AlphaMode = WallpaperProtocol.AlphaMode;
using static WallpaperHost.Native;

namespace WallpaperHost;

/// <summary>
/// Host side of one producer session. The host creates every resource (it is the D3D11 consumer);
/// producers only open or import them, whatever their API.
/// </summary>
internal abstract class HostSession : IDisposable
{
    public int Id { get; }
    public Layer Layer { get; }
    public SessionMap Map { get; }
    public int Width => Map.Width;
    public int Height => Map.Height;
    public long LastSeq = -1;

    protected HostSession(int id, Layer layer, SessionMap map) { Id = id; Layer = layer; Map = map; }

    /// <summary>Copy the newest published frame into <paramref name="dst"/>. False if there is nothing new (or it tore).</summary>
    public abstract bool CopyLatest(ID3D11DeviceContext4 ctx, ID3D11Texture2D dst);

    public abstract SessionDescriptor Describe();

    public virtual void Dispose() => Map.Dispose();
}

/// <summary>CPU transport: BGRA8 rows in the shared map, uploaded with UpdateSubresource.</summary>
internal sealed unsafe class CpuHostSession : HostSession
{
    public long Torn;

    public CpuHostSession(int id, Layer layer, int w, int h, AlphaMode alpha)
        : base(id, layer, SessionMap.Create(id, Transport.Cpu, w, h, alpha)) { }

    public override bool CopyLatest(ID3D11DeviceContext4 ctx, ID3D11Texture2D dst)
    {
        var (seq, slot) = Map.ReadLatest();
        if (seq < 0 || seq == LastSeq) return false;
        LastSeq = seq;
        // UpdateSubresource copies synchronously, so the slot is free again once it returns.
        ctx.UpdateSubresource(dst, 0u, null, (nint)Map.SlotPointer(slot), (uint)Map.Stride, 0u);
        if (!Map.StillValid(seq)) { Torn++; return false; } // producer lapped us mid-copy
        return true;
    }

    public override SessionDescriptor Describe() => new(Id, Layer, Transport.Cpu, Width, Height, Map.Stride, Map.Slots,
        Map.Alpha, Map.MapName, Map.EventName, null);
}

/// <summary>
/// GPU transport: a ring of shared textures plus two shared fences.
///   produced: the producer signals Seq + 1 after rendering frame Seq.
///   consumed: the host signals Seq + 1 after copying it; producers check it before reusing a slot.
/// The host waits on "produced" on the GPU, so neither side ever blocks a CPU thread.
/// </summary>
internal sealed class GpuHostSession : HostSession
{
    private const int GenericAll = 0x10000000;
    private readonly ID3D11Texture2D[] _textures;
    private readonly ID3D11Fence _produced, _consumed;
    private readonly GpuResources _remote;

    public GpuHostSession(int id, Layer layer, int w, int h, AlphaMode alpha, int producerPid, ID3D11Device device, long adapterLuid)
        : base(id, layer, SessionMap.Create(id, Transport.Gpu, w, h, alpha))
    {
        using var dev5 = device.QueryInterface<ID3D11Device5>();
        var target = OpenProcess(PROCESS_DUP_HANDLE, false, producerPid);
        if (target == 0) throw new InvalidOperationException($"Can't open producer process {producerPid} to hand it the GPU handles (error {Marshal.GetLastWin32Error()})");
        try
        {
            _textures = new ID3D11Texture2D[Map.Slots];
            var remoteTextures = new long[Map.Slots];
            for (var i = 0; i < Map.Slots; i++)
            {
                _textures[i] = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    MiscFlags = ResourceOptionFlags.SharedNTHandle | ResourceOptionFlags.Shared,
                });
                using var res = _textures[i].QueryInterface<IDXGIResource1>();
                var h0 = res.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null!);
                remoteTextures[i] = Give(h0, target);
            }
            _produced = dev5.CreateFence<ID3D11Fence>(0, FenceFlags.Shared);
            _consumed = dev5.CreateFence<ID3D11Fence>(0, FenceFlags.Shared);
            var producedRemote = Give(Com.CreateFenceSharedHandle(_produced, GenericAll), target);
            var consumedRemote = Give(Com.CreateFenceSharedHandle(_consumed, GenericAll), target);
            _remote = new GpuResources(adapterLuid, remoteTextures, producedRemote, consumedRemote);
        }
        finally { CloseHandle(target); }
    }

    /// <summary>Move our NT handle into the producer process (closing ours); returns the value valid over there.</summary>
    private static long Give(nint handle, nint targetProcess)
    {
        if (!DuplicateHandle(GetCurrentProcess(), handle, targetProcess, out var remote, 0, false, DUPLICATE_SAME_ACCESS | DUPLICATE_CLOSE_SOURCE))
            throw new InvalidOperationException("DuplicateHandle failed: " + Marshal.GetLastWin32Error());
        return remote;
    }

    public override bool CopyLatest(ID3D11DeviceContext4 ctx, ID3D11Texture2D dst)
    {
        var (seq, slot) = Map.ReadLatest();
        if (seq < 0 || seq == LastSeq) return false;
        LastSeq = seq;
        ctx.Wait(_produced, (ulong)seq + 1);       // GPU-side wait for the producer's rendering
        ctx.CopyResource(dst, _textures[slot]);
        ctx.Signal(_consumed, (ulong)seq + 1);     // slot may be reused
        return true;
    }

    public override SessionDescriptor Describe() => new(Id, Layer, Transport.Gpu, Width, Height, 0, Map.Slots,
        Map.Alpha, Map.MapName, Map.EventName, _remote);

    public override void Dispose()
    {
        foreach (var t in _textures) t.Dispose();
        _produced.Dispose(); _consumed.Dispose();
        base.Dispose();
    }
}

/// <summary>Raw vtable calls for members Vortice 3.8 doesn't expose (see docs/architecture.md).</summary>
internal static unsafe class Com
{
    // ID3D11Fence: IUnknown(3) + ID3D11DeviceChild(4) => CreateSharedHandle = 7
    public static nint CreateFenceSharedHandle(ID3D11Fence f, int access)
    {
        nint h;
        var hr = ((delegate* unmanaged[Stdcall]<nint, void*, int, char*, nint*, int>)(*(void***)f.NativePointer)[7])(f.NativePointer, null, access, null, &h);
        Marshal.ThrowExceptionForHR(hr);
        return h;
    }
}
