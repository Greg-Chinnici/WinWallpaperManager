using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using WallpaperProtocol;

namespace WallpaperTray;

/// <summary>Short-lived request/response calls to the daemon; starts it when it isn't running.</summary>
internal static class DaemonClient
{
    public static async Task<JsonObject> RequestAsync(JsonObject msg, bool startIfNeeded = true)
    {
        JsonPipe pipe;
        try { pipe = await JsonPipe.ConnectAsync(Names.DaemonPipe, 400); }
        catch (TimeoutException) when (startIfNeeded)
        {
            StartDaemon();
            pipe = await JsonPipe.ConnectAsync(Names.DaemonPipe, 8000);
        }
        using (pipe) return await pipe.RequestAsync(msg);
    }

    public static Task<JsonObject> SendAsync(string cmd, Action<JsonObject>? fill = null)
    {
        var o = new JsonObject { ["cmd"] = cmd };
        fill?.Invoke(o);
        return RequestAsync(o);
    }

    /// <summary>Status without auto-starting the daemon (null when offline).</summary>
    public static async Task<JsonObject?> TryStatusAsync()
    {
        try { return await RequestAsync(new JsonObject { ["cmd"] = "status" }, startIfNeeded: false); }
        catch { return null; }
    }

    public static void StartDaemon()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "WallpaperDaemon.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("WallpaperDaemon.exe not found next to the tray app", exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
    }
}
