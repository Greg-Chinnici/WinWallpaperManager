# Windows Live Wallpaper System — Architecture

A lightweight, event-driven live wallpaper system for Windows 11.

The system separates wallpaper rendering, wallpaper content/control, and user
configuration. The renderer only displays frames; a daemon manages wallpaper
sources and responds to events. The user interacts through a small native
system-tray application.

See also: [desktop-integration.md](desktop-integration.md) — how `WallpaperHost` attaches to the desktop.

## Goals

- Lightweight while idle
- Support arbitrary rendered frames
- Support animated wallpapers without the renderer understanding video/GIF formats
- Event-driven rather than constantly polling
- Multi-monitor support
- Native Windows 11 experience
- Allow external applications/services to control the wallpaper
- Keep rendering and content management separate
- No heavyweight UI framework in the wallpaper renderer

## Architecture

```
┌───────────────────────┐
│     Tray UI App       │  Settings, wallpaper selection,
│  WallpaperTray.exe    │  monitor configuration, start/stop
└───────────┬───────────┘
            │ IPC (named pipe, control)
            ▼
┌───────────────────────┐
│   Wallpaper Daemon    │  Sources, scheduling, events,
│ WallpaperDaemon.exe   │  playback, state
└───────────┬───────────┘
            │ Frame/control IPC (pipe + shared memory + named events)
            ▼
┌───────────────────────┐
│   Wallpaper Host      │  Win32 HWNDs, D3D11,
│  WallpaperHost.exe    │  DirectComposition, monitor management
└───────────┬───────────┘
            ▼
       Windows DWM → Desktop
```

## Components

### 1. Wallpaper Host (`WallpaperHost.exe`)

Lowest-level component. Creates wallpaper windows, attaches them to the desktop,
handles multiple monitors, manages D3D11 resources, presents frames, handles
monitor/resolution changes, and receives frames from the daemon — without knowing
where they came from.

It has **no** concept of GIFs, videos, games, schedules, events, or wallpaper libraries.

### 2. Wallpaper Daemon (`WallpaperDaemon.exe`)

Main runtime/controller. Selects the active wallpaper, manages sources, decodes
animated formats, runs procedural renderers, handles events, manages playback,
schedules changes, talks to `WallpaperHost`, and exposes an IPC API for external apps.

Sources (image, GIF, video, procedural, game events, external app) all feed a
common frame pipeline that ends at `WallpaperHost`.

### 3. Tray UI (`WallpaperTray.exe`)

User-facing control surface in the system tray: start/stop, wallpaper selection,
monitor/playback/schedule/event configuration, source management, settings, status.
The tray app can exit without interrupting the daemon or host.

Startup: `Windows Startup → WallpaperTray.exe → starts WallpaperDaemon.exe and WallpaperHost.exe`.

Tray menu sketch:

```
Wallpaper System
─────────────────────
Current: Aurora
▶ Pause Wallpaper
🔄 Next Wallpaper
Monitors >    Monitor 1 / Monitor 2 / All Monitors
Wallpapers >  Aurora / Cyberpunk / Forest
Events >      Gaming Mode / Idle Mode
⚙ Settings
──────────────
Exit
```

## IPC

Small, **versioned** protocol.

- **Control channel:** Windows named pipes. Commands: `Start`, `Stop`, `Pause`,
  `Resume`, `SetWallpaper`, `SetMonitorWallpaper`, `SetFPS`, `SetPlaybackState`,
  `Reload`, `GetStatus`.
- **Frame transport:** never through the control pipe. Memory-mapped files for
  pixels, named events for signalling: `Daemon → write frame → shared memory → signal named event → WallpaperHost`.

### Frame transport: spike findings

