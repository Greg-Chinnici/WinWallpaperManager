using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using WallpaperDaemon.Sources;
using WallpaperProtocol;

namespace WallpaperDaemon;

/// <summary>
/// Owns wallpaper state: what is on each layer, why, and who may draw there.
///   Desktop layer: the daemon's own source (image/GIF/video) or an external renderer.
///   Overlay layer: an external renderer or nothing.
/// The host owns every session's resources; the daemon brokers them and decides which is active.
/// API: \\.\pipe\WinWallpaper.Daemon.v2, one JSON object per line (see docs/custom-render-outputs.md).
/// </summary>
internal sealed class Daemon : IDisposable
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinWallpaper", "state.json");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HostLink _host = new();
    private readonly CancellationTokenSource _quit = new();
    private readonly FrameSink _sink = new();

    // The daemon's own wallpaper and the CPU session it plays into.
    private Source? _source;
    private int _sourceSession;
    private SessionMap? _sourceMap;
    private GpuSink? _sourceGpuSink;          // set when the source decodes on the GPU
    private VideoGpu? _videoGpu;               // shared D3D11 decode device on the host's GPU
    private bool _videoGpuFailed;
    private string _decode = "auto";           // "auto" (GPU, CPU fallback) | "cpu"
    private bool _paused;
    private string _fit = "fill", _sampling = "smooth", _lastError = "";

    // External renderer owning each layer (index = Layer).
    private readonly ExternalSession?[] _external = new ExternalSession?[2];

    private sealed record ExternalSession(int Id, Layer Layer, string Name, Transport Transport, string Api, int Width, int Height, JsonPipe Pipe);

    public Daemon()
    {
        _host.Reconnected += OnHostReconnected;
        // If the host dies (crash, Task Manager), bring it straight back: reconnecting starts a new host
        // and raises Reconnected, which restores the wallpaper. Not when we're the ones shutting it down.
        _host.HostExited += () => _ = Task.Run(async () =>
        {
            if (_quit.IsCancellationRequested || _shuttingDown) return;
            await Task.Delay(500);
            try { await _host.SendAsync(new JsonObject { ["cmd"] = "status" }); }
            catch (Exception ex) { Log.Write("Couldn't restart the host: " + ex.Message); }
        });
    }

    private volatile bool _shuttingDown;

    public async Task RunAsync()
    {
        await RestoreAsync();
        var listeners = Enumerable.Range(0, 4).Select(_ => AcceptLoop()).ToArray();
        try { await Task.Delay(Timeout.Infinite, _quit.Token); } catch (OperationCanceledException) { }
        Log.Write("Daemon stopping");
    }

    private async Task<JsonObject> Host(JsonObject msg)
    {
        var r = await _host.SendAsync(msg);
        if (r["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException($"host: {r["error"]}");
        return r;
    }

    // ---------------- pipe server ----------------

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

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
            GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid); // trusted: comes from the kernel, not the client
            _ = Task.Run(() => HandleClient(new JsonPipe(server), (int)pid));
        }
    }

    private async Task HandleClient(JsonPipe pipe, int clientPid)
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
                        if (owned is not null) throw new InvalidOperationException("this connection already owns a session; open another connection");
                        (resp, owned) = await BeginExternalAsync(pipe, req, clientPid);
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
            if (owned is not null) await EndExternalAsync(owned);
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
                    if (req["decode"] is { } dec) _decode = (string?)dec == "cpu" ? "cpu" : "auto";
                    await SetSourceLocked(kind, path, req["fps"]?.GetValue<double>() ?? 0);
                    await SaveAsync();
                    return Status();
                }
                case "fps":
                    _source?.SetFps(req["fps"]?.GetValue<double>() ?? 0);
                    await SaveAsync();
                    return Status();
                case "fit":
                    _fit = ((string?)req["mode"] ?? "fill").ToLowerInvariant();
                    await Host(new JsonObject { ["cmd"] = "fit", ["mode"] = _fit });
                    await SaveAsync();
                    return Status();
                case "sampling":
                {
                    var mode = ((string?)req["mode"] ?? "smooth").ToLowerInvariant();
                    if (mode is not ("smooth" or "pixelated")) return JsonPipe.Error("mode must be 'smooth' or 'pixelated'");
                    _sampling = mode;
                    await Host(new JsonObject { ["cmd"] = "sampling", ["mode"] = _sampling });
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
                case "stop": // clear the desktop layer: Windows' own wallpaper shows again
                    await RevokeExternalLocked(Layer.Desktop, restore: false);
                    await StopSourceLocked();
                    await SaveAsync();
                    return Status();
                case "overlay.stop":
                    await RevokeExternalLocked(Layer.Overlay, restore: false);
                    return Status();
                case "quit":
                    _shuttingDown = true;
                    await RevokeExternalLocked(Layer.Overlay, restore: false);
                    await RevokeExternalLocked(Layer.Desktop, restore: false);
                    await StopSourceLocked();
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

    // ---------------- the daemon's own wallpaper ----------------

    private async Task SetSourceLocked(string kind, string path, double fps)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found", path);
        var gpu = kind == "video" && _decode != "cpu" ? await GetVideoGpuAsync() : null;
        Source source = kind switch
        {
            "image" => new ImageSource(path),
            "gif" => new GifSource(path, fps),
            "video" => new VideoSource(path, fps, () => gpu),
            _ => throw new ArgumentException($"unknown kind '{kind}'"),
        };
        int session = 0;
        SessionMap? map = null;
        GpuSink? gpuSink = null;
        try
        {
            source.Prepare(); // decode/open first: a bad file fails here, with the old wallpaper untouched
            SessionDescriptor d;
            (d, map) = await CreateOwnSessionAsync(source.Width, source.Height, source.UsesGpu);
            session = d.Id;
            if (source.UsesGpu) gpuSink = new GpuSink(map, d.Gpu!, gpu!.Device);
        }
        catch (Exception ex)
        {
            source.Dispose(); gpuSink?.Dispose(); map?.Dispose();
            if (session != 0) try { await Host(new JsonObject { ["cmd"] = "session.destroy", ["id"] = session }); } catch { }
            _lastError = $"{Path.GetFileName(path)}: {ex.Message}";
            Log.Write("Source failed: " + ex);
            throw;
        }

        // Switch over: new source starts in its own session, then the old one goes away (no flash).
        await RevokeExternalLocked(Layer.Desktop, restore: false);
        var (oldSource, oldSession, oldMap, oldGpuSink) = (_source, _sourceSession, _sourceMap, _sourceGpuSink);
        oldSource?.Dispose();
        _sink.Attach(source.UsesGpu ? null : map);
        _source = source; _sourceSession = session; _sourceMap = map; _sourceGpuSink = gpuSink;
        if (gpuSink is not null) source.StartGpu(gpuSink); else source.Start(_sink);
        if (_paused) source.Pause();
        await Host(new JsonObject { ["cmd"] = "session.activate", ["id"] = session });
        if (oldSession != 0) await Host(new JsonObject { ["cmd"] = "session.destroy", ["id"] = oldSession });
        oldGpuSink?.Dispose();
        oldMap?.Dispose();
        _lastError = "";
        Log.Write($"Now playing {kind} {path} ({source.Width}x{source.Height}, fps={(fps > 0 ? fps : "native")}, {source.Decode} decode, session {session})");
    }

    /// <summary>The shared GPU decode device, created once on the host's adapter. Null if it can't be made.</summary>
    private async Task<VideoGpu?> GetVideoGpuAsync()
    {
        if (_videoGpu is not null || _videoGpuFailed) return _videoGpu;
        try
        {
            var status = await Host(new JsonObject { ["cmd"] = "status" });
            _videoGpu = VideoGpu.Create(long.Parse((string)status["adapterLuid"]!));
        }
        catch (Exception ex)
        {
            _videoGpuFailed = true; // don't retry on every video; CPU decode still works
            Log.Write("GPU video decode disabled: " + ex.Message);
        }
        return _videoGpu;
    }

    private async Task<(SessionDescriptor, SessionMap)> CreateOwnSessionAsync(int w, int h, bool gpu)
    {
        var r = await Host(new JsonObject
        {
            ["cmd"] = "session.create", ["layer"] = "desktop", ["transport"] = gpu ? "gpu" : "cpu",
            ["width"] = w, ["height"] = h, ["alpha"] = "opaque", ["pid"] = Environment.ProcessId,
        });
        var d = SessionDescriptor.FromJson((JsonObject)r["session"]!);
        return (d, SessionMap.Open(d.MapName, d.ReadyEvent));
    }

    private async Task StopSourceLocked()
    {
        _source?.Dispose();
        _source = null;
        _sink.Attach(null);
        _sourceGpuSink?.Dispose();
        _sourceGpuSink = null;
        if (_sourceSession != 0) { try { await Host(new JsonObject { ["cmd"] = "session.destroy", ["id"] = _sourceSession }); } catch { } }
        _sourceMap?.Dispose();
        _sourceSession = 0; _sourceMap = null;
    }

    // ---------------- external renderers ----------------

    private async Task<(JsonObject, ExternalSession)> BeginExternalAsync(JsonPipe pipe, JsonObject req, int clientPid)
    {
        var name = (string?)req["name"] ?? "Custom renderer";
        var layer = ProtocolText.ParseLayer((string?)req["layer"]);
        var transport = ProtocolText.ParseTransport((string?)req["transport"]);
        var w = req["width"]?.GetValue<int>() ?? 1920;
        var h = req["height"]?.GetValue<int>() ?? 1080;
        var alpha = req["alpha"] is null
            ? (layer == Layer.Overlay ? AlphaMode.Premultiplied : AlphaMode.Opaque)
            : ProtocolText.ParseAlpha((string?)req["alpha"]);
        var api = (string?)req["api"] ?? transport.ToWire();

        await _gate.WaitAsync();
        try
        {
            var r = await Host(new JsonObject
            {
                ["cmd"] = "session.create", ["layer"] = layer.ToWire(), ["transport"] = transport.ToWire(),
                ["width"] = w, ["height"] = h, ["alpha"] = alpha.ToWire(), ["pid"] = clientPid,
            });
            var descriptor = (JsonObject)r["session"]!;
            var session = new ExternalSession(descriptor["id"]!.GetValue<int>(), layer, name, transport, api, w, h, pipe);

            await RevokeExternalLocked(layer, restore: false); // last one wins
            if (layer == Layer.Desktop) _source?.Pause();      // keep it, so we can go back when the renderer leaves
            _external[(int)layer] = session;
            await Host(new JsonObject { ["cmd"] = "session.activate", ["id"] = session.Id });
            Log.Write($"'{name}' took the {layer.ToWire()} layer: {transport.ToWire()}/{api} {w}x{h} (session {session.Id}, pid {clientPid})");
            return (JsonPipe.Ok(new JsonObject { ["protocol"] = Names.ProtocolVersion, ["session"] = descriptor.DeepClone() }), session);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Someone else took the layer (or it was stopped): tell the renderer and drop its session.</summary>
    private async Task RevokeExternalLocked(Layer layer, bool restore)
    {
        if (_external[(int)layer] is not { } ext) return;
        _external[(int)layer] = null;
        Log.Write($"'{ext.Name}' revoked from the {layer.ToWire()} layer");
        try { await ext.Pipe.SendAsync(new JsonObject { ["event"] = "external.ended" }); } catch { }
        try { await Host(new JsonObject { ["cmd"] = "session.destroy", ["id"] = ext.Id }); } catch { }
        if (restore) await RestoreDesktopSourceLocked();
    }

    private async Task EndExternalAsync(ExternalSession session)
    {
        await _gate.WaitAsync();
        try
        {
            if (_external[(int)session.Layer] != session) // already revoked; just make sure its resources are gone
            {
                try { await Host(new JsonObject { ["cmd"] = "session.destroy", ["id"] = session.Id }); } catch { }
                return;
            }
            Log.Write($"'{session.Name}' disconnected from the {session.Layer.ToWire()} layer");
            await RevokeExternalLocked(session.Layer, restore: session.Layer == Layer.Desktop);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Put the daemon's own wallpaper back on the desktop layer after an external renderer.</summary>
    private async Task RestoreDesktopSourceLocked()
    {
        if (_source is null || _sourceSession == 0) return;
        await Host(new JsonObject { ["cmd"] = "session.activate", ["id"] = _sourceSession });
        if (!_paused) _source.Resume();
        _source.Republish();
    }

    // ---------------- state ----------------

    private JsonObject Status()
    {
        var desktopExt = _external[(int)Layer.Desktop];
        var o = new JsonObject
        {
            ["protocol"] = Names.ProtocolVersion,
            ["state"] = desktopExt is not null ? "external" : _source is null ? "stopped" : _paused ? "paused" : "playing",
            ["fit"] = _fit,
            ["sampling"] = _sampling,
            ["error"] = _lastError,
        };
        if (_source is { } s)
        {
            o["source"] = new JsonObject
            {
                ["kind"] = s.Kind, ["path"] = s.Path, ["fps"] = s.Fps, ["nativeFps"] = s.NativeFps,
                ["width"] = s.Width, ["height"] = s.Height, ["frames"] = s.FrameCount, ["decode"] = s.Decode,
            };
        }
        if (desktopExt is { } d) o["external"] = Describe(d);
        if (_external[(int)Layer.Overlay] is { } ov) o["overlay"] = Describe(ov);
        return JsonPipe.Ok(o);
    }

    private static JsonObject Describe(ExternalSession e) => new()
    {
        ["name"] = e.Name, ["session"] = e.Id, ["transport"] = e.Transport.ToWire(), ["api"] = e.Api,
        ["width"] = e.Width, ["height"] = e.Height,
    };

    private async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var o = new JsonObject { ["fit"] = _fit, ["sampling"] = _sampling, ["paused"] = _paused, ["decode"] = _decode };
            if (_source is { } s) o["source"] = new JsonObject { ["kind"] = s.Kind, ["path"] = s.Path, ["fps"] = s.Fps };
            await File.WriteAllTextAsync(StatePath, o.ToJsonString());
        }
        catch (Exception ex) { Log.Write("Save failed: " + ex.Message); }
    }

    private async Task RestoreAsync()
    {
        try
        {
            var o = File.Exists(StatePath) ? JsonNode.Parse(await File.ReadAllTextAsync(StatePath)) as JsonObject : null;
            _fit = (string?)o?["fit"] ?? "fill";
            _sampling = (string?)o?["sampling"] ?? "smooth";
            _paused = o?["paused"]?.GetValue<bool>() ?? false;
            _decode = (string?)o?["decode"] == "cpu" ? "cpu" : "auto";
            await Host(new JsonObject { ["cmd"] = "fit", ["mode"] = _fit }); // also starts the host
            await Host(new JsonObject { ["cmd"] = "sampling", ["mode"] = _sampling });
            if (o?["source"] is JsonObject s)
            {
                await _gate.WaitAsync();
                try { await SetSourceLocked((string)s["kind"]!, (string)s["path"]!, s["fps"]?.GetValue<double>() ?? 0); }
                finally { _gate.Release(); }
            }
        }
        catch (Exception ex) { Log.Write("Restore failed: " + ex.Message); }
    }

    /// <summary>A new host knows nothing: external sessions are gone, and our own needs recreating.</summary>
    private async Task OnHostReconnected()
    {
        await _gate.WaitAsync();
        try
        {
            Log.Write("Host restarted; restoring state");
            foreach (var layer in new[] { Layer.Overlay, Layer.Desktop })
                if (_external[(int)layer] is { } ext)
                {
                    _external[(int)layer] = null;
                    try { await ext.Pipe.SendAsync(new JsonObject { ["event"] = "external.ended" }); } catch { }
                }
            await Host(new JsonObject { ["cmd"] = "fit", ["mode"] = _fit });
            await Host(new JsonObject { ["cmd"] = "sampling", ["mode"] = _sampling });
            if (_source is { } src)
            {
                // Its session died with the old host: restart the same wallpaper from scratch.
                _sourceSession = 0;
                await SetSourceLocked(src.Kind, src.Path, src.Fps);
            }
        }
        catch (Exception ex) { Log.Write("Restore after host restart failed: " + ex.Message); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _source?.Dispose();
        _sourceGpuSink?.Dispose();
        _sourceMap?.Dispose();
        _videoGpu?.Dispose();
        _host.Dispose();
    }
}
