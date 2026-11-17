using System.Runtime.InteropServices;
using System.Text;

namespace WallpaperHost;

// Minimal hand-written Win32 interop for the spike.
internal static unsafe class Native
{
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint WS_POPUP = 0x80000000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000,
        WS_DISABLED = 0x08000000, WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000,
        WS_EX_NOREDIRECTIONBITMAP = 0x200000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const uint SMTO_NORMAL = 0x0, SMTO_ABORTIFHUNG = 0x2;
    public const uint WM_DESTROY = 0x2, WM_PAINT = 0xF, WM_ERASEBKGND = 0x14, WM_NCHITTEST = 0x84,
        WM_DISPLAYCHANGE = 0x7E, WM_SETTINGCHANGE = 0x1A, WM_CLOSE = 0x10, WM_APP = 0x8000;
    public const int HTTRANSPARENT = -1;
    public const uint GW_HWNDNEXT = 2, GW_CHILD = 5;
    public const uint LWA_ALPHA = 0x2;
    public const uint SPI_GETDESKWALLPAPER = 0x73, SPI_SETDESKWALLPAPER = 0x14;
    public const int SW_HIDE = 0, SW_SHOWNA = 8;

    public delegate bool EnumProc(nint hwnd, nint lParam);
    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int W => Right - Left; public int H => Bottom - Top; public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {W}x{H}"; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName; public nint hIconSm;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }

    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string? cls, string? name);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindowEx(nint parent, nint after, string? cls, string? name);
    [DllImport("user32")] public static extern bool EnumWindows(EnumProc f, nint l);
    [DllImport("user32")] public static extern bool EnumChildWindows(nint parent, EnumProc f, nint l);
    [DllImport("user32")] public static extern nint GetParent(nint h);
    [DllImport("user32")] public static extern nint GetWindow(nint h, uint cmd);
    [DllImport("user32")] public static extern bool IsWindow(nint h);
    [DllImport("user32")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint h, StringBuilder sb, int n);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint h, int idx);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint h, int idx, nint v);
    [DllImport("user32")] public static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32")] public static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("user32", SetLastError = true)] public static extern nint SetParent(nint child, nint parent);
    [DllImport("user32")] public static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32")] public static extern bool DestroyWindow(nint h);
    [DllImport("user32")] public static extern int MapWindowPoints(nint from, nint to, ref RECT pts, int n);
    [DllImport("user32")] public static extern nint SendMessageTimeout(nint h, uint msg, nint w, nint l, uint flags, uint timeout, out nint result);
    [DllImport("user32")] public static extern bool PostMessage(nint h, uint msg, nint w, nint l);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint DefWindowProc(nint h, uint msg, nint w, nint l);
    [DllImport("user32")] public static extern int GetMessage(out MSG m, nint h, uint min, uint max);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] public static extern nint DispatchMessage(ref MSG m);
    [DllImport("user32")] public static extern void PostQuitMessage(int code);
    [DllImport("user32")] public static extern bool ValidateRect(nint h, nint r);
    [DllImport("user32")] public static extern bool SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);
    [DllImport("user32")] public static extern nint MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(nint mon, ref MONITORINFOEX mi);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder? pv, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, string pv, uint flags);
    [DllImport("user32")] public static extern bool ChangeWindowMessageFilterEx(nint h, uint msg, uint action, nint p);
    public delegate bool MonitorEnumProc(nint mon, nint hdc, ref RECT rc, nint l);
    [DllImport("user32")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc f, nint l);
    [DllImport("shcore")] public static extern int GetDpiForMonitor(nint mon, int type, out uint dpiX, out uint dpiY);
    [DllImport("kernel32")] public static extern nint GetModuleHandle(nint name);
    [DllImport("kernel32")] public static extern uint GetCurrentThreadId();

    public static string ClassOf(nint h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static uint Style(nint h) => (uint)GetWindowLongPtr(h, GWL_STYLE);
    public static uint ExStyle(nint h) => (uint)GetWindowLongPtr(h, GWL_EXSTYLE);
    public static string Hex(nint h) => "0x" + h.ToString("X");

    public static List<nint> Children(nint parent)
    {
        // Direct children only, in z-order (top first).
        var list = new List<nint>();
        for (var c = GetWindow(parent, GW_CHILD); c != 0; c = GetWindow(c, GW_HWNDNEXT)) list.Add(c);
        return list;
    }
}
