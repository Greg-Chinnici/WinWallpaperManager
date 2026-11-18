// Digital clock wallpaper: your background image with a large clock on top, streamed to the
// desktop through the WinWallpaper daemon. See docs/custom-render-outputs.md.
//
//   dotnet run --project samples/ClockRenderer -- --background "C:\Pictures\mountains.jpg" --seconds
//
// Options
//   --overlay             float the clock above all windows (transparent, click-through) instead of
//                         replacing the wallpaper. Defaults to top-right and a smaller size.
//   --background <file>   jpg/png/bmp/tiff/gif (first frame). Default: a dark gradient (desktop only)
//   --seconds             show seconds (redraws once a second instead of once a minute)
//   --12h                 12-hour clock with AM/PM (default: 24-hour)
//   --no-date             hide the date line
//   --position <where>    center | top-left | top-right | bottom-left | bottom-right (default: center)
//   --dim <0-100>         darken the background so the clock stays readable (default: 25)
//   --color <#RRGGBB>     clock colour (default: #FFFFFF)
//   --font <family>       default: Segoe UI Variable Display
//   --width/--height <n>  frame size (default: primary monitor resolution)
//   --duration <seconds>  stop after this long (default: run until Ctrl+C)
//
// Cost: it publishes a frame only when the displayed text changes, so it is idle between ticks.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using WallpaperProtocol;

var options = Options.Parse(args);
var (width, height) = options.FrameSize();

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
if (options.Duration > 0) stop.CancelAfter(TimeSpan.FromSeconds(options.Duration));

using var clock = new ClockFace(options, width, height);
using var session = await WallpaperSession.ConnectAsync(new SessionOptions
{
    Name = "Digital clock", Width = width, Height = height,
    Layer = options.Overlay ? Layer.Overlay : Layer.Desktop, // overlay => premultiplied alpha
});
session.RevokedChanged += () => { Console.WriteLine("Another wallpaper or overlay took this layer; stopping."); stop.Cancel(); };
Console.WriteLine($"Clock running at {width}x{height} on the {(options.Overlay ? "overlay" : "desktop")} layer (seconds: {(options.Seconds ? "on" : "off")}). Ctrl+C to stop.");

var frames = 0;
try
{
    while (!stop.IsCancellationRequested)
    {
        var now = DateTime.Now;
        session.Cpu!.Submit(clock.Render(now));
        frames++;
        // Sleep until the displayed text next changes: the next second, or the next minute.
        var next = options.Seconds
            ? now.Date.AddSeconds(Math.Floor(now.TimeOfDay.TotalSeconds) + 1)
            : now.Date.AddMinutes(Math.Floor(now.TimeOfDay.TotalMinutes) + 1);
        var wait = next - DateTime.Now + TimeSpan.FromMilliseconds(5);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, stop.Token);
    }
}
catch (OperationCanceledException) { }
Console.WriteLine($"Published {frames} frames. Disconnecting (the previous wallpaper comes back).");

/// <summary>Draws the clock. The background is scaled, dimmed and cached once; each tick only copies it and draws text.</summary>
internal sealed class ClockFace : IDisposable
{
    private readonly Options _o;
    private readonly int _w, _h;
    private readonly byte[] _background, _frame;
    private readonly GCHandle _pin;
    private readonly Bitmap _canvas;          // GDI+ bitmap drawn straight into _frame (BGRA, top-down)
    private readonly Font _timeFont, _suffixFont, _dateFont;
    private readonly Color _color;

