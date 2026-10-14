using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Writes a continuous video from JPEG frames: an AVI with one Motion-JPEG stream. The JPEGs are used as they are (no second encode), so it is
// as cheap as writing the image files, needs no codec library, and plays in VLC, ffmpeg, OpenCV and most editors (Windows' own player may need a
// MJPEG codec; the recorder can also convert to .mp4 with ffmpeg afterwards).
//
// The video has a CONSTANT frame rate and plays at the speed of the clock its frames are stamped with. Append(frame, time) says when, on that clock,
// the frame was captured; the writer lays the frames on a grid of `fps` ticks (tick m = t0 + m / fps, t0 = the first frame's time) and tick m shows
// the newest frame stamped at or before it: a capture slower than the video repeats a frame, a faster one leaves some out, an uneven one (hitches,
// frames dropped under load) is evened out. Writing the captured frames back to back at a nominal rate instead plays at nominal / actual of the real
// speed: 30 fps over a capture that really ran at 28.2 fps ran 6 % fast (4 s ahead of the animation after a minute), and every irregular gap of the
// capture became a visible change of speed. Two videos made on the same clock with the same fps have the same frame at every tick, which is what lets
// a video of the digital twin be compared with a video of the dataset it replays (visualization/view_session.py uses the same rule).
//
// Frames arrive as tasks, in order, from the main thread (Append); the encoders finish them in any order, and one writer thread waits for them in
// that order and appends them to the file. A task that ends with null (failed encode) leaves that frame out (the previous frame is held over its
// time). Frames are put in time order within a short window first (the GPU read-backs of consecutive frames can complete out of order); a frame
// that arrives later than that is left out.
//
// A classic AVI holds 2 GB at most: the writer starts a new file (name_part2.avi, ...) before that. Next to the first file it writes
// <name>.timeline.csv: one line per video frame with its time on the clock and the id (the recorder's frame_index) of the captured frame it shows.
public sealed class MjpegAviWriter
{
    public const long MaxSegmentBytes = 1_900_000_000L;
    const int HeaderBytes = 224;                       // RIFF + hdrl (avih, strl) + the 'movi' list header; chunks start here

    readonly string basePath;
    readonly int width, height;
    readonly double fps, period;
    readonly Action<string> log;
    readonly BlockingCollection<(Task<byte[]> jpeg, double time, int id)> queue = new BlockingCollection<(Task<byte[]>, double, int)>(4096);
    readonly Thread thread;
    readonly List<string> files = new List<string>();
    int frames, skipped, late;
    bool closed;

    // The GPU read-backs of two consecutive frames can complete out of order (one pair in 2000 in a real session), so frames are put in time order
    // within a window of this many before they are placed on the grid; one that arrives later than that is left out.
    const int ReorderWindow = 4;

    // writer thread only
    Segment seg;
    int segmentNumber;
    byte[] held;                                       // the newest placed frame: what the next ticks show
    double heldTime, t0, newest = double.NegativeInfinity;
    int heldId;
    long tick;                                         // the next tick to write
    readonly List<(byte[] jpg, double time, int id)> window = new List<(byte[], double, int)>();
    readonly List<(int id, int file)> timeline = new List<(int, int)>();

    public IReadOnlyList<string> Files { get { lock (files) return files.ToArray(); } }
    public int Frames => Volatile.Read(ref frames);            // frames in the file(s): one per tick, repeats included
    public int Skipped => Volatile.Read(ref skipped);          // captured frames left out of the video (failed encode, queue full)
    public int Late => Volatile.Read(ref late);                // ... because a newer frame had been placed already
    public double Fps => fps;
    public string FirstPath => basePath;

    // fps: the video's frame rate (<= 0: 30).
    public MjpegAviWriter(string path, int width, int height, double fps, Action<string> log = null)
    {
        basePath = path; this.width = width; this.height = height; this.log = log;
        this.fps = fps > 0.0 && !double.IsNaN(fps) && !double.IsInfinity(fps) ? fps : 30.0;
        period = 1.0 / this.fps;
        thread = new Thread(Run) { IsBackground = true, Name = "MjpegAviWriter " + Path.GetFileName(path) };
        thread.Start();
    }

    // Main thread: the next frame, as a task that yields its JPEG bytes. `time` is the capture time in seconds on the video's clock (the game's clock,
    // or the dataset's in a replay); `id` is written to the timeline file (the recorder's frame_index).
    public void Append(Task<byte[]> jpeg, double time, int id = -1)
    {
        if (closed) return;
        if (!queue.TryAdd((jpeg, time, id))) { Interlocked.Increment(ref skipped); log?.Invoke("video: the writer is too far behind, a frame was left out of the video"); }
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
        public uint maxChunk;
    }

