using System.Diagnostics;
using Vortice.MediaFoundation;
using WallpaperProtocol;

namespace WallpaperDaemon.Sources;

/// <summary>
/// Video via Media Foundation's source reader, decoded to RGB32 on the CPU and paced by
/// presentation timestamps. Loops forever, no audio. fps = 0 plays every frame; otherwise
/// frames are dropped to stay at or under the requested rate.
///
/// POC limitation: software decode + CPU colour conversion. The production path should give
/// the reader a DXGI device manager so frames stay on the GPU (shared texture + fence).
/// </summary>
internal sealed class VideoSource : Source
{
    private readonly ManualResetEvent _running = new(true);
    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private FrameSink? _sink;
    private Exception? _startError;
    private readonly ManualResetEventSlim _started = new(false);

    public long FramesShown, FramesSkipped;

    public VideoSource(string path, double fps) : base(path) => Fps = fps;
    public override string Kind => "video";

    public override void Start(FrameSink sink)
    {
        _sink = sink;
        _thread = new Thread(Play) { IsBackground = true, Name = "video" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(10));
        if (_startError is not null) throw _startError;
    }

    private IMFSourceReader Open()
    {
        using var attrs = MediaFactory.MFCreateAttributes(2);
        attrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
        var reader = MediaFactory.MFCreateSourceReaderFromURL(Path, attrs);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);

        using var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        var size = cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        Width = (int)(size >> 32); Height = (int)(size & 0xFFFFFFFF);
        try
        {
            var rate = cur.GetUInt64(MediaTypeAttributeKeys.FrameRate);
            var num = (double)(rate >> 32); var den = (double)(rate & 0xFFFFFFFF);
            NativeFps = den > 0 ? Math.Round(num / den, 2) : 0;
        }
        catch { NativeFps = 0; }
        if (Width > FrameChannel.MaxWidth || Height > FrameChannel.MaxHeight)
            throw new NotSupportedException($"Video is {Width}x{Height}; max {FrameChannel.MaxWidth}x{FrameChannel.MaxHeight}");
        return reader;
    }

    private unsafe void Play()
    {
        IMFSourceReader reader;
        try { reader = Open(); }
        catch (Exception ex) { _startError = ex; _started.Set(); return; }
        _started.Set();

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
                var now = Stopwatch.GetTimestamp();
                if (now - due > freq / 10) { FramesSkipped++; continue; } // >100 ms late: drop to catch up
                if (!pacer.WaitUntil(due, _wake)) { if (_stop.WaitOne(0)) break; }

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
        }
        catch (Exception ex) { Console.Error.WriteLine("video playback failed: " + ex); }
        finally { reader.Dispose(); }
    }

    public override void SetFps(double fps) { base.SetFps(fps); _wake.Set(); }
    public override void Pause() => _running.Reset();
    public override void Resume() => _running.Set();

    public override void Republish()
    {
        // The last frame is still in the ring (the daemon keeps it mapped); just wake the host.
        _sink?.Poke();
    }

    public override void Dispose()
    {
        _stop.Set(); _wake.Set(); _running.Set();
        _thread?.Join(3000);
    }
}
