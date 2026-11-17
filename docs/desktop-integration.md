# WallpaperHost — Desktop Integration Strategy

How `WallpaperHost.exe` puts pixels *behind the desktop icons and above the
system wallpaper* on Windows 11, and keeps them there.

Status: **design, spike validated on Windows 11 build 26200 (2026-11-16).** See
§13 for results. Items still marked **[verify]** were not covered by the spike.

---

## 1. Decision summary

| Concern | Decision |
|---|---|
| Attachment technique | Reparent our own HWNDs into Explorer's desktop window tree (Progman / WorkerW) |
| Windows versions | Detect the window *topology* at runtime, not the build number. Two layouts: **Modern** (24H2+) and **Legacy** (≤ 23H2) |
| Windows per monitor | One wallpaper HWND per monitor, each with its own swap chain |
| Presentation | DXGI flip-model composition swap chain → DirectComposition visual on our HWND (`WS_EX_NOREDIRECTIONBITMAP`). Fallback: flip-model HWND swap chain |
| Threading | Window/message thread never blocks. D3D + Present happen on a render thread |
| Input | Wallpaper windows never take input (disabled, no-activate). Interactive wallpapers are out of scope |
| Explorer restart | Hidden top-level control window listens for `TaskbarCreated`; host re-attaches automatically |
| Display changes | Re-enumerate on `WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE` / DPI change; key monitors by stable device path |
| Exit / pause | Hide our windows and force Explorer to repaint the user's real wallpaper. Never change the user's wallpaper setting |
| Visibility facts | Host *reports* per-monitor visibility (occluded/full-screen app); the daemon *decides* whether to pause |

---

## 2. Background: how the desktop is built

The desktop you see is a stack of Explorer-owned windows:

```
 top    ┌─────────────────────────────┐
        │ SHELLDLL_DefView            │  icons (SysListView32), transparent background
        ├─────────────────────────────┤
        │ ← our wallpaper goes here → │
        ├─────────────────────────────┤
        │ WorkerW                     │  paints the static wallpaper / slideshow transitions
 bottom └─────────────────────────────┘
        Progman (root "Program Manager")
```

The `WorkerW` that paints the wallpaper only exists after Progman receives the
undocumented message **`0x052C`** (the same message Explorer uses for animated
wallpaper slideshow transitions). Every mainstream live-wallpaper tool relies on
this message. It is undocumented, so the integration is isolated behind one
interface (§9) and verified on every startup.

### Two topologies

**Legacy (Windows 10, Windows 11 ≤ 23H2)** — after `0x052C`:

```
(top-level) WorkerW  ── SHELLDLL_DefView ── SysListView32     ← icons
(top-level) WorkerW                                           ← wallpaper target: parent to THIS
(top-level) Progman
```

Target = the top-level `WorkerW` that is the next sibling after the `WorkerW`
containing `SHELLDLL_DefView`.

**Modern (Windows 11 24H2+)** — `WorkerW` and `SHELLDLL_DefView` are both
**children of Progman**:

```
Progman  (WS_EX_NOREDIRECTIONBITMAP)
├── SHELLDLL_DefView  (WS_EX_LAYERED)   ← icons, top of child z-order
├── [our wallpaper HWNDs]               ← inserted here
└── WorkerW                             ← system wallpaper
```

Target = parent to **Progman**, z-order **directly below `SHELLDLL_DefView`**
(`SetWindowPos(ours, defView, …, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`).

### Observed on the dev machine (Windows 11 build 26200, before sending `0x052C`)

```
Progman            exstyle=0x00200080  (WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW)
└─ SHELLDLL_DefView exstyle=0x00080000  (WS_EX_LAYERED)
   └─ SysListView32
      └─ SysHeader32
```

This confirms the Modern topology on our target OS. Two consequences:

- Progman has **no redirection bitmap**, so GDI painting into our child window
  is not a viable path. We present through DXGI/DirectComposition only.
- `SHELLDLL_DefView` is a **layered** child, which is what lets windows beneath
  it show through around the icons.

---

## 3. Attach algorithm

Run at startup, after Explorer restarts, and whenever verification fails.

