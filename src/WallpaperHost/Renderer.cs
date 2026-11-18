using System.Collections.Concurrent;
using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using WallpaperProtocol;
using AlphaMode = WallpaperProtocol.AlphaMode;
using DCompInterpolation = Vortice.DirectComposition.BitmapInterpolationMode;

namespace WallpaperHost;

internal enum FitMode { Fill, Fit, Stretch, Center }

internal sealed record TargetSpec(nint Hwnd, int Width, int Height);

/// <summary>
/// Owns D3D11/DXGI/DComp and all producer sessions; presents on its own thread. The window
/// thread only posts commands (doc §4.1, §7).
///
/// Two layers, each with one window per monitor and at most one active session:
///   Desktop: windows behind the icons; content over a black letterbox; alpha ignored.
///   Overlay: top-most click-through windows; premultiplied alpha, no background.
/// Scaling is a DirectComposition transform, so DWM does it on the GPU.
/// </summary>
internal sealed class Renderer : IDisposable
{
    private sealed class Target
    {
        public nint Hwnd; public int MonW, MonH;
        public IDXGISwapChain1? Content, Background;
        public int ContentW, ContentH;
        public IDCompositionTarget? CompTarget;
        public IDCompositionVisual? Root, BgVisual, ContentVisual;

        public void DisposeComp()
        {
            ContentVisual?.Dispose(); BgVisual?.Dispose(); Root?.Dispose(); CompTarget?.Dispose();
            ContentVisual = BgVisual = Root = null; CompTarget = null;
        }
        public void DisposeAll() { DisposeComp(); Content?.Dispose(); Background?.Dispose(); Content = Background = null; ContentW = ContentH = 0; }
    }

    private sealed class LayerState
    {
        public required Layer Kind;
        public readonly List<Target> Targets = new();
        public HostSession? Active;
        public ID3D11Texture2D? Frame;
        public int FrameW, FrameH;
        public bool HasFrame;
        public long Presented;
        public bool Opaque => Kind == Layer.Desktop;
    }

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _quit = new(false);
    private readonly Thread _thread;
    private readonly LayerState[] _layers = { new() { Kind = Layer.Desktop }, new() { Kind = Layer.Overlay } };
    private readonly Dictionary<int, HostSession> _sessions = new();
    private readonly Action<Layer, bool> _onLayerVisible;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IDXGIFactory2? _factory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext4? _ctx;
    private IDCompositionDevice? _comp;
    private long _adapterLuid;
    private FitMode _fit = FitMode.Fill;
    private DCompInterpolation _sampling = DCompInterpolation.Linear;
    private int _nextSessionId = 1;

    public Renderer(Action<Layer, bool> onLayerVisible)
    {
        _onLayerVisible = onLayerVisible;
        _thread = new Thread(Loop) { IsBackground = true, Name = "render" };
        _thread.Start();
    }

    // ---------------- commands (any thread) ----------------

    private void Post(Action a) { _commands.Enqueue(a); _wake.Set(); }

    private async Task<T> Call<T>(Func<T> f)
    {
        await _ready.Task;
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
        return await tcs.Task;
    }

    public void SetTargets(Layer layer, IReadOnlyList<TargetSpec> specs) { var copy = specs.ToList(); Post(() => DoSetTargets(L(layer), copy)); }
    public void SetFit(FitMode mode) => Post(() => { _fit = mode; foreach (var l in _layers) foreach (var t in l.Targets) Layout(l, t); _comp?.Commit(); });
    public void SetSampling(bool pixelated) => Post(() =>
    {
        _sampling = pixelated ? DCompInterpolation.NearestNeighbor : DCompInterpolation.Linear;
        foreach (var l in _layers) foreach (var t in l.Targets) t.ContentVisual?.SetBitmapInterpolationMode(_sampling);
        _comp?.Commit();
    });

    /// <summary>Create a session's resources. GPU handles are duplicated into <paramref name="producerPid"/>.</summary>
    public Task<SessionDescriptor> CreateSessionAsync(Layer layer, Transport transport, int w, int h, AlphaMode alpha, int producerPid) => Call(() =>
    {
        var id = _nextSessionId++;
        HostSession s = transport == Transport.Gpu
            ? new GpuHostSession(id, layer, w, h, alpha, producerPid, _device!, _adapterLuid)
            : new CpuHostSession(id, layer, w, h, alpha);
        _sessions[id] = s;
        Log.Write($"Session {id}: {transport.ToWire()} {layer.ToWire()} {w}x{h} {alpha.ToWire()} (producer pid {producerPid})");
        return s.Describe();
    });

