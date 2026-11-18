using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace WallpaperDaemon.Sources;

/// <summary>
/// Video via Media Foundation's source reader, paced by presentation timestamps. Loops forever,
/// no audio. fps = 0 plays every frame; otherwise frames are dropped to stay at or under the rate.
///
/// Two decode paths, chosen in <see cref="Prepare"/>:
///   GPU (default): hardware decode (DXVA) on a D3D11 device on the host's GPU, converted to BGRA
///        by MF's GPU video processor, copied texture-to-texture into a GPU session. No pixel ever
///        touches system memory.
///   CPU (fallback): software decode to RGB32, copied into a CPU session. Used when the GPU path
///        can't be set up or doesn't hand back GPU textures.
/// </summary>
internal sealed class VideoSource : Source
{
    private readonly ManualResetEvent _running = new(true);
    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _wake = new(false);
    private readonly Func<VideoGpu?>? _gpuProvider;
    private Thread? _thread;
    private FrameSink? _sink;
    private GpuSink? _gpuSink;
    private IMFSourceReader? _reader;
    private bool _gpu;

    public long FramesShown, FramesSkipped;

    /// <param name="gpuProvider">Returns the shared decode device, or null to force CPU decoding.</param>
    public VideoSource(string path, double fps, Func<VideoGpu?>? gpuProvider) : base(path)
    {
        Fps = fps;
        _gpuProvider = gpuProvider;
    }

    public override string Kind => "video";
    public override bool UsesGpu => _gpu;
    public override string Decode => _gpu ? "gpu" : "cpu";

