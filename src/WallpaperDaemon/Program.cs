// WallpaperDaemon: owns wallpaper state, decodes sources into frames, and controls the host.
using Vortice.MediaFoundation;
using WallpaperDaemon;
using WallpaperProtocol;

using var instance = new Mutex(true, Names.DaemonMutex, out var isNew);
if (!isNew) { Log.Write("Another WallpaperDaemon is already running"); return 1; }

MediaFactory.MFStartup(true); // lite: no network sources
try
{
    Log.Write($"WallpaperDaemon started (pid {Environment.ProcessId}), pipe \\\\.\\pipe\\{Names.DaemonPipe}");
    using var daemon = new Daemon();
    await daemon.RunAsync();
}
finally { MediaFactory.MFShutdown(); }
return 0;

namespace WallpaperDaemon
{
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
                return new StreamWriter(Path.Combine(dir, "daemon.log"), append: false) { AutoFlush = true };
            }
            catch { return null; }
        }

        public static void Write(string s)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {s}";
            lock (Gate) { Console.WriteLine(line); File?.WriteLine(line); }
        }
    }
}