    public ClockFace(Options o, int w, int h)
    {
        _o = o; _w = w; _h = h;
        _frame = new byte[w * h * 4];
        _pin = GCHandle.Alloc(_frame, GCHandleType.Pinned);
        _canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, _pin.AddrOfPinnedObject()); // premultiplied BGRA
        _color = ColorTranslator.FromHtml(o.Color);
        var family = FontFamily.Families.Any(f => f.Name == o.Font) ? o.Font : "Segoe UI";
        var scale = o.Overlay ? 0.45f : 1f;
        var timeSize = h * (o.Seconds ? 0.15f : 0.19f) * scale;
        _timeFont = new Font(family, timeSize, FontStyle.Regular, GraphicsUnit.Pixel);
        _suffixFont = new Font(family, timeSize * 0.32f, FontStyle.Regular, GraphicsUnit.Pixel);
        _dateFont = new Font(family, h * 0.034f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _background = RenderBackground();
    }

    private byte[] RenderBackground()
    {
        using (var g = Graphics.FromImage(_canvas))
        {
            if (_o.Overlay) { g.Clear(Color.Transparent); return (byte[])_frame.Clone(); } // nothing behind the clock
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (_o.Background is { } path)
            {
                using var img = Image.FromFile(path);
                // Cover: fill the frame, crop the overflow, keep the aspect ratio.
                var scale = Math.Max((float)_w / img.Width, (float)_h / img.Height);
                float dw = img.Width * scale, dh = img.Height * scale;
                g.DrawImage(img, (_w - dw) / 2, (_h - dh) / 2, dw, dh);
            }
            else
            {
                using var brush = new LinearGradientBrush(new Rectangle(0, 0, _w, _h), Color.FromArgb(0x1B, 0x1F, 0x3A), Color.FromArgb(0x4A, 0x1D, 0x4F), 35f);
                g.FillRectangle(brush, 0, 0, _w, _h);
            }
            if (_o.Dim > 0)
            {
                using var veil = new SolidBrush(Color.FromArgb((int)(_o.Dim * 2.55), 0, 0, 0));
                g.FillRectangle(veil, 0, 0, _w, _h);
            }
        }
        return (byte[])_frame.Clone();
    }

