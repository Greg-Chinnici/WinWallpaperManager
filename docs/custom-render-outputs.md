# Custom render outputs

Put your own pixels on the desktop, either as the wallpaper or floating above every window:
a game, a shader toy, a telemetry dashboard, a clock.

- **Two layers.** `desktop` sits behind the icons (the wallpaper). `overlay` floats above
  all windows, click-through, with per-pixel alpha. Both can be in use at once, by different apps.
- **Two transports.** `cpu`: write BGRA rows into shared memory, from any language.
  `gpu`: render straight into shared textures with **Direct3D 11, Direct3D 12 or Vulkan**,
  with no copies through system memory.
- **Polite.** Each layer has one owner. If the user picks something else, or another app takes
  the layer, you're told and your session ends. Disconnect, and the desktop layer goes back to
  the user's wallpaper.

Protocol version **2**. Pipe: `\\.\pipe\WinWallpaper.Daemon.v2`.

| Measured (RTX 2060, 2560×1440 monitor) | |
|---|---|
| GPU transport, D3D11 / D3D12 / Vulkan, 1920×1080 @ 60 | ~60 fps each, 0 skipped frames, no CPU copies |
| CPU transport, 1280×720 @ 30 | daemon 0 % CPU (pixels bypass it), host ~5 % of one core |

---

## Quick start (C#)

Reference `src/WallpaperProtocol` (core, no dependencies) and, for the GPU transport, the
helper for your API: `src/WallpaperProtocol.D3D11`, `.D3D12` or `.Vulkan`.

### CPU transport

```csharp
using WallpaperProtocol;

using var session = await WallpaperSession.ConnectAsync(new SessionOptions
{
    Name = "My Renderer",
    Layer = Layer.Desktop,          // or Layer.Overlay
    Transport = Transport.Cpu,
    Width = 1920, Height = 1080,    // the host scales this to every monitor
});
session.RevokedChanged += () => Console.WriteLine("Someone else took the layer");

var pixels = new byte[1920 * 1080 * 4];   // BGRA8, top-down; premultiplied alpha on the overlay
while (!session.Revoked)
{
    Draw(pixels);
    session.Cpu!.Submit(pixels);          // or: Span<byte> dst = session.Cpu.BeginFrame(); ...; Publish();
    await Task.Delay(33);                 // your pace; publish only when something changed
}
```

### GPU transport, Direct3D 11

```csharp
using var session = await WallpaperSession.ConnectAsync(new() { Name = "Shader", Transport = Transport.Gpu, Api = "d3d11" });
using var adapter = D3D11FrameOutput.FindAdapter(session.Gpu!);       // shared textures don't cross GPUs
D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out var device, out var ctx);
using var output = new D3D11FrameOutput(session, device);

if (output.BeginFrame() is { } texture)   // null: the host still holds that slot, skip this tick
{
    /* render into texture (RenderTarget + ShaderResource) */
    output.EndFrame();                    // signals the produced fence, publishes
}
```

### GPU transport, Direct3D 12

```csharp
using var output = new D3D12FrameOutput(session, device, queue);   // device on D3D12FrameOutput.FindAdapter(...)
if (output.BeginFrame() is { } resource)
{
    // record: COMMON -> RENDER_TARGET, draw, RENDER_TARGET -> COMMON  (leave it in COMMON)
    queue.ExecuteCommandList(list);
    output.EndFrame();                    // queue.Signal(produced fence) + publish
}
```

### GPU transport, Vulkan

```csharp
// Vulkan 1.2, timelineSemaphore enabled, VulkanFrameOutput.RequiredDeviceExtensions enabled,
// physical device where VulkanFrameOutput.MatchesAdapter(...) is true.
using var output = new VulkanFrameOutput(session, instanceApi, physicalDevice, deviceApi);
if (output.BeginFrame(out VkImage image))     // VK_FORMAT_B8G8R8A8_UNORM
{
    // record: acquire from VK_QUEUE_FAMILY_EXTERNAL, render, then GENERAL + release to EXTERNAL
    output.SubmitAndPublish(queue, cmd, fence);   // signals the produced timeline semaphore
}
```

### Samples

