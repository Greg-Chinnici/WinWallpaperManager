using System.Diagnostics;
using System.Text.Json.Nodes;
using WallpaperProtocol;

namespace WallpaperDaemon;

/// <summary>Owns the connection to WallpaperHost, starting the host if it isn't running.</summary>
internal sealed class HostLink : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private JsonPipe? _pipe;
    private bool _everConnected;

    /// <summary>Raised when the host process exits (crash, kill, or a quit we sent).</summary>
    public event Action? HostExited;

    /// <summary>Raised after reconnecting to a (new) host, which has lost every session.</summary>
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
                    if (_pipe is null || !_pipe.IsConnected)
                    {
                        _pipe?.Dispose();
                        _pipe = await ConnectOrStartAsync();
                        reconnected = _everConnected; // only a *re*connect means the host lost its sessions
                        _everConnected = true;
                        WatchHost(_pipe.ServerProcessId);
                    }
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

    /// <summary>Event-driven: wait on the host process handle rather than noticing on the next command.</summary>
    private void WatchHost(int pid)
    {
        if (pid == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                await p.WaitForExitAsync();
            }
            catch (ArgumentException) { /* already gone */ }
            Log.Write($"Host (pid {pid}) exited");
            HostExited?.Invoke();
        });
    }

    private static async Task<JsonPipe> ConnectOrStartAsync()
    {
        try { return await JsonPipe.ConnectAsync(Names.HostPipe, 300); }
        // No host (timeout), or a dying host's pipe that's already broken (IOException): start a fresh one.
        catch (Exception ex) when (ex is TimeoutException or IOException) { }

        var exe = Path.Combine(AppContext.BaseDirectory, "WallpaperHost.exe");
        Log.Write("Starting " + exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
        return await JsonPipe.ConnectAsync(Names.HostPipe, 10_000);
    }

    public void Dispose() => _pipe?.Dispose();
}