    /// <summary>Show this session's frames on its layer (replacing whatever was active there).</summary>
    public Task<bool> ActivateAsync(int id) => Call(() =>
    {
        if (!_sessions.TryGetValue(id, out var s)) throw new ArgumentException($"no session {id}");
        var l = L(s.Layer);
        if (l.Active != s) { SetHasFrame(l, false); l.Active = s; s.LastSeq = -1; }
        TryTakeFrame(l); // it may already have a frame (e.g. the daemon's wallpaper coming back)
        return true;
    });

    public Task<bool> DestroyAsync(int id) => Call(() =>
    {
        if (!_sessions.Remove(id, out var s)) return false;
        var l = L(s.Layer);
        if (l.Active == s) { l.Active = null; SetHasFrame(l, false); }
        s.Dispose();
        Log.Write($"Session {id} destroyed");
        return true;
    });

    public Task<System.Text.Json.Nodes.JsonObject> StatusAsync() => Call(() =>
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var l in _layers)
            o[l.Kind.ToWire()] = new System.Text.Json.Nodes.JsonObject
            {
                ["session"] = l.Active?.Id, ["visible"] = l.HasFrame, ["presented"] = l.Presented,
                ["transport"] = l.Active?.Map.Transport.ToWire(), ["width"] = l.Active?.Width, ["height"] = l.Active?.Height,
            };
        o["sessions"] = _sessions.Count;
        return o;
    });

    private LayerState L(Layer layer) => _layers[(int)layer];

    private void SetHasFrame(LayerState l, bool has)
    {
        if (l.HasFrame == has) return;
        l.HasFrame = has;
        _onLayerVisible(l.Kind, has);
    }

    // ---------------- render thread ----------------

    private void Loop()
    {
        try
        {
            CreateDevice();
            _ready.SetResult();
            Log.Write("Renderer ready");
            while (true)
            {
                // Event-driven: wait for a command or a frame from either layer's active session.
                var handles = new List<WaitHandle> { _quit, _wake };
                var owners = new List<LayerState>();
                foreach (var l in _layers) if (l.Active is { } s) { handles.Add(s.Map.Ready); owners.Add(l); }
                var idx = WaitHandle.WaitAny(handles.ToArray());
                if (idx == 0) break;
                if (idx >= 2) TryTakeFrame(owners[idx - 2]);
                while (_commands.TryDequeue(out var cmd))
                {
                    try { cmd(); } catch (Exception ex) { Log.Write("Render command failed: " + ex.Message); }
                }
            }
        }
        catch (Exception ex) { _ready.TrySetException(ex); Log.Write("RENDER THREAD FAILED: " + ex); }
        finally
        {
            foreach (var l in _layers) foreach (var t in l.Targets) t.DisposeAll();
            _comp?.Commit();
        }
    }

    private void CreateDevice()
    {
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        _factory.EnumAdapters1(0, out var adapter).CheckError();
        var desc = adapter.Description1;
        _adapterLuid = ((long)desc.Luid.HighPart << 32) | desc.Luid.LowPart;
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext ctx).CheckError();
        _device = device;
        _ctx = ctx.QueryInterface<ID3D11DeviceContext4>(); // Wait/Signal for GPU sessions
        ctx.Dispose();
        Log.Write($"GPU: {desc.Description} ({device.FeatureLevel}), LUID {_adapterLuid:X}");
        adapter.Dispose();
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        _comp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
    }

    private IDXGISwapChain1 CreateSwapChain(int w, int h, bool opaque) =>
        _factory!.CreateSwapChainForComposition(_device!, new SwapChainDescription1
        {
            Width = (uint)w, Height = (uint)h, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = opaque ? Vortice.DXGI.AlphaMode.Ignore : Vortice.DXGI.AlphaMode.Premultiplied,
        }, null);

    private void DoSetTargets(LayerState l, List<TargetSpec> specs)
    {
        for (var i = l.Targets.Count - 1; i >= specs.Count; i--) { l.Targets[i].DisposeAll(); l.Targets.RemoveAt(i); }
        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            if (i >= l.Targets.Count) l.Targets.Add(new Target());
            var t = l.Targets[i];
            if (t.Hwnd == s.Hwnd && t.MonW == s.Width && t.MonH == s.Height && t.CompTarget is not null) continue;

            t.DisposeComp(); // swap chains survive HWND re-creation (Explorer restart)
            t.Hwnd = s.Hwnd; t.MonW = s.Width; t.MonH = s.Height;

            if (l.Opaque && t.Background is null)
            {
                t.Background = CreateSwapChain(2, 2, opaque: true);
                using var bb = t.Background.GetBuffer<ID3D11Texture2D>(0);
                using var rtv = _device!.CreateRenderTargetView(bb);
                _ctx!.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
                t.Background.Present(0, PresentFlags.None);
            }

            _comp!.CreateTargetForHwnd(s.Hwnd, true, out t.CompTarget).CheckError();
            t.Root = _comp.CreateVisual();
            t.ContentVisual = _comp.CreateVisual();
            t.ContentVisual.SetBitmapInterpolationMode(_sampling);
            if (t.Content is not null) t.ContentVisual.SetContent(t.Content);
            if (t.Background is not null)
            {
                t.BgVisual = _comp.CreateVisual();
                t.BgVisual.SetContent(t.Background);
                t.Root.AddVisual(t.BgVisual, false, null);
                t.Root.AddVisual(t.ContentVisual, true, t.BgVisual); // content above the black letterbox
            }
            else t.Root.AddVisual(t.ContentVisual, false, null);
            t.CompTarget.SetRoot(t.Root);
            Layout(l, t);
            Log.Write($"{l.Kind.ToWire()} target[{i}] -> {Native.Hex(s.Hwnd)} {s.Width}x{s.Height}");
            if (l.HasFrame) PresentTo(l, t); // new/re-created monitor: show what we already have
        }
        _comp?.Commit();
    }

    private void Layout(LayerState l, Target t)
    {
        if (t.Root is null) return;
        if (t.BgVisual is not null) { var bg = Matrix3x2.CreateScale(t.MonW / 2f, t.MonH / 2f); t.BgVisual.SetTransform(ref bg); }
        if (t.ContentW == 0) return;
        float fw = t.ContentW, fh = t.ContentH, mw = t.MonW, mh = t.MonH, sx, sy;
        switch (_fit)
        {
            case FitMode.Stretch: sx = mw / fw; sy = mh / fh; break;
            case FitMode.Center: sx = sy = 1; break;
            case FitMode.Fit: sx = sy = Math.Min(mw / fw, mh / fh); break;
            default: sx = sy = Math.Max(mw / fw, mh / fh); break; // Fill (cover)
        }
        var m = Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation((mw - fw * sx) / 2, (mh - fh * sy) / 2);
        t.ContentVisual!.SetTransform(ref m);
    }

    private void TryTakeFrame(LayerState l)
    {
        if (l.Active is not { } s) return;
        if (l.Frame is null || l.FrameW != s.Width || l.FrameH != s.Height)
        {
            l.Frame?.Dispose();
            l.Frame = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)s.Width, Height = (uint)s.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            l.FrameW = s.Width; l.FrameH = s.Height;
        }
        if (!s.CopyLatest(_ctx!, l.Frame)) return;
        foreach (var t in l.Targets) PresentTo(l, t);
        _comp!.Commit();
        l.Presented++;
        SetHasFrame(l, true);
    }

    private void PresentTo(LayerState l, Target t)
    {
        if (t.Root is null || l.Frame is null) return;
        if (t.Content is null)
        {
            t.Content = CreateSwapChain(l.FrameW, l.FrameH, l.Opaque);
            t.ContentVisual!.SetContent(t.Content);
        }
        else if (t.ContentW != l.FrameW || t.ContentH != l.FrameH)
        {
            t.Content.ResizeBuffers(2, (uint)l.FrameW, (uint)l.FrameH, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        }
        if (t.ContentW != l.FrameW || t.ContentH != l.FrameH) { t.ContentW = l.FrameW; t.ContentH = l.FrameH; Layout(l, t); }
        using (var back = t.Content.GetBuffer<ID3D11Texture2D>(0)) _ctx!.CopyResource(back, l.Frame);
        t.Content.Present(1, PresentFlags.None);
    }

    public void Dispose()
    {
        _quit.Set();
        _thread.Join(3000);
        foreach (var s in _sessions.Values) s.Dispose();
        foreach (var l in _layers) l.Frame?.Dispose();
        _comp?.Dispose(); _ctx?.Dispose(); _device?.Dispose(); _factory?.Dispose();
    }
}
