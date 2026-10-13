using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Writes a continuous video from JPEG frames: an AVI with one Motion-JPEG stream. The JPEGs are used as they are (no second encode), so it is
// as cheap as writing the image files, needs no codec library, and plays in VLC, ffmpeg, OpenCV and most editors (Windows' own player may need a
// MJPEG codec; the recorder can also convert to .mp4 with ffmpeg afterwards).
//
// Frames arrive as tasks, in order, from the main thread (Append); the encoders finish them in any order, and one writer thread waits for them in
// that order and appends them to the file, so the video has the recording's frame order whatever the encode threads do. A task that ends with null
// (failed encode) leaves that frame out.
//
// A classic AVI holds 2 GB at most: the writer starts a new file (name_part2.avi, ...) before that. The frame rate is fixed up front when it is known
// (deterministic capture), otherwise it is the average over the file, measured from the times given to Append.
public sealed class MjpegAviWriter
{
    public const long MaxSegmentBytes = 1_900_000_000L;
    const int HeaderBytes = 224;                       // RIFF + hdrl (avih, strl) + the 'movi' list header; chunks start here

    readonly string basePath;
    readonly int width, height;
    readonly double fixedFps;                          // <= 0: measure
    readonly Action<string> log;
    readonly BlockingCollection<(Task<byte[]> jpeg, double time)> queue = new BlockingCollection<(Task<byte[]>, double)>(4096);
    readonly Thread thread;
    readonly List<string> files = new List<string>();
    int frames, skipped;
    bool closed;

    public IReadOnlyList<string> Files { get { lock (files) return files.ToArray(); } }
    public int Frames => Volatile.Read(ref frames);
    public int Skipped => Volatile.Read(ref skipped);
    public string FirstPath => basePath;

    public MjpegAviWriter(string path, int width, int height, double fps, Action<string> log = null)
    {
        basePath = path; this.width = width; this.height = height; fixedFps = fps; this.log = log;
        thread = new Thread(Run) { IsBackground = true, Name = "MjpegAviWriter " + Path.GetFileName(path) };
        thread.Start();
    }

    // Main thread: the next frame, as a task that yields its JPEG bytes. `time` is any clock in seconds (used to measure the frame rate).
    public void Append(Task<byte[]> jpeg, double time)
    {
        if (closed) return;
        if (!queue.TryAdd((jpeg, time))) { Interlocked.Increment(ref skipped); log?.Invoke("video: the writer is too far behind, a frame was left out of the video"); }
    }

    // Waits for every frame handed over so far and finishes the file(s).
    public void Close()
    {
        if (closed) return;
        closed = true;
        queue.CompleteAdding();
        if (!thread.Join(30000)) log?.Invoke("video: the writer did not finish within 30 s; " + Path.GetFileName(basePath) + " may be incomplete");
    }

    // ---------------- writer thread ----------------

    sealed class Segment
    {
        public FileStream fs;
        public BinaryWriter bw;
        public string path;
        public readonly List<(uint offset, uint size)> index = new List<(uint, uint)>();
        public double t0, t1;
        public uint maxChunk;
    }

