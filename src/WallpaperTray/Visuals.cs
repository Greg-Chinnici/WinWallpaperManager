using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WallpaperTray;

/// <summary>App icon (from assets/WinWallpaper.ico) and Explorer thumbnails for any file type.</summary>
internal static class Visuals
{
    // Single source for the icon: assets/WinWallpaper.ico (regenerate with build/make-icon.ps1).
    private static readonly Uri IconUri = new("pack://application:,,,/WinWallpaper.ico");

    public static Icon TrayIcon()
    {
        using var stream = Application.GetResourceStream(IconUri)!.Stream;
        return new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize); // DPI-scaled small icon
    }

    /// <summary>The icon frame closest to (but not smaller than) <paramref name="size"/> pixels.</summary>
    public static BitmapSource WindowIcon(int size = 64)
    {
        var decoder = new IconBitmapDecoder(IconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderBy(f => f.PixelWidth < size ? int.MaxValue : f.PixelWidth).First();
    }

    // ---------------- Explorer thumbnails (images, GIFs, videos alike) ----------------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out nint phbm); }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [DllImport("shell32", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);

    [DllImport("gdi32")] private static extern bool DeleteObject(nint h);

    public static BitmapSource? Thumbnail(string path, int width, int height)
    {
        try
        {
            SHCreateItemFromParsingName(path, 0, typeof(IShellItemImageFactory).GUID, out var factory);
            // SIIGBF_BIGGERSIZEOK (0x1) | SIIGBF_THUMBNAILONLY (0x8)
            if (factory.GetImage(new SIZE { cx = width, cy = height }, 0x1 | 0x8, out var hbm) != 0 || hbm == 0) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
    }
}
