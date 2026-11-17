// WallpaperHost: attaches one window per monitor behind the desktop icons and shows
// frames from the shared ring. It knows nothing about images, GIFs or video.
// Controlled by WallpaperDaemon over \\.\pipe\WinWallpaper.Host.v1 (JSON lines):
//   {"cmd":"show"} {"cmd":"hide"} {"cmd":"fit","mode":"fill|fit|stretch|center"}
//   {"cmd":"sampling","mode":"smooth|pixelated"} {"cmd":"status"} {"cmd":"quit"}

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using WallpaperHost;
using WallpaperProtocol;
using static WallpaperHost.Attachment;
using Log = WallpaperHost.Log;
using static WallpaperHost.Native;

using var instance = new Mutex(true, Names.HostMutex, out var isNew);
if (!isNew) { Log.Write("Another WallpaperHost is already running"); return 1; }
return new HostApp().Run();

internal sealed class HostApp
{
    private const uint WM_APP_ATTACH = WM_APP + 1, WM_APP_FRAME = WM_APP + 2, WM_APP_CMD = WM_APP + 3;

    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly WndProc _controlProc, _wallProc;
    private readonly Renderer _renderer;
    private readonly List<nint> _windows = new();
    private readonly ConcurrentQueue<(JsonObject Req, TaskCompletionSource<JsonObject> Done)> _cmds = new();
    private nint _control;
    private AttachTarget? _target;
    private int _retryDelayMs = 1000;
    private System.Threading.Timer? _retryTimer;
    private bool _wantVisible, _hasFrame;
    private string _fit = "fill";
    private string _sampling = "smooth";

    public HostApp()
    {
        _controlProc = ControlProc;
        _wallProc = WallpaperProc;
        _renderer = new Renderer(() => PostMessage(_control, WM_APP_FRAME, 0, 0));
    }

    public int Run()
    {
        var inst = GetModuleHandle(0);
        Register("WinWallpaperHostControl", _controlProc, inst);
        Register("WinWallpaperHostWindow", _wallProc, inst);
        // Hidden top-level window: message-only windows don't receive the TaskbarCreated broadcast.
        _control = CreateWindowEx(WS_EX_TOOLWINDOW, "WinWallpaperHostControl", "WinWallpaper host", WS_POPUP, 0, 0, 0, 0, 0, 0, inst, 0);
        Log.Write($"WallpaperHost started (pid {Environment.ProcessId})");

        _ = Task.Run(PipeServerLoop);
        PostMessage(_control, WM_APP_ATTACH, 0, 0);
        while (GetMessage(out var msg, 0, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        Log.Write($"WallpaperHost exiting. presented={_renderer.FramesPresented} rejected={_renderer.FramesRejected} torn={_renderer.FramesTorn}");
        return 0;
    }

    private static void Register(string name, WndProc proc, nint inst)
    {
        var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = inst, lpszClassName = name };
        if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + name);
    }

    // ---------------- desktop attachment ----------------

    private sealed record MonitorInfo(string Device, RECT Rect, bool Primary);

