namespace WallpaperHost;

/// <summary>Tiny file + console logger: %LOCALAPPDATA%\WinWallpaper\logs\host.log (truncated at start).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly StreamWriter? File = Open();

    private static StreamWriter? Open()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinWallpaper", "logs");
            Directory.CreateDirectory(dir);
            return new StreamWriter(Path.Combine(dir, "host.log"), append: false) { AutoFlush = true };
        }
        catch { return null; }
    }

    public static void Write(string s)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {s}";
        lock (Gate)
        {
            Console.WriteLine(line);
            File?.WriteLine(line);
        }
    }
}
