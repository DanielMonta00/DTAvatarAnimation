using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// Wire format shared with Server~/fvp_server.py (kept free of UnityEngine so it can be exercised
// outside the editor). One packet per message, little-endian, both directions:
//
//   uint32 magic 'FVP1' | uint32 json_len | uint32 bin_len | json (utf-8) | bin
//
//   Unity -> server   config   cameras + capture volume          frame   V synced RGB views (bin)
//                     ping, shutdown
//   server -> Unity   hello, config_ok, result (bin = float32 [people, joints, 5]), pong, error
public static class FvpProtocol
{
    public const uint Magic = 0x31505646; // 'F' 'V' 'P' '1'
    public const int HeaderSize = 12;
    public const int MaxJsonBytes = 1 << 22;
    public const int MaxBinaryBytes = 1 << 28;
    public const int JointCount = 15;
    public const int PoseStride = 5; // x, y, z, valid, score

    public static void WritePacket(Stream s, string json, IList<ArraySegment<byte>> parts)
    {
        byte[] j = Encoding.UTF8.GetBytes(json);
        long bin = 0;
        if (parts != null) for (int i = 0; i < parts.Count; i++) bin += parts[i].Count;
        if (bin > MaxBinaryBytes) throw new InvalidDataException($"payload of {bin} bytes is over the protocol limit");

        var h = new byte[HeaderSize];
        PutU32(h, 0, Magic); PutU32(h, 4, (uint)j.Length); PutU32(h, 8, (uint)bin);
        s.Write(h, 0, h.Length);
        s.Write(j, 0, j.Length);
        if (parts != null)
            for (int i = 0; i < parts.Count; i++) s.Write(parts[i].Array, parts[i].Offset, parts[i].Count);
        s.Flush();
    }

    // False on a clean end of stream between packets; throws on a truncated or malformed one.
    public static bool ReadPacket(Stream s, out string json, out byte[] bin)
    {
        json = null; bin = null;
        var h = new byte[HeaderSize];
        if (!ReadExact(s, h, h.Length, true)) return false;
        uint magic = GetU32(h, 0), jl = GetU32(h, 4), bl = GetU32(h, 8);
        if (magic != Magic) throw new InvalidDataException($"bad packet magic 0x{magic:x8}");
        if (jl > MaxJsonBytes || bl > MaxBinaryBytes) throw new InvalidDataException($"packet too large ({jl} + {bl} bytes)");

        var jb = new byte[jl];
        if (jl > 0) ReadExact(s, jb, (int)jl, false);
        json = Encoding.UTF8.GetString(jb);
        bin = bl == 0 ? Array.Empty<byte>() : new byte[bl];
        if (bl > 0) ReadExact(s, bin, (int)bl, false);
        return true;
    }

    static bool ReadExact(Stream s, byte[] buf, int count, bool eofAllowedAtStart)
    {
        int got = 0;
        while (got < count)
        {
            int n = s.Read(buf, got, count - got);
            if (n <= 0)
            {
                if (got == 0 && eofAllowedAtStart) return false;
                throw new EndOfStreamException("connection closed in the middle of a packet");
            }
            got += n;
        }
        return true;
    }

    static void PutU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    static uint GetU32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

    // ---------------- messages ----------------

    // Values are rounded so that float noise in a Transform does not read as "the cameras moved".
    public static string BuildConfigJson(int configId, int width, int height, double[] spaceCenterMm, double[] spaceSizeMm,
                                         float minScore, int maxPeople, IList<FvpCameraModel> cameras)
    {
        var sb = new StringBuilder(2048);
        sb.Append("{\"type\":\"config\",\"config_id\":").Append(configId);
        sb.Append(",\"width\":").Append(width).Append(",\"height\":").Append(height);
        sb.Append(",\"min_score\":").Append(F(Math.Round(minScore, 4))).Append(",\"max_people\":").Append(maxPeople);
        sb.Append(",\"voxels_per_axis\":[80,80,20]");
        sb.Append(",\"space_center\":").Append(Arr(spaceCenterMm, 1));
        sb.Append(",\"space_size\":").Append(Arr(spaceSizeMm, 1));
        sb.Append(",\"cameras\":[");
        for (int i = 0; i < cameras.Count; i++)
        {
            FvpCameraModel c = cameras[i];
            c.ToModelFrame(out double[] R, out double[] T);
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"").Append(Esc(c.name)).Append('"');
            sb.Append(",\"R\":").Append(Arr(R, 6));
            sb.Append(",\"T\":").Append(Arr(T, 1));
            sb.Append(",\"fx\":").Append(F(Math.Round(c.fx, 3))).Append(",\"fy\":").Append(F(Math.Round(c.fy, 3)));
            sb.Append(",\"cx\":").Append(F(Math.Round(c.cx, 3))).Append(",\"cy\":").Append(F(Math.Round(c.cy, 3)));
            sb.Append(",\"k\":[").Append(F(c.k1)).Append(',').Append(F(c.k2)).Append(',').Append(F(c.k3)).Append(']');
            sb.Append(",\"p\":[").Append(F(c.p1)).Append(',').Append(F(c.p2)).Append(']');
            if (c.maxRadius > 0) sb.Append(",\"max_radius\":").Append(F(Math.Round(c.maxRadius, 5)));
            sb.Append('}');
        }
        return sb.Append("]}").ToString();
    }

    public static string BuildFrameJson(int id, int configId, int views, int width, int height, float minScore) =>
        "{\"type\":\"frame\",\"id\":" + id + ",\"config_id\":" + configId + ",\"views\":" + views +
        ",\"width\":" + width + ",\"height\":" + height + ",\"min_score\":" + F(Math.Round(minScore, 4)) + "}";

    // float32 [people, joints, 5] -> float[]
    public static float[] DecodePoses(byte[] bin)
    {
        var f = new float[bin.Length / 4];
        Buffer.BlockCopy(bin, 0, f, 0, f.Length * 4); // the wire is little-endian; so is every Unity platform
        return f;
    }

    static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    static string Arr(double[] v, int digits)
    {
        var sb = new StringBuilder(v.Length * 12).Append('[');
        for (int i = 0; i < v.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(F(Math.Round(v[i], digits)));
        }
        return sb.Append(']').ToString();
    }

    static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
