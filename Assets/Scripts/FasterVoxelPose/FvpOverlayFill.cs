using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// (own file: Unity needs a MonoBehaviour's file to carry its name for AddComponent)
// Filled convex polygons for a screen overlay (FvpOverlay), in the same normalized image coordinates as FvpOverlayGraphic's lines
// (0..1, origin top-left). It sits under the lines: the protection capsule's silhouette is a fill with an outline on top.
[RequireComponent(typeof(CanvasRenderer))]
public sealed class FvpOverlayFill : MaskableGraphic
{
    readonly List<Vector2> points = new List<Vector2>();   // every polygon, one after the other
    readonly List<int> counts = new List<int>();           // vertices of each
    readonly List<Color> colors = new List<Color>();
    int hash, drawnHash;

    public int PolygonCount => counts.Count;
    public int PopulatedVertices { get; private set; }

    // For tests and tools: the vertices of polygon k (normalized image coordinates), appended to `into`. Returns their number.
    public int GetPolygon(int k, List<Vector2> into)
    {
        if (k < 0 || k >= counts.Count) return 0;
        int at = 0;
        for (int i = 0; i < k; i++) at += counts[i];
        for (int i = 0; i < counts[k]; i++) into.Add(points[at + i]);
        return counts[k];
    }

    protected override void Awake()
    {
        base.Awake();
        raycastTarget = false;
    }

    public void Begin()
    {
        points.Clear(); counts.Clear(); colors.Clear();
        hash = 17;
    }

    // `poly` holds a convex polygon in its first `count` entries (either winding).
    public void AddPolygon(Vector2[] poly, int count, Color c)
    {
        if (count < 3) return;
        for (int i = 0; i < count; i++) points.Add(poly[i]);
        counts.Add(count);
        colors.Add(c);
        unchecked
        {
            for (int i = 0; i < count; i++)
            {
                hash = hash * 31 + Mathf.RoundToInt(poly[i].x * 4096f);
                hash = hash * 31 + Mathf.RoundToInt(poly[i].y * 4096f);
            }
            hash = hash * 31 + Mathf.RoundToInt((c.r + 2f * c.g + 4f * c.b + 8f * c.a) * 64f);
        }
    }

    // Rebuilds the mesh only when something moved (or changed colour).
    public void End()
    {
        if (hash == drawnHash) return;
        drawnHash = hash;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = rectTransform.rect;
        if (r.width <= 0f || r.height <= 0f) return;

        int at = 0;
        for (int k = 0; k < counts.Count; k++)
        {
            int n = counts[k];
            UIVertex v = UIVertex.simpleVert;
            v.color = colors[k];
            int first = vh.currentVertCount;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = points[at + i];
                v.position = new Vector2(r.xMin + p.x * r.width, r.yMax - p.y * r.height);
                vh.AddVert(v);
            }
            for (int i = 1; i < n - 1; i++) vh.AddTriangle(first, first + i, first + i + 1);   // a fan: the polygons are convex
            at += n;
        }
        PopulatedVertices = vh.currentVertCount;
    }
}