    void Run()
    {
        Segment seg = null;
        int segmentNumber = 0;
        try
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                byte[] jpg;
                try { jpg = item.jpeg.Result; } catch { jpg = null; }
                if (jpg == null || jpg.Length < 4) { Interlocked.Increment(ref skipped); continue; }

                if (seg != null && seg.fs.Position + jpg.Length + 16 + (seg.index.Count + 1) * 16L + 64 > MaxSegmentBytes)
                {
                    Finish(seg);
                    seg = null;
                }
                if (seg == null) seg = Open(segmentNumber++, item.time);

                long chunkPos = seg.fs.Position;
                seg.bw.Write(Fourcc("00dc"));
                seg.bw.Write((uint)jpg.Length);
                seg.bw.Write(jpg);
                if ((jpg.Length & 1) != 0) seg.bw.Write((byte)0);        // chunks are word aligned
                seg.index.Add(((uint)(chunkPos - MoviTag), (uint)jpg.Length));
                if ((uint)jpg.Length > seg.maxChunk) seg.maxChunk = (uint)jpg.Length;
                seg.t1 = item.time;
                Interlocked.Increment(ref frames);
            }
        }
        catch (Exception e) { log?.Invoke("video: " + e.Message); }
        finally
        {
            if (seg != null) { try { Finish(seg); } catch (Exception e) { log?.Invoke("video: " + e.Message); } }
        }
    }

    const long MoviTag = 220;                            // where the 'movi' four-cc sits; index offsets count from it

    Segment Open(int number, double time)
    {
        string path = number == 0 ? basePath
                    : Path.Combine(Path.GetDirectoryName(basePath) ?? "", Path.GetFileNameWithoutExtension(basePath) + "_part" + (number + 1) + Path.GetExtension(basePath));
        var seg = new Segment { path = path, t0 = time, t1 = time };
        seg.fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        seg.bw = new BinaryWriter(seg.fs, Encoding.ASCII);
        seg.bw.Write(new byte[HeaderBytes]);              // reserved: written for real in Finish, when the counts are known
        lock (files) files.Add(path);
        return seg;
    }

    void Finish(Segment seg)
    {
        BinaryWriter bw = seg.bw;
        int n = seg.index.Count;
        long moviEnd = seg.fs.Position;

        // 'idx1': one entry per frame
        bw.Write(Fourcc("idx1"));
        bw.Write((uint)(16 * n));
        foreach (var e in seg.index)
        {
            bw.Write(Fourcc("00dc"));
            bw.Write(0x10u);                              // AVIIF_KEYFRAME: every JPEG is one
            bw.Write(e.offset);
            bw.Write(e.size);
        }
        long fileEnd = seg.fs.Position;

        double elapsed = Math.Max(1e-3, seg.t1 - seg.t0);
        double fps = fixedFps > 0.0 ? fixedFps : (n > 1 ? (n - 1) / elapsed : 30.0);
        if (fps < 0.01 || double.IsNaN(fps) || double.IsInfinity(fps)) fps = 30.0;
        uint rate = (uint)Math.Max(1.0, Math.Round(fps * 1000.0));
        uint usPerFrame = (uint)Math.Max(1.0, Math.Round(1e6 / fps));

        seg.fs.Seek(0, SeekOrigin.Begin);
        bw.Write(Fourcc("RIFF")); bw.Write((uint)(fileEnd - 8)); bw.Write(Fourcc("AVI "));
        bw.Write(Fourcc("LIST")); bw.Write(192u); bw.Write(Fourcc("hdrl"));

        bw.Write(Fourcc("avih")); bw.Write(56u);
        bw.Write(usPerFrame);                             // dwMicroSecPerFrame
        bw.Write((uint)Math.Min(uint.MaxValue, (long)(seg.maxChunk * fps)));   // dwMaxBytesPerSec
        bw.Write(0u);                                     // dwPaddingGranularity
        bw.Write(0x10u);                                  // AVIF_HASINDEX
        bw.Write((uint)n);                                // dwTotalFrames
        bw.Write(0u);                                     // dwInitialFrames
        bw.Write(1u);                                     // dwStreams
        bw.Write(seg.maxChunk);                           // dwSuggestedBufferSize
        bw.Write((uint)width); bw.Write((uint)height);
        bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);

        bw.Write(Fourcc("LIST")); bw.Write(116u); bw.Write(Fourcc("strl"));
        bw.Write(Fourcc("strh")); bw.Write(56u);
        bw.Write(Fourcc("vids")); bw.Write(Fourcc("MJPG"));
        bw.Write(0u);                                     // flags
        bw.Write((ushort)0); bw.Write((ushort)0);         // priority, language
        bw.Write(0u);                                     // initial frames
        bw.Write(1000u); bw.Write(rate);                  // scale, rate: rate / scale frames a second
        bw.Write(0u);                                     // start
        bw.Write((uint)n);                                // length
        bw.Write(seg.maxChunk);                           // suggested buffer size
        bw.Write(0xFFFFFFFFu);                            // quality: default
        bw.Write(0u);                                     // sample size: variable
        bw.Write((short)0); bw.Write((short)0); bw.Write((short)width); bw.Write((short)height);
        bw.Write(Fourcc("strf")); bw.Write(40u);
        bw.Write(40u); bw.Write(width); bw.Write(height);
        bw.Write((ushort)1); bw.Write((ushort)24);
        bw.Write(Fourcc("MJPG"));
        bw.Write((uint)(width * (long)height * 3));
        bw.Write(0); bw.Write(0); bw.Write(0u); bw.Write(0u);

        bw.Write(Fourcc("LIST")); bw.Write((uint)(moviEnd - 216 - 4)); bw.Write(Fourcc("movi"));
        bw.Flush();
        bw.Dispose();
        seg.fs.Dispose();
    }

    static uint Fourcc(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);
}
