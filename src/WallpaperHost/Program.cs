// WallpaperHost: one window per monitor on each of two layers (desktop, overlay), showing frames
// from producer sessions. It knows nothing about images, GIFs or video.
// Controlled by WallpaperDaemon over \\.\pipe\WinWallpaper.Host.v2 (JSON lines):
//   {"cmd":"session.create","layer":"desktop|overlay","transport":"cpu|gpu","width":..,"height":..,"alpha":"opaque|premultiplied","pid":..}
//   {"cmd":"session.activate","id":..}  {"cmd":"session.destroy","id":..}
//   {"cmd":"fit","mode":"fill|fit|stretch|center"}  {"cmd":"sampling","mode":"smooth|pixelated"}
//   {"cmd":"status"}  {"cmd":"quit"}

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using WallpaperHost;
using WallpaperProtocol;
using static WallpaperHost.Attachment;
using static WallpaperHost.Native;
using Log = WallpaperHost.Log;

using var instance = new Mutex(true, Names.HostMutex, out var isNew);
if (!isNew) { Log.Write("Another WallpaperHost is already running"); return 1; }
return new HostApp().Run();

internal sealed class HostApp
{
    private const uint WM_APP_ATTACH = WM_APP + 1, WM_APP_LAYER = WM_APP + 2, WM_APP_CMD = WM_APP + 3;

    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly WndProc _controlProc, _wallProc;
    private readonly Renderer _renderer;
    private readonly List<nint> _desktopWindows = new(), _overlayWindows = new();
    private readonly bool[] _layerVisible = new bool[2];
    private readonly ConcurrentQueue<(JsonObject Req, TaskCompletionSource<JsonObject> Done)> _cmds = new();
    private nint _control;
    private AttachTarget? _target;
    private int _retryDelayMs = 1000;
    private System.Threading.Timer? _retryTimer;
    private string _fit = "fill", _sampling = "smooth";

    public HostApp()
    {
        _controlProc = ControlProc;
        _wallProc = WallpaperProc;
        _renderer = new Renderer((layer, visible) => PostMessage(_control, WM_APP_LAYER, (nint)layer, visible ? 1 : 0));
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
        Log.Write("WallpaperHost exiting");
        return 0;
    }

