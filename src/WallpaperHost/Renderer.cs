using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using WallpaperProtocol;
using DCompInterpolation = Vortice.DirectComposition.BitmapInterpolationMode;

namespace WallpaperHost;

internal enum FitMode { Fill, Fit, Stretch, Center }

internal sealed record TargetSpec(nint Hwnd, int Width, int Height);

/// <summary>
/// Presents frames from the shared ring on every monitor window. All D3D/DXGI/DComp work
/// happens on this class's thread; the window thread only posts commands (doc §4.1, §7).
///
/// Per monitor: root visual → [background: 2×2 black swap chain scaled to the monitor,
/// content: frame-sized swap chain positioned/scaled by a DComp transform]. Scaling is done
/// by DWM, so the host never needs shaders and frames of any size work.
/// </summary>
internal sealed unsafe class Renderer : IDisposable
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
        public void DisposeAll() { DisposeComp(); Content?.Dispose(); Background?.Dispose(); Content = Background = null; }
    }

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _quit = new(false);
    private readonly Thread _thread;
    private readonly List<Target> _targets = new();
    private readonly Action _onFirstFramePresented;

    private IDXGIFactory2? _factory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDCompositionDevice? _comp;
    private FrameChannel? _channel;
    private ID3D11Texture2D? _frame;     // last accepted frame, kept for redraws/new monitors
    private int _frameW, _frameH;
    private long _lastSeq = -1;
    private FitMode _fit = FitMode.Fill;
    private DCompInterpolation _sampling = DCompInterpolation.Linear;

    public long FramesPresented, FramesRejected, FramesTorn;

    public Renderer(Action onFirstFramePresented)
    {
        _onFirstFramePresented = onFirstFramePresented;
        _thread = new Thread(Loop) { IsBackground = true, Name = "render" };
        _thread.Start();
    }

    public void SetTargets(IReadOnlyList<TargetSpec> specs) => Post(() => DoSetTargets(specs.ToList()));
    /// <summary>Smooth (bilinear) for photos and video; pixelated (nearest neighbour) keeps pixel art crisp.</summary>
    public void SetSampling(bool pixelated) => Post(() =>
    {
        _sampling = pixelated ? DCompInterpolation.NearestNeighbor : DCompInterpolation.Linear;
        foreach (var t in _targets) t.ContentVisual?.SetBitmapInterpolationMode(_sampling);
        _comp?.Commit();
    });
    public void SetFit(FitMode mode) => Post(() => { _fit = mode; foreach (var t in _targets) Layout(t); _comp?.Commit(); });
    /// <summary>Forget the current frame (so the next "show" waits for a fresh one).</summary>
    public void ClearFrame() => Post(() => { _frame?.Dispose(); _frame = null; _frameW = _frameH = 0; });

    private void Post(Action a) { _commands.Enqueue(a); _wake.Set(); }

    private void Loop()
    {
        try
        {
            CreateDevice();
            _channel = FrameChannel.Open();
            var handles = new WaitHandle[] { _quit, _wake, _channel.FrameReady };
            Log.Write("Renderer ready; waiting for frames");
            while (true)
            {
                // Event-driven: no timeout. Idle = blocked here.
                var idx = WaitHandle.WaitAny(handles);
                if (idx == 0) break;
                while (_commands.TryDequeue(out var cmd))
                {
                    try { cmd(); } catch (Exception ex) { Log.Write("Render command failed: " + ex.Message); }
                }
                if (idx == 2) TryTakeFrame();
            }
        }
        catch (Exception ex) { Log.Write("RENDER THREAD FAILED: " + ex); }
        finally
        {
            foreach (var t in _targets) t.DisposeAll();
            _comp?.Commit();
        }
    }

    private void CreateDevice()
    {
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        _factory.EnumAdapters1(0, out var adapter).CheckError();
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext ctx).CheckError();
        _device = device; _ctx = ctx;
        Log.Write($"GPU: {adapter.Description1.Description} ({device.FeatureLevel})");
        adapter.Dispose();
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        _comp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
    }

    private IDXGISwapChain1 CreateSwapChain(int w, int h) =>
        _factory!.CreateSwapChainForComposition(_device!, new SwapChainDescription1
        {
            Width = (uint)w, Height = (uint)h, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Ignore,
        }, null);

    private void DoSetTargets(List<TargetSpec> specs)
    {
        for (var i = _targets.Count - 1; i >= specs.Count; i--) { _targets[i].DisposeAll(); _targets.RemoveAt(i); }
        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            if (i >= _targets.Count) _targets.Add(new Target());
            var t = _targets[i];
            if (t.Hwnd == s.Hwnd && t.MonW == s.Width && t.MonH == s.Height && t.CompTarget is not null) continue;

            t.DisposeComp(); // swap chains survive HWND re-creation (Explorer restart)
            t.Hwnd = s.Hwnd; t.MonW = s.Width; t.MonH = s.Height;

            if (t.Background is null)
            {
                t.Background = CreateSwapChain(2, 2);
                using var bb = t.Background.GetBuffer<ID3D11Texture2D>(0);
                using var rtv = _device!.CreateRenderTargetView(bb);
                _ctx!.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
                t.Background.Present(0, PresentFlags.None);
            }

            _comp!.CreateTargetForHwnd(s.Hwnd, true, out t.CompTarget).CheckError();
            t.Root = _comp.CreateVisual();
            t.BgVisual = _comp.CreateVisual();
            t.BgVisual.SetContent(t.Background);
            t.ContentVisual = _comp.CreateVisual();
            t.ContentVisual.SetBitmapInterpolationMode(_sampling);
            if (t.Content is not null) t.ContentVisual.SetContent(t.Content);
            t.Root.AddVisual(t.BgVisual, false, null);
            t.Root.AddVisual(t.ContentVisual, true, t.BgVisual); // content above background
            t.CompTarget.SetRoot(t.Root);
            Layout(t);
            Log.Write($"Monitor target[{i}] -> {Native.Hex(s.Hwnd)} {s.Width}x{s.Height}");

            if (_frame is not null) PresentTo(t); // new/re-created monitor: show what we already have
        }
        _comp?.Commit();
    }

    /// <summary>Position the content visual for the current fit mode, and stretch the black background.</summary>
    private void Layout(Target t)
    {
        if (t.Root is null) return;
        var bg = Matrix3x2.CreateScale(t.MonW / 2f, t.MonH / 2f);
        t.BgVisual!.SetTransform(ref bg);
        if (t.ContentW == 0) return;

        float fw = t.ContentW, fh = t.ContentH, mw = t.MonW, mh = t.MonH;
        float sx, sy;
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

    private void TryTakeFrame()
    {
        var ch = _channel!;
        var h = ch.ReadHeader();
        if (h.Seq == _lastSeq || h.Width <= 0) return;
        _lastSeq = h.Seq;
        var active = ch.ActiveWriter;
        if (active == 0 || h.Writer != active) { FramesRejected++; return; }
        if (h.Width > FrameChannel.MaxWidth || h.Height > FrameChannel.MaxHeight || h.Format != 0) { FramesRejected++; return; }

        if (_frame is null || _frameW != h.Width || _frameH != h.Height)
        {
            _frame?.Dispose();
            _frame = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)h.Width, Height = (uint)h.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            _frameW = h.Width; _frameH = h.Height;
        }
        // UpdateSubresource copies synchronously, so the slot is free again once it returns.
        _ctx!.UpdateSubresource(_frame, 0u, null, (nint)ch.SlotPointer(h.Slot), (uint)h.Stride, 0u);
        if (!ch.StillValid(h.Seq)) { FramesTorn++; return; } // producer lapped us mid-copy

        var first = FramesPresented == 0;
        foreach (var t in _targets) PresentTo(t);
        _comp!.Commit();
        FramesPresented++;
        if (first) _onFirstFramePresented();
    }

    private void PresentTo(Target t)
    {
        if (t.Root is null || _frame is null) return;
        if (t.Content is null)
        {
            t.Content = CreateSwapChain(_frameW, _frameH);
            t.ContentVisual!.SetContent(t.Content);
        }
        else if (t.ContentW != _frameW || t.ContentH != _frameH)
        {
            t.Content.ResizeBuffers(2, (uint)_frameW, (uint)_frameH, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        }
        if (t.ContentW != _frameW || t.ContentH != _frameH)
        {
            t.ContentW = _frameW; t.ContentH = _frameH;
            Layout(t);
        }
        using (var back = t.Content.GetBuffer<ID3D11Texture2D>(0)) _ctx!.CopyResource(back, _frame);
        t.Content.Present(1, PresentFlags.None);
    }

    /// <summary>Ask for the next "first frame" callback (used after hide → show).</summary>
    public void ResetFirstFrame() => Post(() => FramesPresented = 0);

    public void Dispose()
    {
        _quit.Set();
        _thread.Join(3000);
        _frame?.Dispose(); _channel?.Dispose();
        _comp?.Dispose(); _ctx?.Dispose(); _device?.Dispose(); _factory?.Dispose();
    }
}
