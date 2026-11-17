using System.IO.Pipes;
using System.Text.Json.Nodes;
using WallpaperDaemon.Sources;
using WallpaperProtocol;

namespace WallpaperDaemon;

/// <summary>
/// Owns wallpaper state: what is playing, why, and who may write frames.
/// API: \\.\pipe\WinWallpaper.Daemon.v1, one JSON object per line (see docs/custom-render-outputs.md).
/// </summary>
internal sealed class Daemon : IDisposable
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinWallpaper", "state.json");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FrameChannel _channel = FrameChannel.Open();
    private readonly FrameSink _sink;
    private readonly HostLink _host = new();
    private readonly CancellationTokenSource _quit = new();

    private Source? _source;
    private bool _paused;
    private string _fit = "fill";
    private string _sampling = "smooth"; // smooth | pixelated
    private string _lastError = "";

    // External render output currently owning the wallpaper.
    private ExternalSession? _external;
    private int _nextWriter = 2;

    private sealed record ExternalSession(int Writer, string Name, JsonPipe Pipe);

    public Daemon()
    {
        _sink = new FrameSink(_channel);
        _host.Reconnected += OnHostReconnected;
    }

    public async Task RunAsync()
    {
        await RestoreAsync();
        var servers = Enumerable.Range(0, 4).Select(_ => AcceptLoop()).ToArray(); // up to 4 waiting listeners
        try { await Task.Delay(Timeout.Infinite, _quit.Token); } catch (OperationCanceledException) { }
        Log.Write("Daemon stopping");
    }

    // ---------------- pipe server ----------------

    private async Task AcceptLoop()
    {
        while (!_quit.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(Names.DaemonPipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_quit.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write("Accept failed: " + ex.Message); await Task.Delay(500); continue; }
            _ = Task.Run(() => HandleClient(new JsonPipe(server)));
        }
    }

    private async Task HandleClient(JsonPipe pipe)
    {
        ExternalSession? owned = null;
        try
        {
            while (true)
            {
                JsonObject? req;
                try { req = await pipe.ReceiveAsync(_quit.Token); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
                {
                    await pipe.SendAsync(JsonPipe.Error("malformed JSON: " + ex.Message));
                    continue;
                }
                if (req is null) break;
                JsonObject resp;
                try
                {
                    if ((string?)req["cmd"] == "external.begin")
                    {
                        (resp, owned) = await BeginExternalAsync(pipe, (string?)req["name"] ?? "External renderer");
                    }
                    else resp = await HandleAsync(req);
                }
                catch (Exception ex) { resp = JsonPipe.Error(ex.Message); }
                await pipe.SendAsync(resp);
            }
        }
        catch (Exception) { /* client went away */ }
        finally
        {
            if (owned is not null) await EndExternalAsync(owned, restorePrevious: true);
            pipe.Dispose();
        }
    }

    private async Task<JsonObject> HandleAsync(JsonObject req)
    {
        var cmd = (string?)req["cmd"];
        await _gate.WaitAsync();
        try
        {
            switch (cmd)
            {
                case "status": return Status();
                case "set":
                {
                    var path = (string?)req["path"] ?? throw new ArgumentException("path is required");
                    var kind = (string?)req["kind"] ?? KindFromExtension(path);
                    var fps = req["fps"]?.GetValue<double>() ?? 0;
                    await SetSourceLocked(kind, path, fps);
                    await SaveAsync();
                    return Status();
                }
                case "fps":
                    _source?.SetFps(req["fps"]?.GetValue<double>() ?? 0);
                    await SaveAsync();
                    return Status();
                case "fit":
                    _fit = ((string?)req["mode"] ?? "fill").ToLowerInvariant();
                    await _host.SendAsync(new JsonObject { ["cmd"] = "fit", ["mode"] = _fit });
                    await SaveAsync();
                    return Status();
                case "sampling":
                {
                    var mode = ((string?)req["mode"] ?? "smooth").ToLowerInvariant();
                    if (mode is not ("smooth" or "pixelated")) return JsonPipe.Error("mode must be 'smooth' or 'pixelated'");
                    _sampling = mode;
                    await _host.SendAsync(new JsonObject { ["cmd"] = "sampling", ["mode"] = _sampling });
                    await SaveAsync();
                    return Status();
                }
                case "pause":
                    _paused = true; _source?.Pause();
                    await SaveAsync();
                    return Status();
                case "resume":
                    _paused = false; _source?.Resume();
                    await SaveAsync();
                    return Status();
                case "stop":
                    await RevokeExternalLocked();
                    StopSourceLocked();
                    _channel.ActiveWriter = 0;
                    await _host.SendAsync(new JsonObject { ["cmd"] = "hide" });
                    await SaveAsync();
                    return Status();
                case "quit":
                    await RevokeExternalLocked();
                    StopSourceLocked();
                    _channel.ActiveWriter = 0;
                    try { await _host.SendAsync(new JsonObject { ["cmd"] = "quit" }); } catch { }
                    _quit.Cancel();
                    return JsonPipe.Ok();
                default:
                    return JsonPipe.Error($"unknown cmd '{cmd}'");
            }
        }
        finally { _gate.Release(); }
    }

    private static string KindFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gif" => "gif",
        ".mp4" or ".m4v" or ".mov" or ".wmv" or ".avi" or ".mkv" or ".webm" => "video",
        _ => "image",
    };

    private async Task SetSourceLocked(string kind, string path, double fps)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found", path);
        await RevokeExternalLocked();
        StopSourceLocked();
        Source source = kind switch
        {
            "image" => new ImageSource(path),
            "gif" => new GifSource(path, fps),
            "video" => new VideoSource(path, fps),
            _ => throw new ArgumentException($"unknown kind '{kind}'"),
        };
        try
        {
            _channel.ActiveWriter = FrameSink.DaemonWriter;
            source.Start(_sink);
        }
        catch (Exception ex)
        {
            source.Dispose();
            _lastError = $"{Path.GetFileName(path)}: {ex.Message}";
            Log.Write("Source failed: " + ex);
            throw;
        }
        _source = source;
        _lastError = "";
        if (_paused) source.Pause();
        await _host.SendAsync(new JsonObject { ["cmd"] = "fit", ["mode"] = _fit });
        await _host.SendAsync(new JsonObject { ["cmd"] = "sampling", ["mode"] = _sampling });
        await _host.SendAsync(new JsonObject { ["cmd"] = "show" });
        Log.Write($"Now playing {kind} {path} ({source.Width}x{source.Height}, fps={(fps > 0 ? fps : "native")})");
    }

    private void StopSourceLocked()
    {
        _source?.Dispose();
        _source = null;
    }

    // ---------------- external render outputs ----------------

    private async Task<(JsonObject, ExternalSession)> BeginExternalAsync(JsonPipe pipe, string name)
    {
        await _gate.WaitAsync();
        try
        {
            await RevokeExternalLocked();
            _source?.Pause(); // keep it around so we can go back when the external app leaves
            var session = new ExternalSession(_nextWriter++, name, pipe);
            _external = session;
            _channel.ActiveWriter = session.Writer;
            await _host.SendAsync(new JsonObject { ["cmd"] = "hide" }); // drop the old frame...
            await _host.SendAsync(new JsonObject { ["cmd"] = "show" }); // ...and show the first external one
            Log.Write($"External output '{name}' took over (writer {session.Writer})");
            return (JsonPipe.Ok(new JsonObject
            {
                ["writer"] = session.Writer,
                ["protocol"] = Names.ProtocolVersion,
                ["frameMap"] = Names.FrameMap,
                ["frameReadyEvent"] = Names.FrameReadyEvent,
                ["maxWidth"] = FrameChannel.MaxWidth,
                ["maxHeight"] = FrameChannel.MaxHeight,
            }), session);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Another wallpaper was chosen: tell the external app it lost the wallpaper.</summary>
    private async Task RevokeExternalLocked()
    {
        if (_external is not { } ext) return;
        _external = null;
        Log.Write($"External output '{ext.Name}' revoked");
        try { await ext.Pipe.SendAsync(new JsonObject { ["event"] = "external.ended" }); } catch { }
    }

    private async Task EndExternalAsync(ExternalSession session, bool restorePrevious)
    {
        await _gate.WaitAsync();
        try
        {
            if (_external != session) return; // already revoked
            _external = null;
            Log.Write($"External output '{session.Name}' disconnected");
            if (restorePrevious && _source is not null)
            {
                _channel.ActiveWriter = FrameSink.DaemonWriter;
                await _host.SendAsync(new JsonObject { ["cmd"] = "hide" });
                await _host.SendAsync(new JsonObject { ["cmd"] = "show" });
                if (!_paused) _source.Resume();
                _source.Republish();
            }
            else
            {
                _channel.ActiveWriter = 0;
                await _host.SendAsync(new JsonObject { ["cmd"] = "hide" });
            }
        }
        finally { _gate.Release(); }
    }

    // ---------------- state ----------------

    private JsonObject Status()
    {
        var o = new JsonObject
        {
            ["protocol"] = Names.ProtocolVersion,
            ["state"] = _external is not null ? "external" : _source is null ? "stopped" : _paused ? "paused" : "playing",
            ["fit"] = _fit,
            ["sampling"] = _sampling,
            ["error"] = _lastError,
        };
        if (_source is { } s)
        {
            o["source"] = new JsonObject
            {
                ["kind"] = s.Kind, ["path"] = s.Path, ["fps"] = s.Fps, ["nativeFps"] = s.NativeFps,
                ["width"] = s.Width, ["height"] = s.Height, ["frames"] = s.FrameCount,
            };
        }
        if (_external is { } e) o["external"] = new JsonObject { ["name"] = e.Name, ["writer"] = e.Writer };
        return JsonPipe.Ok(o);
    }

    private async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var o = new JsonObject { ["fit"] = _fit, ["sampling"] = _sampling, ["paused"] = _paused };
            if (_source is { } s) o["source"] = new JsonObject { ["kind"] = s.Kind, ["path"] = s.Path, ["fps"] = s.Fps };
            await File.WriteAllTextAsync(StatePath, o.ToJsonString());
        }
        catch (Exception ex) { Log.Write("Save failed: " + ex.Message); }
    }

    private async Task RestoreAsync()
    {
        try
        {
            if (!File.Exists(StatePath)) { await _host.SendAsync(new JsonObject { ["cmd"] = "status" }); return; }
            var o = JsonNode.Parse(await File.ReadAllTextAsync(StatePath)) as JsonObject;
            _fit = (string?)o?["fit"] ?? "fill";
            _sampling = (string?)o?["sampling"] ?? "smooth";
            _paused = o?["paused"]?.GetValue<bool>() ?? false;
            if (o?["source"] is JsonObject s)
            {
                await _gate.WaitAsync();
                try { await SetSourceLocked((string)s["kind"]!, (string)s["path"]!, s["fps"]?.GetValue<double>() ?? 0); }
                finally { _gate.Release(); }
            }
            else await _host.SendAsync(new JsonObject { ["cmd"] = "status" }); // make sure the host is up
        }
        catch (Exception ex) { Log.Write("Restore failed: " + ex.Message); }
    }

    private async Task OnHostReconnected()
    {
        // A fresh host knows nothing: replay fit/show and the current frame.
        if (_source is null && _external is null) return;
        await _host.SendAsync(new JsonObject { ["cmd"] = "fit", ["mode"] = _fit });
        await _host.SendAsync(new JsonObject { ["cmd"] = "sampling", ["mode"] = _sampling });
        await _host.SendAsync(new JsonObject { ["cmd"] = "show" });
        if (_external is null) _source?.Republish(); else _channel.FrameReady.Set();
    }

    public void Dispose()
    {
        _source?.Dispose();
        _host.Dispose();
        _channel.Dispose();
    }
}
