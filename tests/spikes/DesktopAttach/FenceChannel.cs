using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DesktopAttach;

/// <summary>
/// GPU frame transport with shared D3D11 fences instead of keyed mutexes.
///
///   producer fence  P: producer draws frame N into slot N%3, then GPU-signals P = N+1.
///   consumed fence  C: host GPU-waits P >= N+1, copies, then GPU-signals C = N+1.
///   back-pressure    : before drawing frame N, producer requires C >= N-2 on the CPU
///                      (i.e. the host is done with whatever it last read from that slot);
///                      otherwise it skips the tick. Nobody ever blocks on the CPU.
///
/// D3D11 has no open-fence-by-name, so each side publishes (pid, handle) and the other
/// duplicates the handle into its own process.
/// </summary>
internal sealed unsafe class FenceChannel : IDisposable
{
    public const int Slots = 3;
    public const string HeaderName = @"Local\DesktopAttach.FenceFrame";
    public const string EventName = @"Local\DesktopAttach.FenceFrameReady";
    public static string TextureName(int slot) => $"DesktopAttachFenceTex{slot}";
    private const int GenericAll = 0x10000000, ReadWrite = unchecked((int)0x80000001);

    [StructLayout(LayoutKind.Sequential)]
    public struct ProducerHeader { public int Magic, Width, Height, Slot; public long Seq, ProducedAt; public int Pid; public long FenceHandle; }
    [StructLayout(LayoutKind.Sequential)]
    public struct HostHeader { public int Pid; public long FenceHandle; }

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    public readonly EventWaitHandle FrameReady;

    public FenceChannel()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(HeaderName, 4096, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor(0, 4096);
        byte* p = null; _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        FrameReady = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    public ProducerHeader ReadProducer() { Thread.MemoryBarrier(); return *(ProducerHeader*)_base; }
    public void WriteProducer(ProducerHeader h) { Thread.MemoryBarrier(); *(ProducerHeader*)_base = h; Thread.MemoryBarrier(); FrameReady.Set(); }
    public HostHeader ReadHost() { Thread.MemoryBarrier(); return *(HostHeader*)(_base + 256); }
    public void WriteHost(HostHeader h) { *(HostHeader*)(_base + 256) = h; Thread.MemoryBarrier(); }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose(); _mmf.Dispose(); FrameReady.Dispose();
    }