    public byte[] Render(DateTime now)
    {
        Buffer.BlockCopy(_background, 0, _frame, 0, _frame.Length);
        using var g = Graphics.FromImage(_canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        var culture = CultureInfo.CurrentCulture;
        var time = now.ToString(_o.TwelveHour ? (_o.Seconds ? "h:mm:ss" : "h:mm") : (_o.Seconds ? "HH:mm:ss" : "HH:mm"), culture);
        var suffix = _o.TwelveHour ? " " + now.ToString("tt", culture) : "";
        var date = now.ToString("dddd, MMMM d", culture);

        // Lay out against a fixed template ("88:88:88") so the clock doesn't jiggle as digits change.
        var fmt = StringFormat.GenericTypographic;
        var template = _o.Seconds ? "88:88:88" : "88:88";
        var timeSize = g.MeasureString(template, _timeFont, PointF.Empty, fmt);
        var suffixSize = suffix.Length > 0 ? g.MeasureString(suffix, _suffixFont, PointF.Empty, fmt) : SizeF.Empty;
        var dateSize = _o.ShowDate ? g.MeasureString(date, _dateFont, PointF.Empty, fmt) : SizeF.Empty;
        var gap = _o.ShowDate ? _h * 0.01f : 0;
        var blockW = Math.Max(timeSize.Width + suffixSize.Width, dateSize.Width);
        var blockH = timeSize.Height + gap + dateSize.Height;

        var margin = _h * (_o.Overlay ? 0.03f : 0.06f);
        if (_o.Overlay)
        {
            // A soft translucent pill keeps the clock readable over any window.
            var pad = _h * 0.012f;
            var (px, py) = _o.Position switch
            {
                "top-left" => (margin, margin), "bottom-left" => (margin, _h - margin - blockH),
                "bottom-right" => (_w - margin - blockW, _h - margin - blockH), "center" => ((_w - blockW) / 2, (_h - blockH) / 2),
                _ => (_w - margin - blockW, margin),
            };
            using var pill = RoundedRect(px - pad * 2, py - pad, blockW + pad * 4, blockH + pad * 2, pad * 2);
            using var veil = new SolidBrush(Color.FromArgb(110, 10, 12, 18));
            g.FillPath(veil, pill);
        }
        var (x, y, align) = _o.Position switch
        {
            "top-left" => (margin, margin, StringAlignment.Near),
            "top-right" => (_w - margin - blockW, margin, StringAlignment.Far),
            "bottom-left" => (margin, _h - margin - blockH, StringAlignment.Near),
            "bottom-right" => (_w - margin - blockW, _h - margin - blockH, StringAlignment.Far),
            _ => ((_w - blockW) / 2, (_h - blockH) / 2, StringAlignment.Center),
        };

        float AlignX(float w) => align switch
        {
            StringAlignment.Near => x,
            StringAlignment.Far => x + blockW - w,
            _ => x + (blockW - w) / 2,
        };

        var timeX = AlignX(timeSize.Width + suffixSize.Width);
        // Centre the actual digits inside the template's width.
        var actual = g.MeasureString(time, _timeFont, PointF.Empty, fmt);
        DrawText(g, time, _timeFont, timeX + (timeSize.Width - actual.Width) / 2, y, fmt);
        if (suffix.Length > 0)
            DrawText(g, suffix, _suffixFont, timeX + timeSize.Width, y + timeSize.Height - suffixSize.Height * 1.25f, fmt);
        if (_o.ShowDate)
            DrawText(g, date, _dateFont, AlignX(dateSize.Width), y + timeSize.Height + gap, fmt);

        return _frame;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void DrawText(Graphics g, string s, Font f, float x, float y, StringFormat fmt)
    {
        // Soft shadow: a few faint offset copies, then the text itself.
        var d = Math.Max(1f, f.Size * 0.025f);
        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            foreach (var (ox, oy) in new[] { (d, d), (d * 2, d * 2), (0f, d * 1.5f) })
                g.DrawString(s, f, shadow, x + ox, y + oy, fmt);
        using var brush = new SolidBrush(_color);
        g.DrawString(s, f, brush, x, y, fmt);
    }

    public void Dispose()
    {
        _canvas.Dispose(); _timeFont.Dispose(); _suffixFont.Dispose(); _dateFont.Dispose();
        _pin.Free();
    }
}

internal sealed record Options(bool Overlay, string? Background, bool Seconds, bool TwelveHour, bool ShowDate, string Position,
    int Dim, string Color, string Font, int Width, int Height, int Duration)
{
    public static Options Parse(string[] args)
    {
        string? Val(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        int Int(string name, int d) => int.TryParse(Val(name), out var n) ? n : d;
        var bg = Val("--background");
        if (bg is not null && !File.Exists(bg)) throw new FileNotFoundException("Background image not found", bg);
        var overlay = args.Contains("--overlay");
        return new Options(overlay, overlay ? null : bg, args.Contains("--seconds"), args.Contains("--12h"), !args.Contains("--no-date"),
            (Val("--position") ?? (overlay ? "top-right" : "center")).ToLowerInvariant(), Math.Clamp(Int("--dim", 25), 0, 100),
            Val("--color") ?? "#FFFFFF", Val("--font") ?? "Segoe UI Variable Display",
            Int("--width", 0), Int("--height", 0), Int("--duration", 0));
    }

    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32")] private static extern bool SetProcessDpiAwarenessContext(nint value);

    /// <summary>Primary monitor in physical pixels, unless overridden. The host scales it to other monitors.</summary>
    public (int, int) FrameSize()
    {
        if (Width > 0 && Height > 0) return (Math.Min(Width, SessionMap.MaxWidth), Math.Min(Height, SessionMap.MaxHeight));
        SetProcessDpiAwarenessContext(-4); // per-monitor v2, so metrics aren't DPI-virtualised
        return (Math.Min(GetSystemMetrics(0), SessionMap.MaxWidth), Math.Min(GetSystemMetrics(1), SessionMap.MaxHeight));
    }
}
