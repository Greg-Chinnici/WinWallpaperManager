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
    Name = $"GPU sample ({api})", Layer = layer, Width = w, Height = h, Api = api,
    Transport = api == "cpu" ? Transport.Cpu : Transport.Gpu, // --api cpu: CPU transport, for comparison
});
session.RevokedChanged += () => { Console.WriteLine("Revoked: another renderer or wallpaper took this layer."); stop.Cancel(); };

// 2. Import them into the chosen API.
var look = new Look(api, layer == Layer.Overlay);
using IBackend backend = api switch
{
    "cpu" => new CpuBackend(session, look),
    "d3d12" => new D3D12Backend(session, look),
    "vulkan" => new VulkanBackend(session, look),
    _ => new D3D11Backend(session, look),
};
Console.WriteLine($"Session {session.Descriptor.Id}: {api} on the {layer.ToWire()} layer, {w}x{h} @ {fps} fps. Ctrl+C to stop.");

// 3. Render at our own pace. A high-resolution waitable timer keeps 120-180 fps targets honest
//    (PeriodicTimer follows the ~15.6 ms system tick and tops out around 64 fps).
long rendered = 0, skipped = 0;
var proc = Process.GetCurrentProcess();
var cpu0 = proc.TotalProcessorTime;
var clock = Stopwatch.StartNew();
using (var pacer = new HighResPacer())
{
    var period = Stopwatch.Frequency / (double)fps;
    var start = Stopwatch.GetTimestamp();
    for (long tick = 1; !stop.IsCancellationRequested; tick++)
    {
        if (backend.RenderFrame(clock.Elapsed.TotalSeconds)) rendered++; else skipped++;
        var due = start + (long)(tick * period);
        if (Stopwatch.GetTimestamp() > due + (long)period) { start = Stopwatch.GetTimestamp(); tick = 0; } // fell behind: don't burst
        else pacer.WaitUntil(due, stop.Token.WaitHandle);
    }
}
var elapsed = clock.Elapsed.TotalSeconds;
proc.Refresh();
Console.WriteLine($"Rendered {rendered} frames ({skipped} ticks skipped while the host held the slot) in {elapsed:0.0}s.");
// One machine-readable line for build/bench.ps1.
Console.WriteLine($"BENCH {{\"api\":\"{api}\",\"width\":{w},\"height\":{h},\"target\":{fps},\"rendered\":{rendered},\"skipped\":{skipped},\"seconds\":{elapsed:0.000},\"cpuMs\":{(proc.TotalProcessorTime - cpu0).TotalMilliseconds:0}}}");

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
