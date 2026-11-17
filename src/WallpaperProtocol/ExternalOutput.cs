using System.Text.Json.Nodes;

namespace WallpaperProtocol;

/// <summary>
/// Client helper for custom render outputs: ask the daemon for the wallpaper, then push
/// BGRA frames straight into the host's frame ring. See docs/custom-render-outputs.md.
///
/// <code>
/// using var output = await ExternalOutput.ConnectAsync("My Renderer");
/// while (!output.Revoked) { output.Submit(pixels, 1920, 1080); await Task.Delay(33); }
/// </code>
/// The wallpaper stays yours while this object (and its pipe) is alive and nobody picks a
/// different wallpaper; <see cref="Revoked"/> turns true when that happens.
/// </summary>
public sealed class ExternalOutput : IDisposable
{
    private readonly JsonPipe _pipe;
    private readonly FrameChannel _channel;
    private readonly CancellationTokenSource _cts = new();

    public int WriterId { get; }
    public bool Revoked { get; private set; }
    public event Action? RevokedChanged;

    private ExternalOutput(JsonPipe pipe, int writerId)
    {
        _pipe = pipe;
        WriterId = writerId;
        _channel = FrameChannel.Open();
        _ = WatchAsync();
    }

    public static async Task<ExternalOutput> ConnectAsync(string name, int timeoutMs = 3000, CancellationToken ct = default)
    {
        var pipe = await JsonPipe.ConnectAsync(Names.DaemonPipe, timeoutMs, ct).ConfigureAwait(false);
        var resp = await pipe.RequestAsync(new JsonObject { ["cmd"] = "external.begin", ["name"] = name }, ct).ConfigureAwait(false);
        if (resp["ok"]?.GetValue<bool>() != true)
        {
            pipe.Dispose();
            throw new InvalidOperationException("Daemon refused: " + resp["error"]);
        }
        return new ExternalOutput(pipe, resp["writer"]!.GetValue<int>());
    }

    /// <summary>Copy a tightly packed BGRA8 frame (top-down) into the ring and publish it.</summary>
    public unsafe void Submit(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("Buffer smaller than width*height*4");
        var dst = _channel.BeginFrame(width, height);
        bgra[..(width * height * 4)].CopyTo(new Span<byte>(dst, width * height * 4));
        _channel.Publish(WriterId, width, height);
    }

    /// <summary>Zero-copy variant: write rows (width*4 bytes apart) into the returned pointer, then call <see cref="Publish"/>.</summary>
    public unsafe byte* BeginFrame(int width, int height) => _channel.BeginFrame(width, height);
    public void Publish(int width, int height) => _channel.Publish(WriterId, width, height);

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
        _pipe.Dispose(); // closing the pipe ends the session on the daemon side
        _channel.Dispose();
    }
}
