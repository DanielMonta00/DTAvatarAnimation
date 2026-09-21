using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds a complete, working 4-cable robot out of Unity primitives so the kinematics can be
/// driven immediately, before any real meshes exist. Every part is a plain GameObject: swap the
/// MeshFilter/MeshRenderer for imported geometry later and nothing in CableRobot changes, as it
/// only ever reads Transform positions.
///
/// Menu: GameObject > CDPR > Create 4-Cable Robot Rig
/// </summary>
public static class CableRobotRigBuilder
{
    const float RoomWidth = 4f;   // along X
    const float RoomDepth = 3f;   // along Z
    const float CeilingY  = 2.8f;

    const float PlateSize      = 0.4f;
    const float PlateThickness = 0.04f;

    [MenuItem("GameObject/CDPR/Create 4-Cable Robot Rig", false, 10)]
    public static void CreateRig(MenuCommand cmd)
    {
        var root = new GameObject("CDPR_4Cable");
        GameObjectUtility.SetParentAndAlign(root, cmd.context as GameObject);
        Undo.RegisterCreatedObjectUndo(root, "Create 4-Cable Robot Rig");

        Material woodMat   = GetOrCreateMaterial("CDPR_Wood",   new Color(0.62f, 0.44f, 0.24f));
        Material metalMat  = GetOrCreateMaterial("CDPR_Metal",  new Color(0.55f, 0.57f, 0.60f));
        Material targetMat = GetOrCreateMaterial("CDPR_Target", new Color(1f, 0.75f, 0.1f));

        // Anchors at the four ceiling corners, wound counter-clockwise so the gizmo draws a
        // rectangle rather than a bow-tie. Order here is cable index 0..3.
        float hx = RoomWidth * 0.5f, hz = RoomDepth * 0.5f;
        Vector3[] anchorPositions =
        {
            new Vector3(-hx, CeilingY, -hz),
            new Vector3( hx, CeilingY, -hz),
            new Vector3( hx, CeilingY,  hz),
            new Vector3(-hx, CeilingY,  hz),
        };

        var anchorsRoot = NewChild(root.transform, "Anchors", Vector3.zero);
        var winchesRoot = NewChild(root.transform, "Winches", Vector3.zero);

        var anchors = new Transform[4];
        var drums = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            // The anchor is the cable exit point (the pulley), which is what the maths uses.
            var pulley = Primitive(PrimitiveType.Sphere, anchorsRoot.transform, $"Anchor{i}", anchorPositions[i],
                                   Vector3.one * 0.06f, metalMat);
            anchors[i] = pulley.transform;

            // The winch drum sits just above the pulley. Purely cosmetic: it spins to show payout.
            var drum = Primitive(PrimitiveType.Cylinder, winchesRoot.transform, $"Drum{i}",
                                 anchorPositions[i] + Vector3.up * 0.12f,
                                 new Vector3(0.09f, 0.05f, 0.09f), metalMat);
            drums[i] = drum.transform;
        }

        // The platform root is an unscaled empty. The wooden square is a child that carries the
        // non-uniform scale, so the actuator and attachment points hanging off the root are not
        // squashed by it. CableRobot.platform points at this root.
        var platform = NewChild(root.transform, "Platform", new Vector3(0f, 1.2f, 0f));
        Primitive(PrimitiveType.Cube, platform.transform, "Plate", Vector3.zero,
                  new Vector3(PlateSize, PlateThickness, PlateSize), woodMat);
        Primitive(PrimitiveType.Cylinder, platform.transform, "Actuator",
                  new Vector3(0f, PlateThickness * 0.5f + 0.05f, 0f),
                  new Vector3(0.08f, 0.05f, 0.08f), metalMat);

        // Cable attachment points at the plate corners. Assigning these selects the level-plate
        // approximation. Clear the array on the component and the four cables converge on the
        // platform origin instead, which is the exact single-point model.
        float ha = PlateSize * 0.5f - 0.02f;
        float top = PlateThickness * 0.5f;
        Vector3[] corners =
        {
            new Vector3(-ha, top, -ha),
            new Vector3( ha, top, -ha),
            new Vector3( ha, top,  ha),
            new Vector3(-ha, top,  ha),
        };
        var attach = new Transform[4];
        for (int i = 0; i < 4; i++)
            attach[i] = NewChild(platform.transform, $"Attach{i}", corners[i]).transform;

        var target = Primitive(PrimitiveType.Sphere, root.transform, "Target",
                               new Vector3(0.8f, 1.0f, 0.5f), Vector3.one * 0.08f, targetMat);

        var robot = root.AddComponent<CableRobot>();
        robot.anchors = anchors;
        robot.drums = drums;
        robot.platform = platform.transform;
        robot.cableAttachPoints = attach;
        robot.target = target.transform;
        robot.drawWorkspaceSlice = true;

        Selection.activeGameObject = root;
    }

    static GameObject NewChild(Transform parent, string name, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.localPosition = localPos;
        return go;
    }

    // The rig is a visualisation, not a physics body: the platform is driven kinematically by
    // CableRobot. Leaving colliders on would let a moving plate shove avatars around the scene.
    static GameObject Primitive(PrimitiveType type, Transform parent, string name, Vector3 localPos, Vector3 localScale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    // Materials must be assets, not instances: an in-memory Material created by an editor script
    // is discarded on domain reload and every renderer turns magenta.
    static Material GetOrCreateMaterial(string name, Color color)
    {
        // Assets live under Assets/CDPR/; this script lives under Assets/Scripts/CDPR/Editor/.
        const string root = "Assets/CDPR";
        const string dir = root + "/Materials";
        // CreateFolder fails silently when the parent does not exist, which would leave every
        // renderer magenta, so ensure the root is there first.
        if (!AssetDatabase.IsValidFolder(root)) AssetDatabase.CreateFolder("Assets", "CDPR");
        if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder(root, "Materials");

        string path = $"{dir}/{name}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");

        var mat = new Material(shader);
        mat.color = color;
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
