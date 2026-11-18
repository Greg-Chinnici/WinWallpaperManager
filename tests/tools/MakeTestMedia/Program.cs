// Generates deterministic test media for the wallpaper POC:
//   MakeTestMedia <outDir>
//     test.gif  320x180, 20 frames, 10 fps (100 ms delays), red band at the top
//     test.mp4  1280x720 H.264, 30 fps, 6 s, red band at the top, moving bar + frame counter
//     test1080.mp4  same at 1920x1080 (H.264 pads it to 1088 rows)
// The red band makes upside-down output obvious in screenshots.

using Vortice.MediaFoundation;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);
Gif.Write(Path.Combine(outDir, "test.gif"));
Mp4.Write(Path.Combine(outDir, "test.mp4"), 1280, 720);
Mp4.Write(Path.Combine(outDir, "test1080.mp4"), 1920, 1080); // stored as 1088 rows: exercises display-aperture cropping
Console.WriteLine("wrote test.gif and test.mp4 to " + Path.GetFullPath(outDir));

static class Pattern
{
    /// <summary>BGRA frame: dark gradient, red band at top, white moving bar, blocks encoding the frame number.</summary>
    public static uint Pixel(int x, int y, int w, int h, int frame, int frames)
    {
        if (y < h / 10) return 0xFFD02020;                                   // red band (top)
        var barX = (int)((long)frame * (w - w / 16) / Math.Max(1, frames - 1));
        if (x >= barX && x < barX + w / 16) return 0xFFFFFFFF;               // white bar
        var blk = x / (w / 8);
        if (y > h * 8 / 10 && ((frame >> blk) & 1) == 1) return 0xFF20C060;  // frame-number bits (bottom)
        var g = (byte)(40 + 80 * y / h);
        return 0xFF000000u | (uint)(g / 2) << 16 | (uint)(g / 2) << 8 | g;  // blue-ish gradient
    }
}

/// <summary>Minimal GIF89a writer with "uncompressed" LZW (literal codes + periodic clears).</summary>
static class Gif
{
    public static void Write(string path)
    {
        const int w = 320, h = 180, frames = 20;
        // Palette: index 0..255 from a fixed 3-3-2 RGB cube.
        var pal = new byte[256 * 3];
        for (var i = 0; i < 256; i++) { pal[i * 3] = (byte)((i >> 5) * 255 / 7); pal[i * 3 + 1] = (byte)(((i >> 2) & 7) * 255 / 7); pal[i * 3 + 2] = (byte)((i & 3) * 255 / 3); }
        static byte Index(uint bgra) { var r = (bgra >> 16) & 0xFF; var g = (bgra >> 8) & 0xFF; var b = bgra & 0xFF; return (byte)((r * 7 / 255) << 5 | (g * 7 / 255) << 2 | (b * 3 / 255)); }

        using var f = new BinaryWriter(File.Create(path));
        f.Write("GIF89a"u8);
        f.Write((ushort)w); f.Write((ushort)h);
        f.Write((byte)0xF7); f.Write((byte)0); f.Write((byte)0); // global table, 256 entries
        f.Write(pal);
        f.Write(new byte[] { 0x21, 0xFF, 0x0B }); f.Write("NETSCAPE2.0"u8); f.Write(new byte[] { 3, 1, 0, 0, 0 }); // loop forever
        for (var n = 0; n < frames; n++)
        {
            f.Write(new byte[] { 0x21, 0xF9, 4, 0x04 /* disposal 1 */ }); f.Write((ushort)10 /* 100 ms */); f.Write(new byte[] { 0, 0 });
            f.Write((byte)0x2C); f.Write((ushort)0); f.Write((ushort)0); f.Write((ushort)w); f.Write((ushort)h); f.Write((byte)0);
            var idx = new byte[w * h];
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) idx[y * w + x] = Index(Pattern.Pixel(x, y, w, h, n, frames));
            WriteLzw(f, idx);
        }
        f.Write((byte)0x3B);
    }

    private static void WriteLzw(BinaryWriter f, byte[] data)
    {
        const int minCode = 8, clear = 256, eoi = 257;
        var bytes = new List<byte>();
        int acc = 0, bits = 0;
        void Emit(int code) { acc |= code << bits; bits += 9; while (bits >= 8) { bytes.Add((byte)acc); acc >>= 8; bits -= 8; } }
        var sinceClear = 0;
        Emit(clear);
        foreach (var b in data)
        {
            if (sinceClear == 250) { Emit(clear); sinceClear = 0; } // keep the code width at 9 bits
            Emit(b); sinceClear++;
        }
        Emit(eoi);
        if (bits > 0) bytes.Add((byte)acc);
        f.Write((byte)minCode);
        for (var i = 0; i < bytes.Count; i += 255)
        {
            var n = Math.Min(255, bytes.Count - i);
            f.Write((byte)n);
            f.Write(bytes.GetRange(i, n).ToArray());
        }
        f.Write((byte)0);
    }
}

static unsafe class Mp4
{
    public static void Write(string path, int w, int h)
    {
        const int fps = 30, seconds = 6, frames = fps * seconds;
        if (File.Exists(path)) File.Delete(path);
        MediaFactory.MFStartup(true);
        try
        {
            using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, null!);
            using var outType = MediaFactory.MFCreateMediaType();
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, new Guid("34363248-0000-0010-8000-00AA00389B71") /* MFVideoFormat_H264 */);
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, 4_000_000u);
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u /* progressive */);
            outType.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)w << 32) | (uint)h);
            outType.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)fps << 32) | 1);
            outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1);
            var stream = writer.AddStream(outType);

            using var inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            inType.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)w << 32) | (uint)h);
            inType.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)fps << 32) | 1);
            inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1);
            inType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(w * 4)); // positive = top-down
            writer.SetInputMediaType(stream, inType, null!);
            writer.BeginWriting();

            const long duration = 10_000_000 / fps;
            for (var n = 0; n < frames; n++)
            {
                using var buf = MediaFactory.MFCreateMemoryBuffer(w * h * 4);
                buf.Lock(out var ptr, out _, out _);
                var px = (uint*)ptr;
                for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) px[y * w + x] = Pattern.Pixel(x, y, w, h, n, frames);
                buf.Unlock();
                buf.CurrentLength = w * h * 4;
                using var sample = MediaFactory.MFCreateSample();
                sample.AddBuffer(buf);
                sample.SampleTime = n * duration;
                sample.SampleDuration = duration;
                writer.WriteSample(stream, sample);
            }
            writer.Finalize();
        }
        finally { MediaFactory.MFShutdown(); }
    }
}