| Sample | What it shows |
|---|---|
| [GpuRenderer](../samples/GpuRenderer) | The same scene in [D3D11](../samples/GpuRenderer/D3D11Backend.cs), [D3D12](../samples/GpuRenderer/D3D12Backend.cs) and [Vulkan](../samples/GpuRenderer/VulkanBackend.cs), on either layer |
| [ClockRenderer](../samples/ClockRenderer/Program.cs) | CPU: a digital clock as the wallpaper (`--background img.jpg`) or as a floating overlay (`--overlay`). Publishes only when the text changes |
| [ExternalRenderer](../samples/ExternalRenderer/Program.cs) | CPU: animated plasma |
| [python/color_cycle.py](../samples/python/color_cycle.py) | The raw protocol with no SDK, both layers |

```bash
dotnet run --project samples/GpuRenderer -- --api vulkan --layer overlay --seconds 20
dotnet run --project samples/ClockRenderer -- --overlay --seconds
python samples/python/color_cycle.py 10 --overlay
```

---

## The protocol

### 1. Ask for a layer

Connect to `\\.\pipe\WinWallpaper.Daemon.v2`. It speaks one UTF-8 JSON object per line,
request → response, and only accepts connections from the same user. Then send:

```json
→ {"cmd":"external.begin","name":"My Renderer","layer":"overlay","transport":"gpu",
   "width":1920,"height":1080,"alpha":"premultiplied","api":"vulkan"}
```

| Field | Values | Default |
|---|---|---|
| `layer` | `desktop`, `overlay` | `desktop` |
| `transport` | `cpu`, `gpu` | `cpu` |
| `width`, `height` | 1 – 7680 × 4320 | 1920 × 1080 |
| `alpha` | `opaque` (alpha ignored), `premultiplied` | `opaque` on desktop, `premultiplied` on overlay |
| `api` | free text shown in the tray (`d3d11`, `vulkan`, …) | the transport |

The reply contains your **session**:

```json
← {"ok":true,"protocol":2,"session":{
     "id":7,"layer":"overlay","transport":"gpu","width":1920,"height":1080,"stride":0,"slots":3,
     "alpha":"premultiplied","map":"Local\\WinWallpaper.v2.S7.Map","readyEvent":"Local\\WinWallpaper.v2.S7.Ready",
     "gpu":{"adapterLuid":"103942","textures":[2412,2416,2420],"producedFence":2424,"consumedFence":2428}}}
```

**Keep the pipe open. It is your lease.** One connection can own one session; open
another connection for a second layer.

| What happens | Result |
|---|---|
| You close the pipe (or exit) | Your session is destroyed. Desktop layer: the user's wallpaper comes back. Overlay: it disappears |
| The user picks a wallpaper / presses Stop (desktop) or removes the overlay | You receive `{"event":"external.ended"}`; your session is gone |
| Another app asks for the same layer | Same as above: the last one wins |

### 2. Open the session

The host created every resource **for you**. You never create shared objects; you only open
or import them:

- **Map** (`map`): shared memory with a 64-byte header, plus the frame slots for CPU sessions.
- **Ready event** (`readyEvent`): a named auto-reset event you set after each frame.
- **GPU sessions**: the numbers in `gpu` are **NT handles already valid in your process**.
  The host duplicated them into it, using the process ID the kernel reports for your pipe
  connection. There are 3 textures (`DXGI_FORMAT_B8G8R8A8_UNORM`, render target + shader
  resource), a *produced* fence and a *consumed* fence. Render on the adapter whose LUID is
  `adapterLuid` (`LowPart | HighPart << 32`).

| API | Textures | Fences |
|---|---|---|
| Direct3D 11 | `ID3D11Device1::OpenSharedResource1` | `ID3D11Device5::OpenSharedFence` |
| Direct3D 12 | `ID3D12Device::OpenSharedHandle` → `ID3D12Resource` | `ID3D12Device::OpenSharedHandle` → `ID3D12Fence` |
| Vulkan | `VkImportMemoryWin32HandleInfoKHR`, type `D3D11_TEXTURE_BIT`, dedicated allocation | timeline `VkSemaphore` + `vkImportSemaphoreWin32HandleKHR`, type `D3D12_FENCE_BIT` |

Close the handles once you've opened or imported them.

