using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// (own file: Unity needs a MonoBehaviour's file to carry its name for AddComponent)
// The lines and dots as one mesh, drawn like the dataset images: thin constant-width lines and filled round dots.
// Points are normalized image coordinates (0..1, origin top-left). Sizes are given for an image `referenceWidth` pixels
// wide (the dataset images are 1920 wide) and scale with the width the display actually gives the image.
[RequireComponent(typeof(CanvasRenderer))] // Image / Text declare this themselves; a bare Graphic subclass does not
public sealed class FvpOverlayGraphic : MaskableGraphic
{
    struct Line { public Vector2 a, b; public Color c; }
    struct Dot { public Vector2 p; public Color c; }

    const int DiscSegments = 14;

    readonly List<Line> lines = new List<Line>();
    readonly List<Dot> dots = new List<Dot>();
    int hash, drawnHash;

    public float referenceWidth = 1920f;
    public float lineWidth = 2f;      // pixels at referenceWidth
    public float dotRadius = 4f;      // pixels at referenceWidth
    public float minLineWidth = 1.5f; // never thinner than this on screen
    public float minDotRadius = 2.5f;

    public int LineCount => lines.Count;
    public int DotCount => dots.Count;
    public Vector2 DotAt(int i) => dots[i].p;
    public int PopulatedVertices { get; private set; } // vertices of the last mesh built (diagnostics)

    protected override void Awake()
    {
        base.Awake();
        raycastTarget = false;
    }

    public void Begin()
    {
        lines.Clear(); dots.Clear();
        hash = 17;
    }

    public void AddLine(Vector2 a, Vector2 b, Color c)
    {
        lines.Add(new Line { a = a, b = b, c = c });
        hash = Mix(hash, a, b, c);
    }

    public void AddDot(Vector2 p, Color c)
    {
        dots.Add(new Dot { p = p, c = c });
        hash = Mix(hash, p, p, c);
    }

    // Rebuilds the mesh only when something moved (or changed colour).
    public void End()
    {
        float sizeKey = lineWidth * 31f + dotRadius;
        int h = hash * 31 + Mathf.RoundToInt(sizeKey * 16f);
        if (h == drawnHash) return;
        drawnHash = h;
        SetVerticesDirty();
    }

    static int Mix(int h, Vector2 a, Vector2 b, Color c)
    {
        unchecked
        {
            h = h * 31 + Mathf.RoundToInt(a.x * 4096f);
            h = h * 31 + Mathf.RoundToInt(a.y * 4096f);
            h = h * 31 + Mathf.RoundToInt(b.x * 4096f);
            h = h * 31 + Mathf.RoundToInt(b.y * 4096f);
            h = h * 31 + Mathf.RoundToInt((c.r + 2f * c.g + 4f * c.b) * 64f);
            return h;
        }
    }

    Vector2 ToLocal(Vector2 uv, Rect r) => new Vector2(r.xMin + uv.x * r.width, r.yMax - uv.y * r.height);

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = rectTransform.rect;
        if (r.width <= 0f || r.height <= 0f) return;

        float scale = r.width / Mathf.Max(1f, referenceWidth);
        float half = Mathf.Max(minLineWidth, lineWidth * scale) * 0.5f;
        float radius = Mathf.Max(minDotRadius, dotRadius * scale);

        foreach (Line l in lines)
        {
            Vector2 a = ToLocal(l.a, r), b = ToLocal(l.b, r);
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-4f) continue;
            Vector2 n = new Vector2(-d.y, d.x).normalized * half;
            Quad(vh, a - n, a + n, b + n, b - n, l.c);
        }

        foreach (Dot dot in dots)
            Disc(vh, ToLocal(dot.p, r), radius, dot.c);
        PopulatedVertices = vh.currentVertCount;
    }

    static void Quad(VertexHelper vh, Vector2 v0, Vector2 v1, Vector2 v2, Vector2 v3, Color c)
    {
        int i = vh.currentVertCount;
        UIVertex v = UIVertex.simpleVert;
        v.color = c;
        v.position = v0; vh.AddVert(v);
        v.position = v1; vh.AddVert(v);
        v.position = v2; vh.AddVert(v);
        v.position = v3; vh.AddVert(v);
        vh.AddTriangle(i, i + 1, i + 2);
        vh.AddTriangle(i + 2, i + 3, i);
    }

    static void Disc(VertexHelper vh, Vector2 centre, float radius, Color c)
    {
        int first = vh.currentVertCount;
        UIVertex v = UIVertex.simpleVert;
        v.color = c;
        v.position = centre; vh.AddVert(v);
        for (int k = 0; k < DiscSegments; k++)
        {
            float a = k * (Mathf.PI * 2f / DiscSegments);
            v.position = centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            vh.AddVert(v);
        }
        for (int k = 0; k < DiscSegments; k++)
            vh.AddTriangle(first, first + 1 + k, first + 1 + (k + 1) % DiscSegments);
    }
}