    [DllImport("kernel32")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32")] private static extern bool DuplicateHandle(nint srcProc, nint src, nint dstProc, out nint dst, uint access, bool inherit, uint options);
    [DllImport("kernel32")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32")] public static extern bool CloseHandle(nint h);

    public static nint DuplicateFrom(int pid, long handle)
    {
        var proc = OpenProcess(0x0040 /* PROCESS_DUP_HANDLE */, false, pid);
        if (proc == 0) throw new InvalidOperationException($"OpenProcess({pid}) failed");
        try
        {
            if (!DuplicateHandle(proc, (nint)handle, GetCurrentProcess(), out var local, 0, false, 2 /* SAME_ACCESS */))
                throw new InvalidOperationException("DuplicateHandle failed: " + Marshal.GetLastWin32Error());
            return local;
        }
        finally { CloseHandle(proc); }
    }

    public static ID3D11Fence CreateSharedFence(ID3D11Device5 dev, out nint handle)
    {
        var fence = dev.CreateFence<ID3D11Fence>(0, FenceFlags.Shared);
        handle = Com.CreateFenceSharedHandle(fence, GenericAll);
        return fence;
    }

    // ---------------- host side ----------------

    public sealed class Consumer : IDisposable
    {
        private readonly FenceChannel _ch;
        private readonly ID3D11Device1 _dev1;
        private readonly ID3D11Device5 _dev5;
        private readonly ID3D11DeviceContext4 _ctx4;
        private readonly ID3D11Fence _consumed;
        private ID3D11Fence? _produced;
        private readonly ID3D11Texture2D?[] _tex = new ID3D11Texture2D?[Slots];

        public Consumer(FenceChannel ch, ID3D11Device device, ID3D11DeviceContext ctx)
        {
            _ch = ch;
            _dev1 = device.QueryInterface<ID3D11Device1>();
            _dev5 = device.QueryInterface<ID3D11Device5>();
            _ctx4 = ctx.QueryInterface<ID3D11DeviceContext4>();
            _consumed = CreateSharedFence(_dev5, out var h);
            _ch.WriteHost(new HostHeader { Pid = Environment.ProcessId, FenceHandle = h });
        }

        public void CopyTo(ProducerHeader h, ID3D11Resource dst)
        {
            if (_produced is null)
            {
                var local = DuplicateFrom(h.Pid, h.FenceHandle);
                _produced = _dev5.OpenSharedFence<ID3D11Fence>(local);
                CloseHandle(local);
            }
            _tex[h.Slot] ??= new ID3D11Texture2D(Com.OpenSharedResourceByName(_dev1, TextureName(h.Slot), ReadWrite, typeof(ID3D11Texture2D).GUID));
            _ctx4.Wait(_produced, (ulong)h.Seq + 1);   // GPU-side wait; CPU continues
            _ctx4.CopyResource(dst, _tex[h.Slot]);
            _ctx4.Signal(_consumed, (ulong)h.Seq + 1);
        }

        public void Dispose()
        {
            foreach (var t in _tex) t?.Dispose();
            _produced?.Dispose(); _consumed.Dispose(); _ctx4.Dispose(); _dev5.Dispose(); _dev1.Dispose();
        }
    }

    // ---------------- producer side ----------------

    public static int RunProducer(int w, int h, int fps, int seconds)
    {
        [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);
        timeBeginPeriod(1);

        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        factory.EnumAdapters1(0, out var adapter).CheckError();
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1 }, out ID3D11Device device, out ID3D11DeviceContext ctx0).CheckError();
        adapter.Dispose();
        using var dev5 = device.QueryInterface<ID3D11Device5>();
        using var ctx = ctx0.QueryInterface<ID3D11DeviceContext4>();

        var tex = new ID3D11Texture2D[Slots];
        var rtv = new ID3D11RenderTargetView[Slots];
        var texHandles = new nint[Slots];
        for (var i = 0; i < Slots; i++)
        {
            tex[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.SharedNTHandle | ResourceOptionFlags.Shared, // no keyed mutex
            });
            rtv[i] = device.CreateRenderTargetView(tex[i]);
            using var res = tex[i].QueryInterface<IDXGIResource1>();
            texHandles[i] = res.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, TextureName(i));
        }
        using var white = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 64, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        });
        using (var wrtv = device.CreateRenderTargetView(white)) ctx.ClearRenderTargetView(wrtv, new Color4(1, 1, 1, 1));

        using var produced = CreateSharedFence(dev5, out var producedHandle);
        using var ch = new FenceChannel();

        // Wait for the host to publish its consumed fence.
        var sw = Stopwatch.StartNew();
        HostHeader hh;
        while ((hh = ch.ReadHost()).Pid == 0 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(20);
        if (hh.Pid == 0) { Attachment.Log("no host on the fence channel"); return 1; }
        var localConsumed = DuplicateFrom(hh.Pid, hh.FenceHandle);
        using var consumed = dev5.OpenSharedFence<ID3D11Fence>(localConsumed);
        CloseHandle(localConsumed);

        var period = Stopwatch.Frequency / fps;
        var next = Stopwatch.GetTimestamp();
        var end = next + seconds * Stopwatch.Frequency;
        long seq = 0; int busy = 0;
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        while (Stopwatch.GetTimestamp() < end)
        {
            // Slot seq%3 last held frame seq-3; safe once the host has consumed anything >= seq-3.
            if (seq < Slots || consumed.CompletedValue >= (ulong)(seq - Slots + 1))
            {
                var slot = (int)(seq % Slots);
                ctx.ClearRenderTargetView(rtv[slot], new Color4(0.125f, 0.25f, (seq * 2 % 256) / 255f, 1f));
                ctx.CopySubresourceRegion(tex[slot], 0, (uint)(seq * 16 % (w - 64)), 0, 0, white, 0, null);
                ctx.Signal(produced, (ulong)seq + 1);
                ctx.Flush();
                ch.WriteProducer(new ProducerHeader
                {
                    Magic = 0x46454E43, Width = w, Height = h, Slot = slot, Seq = seq,
                    ProducedAt = Stopwatch.GetTimestamp(), Pid = Environment.ProcessId, FenceHandle = producedHandle,
                });
                seq++;
            }
            else busy++;
            next += period;
            while (Stopwatch.GetTimestamp() < next) Thread.Sleep(1);
        }
        proc.Refresh();
        var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        Attachment.Log($"fence producer: {seq} frames {w}x{h}@{fps} in {seconds}s, back-pressure skips {busy}, CPU {cpu / (seconds * 10.0):0.0}% of one core");
        for (var i = 0; i < Slots; i++) { CloseHandle(texHandles[i]); rtv[i].Dispose(); tex[i].Dispose(); }
        CloseHandle(producedHandle);
        ctx0.Dispose(); device.Dispose();
        return 0;
    }
}
