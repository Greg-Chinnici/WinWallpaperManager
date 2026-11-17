using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace WallpaperProtocol;

/// <summary>
/// Shared-memory frame ring (protocol v1, CPU frames).
///
/// Layout of <see cref="Names.FrameMap"/>:
/// <code>
///   0      ControlBlock (64 B)   magic, version, limits, ActiveWriter
///   64     FrameHeader  (64 B)   last published frame
///   4096   slot 0 .. slot 2      MaxWidth * MaxHeight * 4 bytes each, BGRA8, top-down
/// </code>
/// A producer writes slot (Seq % 3), then publishes the header, then sets
/// <see cref="Names.FrameReadyEvent"/>. The host only shows frames whose
/// <c>Writer</c> equals the control block's <c>ActiveWriter</c>, which the daemon
/// sets — that is how the daemon stays in charge of what is on screen.
/// </summary>
public sealed unsafe class FrameChannel : IDisposable
{
    public const uint Magic = 0x46505757; // "WWPF"
    public const int Slots = 3;
    public const int MaxWidth = 3840, MaxHeight = 2160;
    public const int SlotOffset = 4096;
    public const long SlotSize = (long)MaxWidth * MaxHeight * 4;
    public const long Capacity = SlotOffset + Slots * SlotSize;

    [StructLayout(LayoutKind.Sequential)]
    public struct ControlBlock
    {
        public uint Magic; public int Version, MaxWidth, MaxHeight, Slots;
        public int ActiveWriter;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FrameHeader
    {
        public int Writer, Width, Height, Stride, Slot, Format; // Format 0 = BGRA8
        public long Seq, Timestamp;                            // Timestamp = QueryPerformanceCounter
    }

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly EventWaitHandle _ready;
    private long _nextSeq;

    public EventWaitHandle FrameReady => _ready;
    private ControlBlock* Control => (ControlBlock*)_base;
    private FrameHeader* Header => (FrameHeader*)(_base + 64);

    private FrameChannel()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(Names.FrameMap, Capacity, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor(0, Capacity);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        _ready = new EventWaitHandle(false, EventResetMode.AutoReset, Names.FrameReadyEvent);
        if (Control->Magic != Magic)
        {
            *Control = new ControlBlock { Magic = Magic, Version = Names.ProtocolVersion, MaxWidth = MaxWidth, MaxHeight = MaxHeight, Slots = Slots };
        }
        else if (Control->Version != Names.ProtocolVersion)
        {
            throw new InvalidOperationException($"Frame channel version {Control->Version}, expected {Names.ProtocolVersion}");
        }
        _nextSeq = Header->Seq + 1;
    }

    public static FrameChannel Open() => new();

    public int ActiveWriter
    {
        get => Volatile.Read(ref Control->ActiveWriter);
        set => Volatile.Write(ref Control->ActiveWriter, value);
    }

    // ---------------- producer ----------------

    /// <summary>
    /// Returns a pointer to the slot the next frame should be written into.
    /// Rows are <paramref name="width"/>*4 bytes apart (tightly packed BGRA8, top-down).
    /// </summary>
    public byte* BeginFrame(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxWidth || height > MaxHeight)
            throw new ArgumentOutOfRangeException(nameof(width), $"Frame {width}x{height} exceeds {MaxWidth}x{MaxHeight}");
        _nextSeq = Math.Max(_nextSeq, Volatile.Read(ref Header->Seq) + 1);
        return SlotPointer((int)(_nextSeq % Slots));
    }

    /// <summary>Publish the frame written after <see cref="BeginFrame"/> and wake the host.</summary>
    public void Publish(int writer, int width, int height)
    {
        var seq = _nextSeq++;
        Thread.MemoryBarrier();
        var h = new FrameHeader
        {
            Writer = writer, Width = width, Height = height, Stride = width * 4,
            Slot = (int)(seq % Slots), Format = 0, Seq = seq, Timestamp = Stopwatch.GetTimestamp(),
        };
        // Seq last, so a reader that sees the new Seq sees the whole header.
        Header->Writer = h.Writer; Header->Width = h.Width; Header->Height = h.Height; Header->Stride = h.Stride;
        Header->Slot = h.Slot; Header->Format = h.Format; Header->Timestamp = h.Timestamp;
        Thread.MemoryBarrier();
        Volatile.Write(ref Header->Seq, h.Seq);
        _ready.Set();
    }

    // ---------------- consumer (host) ----------------

    public FrameHeader ReadHeader()
    {
        var seq = Volatile.Read(ref Header->Seq);
        Thread.MemoryBarrier();
        var h = *Header;
        h.Seq = seq;
        return h;
    }

    public byte* SlotPointer(int slot) => _base + SlotOffset + slot * SlotSize;

    /// <summary>
    /// After copying a frame out, check it wasn't overwritten meanwhile. The slot for
    /// <paramref name="seq"/> is reused by seq+3, whose writing can start once seq+2 is published.
    /// </summary>
    public bool StillValid(long seq) => Volatile.Read(ref Header->Seq) < seq + 2;

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
        _ready.Dispose();
    }
}
