using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WallpaperTray;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _fpsDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _updating;            // true while applying daemon state to controls (suppresses events)
    private string _state = "offline";
    private string? _kind, _previewPath;

    public bool AllowClose { get; set; }


    public MainWindow()
    {
        _updating = true;   // XAML pre-selects items, which would otherwise fire handlers and
        InitializeComponent(); // overwrite the daemon's saved fit/sampling on every launch
        _updating = false;
        var icon = Visuals.WindowIcon();
        Icon = icon;
        AppIcon.Source = icon;
        _poll.Tick += async (_, _) => await RefreshAsync();
        _fpsDebounce.Tick += async (_, _) => { _fpsDebounce.Stop(); await SendFpsAsync(); };
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { _poll.Start(); await RefreshAsync(); } else _poll.Stop(); // no polling while hidden
        };
    }

    /// <summary>Open bottom-right, above the taskbar, like a tray flyout.</summary>
    public void ShowNearTray()
    {
        if (!IsVisible)
        {
            var wa = SystemParameters.WorkArea;
            Show();
            UpdateLayout();
            Left = wa.Right - ActualWidth - 12;
            Top = Math.Max(wa.Top + 12, wa.Bottom - ActualHeight - 12);
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); } // closing just tucks it back into the tray
        base.OnClosing(e);
    }

    // ---------------- state ----------------

    public async Task RefreshAsync()
    {
        var s = await DaemonClient.TryStatusAsync();
        Apply(s);
    }

    private void Apply(JsonObject? s)
    {
        _updating = true;
        try
        {
            _state = s is null ? "offline" : (string?)s["state"] ?? "stopped";
            var (label, color) = _state switch
            {
                "playing" => ("Playing", "SystemFillColorSuccessBrush"),
                "paused" => ("Paused", "SystemFillColorCautionBrush"),
                "external" => ("Custom renderer", "accent"),
                "stopped" => ("Stopped", "TextFillColorTertiaryBrush"),
                _ => ("Daemon offline", "SystemFillColorCriticalBrush"),
            };
            StatusText.Text = label;
            StatusDot.Fill = color == "accent" ? SystemColors.AccentColorBrush : TryFindResource(color) as Brush ?? Brushes.Gray;

            var src = s?["source"] as JsonObject;
            _kind = (string?)src?["kind"];
            var path = (string?)src?["path"];
            if (_state == "external")
            {
                NowTitle.Text = (string?)s?["external"]?["name"] ?? "Custom renderer";
                NowDetail.Text = "Streaming frames over the daemon pipe";
            }
            else if (src is not null && _state != "stopped")
            {
                NowTitle.Text = Path.GetFileName(path);
                NowDetail.Text = Describe(src);
            }
            else
            {
                NowTitle.Text = "Nothing playing";
                NowDetail.Text = s is null ? "The wallpaper daemon isn't running — pick a wallpaper to start it" : "Pick an image, GIF or video below";
            }

            var showPreview = _state is "playing" or "paused" && path is not null;
            if (showPreview && path != _previewPath)
            {
                Preview.Source = Visuals.Thumbnail(path!, 640, 360);
                _previewPath = path;
            }
            if (!showPreview) { Preview.Source = null; _previewPath = null; }
            EmptyState.Visibility = Preview.Source is null ? Visibility.Visible : Visibility.Collapsed;
            var external = _state == "external";
            EmptyGlyph.Text = external ? "\uE943" : "\uE8B9"; // code / picture glyphs
            EmptyLabel.Text = external ? $"Live frames from {NowTitle.Text}" : "No live wallpaper";

            PauseButton.IsEnabled = _state is "playing" or "paused";
            StopButton.IsEnabled = _state is "playing" or "paused" or "external";
            PauseGlyph.Text = _state == "paused" ? "\uE768" : "\uE769";
            PauseButton.ToolTip = _state == "paused" ? "Resume" : "Pause";

            var animated = _kind is "gif" or "video" && _state is "playing" or "paused";
            FpsPanel.IsEnabled = animated;
            var native = src?["nativeFps"]?.GetValue<double>() ?? 0;
            FpsHint.Text = animated ? (native > 0 ? $"Original is {native:0.##} fps" : "Applies to GIFs and videos") : "Applies to GIFs and videos";
            if (animated)
            {
                var fps = src?["fps"]?.GetValue<double>() ?? 0;
                OriginalFps.IsChecked = fps <= 0;
                if (fps > 0) FpsSlider.Value = Math.Clamp(Math.Round(fps), 1, 60);
            }
            FpsSliderRow.IsEnabled = FpsPanel.IsEnabled && OriginalFps.IsChecked != true;
            FpsValue.Text = ((int)FpsSlider.Value).ToString();

            var fit = (string?)s?["fit"] ?? "fill";
            foreach (ComboBoxItem item in FitBox.Items) if ((string)item.Tag == fit) FitBox.SelectedItem = item;
            var sampling = (string?)s?["sampling"] ?? "smooth";
            foreach (ComboBoxItem item in SamplingBox.Items) if ((string)item.Tag == sampling) SamplingBox.SelectedItem = item;

            var err = (string?)s?["error"];
            ShowError(string.IsNullOrEmpty(err) ? null : err);
        }
        finally { _updating = false; }
    }

    private static string Describe(JsonObject src)
    {
        var kind = (string?)src["kind"];
        var size = $"{src["width"]}×{src["height"]}";
        var fps = src["fps"]?.GetValue<double>() ?? 0;
        var native = src["nativeFps"]?.GetValue<double>() ?? 0;
        var rate = fps > 0 ? $"{fps:0.##} fps" : native > 0 ? $"{native:0.##} fps (original)" : "";
        return kind switch
        {
            "gif" => $"GIF · {size} · {src["frames"]} frames · {rate}",
            "video" => $"Video · {size} · {rate}",
            _ => $"Image · {size}",
        };
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RunAsync(Func<Task<JsonObject>> call)
    {
        try
        {
            var resp = await call();
            if (resp["ok"]?.GetValue<bool>() != true) { ShowError((string?)resp["error"] ?? "Request failed"); return; }
            Apply(resp);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // ---------------- picking ----------------

    private Task PickAsync(string kind, string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return Task.CompletedTask;
        var fps = kind == "image" || OriginalFps.IsChecked == true ? 0 : FpsSlider.Value;
        return RunAsync(() => DaemonClient.SendAsync("set", o => { o["kind"] = kind; o["path"] = dlg.FileName; o["fps"] = fps; }));
    }

    private async void OnPickImage(object sender, RoutedEventArgs e) =>
        await PickAsync("image", "Choose an image", "Images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.heic;*.webp;*.jxr|All files|*.*");

    private async void OnPickGif(object sender, RoutedEventArgs e) =>
        await PickAsync("gif", "Choose an animated GIF", "GIF animations|*.gif");

    private async void OnPickVideo(object sender, RoutedEventArgs e) =>
        await PickAsync("video", "Choose a video", "Videos|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm|All files|*.*");

    // ---------------- playback ----------------

    private void OnFpsModeChanged(object sender, RoutedEventArgs e)
    {
        if (FpsSliderRow is null) return; // fires during InitializeComponent
        FpsSliderRow.IsEnabled = FpsPanel.IsEnabled && OriginalFps.IsChecked != true;
        if (!_updating) { _fpsDebounce.Stop(); _fpsDebounce.Start(); }
    }

    private void OnFpsSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FpsValue is null) return; // during InitializeComponent
        FpsValue.Text = ((int)e.NewValue).ToString();
        if (!_updating) { _fpsDebounce.Stop(); _fpsDebounce.Start(); }
    }

    private Task SendFpsAsync()
    {
        if (_kind is not ("gif" or "video")) return Task.CompletedTask;
        var fps = OriginalFps.IsChecked == true ? 0 : FpsSlider.Value;
        return RunAsync(() => DaemonClient.SendAsync("fps", o => o["fps"] = fps));
    }

    private async void OnFitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || FitBox.SelectedItem is not ComboBoxItem item) return;
        await RunAsync(() => DaemonClient.SendAsync("fit", o => o["mode"] = (string)item.Tag));
    }

    private async void OnSamplingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || SamplingBox?.SelectedItem is not ComboBoxItem item) return;
        await RunAsync(() => DaemonClient.SendAsync("sampling", o => o["mode"] = (string)item.Tag));
    }

    private async void OnPauseClick(object sender, RoutedEventArgs e) =>
        await RunAsync(() => DaemonClient.SendAsync(_state == "paused" ? "resume" : "pause"));

    private async void OnStopClick(object sender, RoutedEventArgs e) =>
        await RunAsync(() => DaemonClient.SendAsync("stop"));

    // ---------------- developers ----------------

    private void OnCopyPipe(object sender, RoutedEventArgs e) => Clipboard.SetText(PipeName.Text);

    private void OnOpenGuide(object sender, RoutedEventArgs e)
    {
        var guide = FindGuide();
        if (guide is null) { ShowError("Couldn't find docs\\custom-render-outputs.md next to the app."); return; }
        Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
    }

    /// <summary>The guide ships in docs/ (next to the exe in a release, at the repo root in a dev build).</summary>
    private static string? FindGuide()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "custom-render-outputs.html");
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "docs", "custom-render-outputs.md");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