    void Run()
    {
        try
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                byte[] jpg;
                try { jpg = item.jpeg.Result; } catch { jpg = null; }
                if (jpg == null || jpg.Length < 4) { Interlocked.Increment(ref skipped); continue; }

                int pos = window.Count;                     // into the window by time, after any frame with the same time
                while (pos > 0 && window[pos - 1].time > item.time) pos--;
                window.Insert(pos, (jpg, item.time, item.id));
                if (window.Count > ReorderWindow) { Place(window[0]); window.RemoveAt(0); }
            }
            foreach (var f in window) Place(f);
            window.Clear();
            if (held != null) Lay(heldTime, true);          // the last frame's own tick(s)
        }
        catch (Exception e) { log?.Invoke("video: " + e.Message); }
        finally
        {
            if (seg != null) { try { Finish(seg); } catch (Exception e) { log?.Invoke("video: " + e.Message); } }
            try { WriteTimeline(); } catch (Exception e) { log?.Invoke("video timeline: " + e.Message); }
        }
    }

    // The next frame in time order becomes the held one; the ticks that fell before it were shown by the previous one.
    void Place((byte[] jpg, double time, int id) f)
    {
        if (f.time < newest) { Interlocked.Increment(ref late); return; }
        newest = f.time;
        if (held == null) t0 = f.time;
        else Lay(f.time, false);
        held = f.jpg; heldTime = f.time; heldId = f.id;
    }

    // Writes the held frame for every tick of the grid before `until` (up to and including it when `inclusive`).
    void Lay(double until, bool inclusive)
    {
        while (true)
        {
            double t = t0 + tick * period;
            if (inclusive ? t > until : t >= until) break;
            WriteFrame(held, heldId);
            tick++;
        }
    }

    void WriteFrame(byte[] jpg, int id)
    {
        if (seg != null && seg.fs.Position + jpg.Length + 16 + (seg.index.Count + 1) * 16L + 64 > MaxSegmentBytes)
        {
            Finish(seg);
            seg = null;
        }
        if (seg == null) seg = Open(segmentNumber++);

        long chunkPos = seg.fs.Position;
        seg.bw.Write(Fourcc("00dc"));
        seg.bw.Write((uint)jpg.Length);
        seg.bw.Write(jpg);
        if ((jpg.Length & 1) != 0) seg.bw.Write((byte)0);        // chunks are word aligned
        seg.index.Add(((uint)(chunkPos - MoviTag), (uint)jpg.Length));
        if ((uint)jpg.Length > seg.maxChunk) seg.maxChunk = (uint)jpg.Length;
        timeline.Add((id, segmentNumber - 1));
        Interlocked.Increment(ref frames);
    }

    const long MoviTag = 220;                            // where the 'movi' four-cc sits; index offsets count from it

    Segment Open(int number)
    {
        string path = number == 0 ? basePath
                    : Path.Combine(Path.GetDirectoryName(basePath) ?? "", Path.GetFileNameWithoutExtension(basePath) + "_part" + (number + 1) + Path.GetExtension(basePath));
        var seg = new Segment { path = path };
        seg.fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        seg.bw = new BinaryWriter(seg.fs, Encoding.ASCII);
        seg.bw.Write(new byte[HeaderBytes]);              // reserved: written for real in Finish, when the counts are known
        lock (files) files.Add(path);
        return seg;
    }

    // <name>.timeline.csv beside the first file: video_frame (0-based, over all the parts), time_s (on the video's clock, from the first frame),
    // frame_index (the captured frame it shows, as in keypoints_transforms.json), file.
    void WriteTimeline()
    {
        if (timeline.Count == 0) return;
        string path = Path.Combine(Path.GetDirectoryName(basePath) ?? "", Path.GetFileNameWithoutExtension(basePath) + ".timeline.csv");
        string[] names;
        lock (files) names = files.ToArray();
        var sb = new StringBuilder(timeline.Count * 40 + 64);
        sb.Append("video_frame,time_s,frame_index,file\n");
        for (int m = 0; m < timeline.Count; m++)
            sb.Append(m.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append((m * period).ToString("F6", CultureInfo.InvariantCulture)).Append(',')
              .Append(timeline[m].id.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Path.GetFileName(names[Math.Min(timeline[m].file, names.Length - 1)])).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
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