Measured 2026-11-16 (details in [desktop-integration.md §13](desktop-integration.md#13-phase-1-spike--what-to-prove)):
CPU frames via shared memory + named event cost the host ~1.15 ms and ~10 % of
one core at 2560×1440@60, with zero dropped frames. So the protocol carries two
frame kinds:

- **CPU frame** — pixels in a shared-memory slot. For images, GIFs, CPU renderers,
  and external apps that can't share GPU resources.
- **GPU frame** — a shared D3D11 texture handle plus a sync primitive. For video
  and shader sources, which already have the frame on the GPU.

The spike's two-slot CPU ring can tear if the host stalls longer than a frame;
the real protocol needs three slots or a seqlock.

Back-to-back comparison, 2560×1440@60, separate producer process, .NET 10 Release:

| | CPU frame (shared memory) | GPU frame (shared texture) |
|---|---|---|
| Host time to get frame into back buffer, p50 / p99 | 1.04 / 1.60 ms | 0.13 / 0.30 ms |
| Produce → Present returned, p50 / p99 | 1.25 / 1.81 ms | 0.23 / 0.52 ms |
| Host CPU | 6.7 % of a core | 4.5 % |
| Producer CPU | 6.4 % | 1.8 % |
| Dropped frames | 0 / 900 | 0 / 900 |

The GPU path also works under Native AOT (0.12 ms p50). How it works in the spike
([GpuChannel.cs](../tests/spikes/DesktopAttach/GpuChannel.cs)):

- Producer creates 3 textures with `SHARED_NTHANDLE | SHARED_KEYEDMUTEX` and
  exports them by **name** (`IDXGIResource1::CreateSharedHandle`). The host opens
  them with `ID3D11Device1::OpenSharedResourceByName`. No handle duplication
  between processes, and no shared adapter LUID handshake beyond both using
  adapter 0 (the protocol must carry the LUID for multi-GPU machines).
- A tiny shared-memory header (`seq`, `slot`, size, timestamp) plus a named
  event carries "frame ready", exactly like the CPU path. Both kinds share one
  signalling scheme.
- Keyed mutex per slot: key 0 = producer may write, key 1 = holds a frame.
  Producer reclaims a key-1 slot the host skipped, so a slow host can't block it.
  The reclaim path never triggered in the run (no host stalls).
- Host does `CopyResource` into the swap chain back buffer — one GPU-side copy,
  no system memory.

Protocol implications:

1. The frame header gets a `kind` (CPU slot | GPU texture), the texture name or
   generation, and the adapter LUID.
2. The host must re-open textures when the producer restarts (new generation) —
   the spike caches them forever.
3. Vortice 3.8 hides `AcquireSync`'s `WAIT_TIMEOUT` result and doesn't expose
   `OpenSharedResourceByName`; the spike calls both through the COM vtable
   (`Com` class). Keep that shim, or move to a shared `ID3D11Fence` (Windows 10
   1703+), which avoids keyed-mutex overhead and is worth measuring later.

#### Keyed mutex vs shared fence (measured 2026-11-16)

Same setup, two alternating runs each, 840 frames per run
([FenceChannel.cs](../tests/spikes/DesktopAttach/FenceChannel.cs)):

| | Keyed mutex | Shared fence |
|---|---|---|
| Host time per frame, p50 / p99 | 0.16 / 0.27 ms | **0.11–0.12 / 0.21 ms** |
| Produce → Present returned, p50 / p99 | 0.28–0.29 / 0.43 ms | **0.24–0.25 / 0.38–0.39 ms** |
| Host / producer CPU | 1.4–4.1 % / 1.3–1.7 % | 2.0–3.0 % / 1.7–1.8 % (noise) |
| Dropped frames, back-pressure skips | 0, 0 | 0, 0 |

How the fence version works: the producer GPU-signals fence P = N+1 after drawing
frame N. The host GPU-*waits* on P (its CPU never blocks), copies, then signals its
own fence C = N+1. Before reusing a slot, the producer checks C on the CPU and skips
the tick if the host is still behind. So neither side can stall the other.

**Decision for protocol v2 GPU frames: shared fences.** They're slightly faster,
neither side ever blocks, and back-pressure is explicit. Costs:

- D3D11 can't open a fence by name. Each side publishes `(pid, handle)` and the
  other `DuplicateHandle`s it, which needs `PROCESS_DUP_HANDLE` on the peer. That's
  fine for same-user desktop apps; sandboxed (AppContainer) producers would need the
  daemon to broker the handles.
- Vortice 3.8 also hides `ID3D11Fence::CreateSharedHandle`, so it goes through the
  same COM vtable shim.

## Protocol v2: sessions, layers, transports (2026-11-17)

v1 had one global frame ring, and the daemon handed out "writer tokens". v2 replaces that with
**sessions**:

- A producer asks the daemon for a session on a **layer** (`desktop` | `overlay`), choosing a
  **transport** (`cpu` | `gpu`), a size and an alpha mode. The daemon's own image/GIF/video
  sources use exactly the same mechanism (a CPU session on the desktop layer).
- The **host creates every session's resources**: a shared-memory map (header plus CPU slots
  sized to the frame, not a fixed 4K), a ready event, and for GPU sessions 3 D3D11 textures
  (`SHARED | SHARED_NTHANDLE`) and two shared fences. GPU handles are `DuplicateHandle`d
  **into the producer's process**, which the daemon identifies with `GetNamedPipeClientProcessId`
  (kernel-reported, so it can't be spoofed).
- Producers therefore only ever **import**. That's what makes three APIs possible against one
  D3D11 host: D3D11 opens with `OpenSharedResource1` / `OpenSharedFence`, D3D12 with
  `OpenSharedHandle`, and Vulkan imports the textures as `D3D11_TEXTURE_BIT` memory and the
  fences as timeline semaphores (`D3D12_FENCE_BIT`). All three were verified on an RTX 2060 at
  ~60 fps with 0 skipped frames, on both layers.
- **Sync is fence-based**, as chosen in the spike: produced fence = Seq + 1 (the host's GPU
  waits on it), consumed fence = Seq + 1 after the copy (producers check it before reusing a
  slot). No CPU thread ever waits on another process.
- **Layers are independent.** Desktop: the daemon's source or one external session.
  Overlay: one external session or nothing. Last request wins, the loser gets `external.ended`,
  and the desktop layer falls back to the daemon's source when an external session ends.
- **Overlay windows** are top-level per monitor with `WS_EX_TOPMOST | WS_EX_LAYERED |
  WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP`, presenting a
  premultiplied-alpha DirectComposition visual. Verified: drawn above windows and the taskbar,
  and clicks go to the window underneath.

Not yet: GPU frames for the daemon's own video (it still decodes on the CPU), a producer
restart reusing its session, AppContainer/elevated producers (handle duplication needs
`PROCESS_DUP_HANDLE` on them), and per-monitor content.

## POC status (2026-11-16)

Working end to end on Windows 11 build 26200, two monitors:

| Piece | What works |
|---|---|
| `WallpaperHost` | Attach behind icons on all monitors, Fill/Fit/Stretch/Center via DComp transforms (black letterbox), Smooth/Pixelated sampling (DComp bilinear vs nearest neighbour; verified: 19 vs 3 distinct colours across an 8× upscaled GIF), writer gating, torn-frame detection, Explorer-restart recovery, hidden until the first frame |
| `WallpaperDaemon` | Image (WIC, any size, downscaled to ≤ 4K), GIF (WIC, composited with disposal modes, native or custom fps), video (Media Foundation, looped, muted, fps cap), pause/resume/stop, external outputs with revoke and auto-restore, state saved to `%LOCALAPPDATA%\WinWallpaper\state.json` |
| `WallpaperTray` | WPF + Fluent theme (follows light/dark and accent), tray icon with menu, Explorer thumbnails, file pickers, live fps slider, scaling, developer card linking to [custom-render-outputs.md](custom-render-outputs.md) |
| External renderers | C# (`ExternalOutput`) and raw-protocol Python samples, both verified |

Deviations from the original plan: the tray starts only the daemon, and the **daemon
starts and supervises the host** (it is the host's only client). External renderers
write frames straight into the host's ring; the daemon only grants the writer token.

Measured (Debug builds):

| Scenario | Daemon CPU | Host CPU |
|---|---|---|
| Stopped or static image | 0 | 0 |
| Paused video | 0 | 0 |
| External renderer, 1280×720 @ 30 | 0 % | ~5 % |
| Video 1280×720 @ 30 (software decode) | **~31 %** | ~4 % |

Scaling stays on DWM's GPU composition; the host never resamples pixels itself. If big
downscales (e.g. 4K video on 1080p) visibly shimmer, add a mip/shader downscale pass in
the host for that case only.

Next: hardware video decode. Give the MF source reader a DXGI device manager and ship
frames as fence-synchronised GPU textures, so video costs about what the external path does.

## Frame API

```
Frame
├── Width
├── Height
├── Format
├── Stride
├── Timestamp
└── Pixel Data / GPU Resource
```

The host does not care whether a frame came from PNG, GIF, MP4, Unity, a shader,
an AI renderer, or game telemetry.

## Event System

Examples: `WindowsUnlocked`, `WindowsLocked`, `ComputerIdle`, `ComputerActive`,
`ApplicationStarted`, `ApplicationExited`, `GameStarted`, `GameExited`,
`MonitorConnected`, `MonitorDisconnected`, `TimeChanged`, `UserChangedWallpaper`.

External applications can eventually send events (e.g. `GameStarted game=VALORANT`
→ daemon switches to a gaming scene), making the system an extensible desktop
event renderer rather than just an animated wallpaper player.

## Project Structure

```
src/
├── WallpaperHost/      Win32/ D3D11/ DirectComposition/ Monitors/ IPC/
├── WallpaperDaemon/    Sources/{Image,Gif,Video,Procedural}/ Events/ Playback/ Scheduler/ IPC/
├── WallpaperTray/      UI/ Settings/ WallpaperLibrary/ IPC/
└── WallpaperProtocol/  Commands/ Events/ Frame/
wallpapers/  config/  tests/  docs/
```

## Phases

1. **Renderer** — Win32 wallpaper HWND, single monitor, D3D11, static RGBA frame, IPC connection, frame update.
2. **Daemon** — wallpaper selection, static image loading, shared-memory transport, pause/resume, FPS limiting, basic config.
3. **Tray UI** — tray icon, wallpaper selection, start/stop, settings, monitor selection, status.
4. **Animation** — GIF, video, frame timing, frame dropping, playback controls.
5. **Events** — lock/unlock, app launch/exit, idle/active, game detection, external event API.
6. **Multi-monitor** — per-monitor wallpaper, mixed resolutions, hot-plug, per-monitor config.

## Design Principles

- **Renderer is dumb.** It displays frames; it doesn't know what a wallpaper is.
- **Daemon owns state.** What is playing, why, when it changes, where frames come from.
- **Tray owns configuration.** Never rendering or playback.
- **IPC is stable and versioned** so others can integrate without depending on implementation.
- **Event-driven by default.** `WaitForFrameEvent(); RenderFrame();` — never a sleep-poll loop.

## Technology Direction

C# on .NET 10 (LTS), Win32, Direct3D 11 (via Vortice), DirectComposition,
named pipes, memory-mapped files, named events, native tray integration.
`WallpaperHost` ships as Native AOT. The spike showed a 2.7 MB exe and a
40 MB working set, mostly GPU driver. The tray UI framework is chosen
independently of the renderer and must stay lightweight and native-feeling.

## Long-Term Possibilities

Plugin sources, shader wallpapers, web sources, game integrations, reactive and
audio-reactive wallpapers, weather, system telemetry, per-application wallpapers,
remote control, LAN event sources, AI-generated scenes, scripting.

Core abstraction: `Event/State → Wallpaper Source → Frame Pipeline → Wallpaper Host → Windows Desktop`.