### 3. Header layout

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | `Magic` | `0x32535757` ("WWS2") |
| 4 | 4 | `Version` | 2 |
| 8 | 4 | `Transport` | 0 = CPU, 1 = GPU |
| 12 / 16 | 4 / 4 | `Width` / `Height` | |
| 20 | 4 | `Stride` | CPU: bytes per row (`Width * 4`) |
| 24 | 4 | `Slots` | 3 |
| 28 | 4 | `Alpha` | 0 = opaque, 1 = premultiplied |
| 32 | 8 | `Seq` | last published frame; −1 before the first |
| 40 | 4 | `Slot` | slot of that frame |
| 48 | 8 | `Timestamp` | QueryPerformanceCounter; informational |
| 4096 + n × Stride × Height | Stride × Height | CPU slot *n* | BGRA8, top-down |

### 4. Publish a frame

```
seq  = Seq + 1                      (your own counter; start from the header's Seq + 1)
slot = seq % Slots

CPU:  write rows into slot
GPU:  wait until consumed fence >= seq - Slots + 1   (only from the 4th frame on; skip the tick if not)
      render into texture[slot]
      signal produced fence = seq + 1 on your queue/context

write Slot, Timestamp, then Seq = seq last
SetEvent(readyEvent)
```

The host wakes on the event. For a GPU frame it makes its GPU wait for the *produced* fence
(it never blocks on the CPU), copies the texture, and signals *consumed* = `seq + 1`. Nobody's
CPU waits for anybody. A slow host makes you skip ticks; a slow producer just shows fewer frames.
For a CPU frame the host copies the slot and drops the frame if you overwrote it mid-copy.

### Frame rules

- **Any size** up to 7680×4320. The host scales to each monitor with the user's scaling
  (Fill / Fit / Stretch / Center) and sampling (Smooth / Pixelated) settings, on the GPU.
- **Desktop layer:** alpha is ignored. **Overlay layer:** premultiplied alpha. (0,0,0,0) is
  fully transparent, and the overlay never takes mouse input.
- **Publish only when something changed.** A static frame costs nothing to keep on screen.
- The overlay is a set of top-most windows (one per monitor). It appears above normal windows
  and the taskbar. Exclusive-fullscreen games bypass the desktop compositor, so it can't
  appear over them.

---

## Other daemon commands

Every response has `"ok"`, plus `"error"` when false. State-changing commands answer with the full status.

| Request | Effect |
|---|---|
| `{"cmd":"status"}` | `state` (desktop) = `playing` / `paused` / `stopped` / `external`; `source`; `external` and `overlay` = owner name, session, transport, api, size |
| `{"cmd":"set","path":"C:\\a.gif","kind":"gif","fps":12}` | Play a file on the desktop layer. `kind` = `image` / `gif` / `video` (default: by extension). `fps` 0 = the file's own timing |
| `{"cmd":"fps","fps":24}` | Change the frame rate of the current GIF/video |
| `{"cmd":"fit","mode":"fill"}` | `fill` / `fit` / `stretch` / `center` |
| `{"cmd":"sampling","mode":"smooth"}` | `smooth` (bilinear) / `pixelated` (nearest neighbour) |
| `{"cmd":"pause"}`, `{"cmd":"resume"}` | Freeze/unfreeze the daemon's own wallpaper |
| `{"cmd":"stop"}` | Clear the desktop layer; Windows' own wallpaper shows again |
| `{"cmd":"overlay.stop"}` | Remove whatever is on the overlay layer |
| `{"cmd":"quit"}` | Stop the daemon and host |

## Troubleshooting

| Symptom | Cause |
|---|---|
| Pipe not found | Daemon not running. Start `WallpaperTray.exe` (it starts the daemon) or `WallpaperDaemon.exe` |
| `external.begin` fails with "Can't open producer process" | Your process runs as a different user, or at a higher integrity level (elevated), than the host |
| GPU import fails | Wrong adapter (check `adapterLuid`), or a Vulkan device without the external memory/semaphore extensions or timeline semaphores |
| Connected but nothing shows | `Seq` not advanced, event not set, or (GPU) produced fence never signalled to `seq + 1` |
| Overlay is a black rectangle | You sent opaque black: use premultiplied alpha with A = 0 for transparent pixels |
| Your frames stop | You were revoked (`external.ended`) |

Logs: `%LOCALAPPDATA%\WinWallpaper\logs\daemon.log` and `host.log`.
