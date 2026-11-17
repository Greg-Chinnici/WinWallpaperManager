using static WallpaperHost.Native;

namespace WallpaperHost;

internal enum Topology { Unsupported, Modern, Legacy }

/// <summary>Where a wallpaper window should be parented and what it goes beneath.</summary>
internal sealed record AttachTarget(Topology Topology, nint Progman, nint Parent, nint InsertAfter, nint DefView, nint SystemWorkerW);

internal static class Attachment
{
    public const uint WM_SPAWN_WORKERW = 0x052C;

    /// <summary>Ask Explorer to create the wallpaper WorkerW. Never blocks more than 1s.</summary>
    public static bool SpawnWorkerW(nint progman, string variant)
    {
        var (w, l) = variant == "z" ? (0, 0) : (0xD, 0x1);
        var ok = SendMessageTimeout(progman, WM_SPAWN_WORKERW, w, l, SMTO_NORMAL | SMTO_ABORTIFHUNG, 1000, out _);
        Log($"SendMessageTimeout(Progman, 0x052C, 0x{w:X}, 0x{l:X}) -> {(ok != 0 ? "ok" : "TIMEOUT/FAIL")}");
        return ok != 0;
    }

    public static AttachTarget Detect()
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0) return new(Topology.Unsupported, 0, 0, 0, 0, 0);

        // Modern (24H2+): DefView and WorkerW are both children of Progman.
        var defView = FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        var workerW = FindWindowEx(progman, 0, "WorkerW", null);
        if (defView != 0 && workerW != 0)
            return new(Topology.Modern, progman, progman, defView, defView, workerW);

        // Legacy: top-level WorkerW holding DefView; target is the next top-level WorkerW.
        nint legacyTarget = 0, legacyDefView = 0;
        EnumWindows((h, _) =>
        {
            var dv = FindWindowEx(h, 0, "SHELLDLL_DefView", null);
            if (dv != 0 && ClassOf(h) == "WorkerW")
            {
                legacyDefView = dv;
                legacyTarget = FindWindowEx(0, h, "WorkerW", null);
                return false;
            }
            return true;
        }, 0);
        if (legacyTarget != 0)
            return new(Topology.Legacy, progman, legacyTarget, 0 /* HWND_TOP */, legacyDefView, legacyTarget);

        return new(Topology.Unsupported, progman, 0, 0, defView, 0);
    }

    /// <summary>Checks parent and z-order. Returns a human-readable verdict.</summary>
    public static (bool ok, string detail) Verify(AttachTarget t, nint ours)
    {
        if (!IsWindow(ours)) return (false, "our window no longer exists");
        if (!IsWindow(t.Parent)) return (false, "parent no longer exists");
        if (GetParent(ours) != t.Parent) return (false, $"parent is {Hex(GetParent(ours))}, expected {Hex(t.Parent)}");
        if (t.Topology == Topology.Modern)
        {
            var order = Children(t.Parent);
            int iDef = order.IndexOf(t.DefView), iOurs = order.IndexOf(ours), iWorker = order.IndexOf(t.SystemWorkerW);
            if (!(iDef >= 0 && iOurs > iDef && (iWorker < 0 || iOurs < iWorker)))
                return (false, $"z-order wrong: DefView={iDef} ours={iOurs} WorkerW={iWorker}");
            return (true, $"z-order ok: DefView={iDef} ours={iOurs} WorkerW={iWorker}");
        }
        return (true, "parent ok");
    }

    public static void DumpTree()
    {
        var progman = FindWindow("Progman", null);
        Log($"Progman {Hex(progman)} style=0x{Style(progman):X8} exstyle=0x{ExStyle(progman):X8}");
        DumpChildren(progman, 1);
        var n = 0;
        EnumWindows((h, _) =>
        {
            if (ClassOf(h) == "WorkerW")
            {
                n++;
                if (Children(h).Count > 0 || IsWindowVisible(h))
                {
                    Log($"top-level WorkerW {Hex(h)} visible={IsWindowVisible(h)} exstyle=0x{ExStyle(h):X8}");
                    DumpChildren(h, 1);
                }
            }
            return true;
        }, 0);
        Log($"({n} top-level WorkerW windows total; only visible or non-empty ones shown)");
    }

    private static void DumpChildren(nint parent, int depth)
    {
        foreach (var c in Children(parent))
        {
            GetWindowRect(c, out var r);
            Log($"{new string(' ', depth * 2)}[{Children(parent).IndexOf(c)}] {ClassOf(c)} {Hex(c)} visible={IsWindowVisible(c)} style=0x{Style(c):X8} exstyle=0x{ExStyle(c):X8} rect={r}");
            if (depth < 2) DumpChildren(c, depth + 1);
        }
    }

    private static void Log(string s) => WallpaperHost.Log.Write(s);
}
