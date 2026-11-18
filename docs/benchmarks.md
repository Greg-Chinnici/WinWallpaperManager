# Frame-rate benchmarks

Best-case throughput of every frame transport, measured with `build/bench.ps1`
(2026-11-17). Every combination of:

- **transport:** CPU shared memory, GPU via Direct3D 11, Direct3D 12 and Vulkan
- **target:** 30, 60, 90, 120, 150 and 180 fps
- **frame size:** 1280×720, 1920×1080, 2560×1440 and 3840×2160

That's 96 runs in total, all on the desktop layer.

**Machine:** RTX 2060. Primary monitor 2560×1440 @ **165 Hz**, secondary 1920×1080 @ 60 Hz.
Windows 11 build 26200, Release builds (JIT).

**Producer:** `samples/GpuRenderer` paced by a high-resolution waitable timer. GPU
backends clear the texture and copy in a bar; the CPU backend fills a full BGRA frame in
shared memory. These are near-zero-cost scenes, so this measures the pipeline, not rendering.

**Host frames** come from the host's own counter (`host.status`) over the middle 4 s of each
6 s run. CPU is process time over the same window and is noisy at the ±5 % level.

## Results

**Every transport delivered every target up to 150 fps at every size, including 4K.** At 180
fps the host presents 165 fps, which is the primary monitor's refresh rate. Presents are
vsynced, so the host shows the newest frame at each refresh and drops the extras. That's
expected, not a bottleneck.

Across all 96 runs: **0 torn frames**, and only 11 skipped producer ticks in total (all at
180 fps, where the host is legitimately holding a slot through vsync).

### Frames presented per second (host)

| Resolution | Transport | @30 | @60 | @90 | @120 | @150 | @180 |
|---|---|---:|---:|---:|---:|---:|---:|
| 1280x720 | cpu | 30 | 60 | 90 | 119 | 150 | 165 |
| 1280x720 | d3d11 | 30 | 60 | 90 | 120 | 150 | 165 |
| 1280x720 | d3d12 | 30 | 60 | 90 | 120 | 150 | 165 |
| 1280x720 | vulkan | 30 | 60 | 90 | 120 | 150 | 165 |
| 1920x1080 | cpu | 30 | 60 | 90 | 120 | 150 | 165 |
| 1920x1080 | d3d11 | 30 | 60 | 90 | 120 | 150 | 165 |
| 1920x1080 | d3d12 | 30 | 60 | 90 | 120 | 150 | 165 |
| 1920x1080 | vulkan | 30 | 60 | 90 | 120 | 150 | 165 |
| 2560x1440 | cpu | 30 | 60 | 90 | 120 | 150 | 165 |
| 2560x1440 | d3d11 | 30 | 60 | 90 | 120 | 150 | 165 |
| 2560x1440 | d3d12 | 30 | 60 | 90 | 120 | 150 | 165 |
| 2560x1440 | vulkan | 30 | 60 | 90 | 120 | 150 | 165 |
| 3840x2160 | cpu | 30 | 60 | 90 | 120 | 150 | 165 |
| 3840x2160 | d3d11 | 30 | 60 | 90 | 120 | 150 | 165 |
| 3840x2160 | d3d12 | 30 | 60 | 90 | 120 | 150 | 165 |
| 3840x2160 | vulkan | 30 | 60 | 90 | 120 | 150 | 165 |

### Host cost per frame (ms: copy into the swap chains + present)

| Resolution | cpu | d3d11 | d3d12 | vulkan |
|---|---:|---:|---:|---:|
| 1280x720 | 0.63 | 0.36 | 0.33 | 0.32 |
| 1920x1080 | 0.85 | 0.30 | 0.30 | 0.32 |
| 2560x1440 | 1.47 | 0.29 | 0.30 | 0.31 |
| 3840x2160 | 3.20 | 0.29 | 0.28 | 0.28 |

### CPU use at 60 / 120 / 150 fps (producer + host, % of one core)

| Resolution | cpu | d3d11 | d3d12 | vulkan |
|---|---|---|---|---|
| 1280x720 | 8 / 14 / 16 | 9 / 18 / 10 | 8 / 5 / 12 | 4 / 9 / 10 |
| 1920x1080 | 9 / 21 / 26 | 6 / 4 / 9 | 7 / 2 / 7 | 8 / 8 / 11 |
| 2560x1440 | 15 / 38 / 41 | 3 / 2 / 9 | 4 / 5 / 7 | 3 / 6 / 7 |
| 3840x2160 | 39 / 73 / 94 | 3 / 8 / 7 | 2 / 1 / 11 | 6 / 10 / 10 |

The host cost at 180 fps is about 6 ms per frame. That isn't work: it's the host waiting in
`Present` for the next 165 Hz vblank (1000 / 165 ≈ 6.06 ms), so it's left out of the table above.

## What this means

- **GPU transports cost the same at any resolution.** The host spends about 0.3 ms of GPU
  copy and present per frame, whether the frame is 720p or 4K, and the CPU cost of both
  processes stays in single digits. D3D11, D3D12 and Vulkan are indistinguishable;
  the shared-texture + fence path dominates.
- **The CPU transport scales with pixels.** The host's upload goes from 0.6 ms (720p) to
  3.2 ms (4K) per frame. At 4K that's about 94 % of a core at 150 fps across producer and host,
  and about 160 % at 180. It's fine for 1080p, and for 1440p at moderate rates. Above that,
  use the GPU transport.
- **Mixed refresh rates don't hold the fast monitor back.** With a 165 Hz and a 60 Hz
  monitor, the host still presents 165 fps. DWM composes each display at its own rate, and
  the 60 Hz panel just shows the latest frame.
- **The refresh rate is the real ceiling.** A producer can run faster than the monitor
  (we tested 180 on 165 Hz) without harm: no tearing, no queue build-up. Extra frames
  are coalesced.

## Re-running

```powershell
dotnet build WinWallpaperManager.slnx -c Release
./build/bench.ps1                                                  # full matrix, ~9 min
./build/bench.ps1 -Apis d3d11,vulkan -Sizes 1920x1080 -Fps 60,180  # a slice
```

Raw results are written to `artifacts/bench/bench.csv`. The script parks your saved
wallpaper while it runs and restores it afterwards.
