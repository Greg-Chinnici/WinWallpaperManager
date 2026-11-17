using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DesktopAttach;

/// <summary>
/// GPU frame transport spike: the producer renders into a ring of shared D3D11
/// textures (NT handles, opened by name) guarded by keyed mutexes, then publishes
/// {seq, slot, timestamp} in a tiny memory-mapped header and signals a named event.
/// Pixels never leave video memory.
///
/// Keyed-mutex protocol per slot: key 0 = free for producer, key 1 = holds a frame
/// for the host. Producer: Acquire(0) — or Acquire(1) to reclaim a frame the host
/// skipped — draw, Release(1). Host: Acquire(1), CopyResource, Release(0).
/// </summary>
internal sealed unsafe class GpuChannel : IDisposable
{
    public const int Slots = 3;
    public const string HeaderName = @"Local\DesktopAttach.GpuFrame";
    public const string EventName = @"Local\DesktopAttach.GpuFrameReady";
    public static string TextureName(int slot) => $"DesktopAttachGpuTex{slot}";
    private const int ReadWrite = unchecked((int)0x80000001); // DXGI_SHARED_RESOURCE_READ | WRITE

    [StructLayout(LayoutKind.Sequential)]
    public struct Header { public int Magic, Width, Height, Slot; public long Seq, ProducedAt; }

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    public readonly EventWaitHandle FrameReady;

    public GpuChannel()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(HeaderName, 4096, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor(0, 4096);
        byte* p = null; _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        FrameReady = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    public Header Read() { Thread.MemoryBarrier(); return *(Header*)_base; }
    public void Write(Header h) { Thread.MemoryBarrier(); *(Header*)_base = h; Thread.MemoryBarrier(); FrameReady.Set(); }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose(); _mmf.Dispose(); FrameReady.Dispose();
    }

    // ---------------- host side ----------------

    /// <summary>Host-side cache of opened slot textures.</summary>
    public sealed class Consumer : IDisposable
    {
        private readonly ID3D11Device1 _dev;
        private readonly ID3D11Texture2D?[] _tex = new ID3D11Texture2D?[Slots];
        private readonly IDXGIKeyedMutex?[] _mutex = new IDXGIKeyedMutex?[Slots];
        public int AcquireTimeouts;

        public Consumer(ID3D11Device device) => _dev = device.QueryInterface<ID3D11Device1>();

        /// <summary>Copy slot into dst (same size). Returns false if the slot couldn't be acquired.</summary>
        public bool CopyTo(ID3D11DeviceContext ctx, int slot, ID3D11Resource dst)
        {
            if (_tex[slot] is null)
            {
                _tex[slot] = new ID3D11Texture2D(Com.OpenSharedResourceByName(_dev, TextureName(slot), ReadWrite, typeof(ID3D11Texture2D).GUID));
                _mutex[slot] = _tex[slot]!.QueryInterface<IDXGIKeyedMutex>();
            }
            var hr = Com.AcquireSync(_mutex[slot]!, 1, 50);
            if (hr != 0) { AcquireTimeouts++; return false; } // WAIT_TIMEOUT (0x102) or abandoned
            ctx.CopyResource(dst, _tex[slot]);
            Com.ReleaseSync(_mutex[slot]!, 0);
            return true;
        }

        public void Dispose()
        {
            for (var i = 0; i < Slots; i++) { _mutex[i]?.Dispose(); _tex[i]?.Dispose(); }
            _dev.Dispose();
        }
    }

    // ---------------- producer side ----------------


