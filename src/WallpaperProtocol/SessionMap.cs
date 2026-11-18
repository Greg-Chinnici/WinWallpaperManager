using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace WallpaperProtocol;

/// <summary>
/// Shared memory of one session (protocol v2). The host creates it; the producer opens it.
/// <code>
///   0     Header (64 B)            see <see cref="Header"/>
///   4096  slot 0 .. slot N-1      CPU transport only: Height * Stride bytes each, BGRA8, top-down
/// </code>
/// Publishing a frame (both transports): write the pixels (CPU: into slot Seq % Slots;
/// GPU: render into texture Seq % Slots and signal the produced fence to Seq + 1), then
/// write Slot and Timestamp, then Seq last, then set the ready event.
/// </summary>
public sealed unsafe class SessionMap : IDisposable
{
    public const uint MagicValue = 0x32535757; // "WWS2"
    public const int DefaultSlots = 3;
    public const int SlotOffset = 4096;
    public const int MaxWidth = 7680, MaxHeight = 4320;

    [StructLayout(LayoutKind.Sequential)]
    public struct Header
    {
        public uint Magic;          //  0
        public int Version;         //  4
        public int Transport;       //  8  0 = CPU, 1 = GPU
        public int Width;           // 12
        public int Height;          // 16
        public int Stride;          // 20  CPU: bytes per row (Width * 4)
        public int Slots;           // 24
        public int Alpha;           // 28  0 = opaque, 1 = premultiplied
        public long Seq;            // 32  last published frame, -1 = none yet
        public int Slot;            // 40  slot of that frame
        public int Reserved;        // 44
        public long Timestamp;      // 48  QueryPerformanceCounter, informational
    }

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private long _nextSeq;

    public EventWaitHandle Ready { get; }
    public string MapName { get; }
    public string EventName { get; }

    private Header* H => (Header*)_base;
    public int Width => H->Width;
    public int Height => H->Height;
    public int Stride => H->Stride;
    public int Slots => H->Slots;
    public Transport Transport => (Transport)H->Transport;
    public AlphaMode Alpha => (AlphaMode)H->Alpha;

    private SessionMap(MemoryMappedFile mmf, long capacity, string mapName, string eventName)
    {
        _mmf = mmf;
        _view = mmf.CreateViewAccessor(0, capacity);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        MapName = mapName;
        EventName = eventName;
        Ready = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
    }

    public static long CapacityFor(Transport t, int width, int height, int slots) =>
        SlotOffset + (t == Transport.Cpu ? (long)slots * width * 4 * height : 0);

    /// <summary>Host side: create the map and event for a new session.</summary>
    public static SessionMap Create(int session, Transport transport, int width, int height, AlphaMode alpha, int slots = DefaultSlots)
    {
        if (width <= 0 || height <= 0 || width > MaxWidth || height > MaxHeight)
            throw new ArgumentOutOfRangeException(nameof(width), $"Session size {width}x{height} must be within {MaxWidth}x{MaxHeight}");
        var cap = CapacityFor(transport, width, height, slots);
        var mapName = Names.SessionMap(Environment.ProcessId, session); // the host creates every map
        var mmf = MemoryMappedFile.CreateNew(mapName, cap, MemoryMappedFileAccess.ReadWrite);
        var map = new SessionMap(mmf, cap, mapName, Names.SessionReadyEvent(Environment.ProcessId, session));
        *map.H = new Header
        {
            Magic = MagicValue, Version = Names.ProtocolVersion, Transport = (int)transport,
            Width = width, Height = height, Stride = width * 4, Slots = slots, Alpha = (int)alpha, Seq = -1,
        };
        return map;
    }

    /// <summary>Producer side: open a session's map by the names the daemon handed out.</summary>
    public static SessionMap Open(string mapName, string eventName)
    {
        var mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite);
        // Read the header first to learn the real size.
        long cap;
        using (var probe = mmf.CreateViewAccessor(0, SlotOffset))
        {
            probe.Read(0, out Header h);
            if (h.Magic != MagicValue || h.Version != Names.ProtocolVersion)
                throw new InvalidOperationException($"Not a v{Names.ProtocolVersion} session map (magic 0x{h.Magic:X8}, version {h.Version})");
            cap = CapacityFor((Transport)h.Transport, h.Width, h.Height, h.Slots);
        }
        var map = new SessionMap(mmf, cap, mapName, eventName);
        map._nextSeq = map.H->Seq + 1;
        return map;
    }

    // ---------------- producer ----------------

    /// <summary>Sequence number and slot of the frame about to be produced.</summary>
    public long NextSeq => _nextSeq;
    public int NextSlot => (int)(_nextSeq % Slots);

    /// <summary>CPU transport: pointer to the slot for the next frame (rows <see cref="Stride"/> bytes apart).</summary>
    public byte* NextSlotPointer => SlotPointer(NextSlot);

    /// <summary>
    /// GPU transport: the slot to render the next frame into, or -1 if the host still needs it.
    /// <paramref name="consumedFenceValue"/> is the consumed fence's current completed value.
    /// </summary>
    public int TryBeginGpuFrame(ulong consumedFenceValue)
    {
        var seq = _nextSeq;
        return seq < Slots || consumedFenceValue >= (ulong)(seq - Slots + 1) ? NextSlot : -1;
    }

    /// <summary>GPU transport: value to signal on the produced fence once the next frame is rendered.</summary>
    public ulong ProducedFenceValue => (ulong)_nextSeq + 1;

    /// <summary>Publish the frame for <see cref="NextSeq"/> (its pixels / GPU signal must already be done) and wake the host.</summary>
    public void Publish()
    {
        var seq = _nextSeq++;
        H->Slot = (int)(seq % Slots);
        H->Timestamp = Stopwatch.GetTimestamp();
        Thread.MemoryBarrier();
        Volatile.Write(ref H->Seq, seq); // last: a new Seq means "header complete"
        Ready.Set();
    }

    // ---------------- consumer (host) ----------------

    public (long Seq, int Slot) ReadLatest()
    {
        var seq = Volatile.Read(ref H->Seq);
        Thread.MemoryBarrier();
        return (seq, H->Slot);
    }

    public byte* SlotPointer(int slot) => _base + SlotOffset + (long)slot * Stride * Height;

    /// <summary>
    /// CPU transport: after copying frame <paramref name="seq"/> out, check it wasn't overwritten.
    /// Its slot is reused by seq + Slots, whose writing can start once seq + Slots - 1 is published.
    /// </summary>
    public bool StillValid(long seq) => Volatile.Read(ref H->Seq) < seq + Slots - 1;

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
        Ready.Dispose();
    }
}
