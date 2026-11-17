// Custom render output sample: draws an animated plasma on the CPU and streams it to the
// desktop through the WinWallpaper daemon. See docs/custom-render-outputs.md.
//
//   dotnet run --project samples/ExternalRenderer [-- --seconds 20 --fps 30 --width 1280 --height 720]
//
// Press Ctrl+C to stop; the daemon then puts the previous wallpaper back.

using System.Diagnostics;
using WallpaperProtocol;

var opts = args.Select((a, i) => (a, i)).Where(t => t.a.StartsWith("--")).ToDictionary(t => t.a[2..], t => args.ElementAtOrDefault(t.i + 1) ?? "");
int Opt(string k, int d) => opts.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d;
var (width, height, fps, seconds) = (Opt("width", 1280), Opt("height", 720), Opt("fps", 30), Opt("seconds", 0));

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
if (seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));

// 1. Ask the daemon for the wallpaper. It answers with a writer id and starts showing our frames.
using var output = await ExternalOutput.ConnectAsync("Plasma sample");
Console.WriteLine($"Connected as writer {output.WriterId}. Streaming {width}x{height} @ {fps} fps. Ctrl+C to stop.");
output.RevokedChanged += () => { Console.WriteLine("Someone picked another wallpaper; stopping."); stop.Cancel(); };

// 2. Render and submit frames at our own pace.
var frame = new byte[width * height * 4];
var period = TimeSpan.FromSeconds(1.0 / fps);
var clock = Stopwatch.StartNew();
long count = 0;
using var timer = new PeriodicTimer(period);
try
{
    do
    {
        RenderPlasma(frame, width, height, clock.Elapsed.TotalSeconds);
        output.Submit(frame, width, height); // copies into the shared ring and wakes the host
        count++;
    }
    while (await timer.WaitForNextTickAsync(stop.Token));
}
catch (OperationCanceledException) { }

Console.WriteLine($"Sent {count} frames in {clock.Elapsed.TotalSeconds:0.0}s. Disconnecting.");
// 3. Disposing closes the pipe; the daemon restores whatever was playing before.

static void RenderPlasma(byte[] px, int w, int h, double time)
{
    // Sine plasma, BGRA8, top-down. Kept deliberately cheap (float math + a colour lookup table)
    // so the sample's own CPU cost doesn't drown out the cost of the wallpaper system itself.
    var t = (float)time;
    var palette = Palette.Table;
    float cx = 5 + 5 * MathF.Sin(t / 5), cy = 5 + 5 * MathF.Cos(t / 3);
    Parallel.For(0, h, y =>
    {
        var fy = y * 10f / h;
        var rowWave = MathF.Sin((fy + t) / 2);
        var row = y * w * 4;
        for (var x = 0; x < w; x++)
        {
            var fx = x * 10f / w;
            float dx = fx - cx, dy = fy - cy;
            var v = MathF.Sin(fx + t) + rowWave + MathF.Sin((fx + fy + t) / 2) + MathF.Sin(MathF.Sqrt(dx * dx + dy * dy) + t);
            var c = palette[(int)((v + 4) * 31.875f) & 255]; // v in [-4, 4] -> 0..255
            var i = row + x * 4;
            px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255;
        }
    });
}

static class Palette
{
    public static readonly (byte R, byte G, byte B)[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var a = i / 255f * MathF.PI * 2;
        return ((byte)(128 + 127 * MathF.Sin(a)), (byte)(128 + 127 * MathF.Sin(a + 2)), (byte)(128 + 127 * MathF.Sin(a + 4)));
    }).ToArray();
}