    /// <summary>Open the file now (a bad file fails the request), pick the decode path, learn the size.</summary>
    public override void Prepare()
    {
        if (_gpuProvider?.Invoke() is { } gpu)
        {
            try
            {
                _reader = Open(gpu);
                VerifyGpuOutput(_reader);
                _gpu = true;
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"GPU decode unavailable for {System.IO.Path.GetFileName(Path)} ({ex.Message}); using CPU decode");
                _reader?.Dispose();
                _reader = null;
            }
        }
        _reader = Open(null);
    }

    private IMFSourceReader Open(VideoGpu? gpu)
    {
        using var attrs = MediaFactory.MFCreateAttributes(3);
        if (gpu is not null)
        {
            attrs.Set(SourceReaderAttributeKeys.D3DManager, gpu.Manager);           // hardware decode on our device
            attrs.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);  // GPU colour conversion
        }
        else attrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);         // CPU colour conversion

        var reader = MediaFactory.MFCreateSourceReaderFromURL(Path, attrs);
        try
        {
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            // GPU: ARGB32 = DXGI_FORMAT_B8G8R8A8_UNORM, the session textures' format, so frames copy
            // straight across. CPU: RGB32 (alpha undefined; the desktop layer ignores it anyway).
            type.Set(MediaTypeAttributeKeys.Subtype, gpu is not null ? VideoFormatGuids.Argb32 : VideoFormatGuids.Rgb32);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);

            using var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            var size = cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            Width = (int)(size >> 32); Height = (int)(size & 0xFFFFFFFF);
            CropToDisplayAperture(cur);
            try
            {
                var rate = cur.GetUInt64(MediaTypeAttributeKeys.FrameRate);
                var num = (double)(rate >> 32); var den = (double)(rate & 0xFFFFFFFF);
                NativeFps = den > 0 ? Math.Round(num / den, 2) : 0;
            }
            catch { NativeFps = 0; }
            if (Width > Limits.MaxWidth || Height > Limits.MaxHeight)
                throw new NotSupportedException($"Video is {Width}x{Height}; max {Limits.MaxWidth}x{Limits.MaxHeight}");
            return reader;
        }
        catch { reader.Dispose(); throw; }
    }

    /// <summary>Decoders pad frames (1080 -> 1088); the display aperture says what to actually show.</summary>
    private void CropToDisplayAperture(IMFMediaType type)
    {
        try
        {
            var blob = type.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture); // MFVideoArea: MFOffset x, y; SIZE
            if (blob.Length < 16) return;
            int cx = BitConverter.ToInt32(blob, 8), cy = BitConverter.ToInt32(blob, 12);
            if (cx > 0 && cy > 0 && cx <= Width && cy <= Height) { Width = cx; Height = cy; }
        }
        catch { /* no aperture attribute: the frame size is the display size */ }
    }

    /// <summary>Decode one frame and make sure it really is a GPU texture, then rewind.</summary>
    private static void VerifyGpuOutput(IMFSourceReader reader)
    {
        for (var tries = 0; tries < 30; tries++)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out _);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
            if (sample is null) continue;
            using var buffer = sample.GetBufferByIndex(0);
            using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>()
                ?? throw new NotSupportedException("decoder returned system-memory frames");
            reader.SetCurrentPosition(0);
            return;
        }
        throw new NotSupportedException("no video frames decoded");
    }

    public override void Start(FrameSink sink) { _sink = sink; StartThread(); }
    public override void StartGpu(GpuSink sink) { _gpuSink = sink; StartThread(); }

    private void StartThread()
    {
        _thread = new Thread(Play) { IsBackground = true, Name = "video" };
        _thread.Start();
    }

    private unsafe void Play()
    {
        var reader = _reader!;
        using var pacer = new Pacer();
        long clockStart = Stopwatch.GetTimestamp(); // QPC time of timestamp 0 in the current loop
        long nextAllowedTs = 0;                     // fps cap, in 100 ns units
        var freq = Stopwatch.Frequency;
        try
        {
            while (!_stop.WaitOne(0))
            {
                if (!_running.WaitOne(0))
                {
                    var pausedAt = Stopwatch.GetTimestamp();
                    if (WaitHandle.WaitAny(new WaitHandle[] { _stop, _running }) == 0) break;
                    clockStart += Stopwatch.GetTimestamp() - pausedAt; // resume where we left off
                }

                using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var ts);
                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    reader.SetCurrentPosition(0);
                    clockStart = Stopwatch.GetTimestamp();
                    nextAllowedTs = 0;
                    continue;
                }
                if (sample is null) continue;

                var fps = Fps;
                if (fps > 0)
                {
                    if (ts < nextAllowedTs) { FramesSkipped++; continue; }
                    var period = (long)(10_000_000 / fps);
                    nextAllowedTs = Math.Max(nextAllowedTs + period, ts - period); // no drift, no bursts
                }

                var due = clockStart + ts * freq / 10_000_000;
                if (Stopwatch.GetTimestamp() - due > freq / 10) { FramesSkipped++; continue; } // >100 ms late: drop
                if (!pacer.WaitUntil(due, _wake)) { if (_stop.WaitOne(0)) break; }

                if (_gpu) PublishGpu(sample); else PublishCpu(sample);
            }
        }
        catch (Exception ex) { Log.Write("video playback failed: " + ex); }
        finally { reader.Dispose(); }
    }

    private void PublishGpu(IMFSample sample)
    {
        using var buffer = sample.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
        var ptr = dxgi.GetResource(typeof(ID3D11Texture2D).GUID); // AddRef'd; the wrapper releases it
        using var texture = new ID3D11Texture2D(ptr);
        if (_gpuSink!.TryPublish(texture, dxgi.SubresourceIndex, Width, Height)) FramesShown++;
        else FramesSkipped++; // the host still holds that slot
    }

    private unsafe void PublishCpu(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        using var buffer2d = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        if (buffer2d is not null)
        {
            buffer2d.Lock2D(out var scan0, out var pitch); // scan0 = top row; pitch < 0 for bottom-up
            try { _sink!.Publish((byte*)scan0, Width, Height, pitch); }
            finally { buffer2d.Unlock2D(); }
        }
        else
        {
            buffer.Lock(out var ptr, out _, out _);
            try { _sink!.Publish((byte*)ptr, Width, Height, Width * 4); }
            finally { buffer.Unlock(); }
        }
        FramesShown++;
    }

    public override void SetFps(double fps) { base.SetFps(fps); _wake.Set(); }
    public override void Pause() => _running.Reset();
    public override void Resume() => _running.Set();

    /// <summary>The last frame is still in the session (ring slot or texture); just wake the host.</summary>
    public override void Republish()
    {
        _sink?.Poke();
        _gpuSink?.Poke();
    }

    public override void Dispose()
    {
        _stop.Set(); _wake.Set(); _running.Set();
        _thread?.Join(3000);
        if (_thread is null) _reader?.Dispose(); // prepared but never started
    }
}
