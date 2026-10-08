using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// (own file: Unity needs a MonoBehaviour's file to carry its name for AddComponent)
// The lines and dots as one mesh. Points are normalized image coordinates (0..1, origin top-left).
[RequireComponent(typeof(CanvasRenderer))] // Image / Text declare this themselves; a bare Graphic subclass does not
public sealed class FvpOverlayGraphic : MaskableGraphic
{
    struct Line { public Vector2 a, b; public Color c; }
    struct Dot { public Vector2 p; public Color c; }

    readonly List<Line> lines = new List<Line>();
    readonly List<Dot> dots = new List<Dot>();
    int hash, drawnHash;

    public float lineWidth = 3f;
    public float dotSize = 7f;

    public int LineCount => lines.Count;
    public int DotCount => dots.Count;
    public Vector2 DotAt(int i) => dots[i].p;

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
        hash = Mix(hash, a, b);
    }

    public void AddDot(Vector2 p, Color c)
    {
        dots.Add(new Dot { p = p, c = c });
        hash = Mix(hash, p, p);
    }

    // Rebuilds the mesh only when something moved.
    public void End()
    {
        if (hash == drawnHash) return;
        drawnHash = hash;
        SetVerticesDirty();
    }

    static int Mix(int h, Vector2 a, Vector2 b)
    {
        unchecked
        {
            h = h * 31 + Mathf.RoundToInt(a.x * 4096f);
            h = h * 31 + Mathf.RoundToInt(a.y * 4096f);
            h = h * 31 + Mathf.RoundToInt(b.x * 4096f);
            h = h * 31 + Mathf.RoundToInt(b.y * 4096f);
            return h;
        }
    }

    Vector2 ToLocal(Vector2 uv, Rect r) => new Vector2(r.xMin + uv.x * r.width, r.yMax - uv.y * r.height);

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = rectTransform.rect;
        if (r.width <= 0f || r.height <= 0f) return;

        foreach (Line l in lines)
        {
            Vector2 a = ToLocal(l.a, r), b = ToLocal(l.b, r);
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-4f) continue;
            Vector2 n = new Vector2(-d.y, d.x).normalized * (lineWidth * 0.5f);
            Quad(vh, a - n, a + n, b + n, b - n, l.c);
        }

        float h = dotSize * 0.5f;
        foreach (Dot dot in dots)
        {
            Vector2 p = ToLocal(dot.p, r);
            Quad(vh, p + new Vector2(-h, -h), p + new Vector2(-h, h), p + new Vector2(h, h), p + new Vector2(h, -h), dot.c);
        }
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
}