    private static List<MonitorInfo> EnumMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(0, 0, (nint h, nint _, ref RECT _, nint _) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(h, ref mi);
            list.Add(new MonitorInfo(mi.szDevice, mi.rcMonitor, (mi.dwFlags & 1) != 0));
            return true;
        }, 0);
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Device).ToList();
    }

    private bool TryAttach()
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0) { Log.Write("Progman not found (Explorer not running?)"); return false; }
        var t = Detect();
        if (t.Topology == Topology.Unsupported)
        {
            SpawnWorkerW(progman, "d"); // only when the wallpaper WorkerW doesn't exist yet
            t = Detect();
        }
        if (t.Topology == Topology.Unsupported) { Log.Write("Unsupported desktop window layout"); return false; }
        if (_target?.Parent != t.Parent) Log.Write($"Topology {t.Topology}: parent={Hex(t.Parent)} below={Hex(t.InsertAfter)}");
        _target = t;

        var monitors = EnumMonitors();
        for (var i = _windows.Count - 1; i >= 0; i--)
            if (!IsWindow(_windows[i]) || i >= monitors.Count) { if (IsWindow(_windows[i])) DestroyWindow(_windows[i]); _windows.RemoveAt(i); }

        var specs = new List<TargetSpec>();
        for (var i = 0; i < monitors.Count; i++)
        {
            var rc = monitors[i].Rect;
            if (i >= _windows.Count)
            {
                _windows.Add(CreateWindowEx(WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                    "WinWallpaperHostWindow", "WinWallpaper", WS_POPUP, 0, 0, rc.W, rc.H, 0, 0, GetModuleHandle(0), 0));
            }
            var w = _windows[i];
            if (GetParent(w) != t.Parent)
            {
                SetWindowLongPtr(w, GWL_STYLE, (nint)(WS_CHILD | WS_DISABLED | WS_CLIPSIBLINGS)); // child style before SetParent
                SetParent(w, t.Parent);
            }
            var r = rc;
            MapWindowPoints(0, t.Parent, ref r, 2);
            SetWindowPos(w, t.InsertAfter, r.Left, r.Top, rc.W, rc.H, SWP_NOACTIVATE);
            specs.Add(new TargetSpec(w, rc.W, rc.H));
        }
        _renderer.SetTargets(specs);
        ApplyVisibility();

        var ok = _windows.All(w => Verify(t, w).ok);
        Log.Write($"Attached {_windows.Count} monitor window(s): {(ok ? "verified" : "VERIFY FAILED")}");
        return ok;
    }

    private void ApplyVisibility()
    {
        var show = _wantVisible && _hasFrame;
        foreach (var w in _windows) if (IsWindow(w)) ShowWindow(w, show ? SW_SHOWNA : SW_HIDE);
    }

    // ---------------- window procs ----------------

    private nint ControlProc(nint hwnd, uint msg, nint w, nint l)
    {
        if (msg == _taskbarCreated)
        {
            Log.Write("Explorer restarted; re-attaching");
            _retryDelayMs = 1000;
            PostMessage(hwnd, WM_APP_ATTACH, 0, 0);
            return 0;
        }
        switch (msg)
        {
            case WM_APP_ATTACH:
                if (TryAttach()) _retryDelayMs = 1000;
                else
                {
                    _retryTimer?.Dispose();
                    _retryTimer = new System.Threading.Timer(_ => PostMessage(_control, WM_APP_ATTACH, 0, 0), null, _retryDelayMs, Timeout.Infinite);
                    _retryDelayMs = Math.Min(_retryDelayMs * 2, 30_000);
                }
                return 0;
            case WM_APP_FRAME:
                _hasFrame = true;
                ApplyVisibility();
                return 0;
            case WM_APP_CMD:
                while (_cmds.TryDequeue(out var c))
                {
                    try { c.Done.SetResult(Execute(c.Req)); }
                    catch (Exception ex) { c.Done.SetResult(JsonPipe.Error(ex.Message)); }
                }
                return 0;
            case WM_DISPLAYCHANGE:
                PostMessage(hwnd, WM_APP_ATTACH, 0, 0); // idempotent; unchanged monitors are left alone
                return 0;
            case WM_SETTINGCHANGE:
                if (_target is not null && _windows.Any(win => !Verify(_target, win).ok)) PostMessage(hwnd, WM_APP_ATTACH, 0, 0);
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

    private static nint WallpaperProc(nint hwnd, uint msg, nint w, nint l) => msg switch
    {
        WM_NCHITTEST => HTTRANSPARENT,
        WM_ERASEBKGND => 1,
        WM_PAINT => ValidateRect(hwnd, 0) ? 0 : 0,
        _ => DefWindowProc(hwnd, msg, w, l),
    };

    private void Shutdown()
    {
        _retryTimer?.Dispose();
        foreach (var w in _windows) if (IsWindow(w)) ShowWindow(w, SW_HIDE);
        _renderer.SetTargets(Array.Empty<TargetSpec>());
        _renderer.Dispose();
        foreach (var w in _windows) if (IsWindow(w)) { SetParent(w, 0); DestroyWindow(w); }
        RefreshSystemWallpaper();
    }

    private static void RefreshSystemWallpaper()
    {
        var sb = new StringBuilder(260);
        SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
        if (sb.Length > 0) SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, sb.ToString(), 0); // repaint only, no SPIF_UPDATEINIFILE
    }

    // ---------------- daemon control pipe ----------------

    private JsonObject Execute(JsonObject req)
    {
        switch ((string?)req["cmd"])
        {
            case "show":
                _wantVisible = true;
                ApplyVisibility();
                return JsonPipe.Ok();
            case "hide":
                _wantVisible = false;
                _hasFrame = false;
                _renderer.ResetFirstFrame();
                _renderer.ClearFrame();
                ApplyVisibility();
                return JsonPipe.Ok();
            case "fit":
                var mode = ((string?)req["mode"] ?? "fill").ToLowerInvariant();
                _renderer.SetFit(mode switch { "fit" => FitMode.Fit, "stretch" => FitMode.Stretch, "center" => FitMode.Center, _ => FitMode.Fill });
                _fit = mode;
                return JsonPipe.Ok();
            case "sampling":
                _sampling = (string?)req["mode"] == "pixelated" ? "pixelated" : "smooth";
                _renderer.SetSampling(_sampling == "pixelated");
                return JsonPipe.Ok();
            case "status":
                return JsonPipe.Ok(new JsonObject
                {
                    ["visible"] = _wantVisible && _hasFrame,
                    ["monitors"] = _windows.Count,
                    ["topology"] = _target?.Topology.ToString() ?? "None",
                    ["fit"] = _fit,
                    ["sampling"] = _sampling,
                    ["framesPresented"] = _renderer.FramesPresented,
                    ["framesRejected"] = _renderer.FramesRejected,
                    ["framesTorn"] = _renderer.FramesTorn,
                });
            case "quit":
                PostMessage(_control, WM_CLOSE, 0, 0);
                return JsonPipe.Ok();
            default:
                return JsonPipe.Error("unknown cmd");
        }
    }

    private async Task PipeServerLoop()
    {
        while (true)
        {
            try
            {
                var server = new NamedPipeServerStream(Names.HostPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                Log.Write("Daemon connected");
                using var pipe = new JsonPipe(server);
                while (await pipe.ReceiveAsync() is { } req)
                {
                    var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _cmds.Enqueue((req, tcs));
                    PostMessage(_control, WM_APP_CMD, 0, 0); // run on the window thread
                    await pipe.SendAsync(await tcs.Task);
                }
                Log.Write("Daemon disconnected (wallpaper stays as is)");
            }
            catch (Exception ex) { Log.Write("Pipe error: " + ex.Message); await Task.Delay(500); }
        }
    }
}
