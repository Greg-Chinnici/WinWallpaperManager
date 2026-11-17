using System.Collections.Concurrent;
using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using static DesktopAttach.Attachment;

namespace DesktopAttach;

internal enum PresentMode { DComp, Hwnd }
internal enum Source { Static, Animate, Shm, Gpu, Fence }

internal sealed record TargetSpec(nint Hwnd, int Width, int Height);

/// <summary>
/// Owns D3D11/DXGI/DComp and presents on its own thread. The window thread only
/// posts commands; it never waits on rendering (doc §4.1, §7).
/// One swap chain per monitor window.
/// </summary>
internal sealed class Renderer : IDisposable
{
    private sealed class Target
    {
        public nint Hwnd; public int W, H;
        public IDXGISwapChain1? Swap; public ID3D11RenderTargetView? Rtv;
        public IDCompositionTarget? CompTarget; public IDCompositionVisual? Visual;
        public void DisposeComp() { Visual?.Dispose(); Visual = null; CompTarget?.Dispose(); CompTarget = null; }
        public void DisposeAll() { DisposeComp(); Rtv?.Dispose(); Rtv = null; Swap?.Dispose(); Swap = null; }
    }

    private static readonly Color4[] Palette =
    {
        new(0.85f, 0.10f, 0.60f, 1f), // magenta  (primary / index 0)
        new(0.10f, 0.55f, 0.85f, 1f), // blue
        new(0.20f, 0.75f, 0.30f, 1f), // green
    };

    private readonly PresentMode _mode;
    private readonly Source _source;
    private readonly bool _stats;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _quit = new(false);
    private readonly Thread _thread;
    private readonly List<Target> _targets = new();

    private IDXGIFactory2? _factory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDCompositionDevice? _comp;
    private FrameChannel? _shm;
    private GpuChannel? _gpu;
    private GpuChannel.Consumer? _gpuConsumer;
    private FenceChannel? _fence;
    private FenceChannel.Consumer? _fenceConsumer;

    public int PresentCount;
    private int _presentsThisSecond, _framesDropped;
    private long _lastSeq = -1, _statTick = Stopwatch.GetTimestamp();
    private readonly List<double> _latMs = new(), _uploadMs = new();

    public Renderer(PresentMode mode, Source source, bool stats)
    {
        _mode = mode; _source = source; _stats = stats;
        _thread = new Thread(Loop) { IsBackground = true, Name = "render" };
        _thread.Start();
    }

    /// <summary>Replace the set of monitor windows. Idempotent: unchanged targets are kept as-is.</summary>
    public void SetTargets(IReadOnlyList<TargetSpec> specs)
    {
        var copy = specs.ToList();
        _commands.Enqueue(() => DoSetTargets(copy));
        _wake.Set();
    }

    private void Loop()
    {
        try
        {
            CreateDevice();
            var handles = new List<WaitHandle> { _quit, _wake };
            if (_source == Source.Shm) { _shm = FrameChannel.Open(); handles.Add(_shm.FrameReady); Log("Waiting for frames on shared memory"); }
            if (_source == Source.Gpu) { _gpu = new GpuChannel(); _gpuConsumer = new GpuChannel.Consumer(_device!); handles.Add(_gpu.FrameReady); Log("Waiting for frames on shared GPU textures"); }
            if (_source == Source.Fence) { _fence = new FenceChannel(); _fenceConsumer = new FenceChannel.Consumer(_fence, _device!, _ctx!); handles.Add(_fence.FrameReady); Log("Waiting for frames on shared GPU textures (fence sync)"); }
            var arr = handles.ToArray();
            var frame = 0;
            while (true)
            {
                // Static: wait forever (zero idle cost). Animate: 2 Hz. Shm: wake on producer's event.
                var timeout = _source == Source.Animate ? 500 : (_stats ? 1000 : Timeout.Infinite);
                var idx = WaitHandle.WaitAny(arr, timeout);
                if (idx == 0) break;
                var dirty = false;
                while (_commands.TryDequeue(out var cmd)) { cmd(); dirty = true; }
                if (idx == 2) { if (_fence is not null) DrawFence(); else if (_gpu is not null) DrawGpu(); else DrawShm(); }
                else if (_targets.Count > 0 && (dirty || _source == Source.Animate)) DrawSolid(frame++);
                MaybeStats();
            }
            foreach (var t in _targets) t.DisposeAll();
            _comp?.Commit();
        }
        catch (Exception ex) { Log("RENDER THREAD FAILED: " + ex); }
    }

