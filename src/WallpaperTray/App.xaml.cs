using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace WallpaperTray;

/// <summary>
/// Tray app: the control surface. It never renders; closing it leaves the daemon and host running.
///   WallpaperTray.exe               start (or focus the running instance) and open the window
///   WallpaperTray.exe --background  start in the tray only (for sign-in startup)
/// </summary>
public partial class App : Application
{
    private const string InstanceMutex = @"Local\WinWallpaper.Tray.Instance";
    private const string ShowEvent = @"Local\WinWallpaper.Tray.Show";

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private Window? _menuHost;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, InstanceMutex, out var isNew);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        if (!isNew) { _showSignal.Set(); Shutdown(); return; } // focus the running instance instead

        _window = new MainWindow();
        new Thread(() => { while (_showSignal.WaitOne()) Dispatcher.Invoke(() => _window.ShowNearTray()); })
            { IsBackground = true, Name = "show-signal" }.Start();

        _tray = new Forms.NotifyIcon { Icon = Visuals.TrayIcon(), Text = "WinWallpaper", Visible = true };
        _tray.MouseClick += (_, me) =>
        {
            if (me.Button == Forms.MouseButtons.Left) ToggleWindow();
            else if (me.Button == Forms.MouseButtons.Right) _ = ShowMenuAsync();
        };

        // Bring the wallpaper back at sign-in even if nobody opens the window.
        if (await DaemonClient.TryStatusAsync() is null)
        {
            try { DaemonClient.StartDaemon(); } catch (Exception ex) { _tray.ShowBalloonTip(5000, "WinWallpaper", ex.Message, Forms.ToolTipIcon.Error); }
        }
        if (!e.Args.Contains("--background")) _window.ShowNearTray();
    }

    private void ToggleWindow()
    {
        if (_window!.IsVisible && _window.IsActive) _window.Hide();
        else _window.ShowNearTray();
    }

    // ---------------- right-click menu (Fluent-styled WPF menu, not the WinForms one) ----------------

    private async Task ShowMenuAsync()
    {
        var status = await DaemonClient.TryStatusAsync();
        var state = (string?)status?["state"] ?? "offline";

        var menu = new ContextMenu();
        var current = state switch
        {
            "playing" or "paused" => System.IO.Path.GetFileName((string?)status?["source"]?["path"]) ?? "",
            "external" => (string?)status?["external"]?["name"] ?? "Custom renderer",
            "stopped" => "Nothing playing",
            _ => "Daemon offline",
        };
        menu.Items.Add(new MenuItem { Header = current, IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open WinWallpaper", "\uE8A7", () => _window!.ShowNearTray()));
        if (state is "playing" or "paused")
            menu.Items.Add(Item(state == "paused" ? "Resume" : "Pause", state == "paused" ? "\uE768" : "\uE769",
                () => _ = Send(state == "paused" ? "resume" : "pause")));
        if (state is "playing" or "paused" or "external")
            menu.Items.Add(Item("Stop live wallpaper", "\uE71A", () => _ = Send("stop")));
        if (status?["overlay"] is System.Text.Json.Nodes.JsonObject ov)
            menu.Items.Add(Item($"Remove overlay ({ov["name"]})", char.ConvertFromUtf32(0xE711), () => _ = Send("overlay.stop")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close tray icon", "\uE711", ExitTray));
        menu.Items.Add(Item("Quit WinWallpaper", "\uE7E8", () => _ = QuitAll()));

        // A menu needs a foreground window to close properly when you click elsewhere.
        _menuHost ??= new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, Width = 0, Height = 0,
            AllowsTransparency = true, Background = null, Topmost = true,
        };
        var pt = Forms.Cursor.Position;
        var dpi = VisualTreeHelperDpi();
        _menuHost.Left = pt.X / dpi; _menuHost.Top = pt.Y / dpi;
        _menuHost.Show();
        _menuHost.Activate();
        menu.PlacementTarget = _menuHost;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) => _menuHost.Hide();
        menu.IsOpen = true;
    }

    /// <summary>Cursor position is in physical pixels; WPF window coordinates are DIPs.</summary>
    private static double VisualTreeHelperDpi()
    {
        using var g = System.Drawing.Graphics.FromHwnd(0);
        return g.DpiX / 96.0;
    }

    private static MenuItem Item(string header, string glyph, Action action)
    {
        var mi = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)Current.Resources["IconFont"], FontSize = 14 },
        };
        mi.Click += (_, _) => action();
        return mi;
    }

    private async Task Send(string cmd)
    {
        try { await DaemonClient.SendAsync(cmd); } catch (Exception ex) { _tray?.ShowBalloonTip(4000, "WinWallpaper", ex.Message, Forms.ToolTipIcon.Warning); }
        if (_window?.IsVisible == true) await _window.RefreshAsync();
    }

    private void ExitTray()
    {
        // The daemon and host keep the wallpaper running without us.
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        if (_window is not null) { _window.AllowClose = true; _window.Close(); }
        Shutdown();
    }

    private async Task QuitAll()
    {
        try { await DaemonClient.RequestAsync(new JsonObject { ["cmd"] = "quit" }, startIfNeeded: false); } catch { }
        ExitTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
