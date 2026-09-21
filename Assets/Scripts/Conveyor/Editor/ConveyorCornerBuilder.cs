using UnityEditor;
using UnityEngine;

/// <summary>
/// The FESTO conveyor kit (00_FESTO_EVO_06.fbx) only ships straight rail/table segments -
/// no elbow or corner piece exists anywhere in that model. This samples the selected straight
/// segment's real cross-section (width/height) and material, then adds a ConveyorCornerPiece -
/// a procedurally swept rounded 90 degree corner matching that profile - so it looks like a real
/// curved turn instead of a placeholder box or a mitered overlap.
///
/// Menu: GameObject > Conveyor > Add Rounded Corner Piece
/// </summary>
public static class ConveyorCornerBuilder
{
    [MenuItem("GameObject/Conveyor/Add Rounded Corner Piece", false, 10)]
    public static void CreateRoundedCorner(MenuCommand cmd)
    {
        GameObject reference = Selection.activeGameObject;
        if (reference == null)
        {
            Debug.LogError("Conveyor: select the straight conveyor segment you want to turn a corner with (e.g. ConveyorTable, Conveyor3) first.");
            return;
        }

        var renderers = reference.GetComponentsInChildren<MeshRenderer>();
        if (renderers.Length == 0)
        {
            Debug.LogError($"Conveyor: '{reference.name}' has no MeshRenderers to sample size/material from.");
            return;
        }

        // World-space AABB across every renderer in the selection. Every conveyor object in this
        // scene is only ever rotated in multiples of 90 degrees about Y, so the world AABB's axes
        // line up with the segment's real width/height/length - no rotated-bounds distortion.
        Bounds bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);

        float height = bounds.size.y;
        float width = Mathf.Min(bounds.size.x, bounds.size.z); // cross-section width; the long axis is the travel direction, excluded

        var go = new GameObject(reference.name + " Corner");
        GameObjectUtility.SetParentAndAlign(go, reference.transform.parent != null ? reference.transform.parent.gameObject : null);
        go.transform.position = reference.transform.position;
        Undo.RegisterCreatedObjectUndo(go, "Add Conveyor Corner Piece");

        var piece = go.AddComponent<ConveyorCornerPiece>();
        piece.width = width;
        piece.height = height;
        piece.radius = Mathf.Max(width * 3f, 0.1f);
        piece.material = PickMetalMaterial(renderers);
        piece.Rebuild();

        Selection.activeGameObject = go;
        Debug.Log($"Conveyor: created '{go.name}' (width {width:F3}, height {height:F3}, material '{piece.material?.name}', sampled from '{reference.name}'). " +
                   "Select it and tune Radius/Segments/Material in the Inspector, then position/rotate it so the start (local +Z) " +
                   "lines up with one straight segment and the end (local +X after the sweep) lines up with the other.");
    }

    // A straight segment carries several submaterials (rail metal, belt rubber, screw plastic,
    // paint...) across its renderers; picking sharedMaterials[0] on whichever renderer happens to
    // come first is a coin flip. The rail itself is what a corner should visually continue, and
    // every submaterial in this FESTO kit that renders as bare metal has "metal" or "alu" in its
    // name, so prefer that over an arbitrary first match.
    static Material PickMetalMaterial(MeshRenderer[] renderers)
    {
        Material firstFound = null;
        foreach (var r in renderers)
        {
            foreach (var mat in r.sharedMaterials)
            {
                if (mat == null) continue;
                if (firstFound == null) firstFound = mat;
                string n = mat.name.ToLowerInvariant();
                if (n.Contains("metal") || n.Contains("alu")) return mat;
            }
        }
        return firstFound;
    }
}
