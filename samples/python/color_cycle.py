"""
Custom render output in plain Python (no SDK): cycles colours with a sweeping bar.
Shows the raw WinWallpaper protocol v1 - see docs/custom-render-outputs.md.

    python samples/python/color_cycle.py [seconds]
"""
import ctypes, json, math, mmap, struct, sys, time

PIPE = r"\\.\pipe\WinWallpaper.Daemon.v1"
W, H, FPS = 640, 360, 20
seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 10

# 1. Ask the daemon for the wallpaper. Keep this pipe open: closing it hands the wallpaper back.
pipe = open(PIPE, "r+b", buffering=0)
pipe.write(json.dumps({"cmd": "external.begin", "name": "Python colour cycle"}).encode() + b"\n")
reply = json.loads(pipe.readline())
assert reply["ok"], reply
writer = reply["writer"]
print(f"writer {writer}; frame map {reply['frameMap']}")

# 2. Open the shared frame ring and the frame-ready event.
CONTROL, HEADER, SLOTS_AT, SLOTS = 0, 64, 4096, 3
slot_size = reply["maxWidth"] * reply["maxHeight"] * 4
ring = mmap.mmap(-1, SLOTS_AT + SLOTS * slot_size, tagname=reply["frameMap"])
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenEventW.restype = ctypes.c_void_p
event = k32.OpenEventW(0x0002, False, reply["frameReadyEvent"])  # EVENT_MODIFY_STATE
assert event, ctypes.get_last_error()

def publish(pixels: bytes):
    seq = struct.unpack_from("<q", ring, HEADER + 24)[0] + 1      # Seq is shared by all writers
    slot = seq % SLOTS
    ring[SLOTS_AT + slot * slot_size : SLOTS_AT + slot * slot_size + len(pixels)] = pixels
    # Writer, Width, Height, Stride, Slot, Format(0=BGRA8) ... Timestamp; Seq goes in last.
    struct.pack_into("<6i", ring, HEADER, writer, W, H, W * 4, slot, 0)
    qpc = ctypes.c_int64(); k32.QueryPerformanceCounter(ctypes.byref(qpc))   # informational
    struct.pack_into("<q", ring, HEADER + 32, qpc.value)
    struct.pack_into("<q", ring, HEADER + 24, seq)
    k32.SetEvent(ctypes.c_void_p(event))

# 3. Render at our own pace. Rows are BGRA8, top-down, tightly packed (stride = width * 4).
start = time.time()
frames = 0
while time.time() - start < seconds:
    t = time.time() - start
    r, g, b = (int(127 + 127 * math.sin(t + o)) for o in (0, 2, 4))
    row = bytes((b, g, r, 255)) * W
    bar = (int(t * 200) % (W - 40)) * 4
    row_with_bar = row[:bar] + b"\xff" * 160 + row[bar + 160:]
    publish(row * (H // 3) + row_with_bar * (H - 2 * (H // 3)) + row * (H // 3))
    frames += 1
    time.sleep(1 / FPS)

print(f"sent {frames} frames; disconnecting")
pipe.close()
