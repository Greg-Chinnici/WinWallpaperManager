using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WallpaperProtocol;

namespace GpuRenderer;

/// <summary>
/// CPU transport, for comparison: fills a full BGRA frame straight into the session's shared
/// memory each tick (background + bar). About the cheapest a CPU producer can be.
/// </summary>
internal sealed class CpuBackend : IBackend
{
    private readonly Look _look;
    private readonly CpuFrameWriter _cpu;
    private readonly uint _bg, _bar;

    public CpuBackend(WallpaperSession session, Look look)
    {
        _look = look;
        _cpu = session.Cpu!;
        _bg = Pack(look.Background);
        _bar = Pack(look.Bar);
    }

    private static uint Pack((float R, float G, float B, float A) c) =>
        (uint)(c.A * 255) << 24 | (uint)(c.R * 255) << 16 | (uint)(c.G * 255) << 8 | (uint)(c.B * 255);

    public bool RenderFrame(double t)
    {
        var px = MemoryMarshal.Cast<byte, uint>(_cpu.BeginFrame());
        int w = _cpu.Width, h = _cpu.Height, x = _look.BarX(w, t), bw = _look.BarWidth(w);
        px.Fill(_bg);
        for (var y = 0; y < h; y++) px.Slice(y * w + x, bw).Fill(_bar);
        _cpu.Publish();
        return true;
    }

    public void Dispose() { }
}

/// <summary>High-resolution waitable timer (no timeBeginPeriod, no busy waiting).</summary>
internal sealed class HighResPacer : IDisposable
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attrs, string? name, uint flags, uint access);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetWaitableTimer(nint h, ref long dueTime, int period, nint cb, nint arg, bool resume);

    private sealed class TimerHandle : WaitHandle { public TimerHandle(nint h) => SafeWaitHandle = new SafeWaitHandle(h, true); }
    private readonly TimerHandle _timer = new(CreateWaitableTimerExW(0, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003));

    public void WaitUntil(long qpcDue, WaitHandle cancel)
    {
        var remaining = qpcDue - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        var due = -(long)(remaining * 10_000_000.0 / Stopwatch.Frequency);
        SetWaitableTimer(_timer.SafeWaitHandle.DangerousGetHandle(), ref due, 0, 0, 0, false);
        WaitHandle.WaitAny(new[] { cancel, _timer });
    }

    public void Dispose() => _timer.Dispose();
}
