using UnityEngine;

// Defines a rectangular movement zone on the XZ plane via two child Transforms
// named (by default) "FirstCorner" and "SecondCorner". Draws a green gizmo
// floor rectangle plus vertical fading "light" strips that hint at walls. Can
// optionally spawn invisible trigger BoxColliders along the four edges so a
// RandomWalker (or any spherecast-based avoider) treats the zone as walls.
[ExecuteAlways]
public class ZoneDelimiter : MonoBehaviour
{
    [Header("Corners (auto-resolved from children when null)")]
    public Transform firstCorner;
    public Transform secondCorner;
    [Tooltip("Re-find FirstCorner / SecondCorner children whenever the inspector changes.")]
    public bool autoResolveCornersOnValidate = true;

    [Header("Visualization")]
    [Tooltip("Wall color. The alpha at the floor; fades to 0 at the top.")]
    public Color zoneColor = new Color(0.2f, 1f, 0.4f, 0.4f);
    [Tooltip("Height of the translucent walls drawn upward from the floor rectangle.")]
    public float wallHeight = 2.0f;
    [Tooltip("Number of horizontal slices stacked to fake the vertical alpha fade. Higher = smoother gradient, more gizmo draws (each slice is a translucent cube).")]
    public int wallFadeSteps = 32;
    [Tooltip("Visual thickness of each translucent wall (purely cosmetic — doesn't affect physics).")]
    public float wallVisualThickness = 0.05f;
    [Tooltip("Falloff exponent for the upward alpha fade (1 = linear, 2 = quadratic, 3 = cubic).")]
    public float fadePower = 2.0f;

    [Header("Physics walls")]
    [Tooltip("At runtime, spawn 4 invisible BoxColliders along the edges. Solid (non-trigger) so the avatar's CharacterController physically cannot pass through.")]
    public bool spawnInvisibleWalls = true;
    [Tooltip("Thickness of the spawned wall colliders. Keep this larger than the avatar's worst-case per-frame movement (forwardSpeed * dt + backupDistance) — sub-thin walls can be tunneled through in a single frame.")]
    public float wallThickness = 0.4f;
    [Tooltip("Solid wall (non-trigger) blocks CharacterControllers / Rigidbodies. Disable to fall back to a trigger which only smart spherecast-based avoiders 'see' — but those can be bypassed (see SphereCast: 'colliders the sphere already overlaps are not detected'), so triggers do not give guaranteed containment.")]
    public bool wallsArePhysicalBarriers = true;
    [Tooltip("Unity layer index (0-31) for the spawned wall colliders. Setup for 'only selected avatars are confined':\n" +
             "  1. Project Settings → Tags and Layers → name an unused layer e.g. 'ZoneWall' (say index 8). Optionally name another, e.g. 'ConfinedAvatar', and put the avatar(s) you want confined on it.\n" +
             "  2. Set this field to the wall layer's index.\n" +
             "  3. Project Settings → Physics → Layer Collision Matrix: tick ZoneWall × ConfinedAvatar (so they collide), untick ZoneWall × every other layer (so nothing else does).\n" +
             "  4. Optional: also include the wall layer in each confined RandomWalker's obstacleLayers so the avatar steers away before bumping the wall.")]
    [Range(0, 31)] public int wallLayer = 0;

    GameObject wallsRoot;

    void Awake()
    {
        if (firstCorner == null) firstCorner = transform.Find("FirstCorner");
        if (secondCorner == null) secondCorner = transform.Find("SecondCorner");
    }

    void OnEnable()
    {
        if (Application.isPlaying && spawnInvisibleWalls) RebuildWalls();
    }

    void OnDisable()
    {
        if (Application.isPlaying) DestroyWalls();
    }

    void OnValidate()
    {
        if (autoResolveCornersOnValidate)
        {
            if (firstCorner == null) firstCorner = transform.Find("FirstCorner");
            if (secondCorner == null) secondCorner = transform.Find("SecondCorner");
        }
    }

    [ContextMenu("Refresh corners from children")]
    public void RefreshCorners()
    {
        firstCorner = transform.Find("FirstCorner");
        secondCorner = transform.Find("SecondCorner");
    }

    [ContextMenu("Rebuild physics walls now")]
    public void RebuildWalls()
    {
        DestroyWalls();
        if (!TryGetBounds2D(out float minX, out float maxX, out float minZ, out float maxZ, out float yLevel)) return;

        wallsRoot = new GameObject("_ZoneDelimiterWalls");
        wallsRoot.transform.SetParent(transform, worldPositionStays: false);
        wallsRoot.transform.position = Vector3.zero;
        wallsRoot.hideFlags = HideFlags.DontSave;

        float midX = (minX + maxX) * 0.5f;
        float midZ = (minZ + maxZ) * 0.5f;
        float lenX = maxX - minX;
        float lenZ = maxZ - minZ;
        float t = Mathf.Max(0.01f, wallThickness);
        float h = Mathf.Max(0.5f, wallHeight);

        SpawnWall("Wall_minZ", new Vector3(midX, yLevel + h * 0.5f, minZ), new Vector3(lenX + t, h, t));
        SpawnWall("Wall_maxZ", new Vector3(midX, yLevel + h * 0.5f, maxZ), new Vector3(lenX + t, h, t));
        SpawnWall("Wall_minX", new Vector3(minX, yLevel + h * 0.5f, midZ), new Vector3(t, h, lenZ + t));
        SpawnWall("Wall_maxX", new Vector3(maxX, yLevel + h * 0.5f, midZ), new Vector3(t, h, lenZ + t));
    }

