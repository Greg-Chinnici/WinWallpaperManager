"""
Custom render output in plain Python (no SDK): cycles colours with a sweeping bar.
Shows the raw WinWallpaper protocol v2 (CPU transport) - see docs/custom-render-outputs.md.

    python samples/python/color_cycle.py [seconds] [--overlay]

With --overlay the frame floats above all windows: transparent background, translucent bar
(premultiplied alpha).
"""
import ctypes, json, math, mmap, struct, sys, time

PIPE = r"\\.\pipe\WinWallpaper.Daemon.v2"
W, H, FPS = 640, 360, 20
overlay = "--overlay" in sys.argv
args = [a for a in sys.argv[1:] if not a.startswith("--")]
seconds = float(args[0]) if args else 10

# 1. Ask the daemon for a layer. Keep this pipe open: closing it hands the layer back.
pipe = open(PIPE, "r+b", buffering=0)
pipe.write(json.dumps({
    "cmd": "external.begin", "name": "Python colour cycle", "layer": "overlay" if overlay else "desktop",
    "transport": "cpu", "width": W, "height": H,
}).encode() + b"\n")
reply = json.loads(pipe.readline())
assert reply["ok"], reply
s = reply["session"]
print(f"session {s['id']} on the {s['layer']} layer; map {s['map']}")

# 2. Open the session's shared memory and ready event (the host created both).
SLOTS_AT, stride, slots = 4096, s["stride"], s["slots"]
ring = mmap.mmap(-1, SLOTS_AT + slots * stride * s["height"], tagname=s["map"])
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenEventW.restype = ctypes.c_void_p
event = k32.OpenEventW(0x0002, False, s["readyEvent"])  # EVENT_MODIFY_STATE
assert event, ctypes.get_last_error()

def publish(pixels: bytes):
    seq = struct.unpack_from("<q", ring, 32)[0] + 1         # header.Seq (-1 before the first frame)
    slot = seq % slots
    start = SLOTS_AT + slot * stride * s["height"]
    ring[start:start + len(pixels)] = pixels
    qpc = ctypes.c_int64(); k32.QueryPerformanceCounter(ctypes.byref(qpc))
    struct.pack_into("<i", ring, 40, slot)                    # header.Slot
    struct.pack_into("<q", ring, 48, qpc.value)               # header.Timestamp (informational)
    struct.pack_into("<q", ring, 32, seq)                     # header.Seq, last
    k32.SetEvent(ctypes.c_void_p(event))

# 3. Render at our own pace. Rows are BGRA8, top-down, `stride` bytes apart.
start = time.time()
frames = 0
while time.time() - start < seconds:
    t = time.time() - start
    r, g, b = (int(127 + 127 * math.sin(t + o)) for o in (0, 2, 4))
    if overlay:
        row = bytes((0, 0, 0, 0)) * W                          # fully transparent
        bar_px = bytes((b * 3 // 4, g * 3 // 4, r * 3 // 4, 191))  # 75 % opaque, premultiplied
    else:
        row = bytes((b, g, r, 255)) * W
        bar_px = b"\xff\xff\xff\xff"
    bar = int(t * 200) % (W - 40)
    row_with_bar = row[:bar * 4] + bar_px * 40 + row[(bar + 40) * 4:]
    publish(row * (H // 3) + row_with_bar * (H - 2 * (H // 3)) + row * (H // 3))
    frames += 1
    time.sleep(1 / FPS)

print(f"sent {frames} frames; disconnecting")
pipe.close()
