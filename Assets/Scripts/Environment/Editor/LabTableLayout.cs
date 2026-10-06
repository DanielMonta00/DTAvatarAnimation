#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Environment > Fit lab tables to measured layout
//
// Resizes and places the lab tables so that, together, they form the rectangle measured in
// the room sketch and the real camera frame: 3 tables end to end, 1.4 m across by 3.4 m
// along, 0.9 m high, with the rectangle 0.8 m from the top wall and 1.3 m from the right wall.
// (The real black table measures 1.42 x 3.38 m when its corners are back-projected through the
// cam107 calibration at 0.9 m height; the sketch's 3.4 m is its length.)
//
// The sketch is drawn in the calibration world (X right, -Z up, right-handed). The scene
// uses Unity x = -X, so in Unity axes the sketch's "right wall" is the -x wall and its
// "top wall" the -z wall; the rectangle extends from those walls toward +x and +z.
// The wall faces below are taken from the invisible collider walls ("Wall (2)" for the top;
// "Wall (4)", "(5)" and "(1)" are three stacked copies of the right one, so their mean is used).
// Edit these constants if the real surfaces differ.
//
// The six LabBench_900mm benches under ConveyorTable/Table1..6 become three tables of two
// benches each (a 2 x 3 grid), every bench 0.7 x 1.13 m. Benches are scaled; the TableN group
// around each bench is moved with it, so the robot and chair parented there follow their bench
// unscaled. Safe to re-run, and undoable.
public static class LabTableLayout
{
    // ---- Measured plan (meters) ----
    const float RightWallInnerX = -5.50f; // inner face of the sketch's right wall (Unity -x side)
    const float TopWallInnerZ = -9.85f;   // inner face of the sketch's top wall (Unity -z side)
    const float GapToRightWall = 1.3f;
    const float GapToTopWall = 0.8f;
    const float AcrossX = 1.4f;           // rectangle width, Unity x
    const float AlongZ = 3.4f;            // rectangle length, Unity z
    const float TableHeight = 0.9f;
    const int Tables = 3;                 // end to end along z
    const int BenchesPerTable = 2;        // side by side along x

    const string BenchName = "LabBench_900mm";
    const string BenchRootName = "ConveyorTable";

    [MenuItem("Tools/Environment/Fit lab tables to measured layout")]
    public static void Fit()
    {
        var benches = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(t => t.name == BenchName && HasAncestorNamed(t, BenchRootName) && t.parent != null)
            .ToList();
        if (benches.Count != Tables * BenchesPerTable)
        {
            EditorUtility.DisplayDialog("Fit lab tables",
                $"Expected {Tables * BenchesPerTable} '{BenchName}' under '{BenchRootName}', found {benches.Count}.", "OK");
            return;
        }

        float x0 = RightWallInnerX + GapToRightWall; // rectangle min x (the side nearest the right wall)
        float z0 = TopWallInnerZ + GapToTopWall;     // rectangle min z (the side nearest the top wall)
        float benchWidth = AcrossX / BenchesPerTable;
        float benchLength = AlongZ / Tables;

        // Benches keep their rough order: rows by z (one row per table), then by x within a row.
        List<Transform> byZ = benches.OrderBy(b => WorldBounds(b).center.z).ThenBy(b => WorldBounds(b).center.x).ToList();
        var placed = new Bounds();
        bool first = true;
        for (int r = 0; r < Tables; r++)
        {
            List<Transform> row = byZ.Skip(r * BenchesPerTable).Take(BenchesPerTable)
                .OrderBy(b => WorldBounds(b).center.x).ToList();
            for (int c = 0; c < BenchesPerTable; c++)
            {
                var targetCenter = new Vector2(x0 + (c + 0.5f) * benchWidth, z0 + (r + 0.5f) * benchLength);
                Bounds b = FitBench(row[c], new Vector3(benchWidth, TableHeight, benchLength), targetCenter);
                if (first) { placed = b; first = false; } else placed.Encapsulate(b);
            }
        }

        EditorSceneManager.MarkSceneDirty(benches[0].gameObject.scene);
        Debug.Log($"[LabTableLayout] {Tables} tables ({benches.Count} benches) placed: x {placed.min.x:F2}..{placed.max.x:F2} " +
                  $"({placed.size.x:F2} across), z {placed.min.z:F2}..{placed.max.z:F2} ({placed.size.z:F2} along), " +
                  $"height {placed.size.y:F2}, top at y {placed.max.y:F2}. " +
                  $"Gaps: top wall {placed.min.z - TopWallInnerZ:F2}, right wall {placed.min.x - RightWallInnerX:F2}.");
    }

    // Scales one bench to `size` (world axes) and moves its TableN group so the bench is
    // centred on `centerXZ`, keeping its base height. Returns the bench's final world bounds.
    static Bounds FitBench(Transform bench, Vector3 size, Vector2 centerXZ)
    {
        Transform group = bench.parent;
        Undo.RecordObject(bench, "Fit lab table");
        Undo.RecordObject(group, "Fit lab table");

        bench.localScale = Vector3.one;
        Bounds b0 = WorldBounds(bench);
        if (b0.size.x > b0.size.z) // long side must run along z, like the other benches
        {
            bench.RotateAround(b0.center, Vector3.up, 90f);
            b0 = WorldBounds(bench);
        }

        // Required factor along each world axis, then handed to the bench's own axes
        // (they line up with world axes up to permutation: only 90 degree turns above it).
        var factor = new Vector3(size.x / b0.size.x, size.y / b0.size.y, size.z / b0.size.z);
        Vector3 scale = Vector3.one;
        for (int i = 0; i < 3; i++)
        {
            Vector3 axis = bench.TransformDirection(i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward);
            scale[i] = factor[DominantAxis(axis)];
        }
        bench.localScale = scale;

        Bounds b1 = WorldBounds(bench);
        Vector3 delta = new Vector3(centerXZ.x - b1.center.x, b0.min.y - b1.min.y, centerXZ.y - b1.center.z);
        group.position += delta;
        EditorUtility.SetDirty(bench);
        EditorUtility.SetDirty(group);
        return WorldBounds(bench);
    }

    static Bounds WorldBounds(Transform root)
    {
        Renderer[] rs = root.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return new Bounds(root.position, Vector3.zero);
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }

    static int DominantAxis(Vector3 v)
    {
        float x = Mathf.Abs(v.x), y = Mathf.Abs(v.y), z = Mathf.Abs(v.z);
        return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
    }

    static bool HasAncestorNamed(Transform t, string name)
    {
        for (Transform p = t.parent; p != null; p = p.parent)
            if (p.name == name) return true;
        return false;
    }
}
#endif