    void SpawnWall(string n, Vector3 worldCenter, Vector3 worldSize)
    {
        var go = new GameObject(n);
        go.transform.SetParent(wallsRoot.transform, worldPositionStays: false);
        go.transform.position = worldCenter;
        go.layer = Mathf.Clamp(wallLayer, 0, 31);
        var bc = go.AddComponent<BoxCollider>();
        bc.size = worldSize;
        // Solid by default: CharacterControllers and Rigidbodies cannot tunnel through.
        // Set wallsArePhysicalBarriers=false to fall back to a trigger (informational only,
        // not containment-safe — spherecasts miss colliders they already overlap).
        bc.isTrigger = !wallsArePhysicalBarriers;
    }

    void DestroyWalls()
    {
        if (wallsRoot == null) return;
        if (Application.isPlaying) Destroy(wallsRoot);
        else                       DestroyImmediate(wallsRoot);
        wallsRoot = null;
    }

    // ---------------- Public API ----------------

    public bool TryGetBounds2D(out float minX, out float maxX, out float minZ, out float maxZ, out float yLevel)
    {
        minX = maxX = minZ = maxZ = yLevel = 0f;
        var c1 = firstCorner != null ? firstCorner : transform.Find("FirstCorner");
        var c2 = secondCorner != null ? secondCorner : transform.Find("SecondCorner");
        if (c1 == null || c2 == null) return false;
        Vector3 a = c1.position, b = c2.position;
        minX = Mathf.Min(a.x, b.x);
        maxX = Mathf.Max(a.x, b.x);
        minZ = Mathf.Min(a.z, b.z);
        maxZ = Mathf.Max(a.z, b.z);
        yLevel = (a.y + b.y) * 0.5f;
        return true;
    }

    public Vector3 Center
    {
        get
        {
            if (!TryGetBounds2D(out var minX, out var maxX, out var minZ, out var maxZ, out var y)) return transform.position;
            return new Vector3((minX + maxX) * 0.5f, y, (minZ + maxZ) * 0.5f);
        }
    }

    // X = width along world X, Y = wallHeight, Z = depth along world Z. Useful as a drop-in for areaSize.
    public Vector3 Size
    {
        get
        {
            if (!TryGetBounds2D(out var minX, out var maxX, out var minZ, out var maxZ, out _)) return Vector3.zero;
            return new Vector3(maxX - minX, wallHeight, maxZ - minZ);
        }
    }

    public bool ContainsXZ(Vector3 worldPos)
    {
        if (!TryGetBounds2D(out var minX, out var maxX, out var minZ, out var maxZ, out _)) return false;
        return worldPos.x >= minX && worldPos.x <= maxX
            && worldPos.z >= minZ && worldPos.z <= maxZ;
    }

    public Vector3 ClampXZ(Vector3 worldPos)
    {
        if (!TryGetBounds2D(out var minX, out var maxX, out var minZ, out var maxZ, out _)) return worldPos;
        return new Vector3(
            Mathf.Clamp(worldPos.x, minX, maxX),
            worldPos.y,
            Mathf.Clamp(worldPos.z, minZ, maxZ));
    }

    // ---------------- Gizmos ----------------

    void OnDrawGizmos()
    {
        if (!TryGetBounds2D(out float minX, out float maxX, out float minZ, out float maxZ, out float y)) return;

        Color baseCol = zoneColor;
        baseCol.a = Mathf.Clamp01(zoneColor.a);

        float vt = Mathf.Max(0.001f, wallVisualThickness);
        float lenX = maxX - minX;
        float lenZ = maxZ - minZ;

        // Each wall: an axis-aligned slab (length × wallHeight × visualThickness)
        // along one edge of the rectangle, sliced horizontally for the fade.
        DrawFadingWall(
            center: new Vector3((minX + maxX) * 0.5f, y, minZ),
            size:   new Vector3(lenX, 0f, vt),
            height: wallHeight, baseColor: baseCol);
        DrawFadingWall(
            center: new Vector3((minX + maxX) * 0.5f, y, maxZ),
            size:   new Vector3(lenX, 0f, vt),
            height: wallHeight, baseColor: baseCol);
        DrawFadingWall(
            center: new Vector3(minX, y, (minZ + maxZ) * 0.5f),
            size:   new Vector3(vt, 0f, lenZ),
            height: wallHeight, baseColor: baseCol);
        DrawFadingWall(
            center: new Vector3(maxX, y, (minZ + maxZ) * 0.5f),
            size:   new Vector3(vt, 0f, lenZ),
            height: wallHeight, baseColor: baseCol);
    }

    // Stack of thin translucent cubes along the wall's length, decreasing
    // alpha from the floor up to fake a vertical fade. `size.y` is replaced
    // by the per-slice height; `center.y` is the bottom of the wall.
    void DrawFadingWall(Vector3 center, Vector3 size, float height, Color baseColor)
    {
        int slices = Mathf.Max(2, wallFadeSteps);
        float sliceH = height / slices;
        Vector3 sliceSize = new Vector3(size.x, sliceH, size.z);
        for (int i = 0; i < slices; i++)
        {
            float t = i / (float)(slices - 1);
            float alpha = baseColor.a * Mathf.Pow(1f - t, fadePower);
            Color col = baseColor; col.a = alpha;
            Gizmos.color = col;
            Vector3 sliceCenter = new Vector3(
                center.x,
                center.y + sliceH * 0.5f + t * (height - sliceH),
                center.z);
            Gizmos.DrawCube(sliceCenter, sliceSize);
        }
    }
}
