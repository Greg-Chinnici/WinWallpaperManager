using System.Diagnostics;
using Vortice.WIC;

namespace WallpaperDaemon.Sources;

internal static class Wic
{
    private static readonly Lazy<IWICImagingFactory> LazyFactory = new(() => new IWICImagingFactory());
    public static IWICImagingFactory Factory => LazyFactory.Value;

    /// <summary>Decode any WIC-supported still image to BGRA, scaled down to fit the frame ring if needed.</summary>
    public static (byte[] Pixels, int W, int H) DecodeStill(string path)
    {
        using var decoder = Factory.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        var size = frame.Size;
        int w = size.Width, h = size.Height;
        IWICBitmapSource src = frame;
        IWICBitmapScaler? scaler = null;
        var scale = Math.Min(1.0, Math.Min((double)Limits.MaxWidth / w, (double)Limits.MaxHeight / h));
        if (scale < 1.0)
        {
            w = Math.Max(1, (int)(w * scale)); h = Math.Max(1, (int)(h * scale));
            scaler = Factory.CreateBitmapScaler();
            scaler.Initialize(frame, (uint)w, (uint)h, BitmapInterpolationMode.Fant);
            src = scaler;
        }
        using var conv = Factory.CreateFormatConverter();
        conv.Initialize(src, PixelFormat.Format32bppBGRA);
        var pixels = new byte[w * h * 4];
        conv.CopyPixels((uint)(w * 4), pixels);
        scaler?.Dispose();
        // Opaque wallpaper: flatten any transparency onto black.
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return (pixels, w, h);
    }

    public static int MetaInt(IWICMetadataQueryReader md, string name, int fallback)
    {
        try { return Convert.ToInt32(md.GetMetadataByName(name)); }
        catch { return fallback; }
    }
}

internal sealed class ImageSource : Source
{
    private byte[] _pixels = Array.Empty<byte>();
    private FrameSink? _sink;

    public ImageSource(string path) : base(path) { }
    public override string Kind => "image";

    public override void Prepare()
    {
        (_pixels, var w, var h) = Wic.DecodeStill(Path);
        Width = w; Height = h;
    }

    public override void Start(FrameSink sink)
    {
        _sink = sink;
        Republish();
    }

    public override void Republish() { if (_sink is not null && _pixels.Length > 0) _sink.Publish(_pixels, Width, Height); }
    public override void Dispose() => _pixels = Array.Empty<byte>();
}

/// <summary>
/// Animated GIF: frames are composed up front (offsets, transparency, disposal methods) so
/// playback is just a timed memcpy. fps = 0 plays each frame for its own GIF delay.
/// </summary>
internal sealed class GifSource : Source
{
    private const long MaxComposedBytes = 768L * 1024 * 1024;
    private readonly List<byte[]> _frames = new();
    private readonly List<int> _delaysMs = new();
    private readonly ManualResetEvent _running = new(true);
    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _retime = new(false);
    private Thread? _thread;
    private FrameSink? _sink;
    private volatile int _current;

    public GifSource(string path, double fps) : base(path) => Fps = fps;
    public override string Kind => "gif";

    public override void Prepare() => Compose();

    public override void Start(FrameSink sink)
    {
        _sink = sink;
        _thread = new Thread(Play) { IsBackground = true, Name = "gif" };
        _thread.Start();
    }

