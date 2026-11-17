// Spike for docs/desktop-integration.md §13.
//
//   DesktopAttach dump    [--spawn d|z]
//   DesktopAttach run     [--present dcomp|hwnd] [--source static|animate|shm|gpu|fence] [--all]
//                         [--spawn d|z] [--layered] [--stats] [--seconds N] [--no-refresh]
//                         [--hang-after S --hang-for N]
//   DesktopAttach produce|produce-gpu|produce-fence [--width W --height H --fps F --seconds N]
//
// "run" attaches one wallpaper window per monitor (primary only unless --all), keeps
// them attached across Explorer restarts, and on exit restores the system wallpaper.

using System.Runtime.InteropServices;
using System.Text;
using DesktopAttach;
using static DesktopAttach.Native;
using static DesktopAttach.Attachment;

var argList = args.ToList();
string Opt(string name, string def) { var i = argList.IndexOf(name); return i >= 0 && i + 1 < argList.Count ? argList[i + 1] : def; }
bool Flag(string name) => argList.Contains(name);
var command = argList.FirstOrDefault() ?? "run";
var spawn = Opt("--spawn", "d");

switch (command)
{
    case "dump":
        if (Flag("--spawn")) SpawnWorkerW(FindWindow("Progman", null), spawn);
        DumpTree();
        var t = Detect();
        Log($"Detected topology: {t.Topology} parent={Hex(t.Parent)} insertAfter={Hex(t.InsertAfter)} systemWorkerW={Hex(t.SystemWorkerW)}");
        return 0;
    case "produce":
        return FrameChannel.RunProducer(int.Parse(Opt("--width", "2560")), int.Parse(Opt("--height", "1440")),
            int.Parse(Opt("--fps", "60")), int.Parse(Opt("--seconds", "10")));
    case "produce-fence":
        return FenceChannel.RunProducer(int.Parse(Opt("--width", "2560")), int.Parse(Opt("--height", "1440")),
            int.Parse(Opt("--fps", "60")), int.Parse(Opt("--seconds", "10")));
    case "produce-gpu":
        return GpuChannel.RunProducer(int.Parse(Opt("--width", "2560")), int.Parse(Opt("--height", "1440")),
            int.Parse(Opt("--fps", "60")), int.Parse(Opt("--seconds", "10")));
    case "run":
        var app = new SpikeApp(
            Opt("--present", "dcomp") == "hwnd" ? PresentMode.Hwnd : PresentMode.DComp,
            Opt("--source", "static") switch { "animate" => Source.Animate, "shm" => Source.Shm, "gpu" => Source.Gpu, "fence" => Source.Fence, _ => Source.Static },
            spawn, Flag("--all"), Flag("--layered"), Flag("--stats"), !Flag("--no-refresh"),
            int.Parse(Opt("--hang-after", "0")), int.Parse(Opt("--hang-for", "0")));
        return app.Run(int.Parse(Opt("--seconds", "0")));
    default:
        Console.Error.WriteLine("usage: DesktopAttach dump|run|produce [options]");
        return 2;
}

internal sealed record Monitor(nint Handle, string Device, RECT Rect, uint Dpi, bool Primary);

internal sealed class SpikeApp
{
    private const uint WM_APP_ATTACH = WM_APP + 1, WM_APP_HANG = WM_APP + 3;

    private readonly string _spawn;
    private readonly bool _all, _layered, _refreshOnExit;
    private readonly int _hangAfter, _hangFor;
    private readonly Renderer _renderer;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly WndProc _controlProc, _wallProc; // keep delegates alive
    private nint _control;
    private readonly List<nint> _windows = new();
    private AttachTarget? _target;
    private int _retryDelayMs = 1000;
    private System.Threading.Timer? _retryTimer, _exitTimer, _hangTimer;

    public SpikeApp(PresentMode mode, Source source, string spawn, bool all, bool layered, bool stats, bool refreshOnExit, int hangAfter, int hangFor)
    {
        _spawn = spawn; _all = all; _layered = layered; _refreshOnExit = refreshOnExit;
        _hangAfter = hangAfter; _hangFor = hangFor;
        _renderer = new Renderer(mode, source, stats);
        _controlProc = ControlProc;
        _wallProc = WallpaperProc;
        Log($"mode={mode} source={source} all={all} layered={layered}");
    }

