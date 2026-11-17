namespace WallpaperProtocol;

/// <summary>Well-known kernel object names. All are per-session ("Local\") and versioned.</summary>
public static class Names
{
    public const int ProtocolVersion = 1;

    /// <summary>Named pipe for the tray and external apps (JSON lines). Path: \\.\pipe\WinWallpaper.Daemon.v1</summary>
    public const string DaemonPipe = "WinWallpaper.Daemon.v1";

    /// <summary>Named pipe the daemon uses to control the host (JSON lines).</summary>
    public const string HostPipe = "WinWallpaper.Host.v1";

    /// <summary>Shared-memory frame ring written by producers, read by the host.</summary>
    public const string FrameMap = @"Local\WinWallpaper.Frames.v1";

    /// <summary>Auto-reset event signalled after each published frame.</summary>
    public const string FrameReadyEvent = @"Local\WinWallpaper.FrameReady.v1";

    public const string DaemonMutex = @"Local\WinWallpaper.Daemon.Instance";
    public const string HostMutex = @"Local\WinWallpaper.Host.Instance";
}
