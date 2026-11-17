using System.Diagnostics;
using System.Text.Json.Nodes;
using WallpaperProtocol;

namespace WallpaperDaemon;

/// <summary>Owns the connection to WallpaperHost, starting the host if it isn't running.</summary>
internal sealed class HostLink : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private JsonPipe? _pipe;

    /// <summary>Raised after a (re)connect, so the daemon can restore show/fit/frame state.</summary>
    public event Func<Task>? Reconnected;

    public async Task<JsonObject> SendAsync(JsonObject msg)
    {
        await _lock.WaitAsync();
        var reconnected = false;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (_pipe is null || !_pipe.IsConnected) { _pipe?.Dispose(); _pipe = await ConnectOrStartAsync(); reconnected = true; }
                    return await _pipe.RequestAsync(msg);
                }
                catch (Exception ex) when (attempt == 0)
                {
                    Log.Write("Host link broken, reconnecting: " + ex.Message);
                    _pipe?.Dispose(); _pipe = null;
                }
            }
            throw new InvalidOperationException("unreachable");
        }
        finally
        {
            _lock.Release();
            if (reconnected && Reconnected is { } handler) _ = handler();
        }
    }

    private static async Task<JsonPipe> ConnectOrStartAsync()
    {
        try { return await JsonPipe.ConnectAsync(Names.HostPipe, 300); }
        catch (TimeoutException) { }

        var exe = Path.Combine(AppContext.BaseDirectory, "WallpaperHost.exe");
        Log.Write("Starting " + exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
        return await JsonPipe.ConnectAsync(Names.HostPipe, 10_000);
    }

    public void Dispose() => _pipe?.Dispose();
}
