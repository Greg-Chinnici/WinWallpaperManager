using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace DesktopAttach;

/// <summary>
/// Spike frame transport (doc architecture §IPC): a named memory-mapped file with a
/// 64-byte header + two pixel slots, and a named auto-reset event for "frame ready".
/// Producer writes slot (seq % 2), then publishes the header, then signals.
/// Not tear-proof under heavy host stalls — a real protocol needs a seqlock or 3 slots.
/// </summary>
internal sealed unsafe class FrameChannel : IDisposable
{
    public const string MapName = @"Local\DesktopAttach.Frame";
    public const string EventName = @"Local\DesktopAttach.FrameReady";
    private const int HeaderSize = 64, MaxW = 3840, MaxH = 2160;
    private const long SlotSize = (long)MaxW * MaxH * 4;
    private const long Capacity = HeaderSize + 2 * SlotSize;

    [StructLayout(LayoutKind.Sequential)]
    public struct Header { public int Magic, Width, Height, Stride; public long Seq, ProducedAt; public int Slot; }

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    public readonly EventWaitHandle FrameReady;

    private FrameChannel()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, Capacity, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor(0, Capacity);
        byte* p = null; _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        FrameReady = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    public static FrameChannel Open() => new();

    public Header ReadHeader() { Thread.MemoryBarrier(); return *(Header*)_base; }
    public byte* SlotPointer(int slot) => _base + HeaderSize + slot * SlotSize;

    /// <summary>Producer: fill a frame and publish it.</summary>
    public void Publish(long seq, int w, int h, Action<IntPtr, int> fill)
    {
        var slot = (int)(seq % 2);
        fill((nint)SlotPointer(slot), w * 4);
        var hdr = new Header { Magic = 0x57504652, Width = w, Height = h, Stride = w * 4, Seq = seq, Slot = slot, ProducedAt = Stopwatch.GetTimestamp() };
        Thread.MemoryBarrier();
        *(Header*)_base = hdr;
        Thread.MemoryBarrier();
        FrameReady.Set();
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose(); _mmf.Dispose(); FrameReady.Dispose();
    }

    /// <summary>`produce` command: a daemon stand-in that pushes CPU frames at a fixed rate.</summary>
    public static int RunProducer(int w, int h, int fps, int seconds)
    {
        [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);
        timeBeginPeriod(1);
        using var ch = Open();
        var period = Stopwatch.Frequency / fps;
        var next = Stopwatch.GetTimestamp();
        var end = next + seconds * Stopwatch.Frequency;
        long seq = 0; double fillMs = 0;
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        while (Stopwatch.GetTimestamp() < end)
        {
            var t0 = Stopwatch.GetTimestamp();
            var s = seq;
            ch.Publish(seq++, w, h, (ptr, stride) =>
            {
                // Background colour drifts; a 64-px vertical bar sweeps across.
                var bg = 0xFF000000u | (uint)((s * 2) & 0xFF) << 16 | 0x20u << 8 | 0x40u;
                var bar = (int)(s * 16 % w);
                for (var y = 0; y < h; y++)
                {
                    var row = new Span<uint>((void*)(ptr + y * stride), w);
                    row.Fill(bg);
                    row.Slice(bar, Math.Min(64, w - bar)).Fill(0xFFFFFFFF);
                }
            });
            fillMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            next += period;
            while (Stopwatch.GetTimestamp() < next) Thread.Sleep(1);
        }
        proc.Refresh();
        var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        Attachment.Log($"producer: {seq} frames {w}x{h}@{fps} in {seconds}s, avg fill {fillMs / seq:0.00} ms, CPU {cpu / (seconds * 10.0):0.0}% of one core");
        return 0;
    }
}