    public int Run(int seconds)
    {
        var inst = GetModuleHandle(0);
        Register("DesktopAttachControl", _controlProc, inst);
        Register("DesktopAttachWallpaper", _wallProc, inst);

        // Hidden *top-level* window: message-only windows don't get the TaskbarCreated broadcast.
        _control = CreateWindowEx(WS_EX_TOOLWINDOW, "DesktopAttachControl", "DesktopAttach control",
            WS_POPUP, 0, 0, 0, 0, 0, 0, inst, 0);
        Log($"Window thread {GetCurrentThreadId()}, control window {Hex(_control)}");

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; PostMessage(_control, WM_CLOSE, 0, 0); };
        if (seconds > 0) _exitTimer = new System.Threading.Timer(_ => PostMessage(_control, WM_CLOSE, 0, 0), null, seconds * 1000, Timeout.Infinite);
        if (_hangFor > 0) _hangTimer = new System.Threading.Timer(_ => PostMessage(_control, WM_APP_HANG, 0, 0), null, _hangAfter * 1000, Timeout.Infinite);

        PostMessage(_control, WM_APP_ATTACH, 0, 0);
        while (GetMessage(out var msg, 0, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }

        Log($"Exited message loop. {_renderer.Summary()}");
        return 0;
    }

    private static void Register(string name, WndProc proc, nint inst)
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = inst, lpszClassName = name,
        };
        if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + name);
    }

    private static List<Monitor> EnumMonitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(0, 0, (nint h, nint _, ref RECT _, nint _) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(h, ref mi);
            GetDpiForMonitor(h, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _);
            list.Add(new Monitor(h, mi.szDevice, mi.rcMonitor, dpi, (mi.dwFlags & 1) != 0));
            return true;
        }, 0);
        return list.OrderByDescending(m => m.Primary).ToList(); // primary first => target index 0
    }

    private bool TryAttach()
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0) { Log("Progman not found (Explorer not running?)"); return false; }

        var t = Detect();
        if (t.Topology == Topology.Unsupported || (_target is not null && t.Parent != _target.Parent))
        {
            // Only ask Explorer for the WorkerW when it isn't there yet (avoids the
            // WM_DISPLAYCHANGE echo loop that 0x052C causes).
            SpawnWorkerW(progman, _spawn);
            t = Detect();
        }
        Log($"Topology: {t.Topology} parent={Hex(t.Parent)} insertAfter={Hex(t.InsertAfter)} defView={Hex(t.DefView)} systemWorkerW={Hex(t.SystemWorkerW)}");
        if (t.Topology == Topology.Unsupported) return false;
        _target = t;

        var monitors = EnumMonitors();
        if (!_all) monitors = monitors.Take(1).ToList();

        // Drop windows that no longer exist (Explorer restart destroys them silently) or are surplus.
        for (var i = _windows.Count - 1; i >= 0; i--)
            if (!IsWindow(_windows[i]) || i >= monitors.Count) { if (IsWindow(_windows[i])) DestroyWindow(_windows[i]); _windows.RemoveAt(i); }
        // Note: removing a dead window from the middle shifts indices; fine for the spike.

        var specs = new List<TargetSpec>();
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var rc = m.Rect;
            if (i >= _windows.Count)
            {
                var ex = WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | (_layered ? WS_EX_LAYERED : 0);
                var hwnd = CreateWindowEx(ex, "DesktopAttachWallpaper", "DesktopAttach wallpaper",
                    WS_POPUP, 0, 0, rc.W, rc.H, 0, 0, GetModuleHandle(0), 0);
                _windows.Add(hwnd);
                Log($"Created wallpaper window {Hex(hwnd)} for {m.Device}");
            }
            var w = _windows[i];
            if (GetParent(w) != t.Parent)
            {
                // Switch to a child style *before* SetParent (per SetParent docs).
                SetWindowLongPtr(w, GWL_STYLE, (nint)(WS_CHILD | WS_DISABLED | WS_CLIPSIBLINGS));
                SetParent(w, t.Parent);
                if (_layered) SetLayeredWindowAttributes(w, 0, 255, LWA_ALPHA);
            }
            var r = rc;
            MapWindowPoints(0, t.Parent, ref r, 2);
            SetWindowPos(w, t.InsertAfter, r.Left, r.Top, rc.W, rc.H, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Log($"Monitor[{i}] {m.Device} primary={m.Primary} dpi={m.Dpi} ({m.Dpi * 100 / 96}%) screen={rc} -> parent-client {r}");
            specs.Add(new TargetSpec(w, rc.W, rc.H));
        }

        _renderer.SetTargets(specs);
        var allOk = true;
        foreach (var w in _windows)
        {
            var (ok, detail) = Verify(t, w);
            allOk &= ok;
            Log($"VERIFY {Hex(w)}: {(ok ? "PASS" : "FAIL")} - {detail}");
        }
        return allOk;
    }

    private void ScheduleRetry()
    {
        Log($"Attach failed; retrying in {_retryDelayMs} ms");
        _retryTimer?.Dispose();
        _retryTimer = new System.Threading.Timer(_ => PostMessage(_control, WM_APP_ATTACH, 0, 0), null, _retryDelayMs, Timeout.Infinite);
        _retryDelayMs = Math.Min(_retryDelayMs * 2, 30_000);
    }

    private nint ControlProc(nint hwnd, uint msg, nint w, nint l)
    {
        if (msg == _taskbarCreated)
        {
            Log($"TaskbarCreated: Explorer (re)started; our windows alive: {string.Join(",", _windows.Select(IsWindow))}");
            _retryDelayMs = 1000;
            PostMessage(hwnd, WM_APP_ATTACH, 0, 0);
            return 0;
        }
        switch (msg)
        {
            case WM_APP_ATTACH:
                if (TryAttach()) _retryDelayMs = 1000; else ScheduleRetry();
                return 0;
            case WM_APP_HANG:
                Log($"Simulating a hung window thread for {_hangFor}s");
                Thread.Sleep(_hangFor * 1000);
                Log("Window thread resumed");
                return 0;
            case WM_DISPLAYCHANGE:
                Log($"WM_DISPLAYCHANGE {w}bpp {(int)(l & 0xFFFF)}x{(int)(l >> 16)} - re-attaching");
                PostMessage(hwnd, WM_APP_ATTACH, 0, 0);
                return 0;
            case WM_SETTINGCHANGE:
                if (_target is not null)
                    foreach (var win in _windows)
                    {
                        var (ok, detail) = Verify(_target, win);
                        if (!ok) { Log($"WM_SETTINGCHANGE 0x{w:X}: verify FAIL - {detail}"); PostMessage(hwnd, WM_APP_ATTACH, 0, 0); break; }
                    }
                return 0;
            case WM_CLOSE:
                Shutdown();
                DestroyWindow(hwnd);
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    private nint WallpaperProc(nint hwnd, uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case WM_NCHITTEST: return HTTRANSPARENT;
            case WM_ERASEBKGND: return 1;
            case WM_PAINT: ValidateRect(hwnd, 0); return 0;
            case WM_DESTROY: Log($"WM_DESTROY on wallpaper window {Hex(hwnd)}"); return 0;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    private void Shutdown()
    {
        Log("Shutting down");
        _retryTimer?.Dispose(); _exitTimer?.Dispose(); _hangTimer?.Dispose();
        foreach (var w in _windows) if (IsWindow(w)) ShowWindow(w, SW_HIDE);
        _renderer.SetTargets(Array.Empty<TargetSpec>());
        _renderer.Dispose();
        foreach (var w in _windows) if (IsWindow(w)) { SetParent(w, 0); DestroyWindow(w); }

        if (_refreshOnExit)
        {
            // Repaint the system wallpaper without changing the user's setting (no SPIF_UPDATEINIFILE).
            var sb = new StringBuilder(260);
            SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
            var path = sb.ToString();
            if (path.Length > 0) Log($"System wallpaper refresh -> {SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, 0)}");
            else Log("No wallpaper path (solid colour?) - skipping refresh");
        }
    }
}
