using System.Text.Json.Nodes;

namespace WallpaperProtocol;

/// <summary>What a producer gets back from <c>external.begin</c>: everything needed to deliver frames.</summary>
public sealed record SessionDescriptor(
    int Id,
    Layer Layer,
    Transport Transport,
    int Width,
    int Height,
    int Stride,
    int Slots,
    AlphaMode Alpha,
    string MapName,
    string ReadyEvent,
    GpuResources? Gpu)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["id"] = Id, ["layer"] = Layer.ToWire(), ["transport"] = Transport.ToWire(),
            ["width"] = Width, ["height"] = Height, ["stride"] = Stride, ["slots"] = Slots,
            ["alpha"] = Alpha.ToWire(), ["map"] = MapName, ["readyEvent"] = ReadyEvent,
        };
        if (Gpu is { } g) o["gpu"] = g.ToJson();
        return o;
    }

    public static SessionDescriptor FromJson(JsonObject o) => new(
        o["id"]!.GetValue<int>(),
        ProtocolText.ParseLayer((string?)o["layer"]),
        ProtocolText.ParseTransport((string?)o["transport"]),
        o["width"]!.GetValue<int>(), o["height"]!.GetValue<int>(),
        o["stride"]!.GetValue<int>(), o["slots"]!.GetValue<int>(),
        ProtocolText.ParseAlpha((string?)o["alpha"]),
        (string)o["map"]!, (string)o["readyEvent"]!,
        o["gpu"] is JsonObject g ? GpuResources.FromJson(g) : null);
}

/// <summary>
/// GPU transport resources. All handles are NT handles that are already valid in the producer's
/// process (the host duplicates them in). Textures are DXGI_FORMAT_B8G8R8A8_UNORM, Width×Height,
/// usable as render target and copy destination.
/// </summary>
/// <param name="AdapterLuid">Render on this adapter (DXGI LUID as Low | High &lt;&lt; 32): shared resources don't cross GPUs.</param>
/// <param name="Textures">One texture per slot.</param>
/// <param name="ProducedFence">You signal it to Seq + 1 after rendering frame Seq.</param>
/// <param name="ConsumedFence">The host signals it to Seq + 1 after copying frame Seq. Don't render frame N
/// into its slot until it is at least N - Slots + 1.</param>
public sealed record GpuResources(long AdapterLuid, long[] Textures, long ProducedFence, long ConsumedFence)
{
    public JsonObject ToJson() => new()
    {
        ["adapterLuid"] = AdapterLuid.ToString(),
        ["textures"] = new JsonArray(Textures.Select(t => (JsonNode)JsonValue.Create(t)).ToArray()),
        ["producedFence"] = ProducedFence,
        ["consumedFence"] = ConsumedFence,
    };

    public static GpuResources FromJson(JsonObject o) => new(
        long.Parse((string)o["adapterLuid"]!),
        o["textures"]!.AsArray().Select(n => n!.GetValue<long>()).ToArray(),
        o["producedFence"]!.GetValue<long>(),
        o["consumedFence"]!.GetValue<long>());

    public uint LuidLow => (uint)(AdapterLuid & 0xFFFFFFFF);
    public int LuidHigh => (int)(AdapterLuid >> 32);
}
