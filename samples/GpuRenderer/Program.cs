// GPU custom render output sample: renders on the GPU with Direct3D 11, Direct3D 12 or Vulkan and
// hands frames to WinWallpaper without any CPU copies. See docs/custom-render-outputs.md.
//
//   dotnet run --project samples/GpuRenderer -- --api vulkan --layer overlay --seconds 20
//
// Options: --api d3d11|d3d12|vulkan (default d3d11)  --layer desktop|overlay (default desktop)
//          --width/--height (default 1920x1080)  --fps (default 60)  --seconds (default: until Ctrl+C)
//
// Each API draws a sweeping bar in its own colour (D3D11 green, D3D12 blue, Vulkan orange). On the
// overlay layer the background is transparent and the bar is translucent (premultiplied alpha).

using System.Diagnostics;
using GpuRenderer;
using WallpaperProtocol;

string Opt(string name, string def) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
var api = Opt("api", "d3d11").ToLowerInvariant();
var layer = ProtocolText.ParseLayer(Opt("layer", "desktop"));
var (w, h, fps, seconds) = (int.Parse(Opt("width", "1920")), int.Parse(Opt("height", "1080")), int.Parse(Opt("fps", "60")), int.Parse(Opt("seconds", "0")));

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
if (seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));

// 1. Ask for a GPU session on the chosen layer. The host creates the shared textures + fences
//    and duplicates their handles into this process.
using var session = await WallpaperSession.ConnectAsync(new SessionOptions
{
    Name = $"GPU sample ({api})", Layer = layer, Transport = Transport.Gpu, Width = w, Height = h, Api = api,
});
session.RevokedChanged += () => { Console.WriteLine("Revoked: another renderer or wallpaper took this layer."); stop.Cancel(); };

// 2. Import them into the chosen API.
var look = new Look(api, layer == Layer.Overlay);
using IBackend backend = api switch
{
    "d3d12" => new D3D12Backend(session, look),
    "vulkan" => new VulkanBackend(session, look),
    _ => new D3D11Backend(session, look),
};
Console.WriteLine($"Session {session.Descriptor.Id}: {api} on the {layer.ToWire()} layer, {w}x{h} @ {fps} fps. Ctrl+C to stop.");

// 3. Render at our own pace.
long rendered = 0, skipped = 0;
var clock = Stopwatch.StartNew();
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / fps));
try
{
    while (await timer.WaitForNextTickAsync(stop.Token))
    {
        if (backend.RenderFrame(clock.Elapsed.TotalSeconds)) rendered++; else skipped++;
    }
}
catch (OperationCanceledException) { }
Console.WriteLine($"Rendered {rendered} frames ({skipped} ticks skipped while the host held the slot) in {clock.Elapsed.TotalSeconds:0.0}s.");

namespace GpuRenderer
{
    internal interface IBackend : IDisposable
    {
        /// <summary>Render and publish one frame; false if the host hadn't released the slot yet.</summary>
        bool RenderFrame(double t);
    }

    /// <summary>What every backend draws, so they can be told apart on screen.</summary>
    internal sealed record Look(string Api, bool Overlay)
    {
        /// <summary>Premultiplied RGBA.</summary>
        public (float R, float G, float B, float A) Background => Overlay ? (0, 0, 0, 0) : (0.06f, 0.07f, 0.11f, 1);

        public (float R, float G, float B, float A) Bar
        {
            get
            {
                var (r, g, b) = Api switch { "d3d12" => (0.2f, 0.5f, 1f), "vulkan" => (1f, 0.45f, 0.1f), _ => (0.2f, 0.8f, 0.35f) };
                var a = Overlay ? 0.75f : 1f;
                return (r * a, g * a, b * a, a);
            }
        }

        public int BarWidth(int w) => Math.Max(8, w / 12);
        public int BarX(int w, double t) => (int)((t * 0.25 % 1.0) * (w - BarWidth(w)));
    }
}
