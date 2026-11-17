using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace WallpaperProtocol;

/// <summary>
/// One JSON object per line over a named pipe. Requests carry "cmd"; responses carry
/// "ok" (and "error" when false). Uses JsonNode only, so it is trimming/AOT-safe.
/// </summary>
public sealed class JsonPipe : IDisposable
{
    private readonly PipeStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public JsonPipe(PipeStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    public bool IsConnected => _pipe.IsConnected;

    public static async Task<JsonPipe> ConnectAsync(string pipeName, int timeoutMs, CancellationToken ct = default)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
        return new JsonPipe(client);
    }

    public async Task SendAsync(JsonObject msg, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _writer.WriteLineAsync(msg.ToJsonString().AsMemory(), ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    /// <summary>Returns null at end of stream.</summary>
    public async Task<JsonObject?> ReceiveAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;
            if (string.IsNullOrWhiteSpace(line)) continue;
            return JsonNode.Parse(line) as JsonObject ?? throw new FormatException("Expected a JSON object");
        }
    }

    /// <summary>Send a request and wait for its response (one request at a time per pipe).</summary>
    public async Task<JsonObject> RequestAsync(JsonObject msg, CancellationToken ct = default)
    {
        await SendAsync(msg, ct).ConfigureAwait(false);
        return await ReceiveAsync(ct).ConfigureAwait(false) ?? throw new IOException("Pipe closed");
    }

    public static JsonObject Ok(JsonObject? extra = null)
    {
        var o = extra ?? new JsonObject();
        o["ok"] = true;
        return o;
    }

    public static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        _pipe.Dispose();
    }
}
