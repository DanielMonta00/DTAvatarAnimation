using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedurally builds a rounded 90 degree conveyor corner: a rectangular cross-section
/// (matching a straight rail segment's width/height) swept along a circular arc, so the piece
/// reads as a real curved turn instead of a mitered overlap of two straight segments. Rebuilds
/// live in the editor (ExecuteAlways + OnValidate) so radius/segments/material can be tuned by
/// eye in the Inspector and Scene view. Purely cosmetic: no belt-movement or item-transport logic.
///
/// Local space convention: the piece starts at the local origin heading toward local +Z, and
/// after sweeping `sweepAngle` degrees ends up heading toward local +X - position/rotate the
/// whole GameObject so the start lines up with one straight segment's end, matching direction.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ConveyorCornerPiece : MonoBehaviour
{
    [Tooltip("Cross-section width, matching the straight segment's rail width.")]
    public float width = 0.1f;
    [Tooltip("Cross-section height, matching the straight segment's rail height.")]
    public float height = 0.05f;
    [Tooltip("Turn radius, measured to the sweep centerline.")]
    public float radius = 0.3f;
    [Tooltip("Total sweep angle in degrees (90 for a right-angle corner).")]
    public float sweepAngle = 90f;
    [Range(2, 64)]
    public int segments = 16;
    [Tooltip("Material to render the corner with - pick whichever of the straight segment's materials actually matches (the rail, not the belt rubber).")]
    public Material material;

    Mesh mesh;

    void OnValidate() => Rebuild();
    void OnEnable() => Rebuild();

    public void Rebuild()
    {
        if (width <= 0f || height <= 0f || radius <= 0f || segments < 2) return;

        if (mesh == null) mesh = new Mesh { name = "ConveyorCornerRounded" };
        BuildSweptMesh(mesh, width, height, radius, sweepAngle, segments);

        GetComponent<MeshFilter>().sharedMesh = mesh;
        if (material != null) GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    static void BuildSweptMesh(Mesh mesh, float width, float height, float radius, float sweepDeg, int segments)
    {
        int rings = segments + 1;
        float sweepRad = sweepDeg * Mathf.Deg2Rad;

        // Profile corners in (lateral, vertical) local space, cyclic order around the rectangle:
        // 0 = bottom/-lateral, 1 = bottom/+lateral, 2 = top/+lateral, 3 = top/-lateral.
        Vector2[] profile =
        {
            new Vector2(-width * 0.5f, 0f),
            new Vector2( width * 0.5f, 0f),
            new Vector2( width * 0.5f, height),
            new Vector2(-width * 0.5f, height),
        };

        // Arc pivot is offset +radius on X from the local origin, so the path starts at the
        // origin heading toward +Z and curves toward +X as the angle sweeps up.
        var pivot = new Vector3(radius, 0f, 0f);
        var pathPos = new Vector3[rings];
        var lateral = new Vector3[rings];
        var tangent = new Vector3[rings];
        for (int i = 0; i < rings; i++)
        {
            float a = sweepRad * i / segments;
            pathPos[i] = pivot + new Vector3(-radius * Mathf.Cos(a), 0f, radius * Mathf.Sin(a));
            lateral[i] = new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a));
            tangent[i] = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
        }

        var ringCorners = new Vector3[rings][];
        for (int i = 0; i < rings; i++)
        {
            ringCorners[i] = new Vector3[4];
            for (int c = 0; c < 4; c++)
                ringCorners[i][c] = pathPos[i] + lateral[i] * profile[c].x + Vector3.up * profile[c].y;
        }

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        // 4 side faces, one per edge of the profile rectangle. For edge (c0 -> c1), the winding
        // below always yields an outward-facing normal (verified algebraically: normal works out
        // to cross(profileEdgeVector, tangent), which lines up with down/up/+-lateral respectively
        // for each of the 4 edges given the profile's cyclic vertex order).
        int[,] edges = { { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 } };
        for (int e = 0; e < 4; e++)
        {
            int c0 = edges[e, 0], c1 = edges[e, 1];
            int baseIndex = verts.Count;
            for (int i = 0; i < rings; i++)
            {
                Vector3 edgeDir = (c0 == 0 && c1 == 1) ? lateral[i]
                                : (c0 == 1 && c1 == 2) ? Vector3.up
                                : (c0 == 2 && c1 == 3) ? -lateral[i]
                                : -Vector3.up;
                Vector3 normal = Vector3.Cross(edgeDir, tangent[i]).normalized;

                verts.Add(ringCorners[i][c0]); normals.Add(normal); uvs.Add(new Vector2((float)i / segments, 0f));
                verts.Add(ringCorners[i][c1]); normals.Add(normal); uvs.Add(new Vector2((float)i / segments, 1f));
            }
            for (int i = 0; i < segments; i++)
            {
                int a0 = baseIndex + i * 2, a1 = a0 + 1;
                int b0 = baseIndex + (i + 1) * 2, b1 = b0 + 1;
                tris.Add(a0); tris.Add(a1); tris.Add(b1);
                tris.Add(a0); tris.Add(b1); tris.Add(b0);
            }
        }

        AddCap(ringCorners[0], -tangent[0], verts, normals, uvs, tris, flip: true);
        AddCap(ringCorners[rings - 1], tangent[rings - 1], verts, normals, uvs, tris, flip: false);

        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
    }

    static void AddCap(Vector3[] corners, Vector3 normal, List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris, bool flip)
    {
        int baseIndex = verts.Count;
        Vector2[] capUvs = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        for (int c = 0; c < 4; c++)
        {
            verts.Add(corners[c]);
            normals.Add(normal);
            uvs.Add(capUvs[c]);
        }
        if (!flip)
        {
            tris.Add(baseIndex); tris.Add(baseIndex + 1); tris.Add(baseIndex + 2);
            tris.Add(baseIndex); tris.Add(baseIndex + 2); tris.Add(baseIndex + 3);
        }
        else
        {
            tris.Add(baseIndex); tris.Add(baseIndex + 2); tris.Add(baseIndex + 1);
            tris.Add(baseIndex); tris.Add(baseIndex + 3); tris.Add(baseIndex + 2);
        }
    }
}
