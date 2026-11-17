using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WallpaperProtocol;

namespace WallpaperDaemon.Sources;

/// <summary>Where sources put frames: the shared ring, as the daemon's writer.</summary>
internal sealed unsafe class FrameSink
{
    public const int DaemonWriter = 1;
    private readonly FrameChannel _channel;
    private readonly object _gate = new();
    public long Published;

    public FrameSink(FrameChannel channel) => _channel = channel;

    /// <summary>Copy rows (any stride, may be negative for bottom-up) into the ring and publish.</summary>
    public void Publish(byte* src, int width, int height, int srcStride)
    {
        lock (_gate)
        {
            var dst = _channel.BeginFrame(width, height);
            var row = width * 4;
            if (srcStride == row) Buffer.MemoryCopy(src, dst, (long)row * height, (long)row * height);
            else for (var y = 0; y < height; y++) Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * row, row, row);
            _channel.Publish(DaemonWriter, width, height);
            Published++;
        }
    }

    /// <summary>Re-signal the host without writing (the last frame is still in the ring).</summary>
    public void Poke() => _channel.FrameReady.Set();

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

    public abstract void Start(FrameSink sink);
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