    private void CreateDevice()
    {
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        _factory.EnumAdapters1(0, out var adapter).CheckError();
        Log($"Adapter: {adapter.Description1.Description}");
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _device, out _ctx).CheckError();
        adapter.Dispose();
        if (_mode == PresentMode.DComp)
        {
            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            _comp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        }
        Log($"D3D11 device created ({_device!.FeatureLevel}), present mode = {_mode}, source = {_source}");
    }

    private void DoSetTargets(List<TargetSpec> specs)
    {
        // Drop surplus targets.
        for (var i = _targets.Count - 1; i >= specs.Count; i--) { _targets[i].DisposeAll(); _targets.RemoveAt(i); }
        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            if (i >= _targets.Count) _targets.Add(new Target());
            var t = _targets[i];
            if (t.Hwnd == s.Hwnd && t.W == s.Width && t.H == s.Height && t.Swap is not null) continue; // unchanged

            var sizeChanged = t.W != s.Width || t.H != s.Height;
            if (_mode == PresentMode.Hwnd || sizeChanged || t.Swap is null) t.DisposeAll(); else t.DisposeComp();

            var desc = new SwapChainDescription1
            {
                Width = (uint)s.Width, Height = (uint)s.Height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
            };
            if (_mode == PresentMode.DComp)
            {
                // Swap chain survives HWND re-creation (Explorer restart); only the DComp target is per-HWND.
                t.Swap ??= _factory!.CreateSwapChainForComposition(_device, desc, null);
                _comp!.CreateTargetForHwnd(s.Hwnd, true, out t.CompTarget).CheckError();
                t.Visual = _comp.CreateVisual();
                t.Visual.SetContent(t.Swap);
                t.CompTarget.SetRoot(t.Visual);
            }
            else
            {
                t.Swap = _factory!.CreateSwapChainForHwnd(_device, s.Hwnd, desc, null, null);
            }
            if (t.Rtv is null) { using var back = t.Swap.GetBuffer<ID3D11Texture2D>(0); t.Rtv = _device!.CreateRenderTargetView(back); }
            t.Hwnd = s.Hwnd; t.W = s.Width; t.H = s.Height;
            Log($"Target[{i}] bound to {Native.Hex(s.Hwnd)} {s.Width}x{s.Height}");
        }
        _comp?.Commit();
    }

    private void DrawSolid(int frame)
    {
        for (var i = 0; i < _targets.Count; i++)
        {
            var t = _targets[i];
            var c = _source == Source.Animate ? Hue(frame * 0.08f + i * 0.33f) : Palette[i % Palette.Length];
            _ctx!.ClearRenderTargetView(t.Rtv!, c);
            Present(t);
        }
    }

    private unsafe void DrawShm()
    {
        var h = _shm!.ReadHeader();
        if (h.Seq == _lastSeq) return;
        if (_lastSeq >= 0 && h.Seq > _lastSeq + 1) _framesDropped += (int)(h.Seq - _lastSeq - 1);
        _lastSeq = h.Seq;

        // Shared-memory frames go to the target whose size matches; others keep their solid colour.
        foreach (var t in _targets)
        {
            if (t.W != h.Width || t.H != h.Height) continue;
            var t0 = Stopwatch.GetTimestamp();
            using var back = t.Swap!.GetBuffer<ID3D11Texture2D>(0);
            _ctx!.UpdateSubresource(back, 0u, null, (nint)_shm.SlotPointer(h.Slot), (uint)h.Stride, 0u);
            var t1 = Stopwatch.GetTimestamp();
            Present(t);
            var t2 = Stopwatch.GetTimestamp();
            _uploadMs.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            _latMs.Add((t2 - h.ProducedAt) * 1000.0 / Stopwatch.Frequency);
        }
    }

    private void DrawFence()
    {
        var h = _fence!.ReadProducer();
        if (h.Seq == _lastSeq) return;
        if (_lastSeq >= 0 && h.Seq > _lastSeq + 1) _framesDropped += (int)(h.Seq - _lastSeq - 1);
        _lastSeq = h.Seq;
        foreach (var t in _targets)
        {
            if (t.W != h.Width || t.H != h.Height) continue;
            var t0 = Stopwatch.GetTimestamp();
            using var back = t.Swap!.GetBuffer<ID3D11Texture2D>(0);
            _fenceConsumer!.CopyTo(h, back);
            var t1 = Stopwatch.GetTimestamp();
            Present(t);
            var t2 = Stopwatch.GetTimestamp();
            _uploadMs.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            _latMs.Add((t2 - h.ProducedAt) * 1000.0 / Stopwatch.Frequency);
        }
    }

    private void DrawGpu()
    {
        var h = _gpu!.Read();
        if (h.Seq == _lastSeq) return;
        if (_lastSeq >= 0 && h.Seq > _lastSeq + 1) _framesDropped += (int)(h.Seq - _lastSeq - 1);
        _lastSeq = h.Seq;
        foreach (var t in _targets)
        {
            if (t.W != h.Width || t.H != h.Height) continue;
            var t0 = Stopwatch.GetTimestamp();
            using var back = t.Swap!.GetBuffer<ID3D11Texture2D>(0);
            if (!_gpuConsumer!.CopyTo(_ctx!, h.Slot, back)) continue;
            var t1 = Stopwatch.GetTimestamp();
            Present(t);
            var t2 = Stopwatch.GetTimestamp();
            _uploadMs.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            _latMs.Add((t2 - h.ProducedAt) * 1000.0 / Stopwatch.Frequency);
        }
    }

    private void Present(Target t)
    {
        var hr = t.Swap!.Present(1, PresentFlags.None);
        PresentCount++; _presentsThisSecond++;
        if (hr.Failure) Log($"Present failed: 0x{hr.Code:X8}");
    }

    private void MaybeStats()
    {
        if (!_stats) return;
        var now = Stopwatch.GetTimestamp();
        if (now - _statTick < Stopwatch.Frequency) return;
        Log($"stats: presents/s={_presentsThisSecond} total={PresentCount} dropped={_framesDropped}");
        _presentsThisSecond = 0; _statTick = now;
    }

    public string Summary()
    {
        static string P(List<double> v, double q) { if (v.Count == 0) return "-"; var s = v.OrderBy(x => x).ToList(); return s[(int)Math.Min(s.Count - 1, q * s.Count)].ToString("0.00"); }
        return $"presents={PresentCount} framesDropped={_framesDropped} acquireTimeouts={_gpuConsumer?.AcquireTimeouts ?? 0} " +
               $"upload ms p50={P(_uploadMs, .5)} p99={P(_uploadMs, .99)} " +
               $"produce->present ms p50={P(_latMs, .5)} p99={P(_latMs, .99)} (n={_latMs.Count})";
    }

    private static Color4 Hue(float t)
    {
        float f(float n) { var k = (n + t * 6) % 6; return 1 - Math.Max(0, Math.Min(Math.Min(k, 4 - k), 1)); }
        return new Color4(f(5) * 0.8f, f(3) * 0.8f, f(1) * 0.8f, 1f);
    }

    public void Dispose()
    {
        _quit.Set();
        _thread.Join(3000);
        _fenceConsumer?.Dispose(); _fence?.Dispose(); _gpuConsumer?.Dispose(); _gpu?.Dispose(); _comp?.Dispose(); _ctx?.Dispose(); _device?.Dispose(); _factory?.Dispose(); _shm?.Dispose();
    }
}