    private static void Register(string name, WndProc proc, nint inst)
    {
        var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = inst, lpszClassName = name };
        if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + name);
    }

    // ---------------- windows ----------------

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
        var monitors = EnumMonitors();
        SyncOverlayWindows(monitors); // independent of Explorer, so do it first

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

        for (var i = _desktopWindows.Count - 1; i >= 0; i--)
            if (!IsWindow(_desktopWindows[i]) || i >= monitors.Count) { if (IsWindow(_desktopWindows[i])) DestroyWindow(_desktopWindows[i]); _desktopWindows.RemoveAt(i); }

        var specs = new List<TargetSpec>();
        for (var i = 0; i < monitors.Count; i++)
        {
            var rc = monitors[i].Rect;
            if (i >= _desktopWindows.Count)
                _desktopWindows.Add(CreateWindowEx(WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                    "WinWallpaperHostWindow", "WinWallpaper", WS_POPUP, 0, 0, rc.W, rc.H, 0, 0, GetModuleHandle(0), 0));
            var w = _desktopWindows[i];
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
        _renderer.SetTargets(Layer.Desktop, specs);
        ApplyVisibility();

        var ok = _desktopWindows.All(w => Verify(t, w).ok);
        Log.Write($"Attached {_desktopWindows.Count} desktop window(s): {(ok ? "verified" : "VERIFY FAILED")}");
        return ok;
    }

    /// <summary>
    /// Overlay windows: top-level, top-most, click-through (WS_EX_LAYERED | WS_EX_TRANSPARENT), never
    /// activated, not in Alt+Tab. Content is a premultiplied-alpha DirectComposition visual.
    /// </summary>
    private void SyncOverlayWindows(List<MonitorInfo> monitors)
    {
        while (_overlayWindows.Count > monitors.Count) { DestroyWindow(_overlayWindows[^1]); _overlayWindows.RemoveAt(_overlayWindows.Count - 1); }
        var specs = new List<TargetSpec>();
        for (var i = 0; i < monitors.Count; i++)
        {
            var rc = monitors[i].Rect;
            if (i >= _overlayWindows.Count)
            {
                var w = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP,
                    "WinWallpaperHostWindow", "WinWallpaper overlay", WS_POPUP, rc.Left, rc.Top, rc.W, rc.H, 0, 0, GetModuleHandle(0), 0);
                SetLayeredWindowAttributes(w, 0, 255, LWA_ALPHA); // layered windows need this to be shown at all
                _overlayWindows.Add(w);
            }
            SetWindowPos(_overlayWindows[i], HWND_TOPMOST, rc.Left, rc.Top, rc.W, rc.H, SWP_NOACTIVATE);
            specs.Add(new TargetSpec(_overlayWindows[i], rc.W, rc.H));
        }
        _renderer.SetTargets(Layer.Overlay, specs);
    }

    private void ApplyVisibility()
    {
        foreach (var w in _desktopWindows) if (IsWindow(w)) ShowWindow(w, _layerVisible[0] ? SW_SHOWNA : SW_HIDE);
        foreach (var w in _overlayWindows)
        {
            if (_layerVisible[1]) SetWindowPos(w, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            else ShowWindow(w, SW_HIDE);
        }
    }

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
            case WM_APP_LAYER:
                _layerVisible[(int)w] = l != 0;
                ApplyVisibility();
                return 0;
            case WM_APP_CMD:
                while (_cmds.TryDequeue(out var c))
                {
                    if ((string?)c.Req["cmd"] == "quit") { c.Done.SetResult(JsonPipe.Ok()); PostMessage(_control, WM_CLOSE, 0, 0); }
                    else c.Done.SetResult(JsonPipe.Error("unknown cmd"));
                }
                return 0;
            case WM_DISPLAYCHANGE:
                PostMessage(hwnd, WM_APP_ATTACH, 0, 0); // idempotent; unchanged monitors are left alone
                return 0;
            case WM_SETTINGCHANGE:
                if (_target is not null && _desktopWindows.Any(win => !Verify(_target, win).ok)) PostMessage(hwnd, WM_APP_ATTACH, 0, 0);
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
        foreach (var w in _desktopWindows.Concat(_overlayWindows)) if (IsWindow(w)) ShowWindow(w, SW_HIDE);
        _renderer.SetTargets(Layer.Desktop, Array.Empty<TargetSpec>());
        _renderer.SetTargets(Layer.Overlay, Array.Empty<TargetSpec>());
        _renderer.Dispose();
        foreach (var w in _desktopWindows) if (IsWindow(w)) { SetParent(w, 0); DestroyWindow(w); }
        foreach (var w in _overlayWindows) if (IsWindow(w)) DestroyWindow(w);
        var sb = new StringBuilder(260); // repaint the system wallpaper without changing the setting
        SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
        if (sb.Length > 0) SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, sb.ToString(), 0);
    }

    // ---------------- daemon control pipe ----------------

    private async Task<JsonObject> ExecuteAsync(JsonObject req)
    {
        switch ((string?)req["cmd"])
        {
            case "session.create":
            {
                var d = await _renderer.CreateSessionAsync(
                    ProtocolText.ParseLayer((string?)req["layer"]), ProtocolText.ParseTransport((string?)req["transport"]),
                    req["width"]!.GetValue<int>(), req["height"]!.GetValue<int>(),
                    ProtocolText.ParseAlpha((string?)req["alpha"]), req["pid"]!.GetValue<int>());
                return JsonPipe.Ok(new JsonObject { ["session"] = d.ToJson() });
            }
            case "session.activate":
                await _renderer.ActivateAsync(req["id"]!.GetValue<int>());
                return JsonPipe.Ok();
            case "session.destroy":
                await _renderer.DestroyAsync(req["id"]!.GetValue<int>());
                return JsonPipe.Ok();
            case "fit":
                _fit = ((string?)req["mode"] ?? "fill").ToLowerInvariant();
                _renderer.SetFit(_fit switch { "fit" => FitMode.Fit, "stretch" => FitMode.Stretch, "center" => FitMode.Center, _ => FitMode.Fill });
                return JsonPipe.Ok();
            case "sampling":
                _sampling = (string?)req["mode"] == "pixelated" ? "pixelated" : "smooth";
                _renderer.SetSampling(_sampling == "pixelated");
                return JsonPipe.Ok();
            case "status":
            {
                var s = await _renderer.StatusAsync();
                s["monitors"] = _desktopWindows.Count;
                s["topology"] = _target?.Topology.ToString() ?? "None";
                s["fit"] = _fit;
                s["sampling"] = _sampling;
                return JsonPipe.Ok(s);
            }
            default:
                // Window-thread commands (quit).
                var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                _cmds.Enqueue((req, tcs));
                PostMessage(_control, WM_APP_CMD, 0, 0);
                return await tcs.Task;
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
                    JsonObject resp;
                    try { resp = await ExecuteAsync(req); }
                    catch (Exception ex) { resp = JsonPipe.Error(ex.Message); Log.Write($"{req["cmd"]} failed: {ex.Message}"); }
                    await pipe.SendAsync(resp);
                }
                Log.Write("Daemon disconnected (wallpaper stays as is)");
            }
            catch (Exception ex) { Log.Write("Pipe error: " + ex.Message); await Task.Delay(500); }
        }
    }
}