    /// <summary>`produce-gpu`: a daemon stand-in that renders frames on the GPU.</summary>
    public static int RunProducer(int w, int h, int fps, int seconds)
    {
        [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("kernel32")] static extern bool CloseHandle(nint h);
        timeBeginPeriod(1);

        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        factory.EnumAdapters1(0, out var adapter).CheckError();
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1 }, out ID3D11Device device, out ID3D11DeviceContext ctx0).CheckError();
        adapter.Dispose();
        using var ctx = ctx0.QueryInterface<ID3D11DeviceContext1>();

        var tex = new ID3D11Texture2D[Slots];
        var rtv = new ID3D11RenderTargetView[Slots];
        var mutex = new IDXGIKeyedMutex[Slots];
        var handles = new nint[Slots];
        for (var i = 0; i < Slots; i++)
        {
            tex[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.SharedNTHandle | ResourceOptionFlags.SharedKeyedMutex,
            });
            rtv[i] = device.CreateRenderTargetView(tex[i]);
            mutex[i] = tex[i].QueryInterface<IDXGIKeyedMutex>();
            using var res = tex[i].QueryInterface<IDXGIResource1>();
            handles[i] = res.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, TextureName(i));
        }

        // 64-px white bar source, copied into each frame on the GPU.
        using var white = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 64, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        });
        using (var wrtv = device.CreateRenderTargetView(white)) ctx.ClearRenderTargetView(wrtv, new Color4(1, 1, 1, 1));

        using var ch = new GpuChannel();
        var period = Stopwatch.Frequency / fps;
        var next = Stopwatch.GetTimestamp();
        var end = next + seconds * Stopwatch.Frequency;
        long seq = 0; int busy = 0, reclaimed = 0;
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        while (Stopwatch.GetTimestamp() < end)
        {
            var slot = (int)(seq % Slots);
            var m = mutex[slot];
            var got = Com.AcquireSync(m, 0, 0) == 0;
            if (!got && Com.AcquireSync(m, 1, 0) == 0) { got = true; reclaimed++; } // host skipped this one
            if (got)
            {
                // Background drifts; a 64-px bar sweeps across — same look as the CPU producer.
                ctx.ClearRenderTargetView(rtv[slot], new Color4(0.25f, 0.125f, (seq * 2 % 256) / 255f, 1f));
                var bar = (uint)(seq * 16 % (w - 64));
                ctx.CopySubresourceRegion(tex[slot], 0, bar, 0, 0, white, 0, null);
                Com.ReleaseSync(m, 1);
                ch.Write(new Header { Magic = 0x47505546, Width = w, Height = h, Slot = slot, Seq = seq, ProducedAt = Stopwatch.GetTimestamp() });
                seq++;
            }
            else busy++; // host is holding this slot right now; skip a tick
            next += period;
            while (Stopwatch.GetTimestamp() < next) Thread.Sleep(1);
        }
        proc.Refresh();
        var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        Attachment.Log($"gpu producer: {seq} frames {w}x{h}@{fps} in {seconds}s, slot busy {busy}, reclaimed {reclaimed}, CPU {cpu / (seconds * 10.0):0.0}% of one core");

        for (var i = 0; i < Slots; i++) { CloseHandle(handles[i]); mutex[i].Dispose(); rtv[i].Dispose(); tex[i].Dispose(); }
        ctx0.Dispose(); device.Dispose();
        return 0;
    }
}

/// <summary>
/// Raw vtable calls for the two methods Vortice 3.8 doesn't expose usefully:
/// IDXGIKeyedMutex::AcquireSync's wrapper returns void, hiding WAIT_TIMEOUT (0x102, a
/// success code), and ID3D11Device1::OpenSharedResourceByName isn't public.
/// </summary>
internal static unsafe class Com
{
    // IUnknown(3) + IDXGIObject(4) + IDXGIDeviceSubObject(1) => AcquireSync = 8, ReleaseSync = 9
    public static int AcquireSync(IDXGIKeyedMutex m, ulong key, uint ms)
        => ((delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)(*(void***)m.NativePointer)[8])(m.NativePointer, key, ms);
    public static int ReleaseSync(IDXGIKeyedMutex m, ulong key)
        => ((delegate* unmanaged[Stdcall]<nint, ulong, int>)(*(void***)m.NativePointer)[9])(m.NativePointer, key);

    // IUnknown(3) + ID3D11Device(40) + ID3D11Device1: GetImmediateContext1..OpenSharedResource1 (6) => ByName = 49
    public static nint OpenSharedResourceByName(ID3D11Device1 d, string name, int access, Guid iid)
    {
        nint result;
        int hr;
        fixed (char* n = name)
            hr = ((delegate* unmanaged[Stdcall]<nint, char*, int, Guid*, nint*, int>)(*(void***)d.NativePointer)[49])(d.NativePointer, n, access, &iid, &result);
        Marshal.ThrowExceptionForHR(hr);
        return result;
    }

    // ID3D11Fence: IUnknown(3) + ID3D11DeviceChild(4) => CreateSharedHandle = 7
    public static nint CreateFenceSharedHandle(ID3D11Fence f, int access)
    {
        nint h;
        var hr = ((delegate* unmanaged[Stdcall]<nint, void*, int, char*, nint*, int>)(*(void***)f.NativePointer)[7])(f.NativePointer, null, access, null, &h);
        Marshal.ThrowExceptionForHR(hr);
        return h;
    }
}