    private void Compose()
    {
        using var decoder = Wic.Factory.CreateDecoderFromFileName(Path, FileAccess.Read, DecodeOptions.CacheOnLoad);
        var count = (int)decoder.FrameCount;
        int w, h;
        using (var gmd = decoder.MetadataQueryReader)
        {
            w = Wic.MetaInt(gmd, "/logscrdesc/Width", 0);
            h = Wic.MetaInt(gmd, "/logscrdesc/Height", 0);
        }
        if (w <= 0 || h <= 0) { using var f0 = decoder.GetFrame(0); w = f0.Size.Width; h = f0.Size.Height; }
        if (w > Limits.MaxWidth || h > Limits.MaxHeight) throw new NotSupportedException($"GIF is {w}x{h}; max {Limits.MaxWidth}x{Limits.MaxHeight}");
        if ((long)w * h * 4 * count > MaxComposedBytes) throw new NotSupportedException($"GIF too large to pre-compose ({count} frames at {w}x{h})");
        Width = w; Height = h;

        var canvas = new uint[w * h];
        uint[]? saved = null;
        for (var i = 0; i < count; i++)
        {
            using var frame = decoder.GetFrame((uint)i);
            int left, top, disposal, delayCs;
            using (var md = frame.MetadataQueryReader)
            {
                left = Wic.MetaInt(md, "/imgdesc/Left", 0);
                top = Wic.MetaInt(md, "/imgdesc/Top", 0);
                disposal = Wic.MetaInt(md, "/grctlext/Disposal", 0);
                delayCs = Wic.MetaInt(md, "/grctlext/Delay", 10);
            }
            var fs = frame.Size;
            using var conv = Wic.Factory.CreateFormatConverter();
            conv.Initialize(frame, PixelFormat.Format32bppBGRA);
            var px = new uint[fs.Width * fs.Height];
            conv.CopyPixels((uint)(fs.Width * 4), System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()));

            if (disposal == 3) saved = (uint[])canvas.Clone();
            for (var y = 0; y < fs.Height; y++)
            {
                var cy = top + y;
                if (cy < 0 || cy >= h) continue;
                for (var x = 0; x < fs.Width; x++)
                {
                    var cx = left + x;
                    if (cx < 0 || cx >= w) continue;
                    var p = px[y * fs.Width + x];
                    if ((p >> 24) != 0) canvas[cy * w + cx] = p | 0xFF000000;
                }
            }
            var composed = new byte[w * h * 4];
            Buffer.BlockCopy(canvas, 0, composed, 0, composed.Length);
            _frames.Add(composed);
            // Browsers treat delays <= 1 cs as 10 cs; do the same.
            _delaysMs.Add(delayCs <= 1 ? 100 : delayCs * 10);

            if (disposal == 2)
            {
                for (var y = Math.Max(0, top); y < Math.Min(h, top + fs.Height); y++)
                    Array.Clear(canvas, y * w + Math.Max(0, left), Math.Max(0, Math.Min(w, left + fs.Width) - Math.Max(0, left)));
            }
            else if (disposal == 3 && saved is not null) canvas = saved;
        }
        FrameCount = _frames.Count;
        NativeFps = Math.Round(1000.0 / _delaysMs.Average(), 2);
    }

    private void Play()
    {
        using var pacer = new Pacer();
        var next = Stopwatch.GetTimestamp();
        var i = 0;
        while (!_stop.WaitOne(0))
        {
            if (WaitHandle.WaitAny(new WaitHandle[] { _stop, _running }) == 0) break;
            _current = i;
            unsafe { fixed (byte* p = _frames[i]) _sink!.Publish(p, Width, Height, Width * 4); }
            if (FrameCount == 1) { WaitHandle.WaitAny(new WaitHandle[] { _stop, _retime }); continue; }

            var ms = Fps > 0 ? 1000.0 / Fps : _delaysMs[i];
            next += (long)(ms * Stopwatch.Frequency / 1000);
            var now = Stopwatch.GetTimestamp();
            if (next < now - Stopwatch.Frequency / 4) next = now; // fell far behind (e.g. resumed): don't sprint
            // _retime fires on stop or fps change; either way re-evaluate immediately.
            if (!pacer.WaitUntil(next, _retime)) { if (_stop.WaitOne(0)) break; next = Stopwatch.GetTimestamp(); }
            i = (i + 1) % FrameCount;
        }
    }

    public override void SetFps(double fps) { base.SetFps(fps); _retime.Set(); }
    public override void Pause() => _running.Reset();
    public override void Resume() => _running.Set();

    public override void Republish()
    {
        if (_sink is null || _frames.Count == 0) return;
        unsafe { fixed (byte* p = _frames[_current]) _sink.Publish(p, Width, Height, Width * 4); }
    }

    public override void Dispose()
    {
        _stop.Set(); _retime.Set(); _running.Set();
        _thread?.Join(2000);
        _frames.Clear();
    }
}
