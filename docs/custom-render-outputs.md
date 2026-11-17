# Custom render outputs

Put your own pixels on the desktop: a game, a shader toy, a telemetry dashboard, a
generative-art script. Any process running as the same user can ask the WinWallpaper
daemon for the wallpaper and then stream frames to it.

- **Language-agnostic.** A named pipe, a memory-mapped file and an event. No SDK required.
- **Zero-copy through the daemon.** Your frames go straight into the host's frame ring;
  the daemon only decides *whose* frames are shown. Measured: daemon 0 % CPU while an
  external renderer streams 1280×720 @ 30 fps; host ~5 % of one core.
- **Polite.** Pick another wallpaper in the tray and your app is told it lost the
  wallpaper. Disconnect, and the previous wallpaper comes back automatically.

Protocol version: **1**. Frames: **BGRA8, top-down, up to 3840×2160**, CPU memory.
(GPU shared textures are validated in the spike and planned for v2. See
[architecture.md](architecture.md#frame-transport-spike-findings).)

---

## Quick start (C#)

Reference `src/WallpaperProtocol` and use `ExternalOutput`:

```csharp
using WallpaperProtocol;

using var output = await ExternalOutput.ConnectAsync("My Renderer");
output.RevokedChanged += () => Console.WriteLine("User picked another wallpaper");

var pixels = new byte[1280 * 720 * 4];          // BGRA8, top-down
while (!output.Revoked)
{
    Render(pixels);                              // your code
    output.Submit(pixels, 1280, 720);            // copy into the ring + wake the host
    await Task.Delay(33);                        // pace yourself (~30 fps)
}
```

Samples:

| Sample | What it shows |
|---|---|
| [ExternalRenderer](../samples/ExternalRenderer/Program.cs) | Animated plasma at a fixed frame rate |
| [ClockRenderer](../samples/ClockRenderer/Program.cs) | Digital clock over your own background image. Publishes only when the text changes (once a minute, or once a second with `--seconds`) |
| [python/color_cycle.py](../samples/python/color_cycle.py) | The raw protocol with no SDK |

```bash
dotnet run --project samples/ExternalRenderer -- --seconds 20 --fps 30
```

```bash
dotnet run --project samples/ClockRenderer -- --background "C:\Pictures\mountains.jpg" --seconds
```

Clock options: `--seconds`, `--12h`, `--no-date`, `--position center|top-left|top-right|bottom-left|bottom-right`,
`--dim 0-100` (darken the background, default 25), `--color #RRGGBB`, `--font <family>`.

For zero-copy rendering, write straight into the ring:
`byte* dst = output.BeginFrame(w, h); /* write rows, w*4 bytes apart */ output.Publish(w, h);`

## Quick start (any language)

[samples/python/color_cycle.py](../samples/python/color_cycle.py) implements the raw
protocol in ~60 lines of standard-library Python:

```bash
python samples/python/color_cycle.py 10
```

---

## The protocol

### 1. Connect to the daemon

Named pipe **`\\.\pipe\WinWallpaper.Daemon.v1`**, one UTF-8 JSON object per line
(`\n`-terminated), request → response. Only the user who started the daemon can connect.

Ask for the wallpaper:

```json
→ {"cmd":"external.begin","name":"My Renderer"}
← {"ok":true,"writer":7,"protocol":1,
   "frameMap":"Local\\WinWallpaper.Frames.v1","frameReadyEvent":"Local\\WinWallpaper.FrameReady.v1",
   "maxWidth":3840,"maxHeight":2160}
```

`writer` is your token. The host shows only frames stamped with the current writer.

**Keep the pipe open.** It is your lease:

| What happens | Result |
|---|---|
| You close the pipe (or your process exits) | Daemon restores the previous wallpaper |
| User picks another wallpaper / presses Stop | Daemon sends `{"event":"external.ended"}` on your pipe; your frames are ignored from then on |
| Another app calls `external.begin` | Same as above: last one wins |

### 2. Open the frame ring

Open (don't create) the named file mapping `Local\WinWallpaper.Frames.v1` and the
named auto-reset event `Local\WinWallpaper.FrameReady.v1`. The host and daemon create them.

Layout (little-endian):

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | `Magic` | `0x46505757` ("WWPF") |
| 4 | 4 | `Version` | 1 |
| 8 | 4 | `MaxWidth` | 3840 |
| 12 | 4 | `MaxHeight` | 2160 |
| 16 | 4 | `Slots` | 3 |
| 20 | 4 | `ActiveWriter` | set by the daemon, **read-only for you** |
| 64 | 4 | `Writer` | your `writer` token |
| 68 | 4 | `Width` | pixels |
| 72 | 4 | `Height` | pixels |
| 76 | 4 | `Stride` | must be `Width * 4` |
| 80 | 4 | `Slot` | 0–2 |
| 84 | 4 | `Format` | 0 = BGRA8 |
| 88 | 8 | `Seq` | frame sequence number, shared by all writers |
| 96 | 8 | `Timestamp` | QueryPerformanceCounter value; informational |
| 4096 + n × 33,177,600 | 33,177,600 | slot *n* | `MaxWidth × MaxHeight × 4` bytes each |

### 3. Publish a frame

```
seq  = read Seq (offset 88) + 1
slot = seq % 3
write pixels to slot (rows Width*4 bytes apart, top row first)
write Writer, Width, Height, Stride, Slot, Format, Timestamp
write Seq = seq           ← last: the host treats a new Seq as "header complete"
SetEvent(FrameReady)
```

The host copies the slot to the GPU as soon as it wakes. If you publish two more
frames while it is still copying, it detects the overwrite and drops that frame rather
than show a torn image.

### Frame rules

- **Any size** up to 3840×2160. The host scales to each monitor on the GPU (DWM
  composition) using the user's scaling (Fill / Fit / Stretch / Center) and sampling
  (Smooth / Pixelated) settings, so render at whatever resolution is cheap for you.
- **Alpha is ignored.** Wallpapers are opaque.
- **Publish only when something changed.** A static scene needs one frame; the host
  keeps showing it at zero cost. Pace animated content yourself. The event wakes the
  host per frame; there is no vsync callback in v1.
- Frames from a writer that isn't active are silently dropped. Check for
  `external.ended` and stop rendering.

---

## Other daemon commands

The tray app uses the same pipe. Every response has `"ok"`, plus `"error"` when false.
State-changing commands answer with the full status.

| Request | Effect |
|---|---|
| `{"cmd":"status"}` | `state` = `playing` / `paused` / `stopped` / `external`, plus `source`, `fit`, `external` |
| `{"cmd":"set","path":"C:\\a.gif","kind":"gif","fps":12}` | Play a file. `kind` = `image` / `gif` / `video` (default: by extension). `fps` 0 = the file's own timing |
| `{"cmd":"fps","fps":24}` | Change the frame rate of the current GIF/video |
| `{"cmd":"fit","mode":"fill"}` | `fill` / `fit` / `stretch` / `center` |
| `{"cmd":"sampling","mode":"smooth"}` | `smooth` (bilinear) / `pixelated` (nearest neighbour). Applies to external frames too: render small and pick `pixelated` for a crisp retro look |
| `{"cmd":"pause"}`, `{"cmd":"resume"}` | Freeze/unfreeze; the frozen frame stays on screen |
| `{"cmd":"stop"}` | Remove the live wallpaper; Windows' own wallpaper shows again |
| `{"cmd":"quit"}` | Stop the daemon and host |

Try it from PowerShell:

```powershell
$p = New-Object IO.Pipes.NamedPipeClientStream('.', 'WinWallpaper.Daemon.v1', 'InOut'); $p.Connect(2000)
$w = New-Object IO.StreamWriter($p); $w.AutoFlush = $true; $r = New-Object IO.StreamReader($p)
$w.WriteLine('{"cmd":"status"}'); $r.ReadLine()
```

## Troubleshooting

| Symptom | Cause |
|---|---|
| Pipe not found | Daemon not running. Start `WallpaperTray.exe` (it starts the daemon) or `WallpaperDaemon.exe` |
| Access denied on the pipe | Your app runs as a different user account than the daemon |
| Connected but nothing shows | `Writer` not set to your token, `Seq` not incremented, or event not signalled |
| Image shifted or garbled | `Stride` isn't `Width * 4`, or rows are bottom-up |
| Your frames stop showing | You were revoked (`external.ended`): the user chose something else |

Logs: `%LOCALAPPDATA%\WinWallpaper\logs\daemon.log` and `host.log`.
