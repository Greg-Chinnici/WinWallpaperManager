using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WallpaperProtocol;

namespace WallpaperDaemon.Sources;

/// <summary>Size limit for the daemon's own decoded media (larger images are downscaled).</summary>
internal static class Limits { public const int MaxWidth = 3840, MaxHeight = 2160; }

/// <summary>
/// Where sources put frames: the daemon's CPU session on the desktop layer. The session can be
/// swapped underneath (e.g. after a host restart) without the source noticing.
/// </summary>
internal sealed unsafe class FrameSink
{
    private readonly object _gate = new();
    private SessionMap? _map;
    public long Published;

    public void Attach(SessionMap? map) { lock (_gate) { _map = map; } }

    /// <summary>Copy rows (any stride, may be negative for bottom-up) into the session and publish.</summary>
    public void Publish(byte* src, int width, int height, int srcStride)
    {
        lock (_gate)
        {
            if (_map is not { } map) return;
            if (width != map.Width || height != map.Height) throw new InvalidOperationException($"Frame {width}x{height} doesn't match session {map.Width}x{map.Height}");
            var dst = map.NextSlotPointer;
            var row = width * 4;
            if (srcStride == row && map.Stride == row) Buffer.MemoryCopy(src, dst, (long)row * height, (long)row * height);
            else for (var y = 0; y < height; y++) Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * map.Stride, row, row);
            map.Publish();
            Published++;
        }
    }

    /// <summary>Re-signal the host without writing (the last frame is still in the session).</summary>
    public void Poke() { lock (_gate) { _map?.Ready.Set(); } }

    public void Publish(ReadOnlySpan<byte> bgra, int width, int height)
    {
        fixed (byte* p = bgra) Publish(p, width, height, width * 4);
    }
}

/// <summary>A wallpaper source. Implementations own a playback thread if they animate.</summary>
internal abstract class Source : IDisposable
{
    public abstract string Kind { get; }
    public string Path { get; }
    public int Width { get; protected set; }
    public int Height { get; protected set; }
    /// <summary>Requested fps; 0 = the content's own timing.</summary>
    public double Fps { get; protected set; }
    /// <summary>The content's own frame rate (0 for still images).</summary>
    public double NativeFps { get; protected set; }
    public int FrameCount { get; protected set; } = 1;

    protected Source(string path) => Path = path;

    /// <summary>Decode/open enough to know <see cref="Width"/> and <see cref="Height"/> (throws on bad files).</summary>
    public abstract void Prepare();
    /// <summary>Start publishing into <paramref name="sink"/> (sized Width x Height).</summary>
    public abstract void Start(FrameSink sink);
    /// <summary>True if <see cref="Prepare"/> chose a GPU path: start it with <see cref="StartGpu"/> on a GPU session.</summary>
    public virtual bool UsesGpu => false;
    public virtual void StartGpu(GpuSink sink) => throw new NotSupportedException();
    /// <summary>"gpu" or "cpu": where frames are decoded/produced.</summary>
    public virtual string Decode => "cpu";
    public virtual void SetFps(double fps) => Fps = fps;
    public virtual void Pause() { }
    public virtual void Resume() { }
    /// <summary>Publish the current frame again (e.g. the host restarted).</summary>
    public abstract void Republish();
    public abstract void Dispose();
}

/// <summary>
/// Event-driven pacing: a high-resolution waitable timer (no timeBeginPeriod, no sleep loops).
/// WaitUntil returns false if <paramref name="cancel"/> fired first.
/// </summary>
internal sealed class Pacer : IDisposable
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attrs, string? name, uint flags, uint access);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetWaitableTimer(nint h, ref long dueTime, int period, nint cb, nint arg, bool resume);

    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(nint h) => SafeWaitHandle = new SafeWaitHandle(h, ownsHandle: true);
    }

    private readonly TimerWaitHandle _timer;

    public Pacer()
    {
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003
        var h = CreateWaitableTimerExW(0, null, 0x2, 0x1F0003);
        if (h == 0) h = CreateWaitableTimerExW(0, null, 0, 0x1F0003);
        _timer = new TimerWaitHandle(h);
    }

    public bool WaitUntil(long qpcTarget, WaitHandle cancel)
    {
        var remaining = qpcTarget - System.Diagnostics.Stopwatch.GetTimestamp();
        if (remaining <= 0) return !cancel.WaitOne(0);
        var due = -(long)(remaining * 10_000_000.0 / System.Diagnostics.Stopwatch.Frequency); // relative, 100 ns units
        SetWaitableTimer(_timer.SafeWaitHandle.DangerousGetHandle(), ref due, 0, 0, 0, false);
        return WaitHandle.WaitAny(new[] { cancel, _timer }) == 1;
    }

    public void Dispose() => _timer.Dispose();
}
