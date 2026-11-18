namespace WallpaperProtocol;

/// <summary>Well-known kernel object names. All are per-session ("Local\") and versioned.</summary>
public static class Names
{
    public const int ProtocolVersion = 2;

    /// <summary>Named pipe for the tray and external apps (JSON lines). Path: \\.\pipe\WinWallpaper.Daemon.v2</summary>
    public const string DaemonPipe = "WinWallpaper.Daemon.v2";

    /// <summary>Named pipe the daemon uses to control the host (JSON lines).</summary>
    public const string HostPipe = "WinWallpaper.Host.v2";

    /// <summary>
    /// Shared-memory control block (and CPU frame slots) of one session. Includes the host's process
    /// ID so a restarted host never collides with maps a producer still holds from the old one.
    /// Producers don't build these: they get the exact names in the session descriptor.
    /// </summary>
    public static string SessionMap(int hostPid, int session) => $@"Local\WinWallpaper.v2.H{hostPid}.S{session}.Map";

    /// <summary>Auto-reset event the producer sets after publishing a frame.</summary>
    public static string SessionReadyEvent(int hostPid, int session) => $@"Local\WinWallpaper.v2.H{hostPid}.S{session}.Ready";

    public const string DaemonMutex = @"Local\WinWallpaper.Daemon.Instance";
    public const string HostMutex = @"Local\WinWallpaper.Host.Instance";
}

/// <summary>Where a session's frames are shown.</summary>
public enum Layer
{
    /// <summary>Behind the desktop icons (the wallpaper). Alpha is ignored.</summary>
    Desktop,
    /// <summary>Above every window, click-through, per-pixel (premultiplied) alpha.</summary>
    Overlay,
}

/// <summary>How a session's pixels reach the host.</summary>
public enum Transport
{
    /// <summary>BGRA8 rows in a shared-memory ring. Works from any language.</summary>
    Cpu,
    /// <summary>Shared D3D11 textures + shared fences. Import them with D3D11, D3D12 or Vulkan.</summary>
    Gpu,
}

/// <summary>How the alpha channel is interpreted.</summary>
public enum AlphaMode
{
    /// <summary>Alpha is ignored; the frame is fully opaque.</summary>
    Opaque,
    /// <summary>Colour channels are already multiplied by alpha (what GPUs and DirectComposition expect).</summary>
    Premultiplied,
}

public static class ProtocolText
{
    public static string ToWire(this Layer l) => l == Layer.Overlay ? "overlay" : "desktop";
    public static string ToWire(this Transport t) => t == Transport.Gpu ? "gpu" : "cpu";
    public static string ToWire(this AlphaMode a) => a == AlphaMode.Premultiplied ? "premultiplied" : "opaque";
    public static Layer ParseLayer(string? s) => s == "overlay" ? Layer.Overlay : Layer.Desktop;
    public static Transport ParseTransport(string? s) => s == "gpu" ? Transport.Gpu : Transport.Cpu;
    public static AlphaMode ParseAlpha(string? s) => s == "premultiplied" ? AlphaMode.Premultiplied : AlphaMode.Opaque;
}