```
1. progman = FindWindow("Progman", null)
   - null → Explorer not running. Wait for TaskbarCreated (§6). Do not spin.

2. SendMessageTimeout(progman, 0x052C, 0xD, 0x1, SMTO_NORMAL, 1000ms)
   - Both 0xD,0x1 and 0,0 create the WorkerW on a fresh Explorer, build 26200
     (spike). Use 0xD,0x1; 0,0 is a harmless fallback.
   - Only send it when Detect() finds no WorkerW yet.
   - A WM_DISPLAYCHANGE arrives ~10 ms after startup *even when 0x052C is not
     sent* (spike, second round) — it coincides with D3D/DXGI device creation,
     source unconfirmed. Either way: attach MUST be idempotent (re-running it on
     an attached window just re-positions it) and display-change handling should
     be debounced and compare monitor rects before rebinding anything.
   - Timeout is mandatory: Explorer may be hung; never block forever.

3. Detect topology:
   a. defView = FindWindowEx(progman, 0, "SHELLDLL_DefView", null)
      workerW = FindWindowEx(progman, 0, "WorkerW", null)
      → both non-null  ⇒ MODERN. parent = progman, insertAfter = defView
   b. else EnumWindows: find top-level WorkerW W with child SHELLDLL_DefView,
      target = FindWindowEx(null, W, "WorkerW", null)
      → non-null       ⇒ LEGACY. parent = target, insertAfter = HWND_TOP
   c. else             ⇒ UNSUPPORTED. Report AttachFailed to daemon; retry with
      backoff (1s, 2s, 5s, 10s, cap 30s). No fallback to a desktop-covering window.

4. For each monitor (§5): create or reuse wallpaper HWND, SetParent(hwnd, parent),
   position it in parent-client coordinates, SetWindowPos(... insertAfter ...).

5. Verify (§3.1). On failure → go to 3c.
```

### 3.1 Verification

After attaching, and after any event that could disturb the tree:

- `GetParent(hwnd) == expectedParent`
- For MODERN: `GetWindow(defView, GW_HWNDNEXT)` chain reaches our HWNDs before `WorkerW`.
- `IsWindow(parent)` still true.

Verification is triggered by events (Explorer restart, display change, our window
receiving `WM_WINDOWPOSCHANGED` from an external z-order change), never on a timer.

---

## 4. The wallpaper window

One class, one instance per monitor.

| Attribute | Value | Why |
|---|---|---|
| Created as | `WS_POPUP` top-level, hidden; then `SetParent` + swap `WS_POPUP`→`WS_CHILD` | Lets us build the swap chain before attaching and survive detach/re-attach without recreating D3D resources |
| Styles | `WS_CHILD \| WS_DISABLED \| WS_CLIPSIBLINGS` | No input, doesn't paint over siblings |
| Ex-styles | `WS_EX_NOREDIRECTIONBITMAP \| WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW` | DComp-only content; never activates; never in Alt-Tab |
| `WM_NCHITTEST` | return `HTTRANSPARENT` | Belt-and-braces: clicks fall through to Explorer |
| `WM_ERASEBKGND` / `WM_PAINT` | validate and return | All content comes from DComp |
| Layered? | No. The spike rendered correctly without `WS_EX_LAYERED` in both present modes on build 26200. Keep `--layered` in the spike as a diagnostic only | Resolved |

### 4.1 Cross-process parenting caveat (important)

Parenting our HWND under an Explorer window is legal, but Windows **attaches the
input queues** of the two threads. If our window thread stalls, Explorer's desktop
(icons, right-click menu) can stall too, and vice versa.

Rules:

- The thread that owns wallpaper HWNDs does **only** message pumping and cheap
  window management. No IPC waits, no file I/O, no D3D Present, no locks shared
  with the render thread that might be held for long.
- All `SendMessage` to Explorer uses `SendMessageTimeout` with `SMTO_ABORTIFHUNG`.
- Rendering runs on a separate thread (§7).

Spike (build 26200): the coupling did **not** show up in either direction.
With our window thread blocked for 12 s, Explorer answered `WM_NULL` in 0 ms
and the desktop right-click menu opened in 60 ms (baseline 55 ms). With
Explorer suspended for 5 s, our window thread still answered in 0 ms and the
render thread kept presenting at full rate. Likely because our windows are
disabled and hit-test transparent, so no input is ever routed to our thread.
Keep the rules anyway — they're cheap, and an input-receiving window
(interactive wallpapers) would change this.

---

## 5. Monitors and coordinates

- Host process is **Per-Monitor V2 DPI aware** (manifest), so all coordinates are
  physical pixels and Windows never bitmap-scales our surfaces.
