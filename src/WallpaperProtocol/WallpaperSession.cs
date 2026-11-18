using System.Text.Json.Nodes;

namespace WallpaperProtocol;

/// <summary>What you ask the daemon for.</summary>
public sealed record SessionOptions
{
    /// <summary>Shown in the tray ("Custom renderer: …").</summary>
    public string Name { get; init; } = "Custom renderer";
    public Layer Layer { get; init; } = Layer.Desktop;
    public Transport Transport { get; init; } = Transport.Cpu;
    /// <summary>Frame size. The host scales it to every monitor.</summary>
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    /// <summary>Default: opaque on the desktop layer, premultiplied on the overlay layer.</summary>
    public AlphaMode? Alpha { get; init; }
    /// <summary>Informational (shown in status): "d3d11", "d3d12", "vulkan", "cpu", …</summary>
    public string? Api { get; init; }
}

/// <summary>
/// A connection to the WinWallpaper daemon that owns one layer until disposed or revoked.
/// <code>
/// using var s = await WallpaperSession.ConnectAsync(new() { Name = "Clock", Layer = Layer.Overlay });
/// s.Cpu!.Submit(bgra);                       // CPU transport
/// // GPU transport: see WallpaperProtocol.D3D11 / .D3D12 / .Vulkan
/// </code>
/// See docs/custom-render-outputs.md.
/// </summary>
public sealed class WallpaperSession : IDisposable
{
    private readonly JsonPipe _pipe;
    private readonly CancellationTokenSource _cts = new();

    public SessionDescriptor Descriptor { get; }
    /// <summary>Frame ring + control block. For GPU sessions only the control block is used.</summary>
    public SessionMap Map { get; }
    /// <summary>Non-null for <see cref="Transport.Cpu"/>.</summary>
    public CpuFrameWriter? Cpu { get; }
    /// <summary>Non-null for <see cref="Transport.Gpu"/>: handles to import into your graphics API.</summary>
    public GpuResources? Gpu => Descriptor.Gpu;

    /// <summary>True once the user picked something else for this layer (or the daemon went away).</summary>
    public bool Revoked { get; private set; }
    public event Action? RevokedChanged;

    private WallpaperSession(JsonPipe pipe, SessionDescriptor d)
    {
        _pipe = pipe;
        Descriptor = d;
        Map = SessionMap.Open(d.MapName, d.ReadyEvent);
        if (d.Transport == Transport.Cpu) Cpu = new CpuFrameWriter(Map);
        _ = WatchAsync();
    }

    public static async Task<WallpaperSession> ConnectAsync(SessionOptions options, int timeoutMs = 3000, CancellationToken ct = default)
    {
        var pipe = await JsonPipe.ConnectAsync(Names.DaemonPipe, timeoutMs, ct).ConfigureAwait(false);
        try
        {
            var alpha = options.Alpha ?? (options.Layer == Layer.Overlay ? AlphaMode.Premultiplied : AlphaMode.Opaque);
            var resp = await pipe.RequestAsync(new JsonObject
            {
                ["cmd"] = "external.begin",
                ["name"] = options.Name,
                ["layer"] = options.Layer.ToWire(),
                ["transport"] = options.Transport.ToWire(),
                ["width"] = options.Width,
                ["height"] = options.Height,
                ["alpha"] = alpha.ToWire(),
                ["api"] = options.Api ?? options.Transport.ToWire(),
            }, ct).ConfigureAwait(false);
            if (resp["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException("Daemon refused: " + resp["error"]);
            return new WallpaperSession(pipe, SessionDescriptor.FromJson((JsonObject)resp["session"]!));
        }
        catch { pipe.Dispose(); throw; }
    }

    /// <summary>
    /// GPU transport: the slot to render the next frame into, or -1 if the host still needs it.
    /// <paramref name="consumedFenceValue"/> is the consumed fence's current completed value.
    /// </summary>
    public int TryBeginGpuFrame(ulong consumedFenceValue) => Map.TryBeginGpuFrame(consumedFenceValue);

    /// <summary>GPU transport: value to signal on the produced fence once the current frame is rendered.</summary>
    public ulong ProducedFenceValue => Map.ProducedFenceValue;

    /// <summary>GPU transport: after queueing the produced-fence signal, tell the host the frame exists.</summary>
    public void PublishGpuFrame() => Map.Publish();

    private async Task WatchAsync()
    {
        try
        {
            while (await _pipe.ReceiveAsync(_cts.Token).ConfigureAwait(false) is { } msg)
                if ((string?)msg["event"] == "external.ended") break;
        }
        catch { /* pipe closed or cancelled */ }
        if (_cts.IsCancellationRequested) return;
        Revoked = true;
        RevokedChanged?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pipe.Dispose(); // closing the pipe ends the session; the daemon restores what was there before
        Map.Dispose();
    }
}

/// <summary>CPU transport writer: BGRA8, top-down, rows <see cref="Stride"/> bytes apart.</summary>
public sealed unsafe class CpuFrameWriter
{
    private readonly SessionMap _map;
    internal CpuFrameWriter(SessionMap map) => _map = map;

    public int Width => _map.Width;
    public int Height => _map.Height;
    public int Stride => _map.Stride;

    /// <summary>Copy a tightly packed Width×Height BGRA8 frame in and publish it.</summary>
    public void Submit(ReadOnlySpan<byte> bgra)
    {
        var size = Stride * Height;
        if (bgra.Length < size) throw new ArgumentException($"Expected {size} bytes ({Width}x{Height} BGRA8)");
        bgra[..size].CopyTo(new Span<byte>(_map.NextSlotPointer, size));
        _map.Publish();
    }

    /// <summary>Zero-copy: render straight into the slot, then call <see cref="Publish"/>.</summary>
    public Span<byte> BeginFrame() => new(_map.NextSlotPointer, Stride * Height);
    public void Publish() => _map.Publish();
}