- Enumerate with `EnumDisplayMonitors` + `GetMonitorInfo` (use `rcMonitor`, not
  `rcWork` — the wallpaper goes under the taskbar).
- Identify monitors by **device path** from `QueryDisplayConfig` +
  `DisplayConfigGetDeviceInfo(DISPLAYCONFIG_TARGET_DEVICE_NAME).monitorDevicePath`.
  `HMONITOR` values are not stable across display changes. The device path is the
  monitor ID used in the protocol (`SetMonitorWallpaper`) and in config.
- Parent client coordinates ≠ screen coordinates (monitors can have negative
  coordinates; the parent spans the virtual screen). Convert each `rcMonitor`
  with `MapWindowPoints(HWND_DESKTOP, parent, …)` rather than assuming an origin.
- Swap chain size = monitor size in physical pixels. Frames of a different size
  are scaled by the host according to a per-monitor fit mode sent by the daemon
  (`Fill`, `Fit`, `Stretch`, `Center`) — a pure presentation concern, so it
  belongs in the host.

Phase 1 implements one monitor (primary) but uses this exact structure, so
Phase 6 adds monitors without reworking attachment.

---

## 6. Lifecycle events the host must handle

A hidden **top-level control window** (not message-only — message-only windows
don't receive broadcasts) owns the host's system-event subscriptions.

| Event | Source | Host action |
|---|---|---|
| Explorer restarted | `RegisterWindowMessage("TaskbarCreated")` broadcast | Our child HWNDs were destroyed with their parent. Recreate HWNDs, rebind DComp targets to existing swap chains/visuals, re-attach (§3), re-present last frame |
| Explorer gone (parent destroyed) | **Not** `WM_DESTROY` — the spike showed it doesn't arrive when Explorer is killed. Treat `TaskbarCreated` as the signal and check `IsWindow(ours)` there | Recreate the HWND if it's gone, then re-attach |
| Display topology change | `WM_DISPLAYCHANGE` | Re-enumerate monitors; create/destroy/resize per-monitor windows; report `MonitorConnected`/`Disconnected` to daemon |
| Work area / taskbar change | `WM_SETTINGCHANGE` (`SPI_SETWORKAREA`) | Re-verify attachment (Explorer sometimes rebuilds the tree) |
| User changed system wallpaper | `WM_SETTINGCHANGE` (`SPI_SETDESKWALLPAPER`) | Re-verify; report `UserChangedWallpaper` to daemon |
| DPI change | `WM_DPICHANGED` on control window / re-enumeration | Resize swap chains |
| GPU device lost | `DXGI_ERROR_DEVICE_REMOVED/RESET` from Present | Recreate device + swap chains on render thread; HWNDs untouched |
| Host exit / stop | Daemon `Stop`, or process shutdown | §8 |

Lock/unlock, power, and idle are **daemon** concerns (they're policy inputs), not
host concerns.

---

## 7. Presentation pipeline

```
render thread                                   window thread
─────────────                                   ─────────────
WaitForMultipleObjects(frameEvent, controlEvt)  GetMessage loop
  │                                             (owns HWNDs, DComp targets)
  ▼
map shared memory → UpdateSubresource / Map
  │   into a DEFAULT texture (one per monitor)
  ▼
draw fullscreen quad (fit mode) into back buffer
  ▼
swapChain.Present(1, 0)   // composition swap chain, flip model
```

- Device: `D3D11CreateDevice` with `D3D11_CREATE_DEVICE_BGRA_SUPPORT`, hardware,
  fallback to WARP.
- Swap chain: `IDXGIFactory2::CreateSwapChainForComposition`,
  `DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL`, 2 buffers, `DXGI_FORMAT_B8G8R8A8_UNORM`,
  `DXGI_ALPHA_MODE_IGNORE` (wallpaper is opaque).
- DComp: `DCompositionCreateDevice` → `CreateTargetForHwnd(hwnd, topmost=TRUE)` →
  visual with content = swap chain → `Commit()`. DComp setup/rebinding happens
  on the window thread; Present happens on the render thread.
- Fallback: `CreateSwapChainForHwnd` with flip model directly on our child HWND.
  Spike: **both paths work** under Progman on build 26200. DComp stays primary
  (swap chain survives HWND re-creation; only the target is rebound).
- Spike: `CreateTargetForHwnd` works when called from the render thread, not the
  thread that owns the HWND — so all D3D/DXGI/DComp work can live on the render
  thread, and the window thread stays pure message pumping.
- **Idle cost:** with a static frame, the render thread sits in
  `WaitForMultipleObjects` and presents nothing. DWM keeps showing the last
  buffer. Zero CPU/GPU while idle is a hard requirement.
- Frame pacing is the daemon's job (it decides *when* to signal). The host
  presents at most once per signal and coalesces: if several frames arrive before
  it wakes, it shows only the newest.
- Use `IDXGISwapChain2::GetFrameLatencyWaitableObject` (with
  `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`) so animated content never
  queues more than one frame ahead.

---

## 8. Detach, pause, and exit

The user's real wallpaper must come back cleanly in every exit path.

- **Pause (daemon request):** keep the last frame up (it *is* the wallpaper).
  Rendering simply stops. Nothing changes on screen.
- **Hide / Stop:** `ShowWindow(SW_HIDE)` on all wallpaper HWNDs. The system
  `WorkerW` underneath becomes visible.
- **Exit:** hide, `SetParent(hwnd, null)`, destroy windows and D3D resources,
  then force Explorer to repaint its wallpaper:
  `SystemParametersInfo(SPI_GETDESKWALLPAPER)` → `SystemParametersInfo(SPI_SETDESKWALLPAPER, path, flags=0)`
  (same path, no `SPIF_UPDATEINIFILE` — repaint only, user setting untouched).
  Spike: on the Modern topology, exiting *without* the refresh left no stale
  pixels (DComp content just disappears). Keep the refresh anyway as cheap
  insurance for the Legacy topology and crash recovery.
- **Crash:** Windows destroys our windows with the process; Explorer repaints the
  exposed region. Stale-pixel cleanup is then done by the next host start or by
  the tray app (which can perform the same `SPI_SETDESKWALLPAPER` refresh when
  it notices the host died).
- The host **never** writes the user's wallpaper setting. (A later, optional
  feature could set a "poster frame" as the real wallpaper via `IDesktopWallpaper`
  so the lock-screen transition and Task View look right — that's a daemon/tray
  decision, not a host one.)

---

## 9. Visibility reporting (facts, not policy)

The host is the only component that knows its windows' visibility, so it reports
it; the daemon decides what to do (pause, drop FPS, keep going).

Per-monitor message to daemon: `SurfaceVisibilityChanged { monitorId, state }`
where `state ∈ { Visible, Covered, Hidden }`.

Detection, event-driven:

- `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` and
  `EVENT_OBJECT_LOCATIONCHANGE` filtered to the foreground window (out-of-context,
  no DLL injection) → recompute: does a non-shell window fully cover monitor *N*
  (maximized or full-screen)? → `Covered`.
- `SHQueryUserNotificationState` (`QUNS_RUNNING_D3D_FULL_SCREEN`,
  `QUNS_PRESENTATION_MODE`) on foreground change → `Covered`.
- Win+D / "Show desktop" un-covers naturally via the same foreground events.
- `Hidden` = our windows are hidden by request or detached.

---

## 10. Code boundary

```csharp
// src/WallpaperHost/Win32/IDesktopAttachment.cs  (sketch)
interface IDesktopAttachment
{
    DesktopTopology Topology { get; }            // Modern | Legacy | Unsupported
    bool TryAttach(IReadOnlyList<WallpaperWindow> windows);
    bool Verify();
    void Detach(bool refreshSystemWallpaper);
    event Action ShellRestarted;                 // TaskbarCreated
}
```

Two implementations (`ModernProgmanAttachment`, `LegacyWorkerWAttachment`) chosen
by topology detection. Everything about undocumented Explorer behaviour lives
behind this interface; nothing else in the host references Progman, WorkerW, or
`0x052C`.

Runtime and interop (decided after the spike):

- **.NET 10 (LTS)**, host published as **Native AOT**. Spike result: a 2.7 MB
  single exe; attach, DComp and the shared-memory feed all work under AOT;
  working set 40 MB vs 57 MB JIT. The rest of the memory is the GPU driver and
  swap chains, not .NET.
- **Vortice 3.8.x** for D3D11/DXGI/DirectComposition. Its runtime (SharpGen)
  emits trim warning IL2104 under AOT. Everything the spike uses worked, but
  re-run the AOT smoke test on every Vortice upgrade.
- Win32 via hand-written P/Invoke for now (small surface); `Microsoft.Windows.CsWin32`
  is the option if it grows.
- AOT build needs the VS C++ build tools, and `vswhere.exe` must be findable:
  add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` or
  publish from a VS Developer prompt.

---

## 11. Alternatives considered

| Option | Verdict |
|---|---|
| `IDesktopWallpaper::SetWallpaper` with rapidly regenerated image files | Rejected for animation (disk writes, slow transitions, flicker). Possible future use for a static "poster frame" only |
| Bottom-most top-level window (`HWND_BOTTOM`) | Rejected — sits above Progman, so it covers desktop icons |
| Drawing directly into Explorer's `WorkerW` DC | Rejected — Explorer repaints over it; no GPU path; Modern topology has no redirection bitmap |
| DLL injection into Explorer / DWM hooks | Rejected — fragile, flagged by AV/anti-cheat, violates "renderer is dumb" |
| One HWND spanning the virtual screen | Rejected — breaks per-monitor resolution/refresh/DPI and per-monitor swap chains |

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| `0x052C` / topology changes in a future Windows build | Topology detection + verification; `Unsupported` state reported to daemon/tray instead of misbehaving; all logic in one interface |
| Cross-process input-queue coupling hangs Explorer | §4.1 threading rules; `SendMessageTimeout` everywhere |
| Anti-cheat or full-screen exclusive games | Report `Covered`; daemon pauses. Host does nothing invasive |
| Stale pixels after exit/crash | §8 repaint via `SPI_SETDESKWALLPAPER` same-path refresh |
| Modern-topology rendering quirks (blank/black child) | §4 layered-window fallback, §7 HWND-swap-chain fallback; both **[verify]** in the spike |

---

## 13. Phase 1 spike — what to prove

A throwaway console app in `tests/spikes/DesktopAttach/` that:

1. Dumps the Progman tree before/after `0x052C` (both wParam variants).
2. Attaches one window per the Modern algorithm and clears it to a solid colour via
   DComp + composition swap chain. Pass: colour visible, icons on top, icons
   clickable, right-click desktop menu works.
3. Repeats with the HWND swap-chain fallback.
4. Survives `taskkill /f /im explorer.exe && start explorer` (re-attach on `TaskbarCreated`).
5. Exits cleanly — real wallpaper restored with no stale pixels.
6. Measures idle CPU/GPU with a static frame (target: ~0%).

### Results — 2026-11-16, Windows 11 build 26200, RTX 2060, two monitors

Setup: primary 2560×1440 at (0,0), secondary 1920 px wide to the left at
(−1920,0). Spike: [tests/spikes/DesktopAttach](../tests/spikes/DesktopAttach)
(rounds 1–2 on .NET 7 + Vortice 3.3.4; since moved to .NET 10 + Vortice 3.8.3
and re-verified — see round 3).

| # | Check | Result |
|---|---|---|
| 1 | Tree before `0x052C` | Progman → `SHELLDLL_DefView` only. **No WorkerW** |
| 1 | Tree after `0x052C (0xD,0x1)` | Progman → [0] DefView, [1] WorkerW. Topology = **Modern** |
| 2 | DComp attach, static frame | **PASS.** Magenta (#D91999) behind icons on primary only; secondary keeps the system wallpaper; z-order DefView=0, ours=1, WorkerW=2 |
| 2 | Coordinates | Parent client origin is the virtual-screen origin: primary (0,0) mapped to (1920,0) via `MapWindowPoints`. Confirms §5 |
| 2 | Click-through | **PASS.** `WindowFromPoint` over our surface returns `SysListView32` (Explorer), not our window |
| 3 | HWND swap-chain fallback, animated | **PASS.** Colour changed between screenshots; 27 presents in 14 s at 2 Hz |
| 4 | Explorer restart (`taskkill /f` + relaunch) | **PASS.** `TaskbarCreated` arrived ~3 s after relaunch; re-attached to the *new* Progman/DefView/WorkerW in ~35 ms, z-order verified, magenta back behind icons, click-through intact. DComp swap chain reused (only the target was re-created). Our old HWND was gone, but **no `WM_DESTROY` reached our window proc** |
| 5 | Clean exit | **PASS.** System wallpaper back, no stale pixels — with *and* without the `SPI_SETDESKWALLPAPER` refresh |
| 6 | Idle cost, static frame | **PASS.** 0 ms CPU over 10 s; 2 presents total (initial bind + re-bind from the spurious `WM_DISPLAYCHANGE`) |

Findings folded into the sections above:

- `WS_EX_LAYERED` is not needed (§4).
- DComp can be driven entirely from the render thread (§7).
- A `WM_DISPLAYCHANGE` arrives right after startup (round 2 showed it isn't caused by `0x052C`); attach must be idempotent (§3).
- The production host should debounce `WM_DISPLAYCHANGE` and skip re-binding the
  renderer when the monitor rect hasn't changed (the spike re-binds; harmless but wasteful).

- When Explorer dies, our child HWND is destroyed without a `WM_DESTROY` reaching
  us; `TaskbarCreated` + `IsWindow` is the reliable recovery path (§6).

### Round 2 — same day

| # | Check | Result |
|---|---|---|
| 7 | All monitors (`--all`) | **PASS.** One window + swap chain per monitor. Secondary at screen (−1920,160) 1920×1080 → parent-client (0,160); primary → (1920,0). Correct colours on each; z-order verified for both. Both monitors at 100 % DPI, so **mixed DPI is still untested** |
| 8 | Our window thread hung 12 s | **PASS.** Explorer unaffected (see §4.1) |
| 9 | Explorer suspended 5 s | **PASS.** Our presents continued at 4/s (2 Hz × 2 monitors), control window responsive. Visual update during the suspension not screenshotted (shell is frozen) |
| 10 | Spawn variant `0,0` on fresh Explorer | **PASS.** WorkerW created as Progman child |
| 11 | Spurious `WM_DISPLAYCHANGE` | Still fires when 0x052C is *not* sent — corrected §3 |
| 12 | Shared-memory frame feed, separate producer process, 2560×1440 BGRA, Release build | See below |

Frame feed (memory-mapped file + named auto-reset event, two slots, `UpdateSubresource` straight into the back buffer):

| Rate | Frames dropped | Upload p50 / p99 | Produce→Present returned p50 / p99 | Host CPU | Producer CPU |
|---|---|---|---|---|---|
| 30 fps | 0 / 450 | 1.14 / 1.62 ms | 1.35 / 1.86 ms | 6.0 % of a core | 2.8 % |
| 60 fps | 0 / 900 | 1.15 / 1.50 ms | 1.36 / 1.73 ms | 10.5 % of a core | 7.5 % |

"Produce→Present returned" excludes DWM composition (≈ one more vsync).
Conclusion: the CPU path from the architecture plan is fine for images and GIFs
and acceptable for 1440p60, but the host's cost is roughly one 14 MB copy per
frame. GPU-native sources (video via Media Foundation, shaders) should hand over
a **shared D3D11 texture** (`IDXGIResource1::CreateSharedHandle` + keyed mutex
or fence) instead, so pixels never touch system memory. The frame protocol
should support both; see [architecture.md](architecture.md#frame-transport-spike-findings).

### Round 3 — .NET 10.0.404, Vortice 3.8.3

| Check | JIT (Release) | Native AOT |
|---|---|---|
| All-monitor attach + verify + colours | PASS | PASS |
| Idle CPU, static frame, 8 s | 15.6 ms (one scheduler tick) | 15.6 ms |
| Working set / private bytes | 57 MB / 57 MB | 40 MB / 52 MB |
| 60 fps feed: dropped, upload p50, host CPU | 0, 1.30 ms, 12.6 % | 0, 1.39 ms, 11.3 % |
| Exe size | — | 2.7 MB |

Numbers match .NET 7 within run-to-run noise. Explorer restart was not re-run
on .NET 10 (that code path didn't change).

### Round 4 — shared GPU texture path

**PASS** under JIT and Native AOT: 0 dropped of 900 frames at 2560×1440@60, host
copy 0.13 ms p50 (vs 1.04 ms for shared memory), produce→present 0.23 ms p50.
Full comparison and protocol implications in
[architecture.md](architecture.md#frame-transport-spike-findings).

Not yet covered: Legacy topology (needs a ≤ 23H2 machine/VM), mixed-DPI
monitors, monitor hot-plug (needs a cable pulled or a display toggled), the
GPU path's slow-host reclaim and producer-restart cases, and shared fences.
