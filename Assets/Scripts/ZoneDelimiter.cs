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
    [Tooltip("Color of the floor outline and the upward light strips.")]
    public Color zoneColor = new Color(0.2f, 1f, 0.4f, 0.85f);
    [Tooltip("Height of the illusory walls drawn upward from the floor rectangle.")]
    public float wallHeight = 2.0f;
    [Tooltip("Number of vertical light strips drawn along each edge (in addition to the corner pillars).")]
    public int lightStripsPerEdge = 6;
    [Tooltip("Number of fade segments per vertical strip — higher = smoother fade.")]
    public int stripFadeSegments = 14;
    [Tooltip("Falloff exponent for the upward alpha fade (1 = linear, 2 = quadratic).")]
    public float fadePower = 2.0f;

    [Header("Physics walls")]
    [Tooltip("At runtime, spawn 4 invisible trigger BoxColliders along the edges so a RandomWalker's spherecast obstacle avoidance treats the zone as walls. Triggers — they don't block other physics objects.")]
    public bool spawnInvisibleWalls = true;
    [Tooltip("Thickness of the spawned wall colliders.")]
    public float wallThickness = 0.1f;
    [Tooltip("Unity layer index (0-31) for the spawned wall colliders. Setup for 'only selected avatars are confined':\n" +
             "  1. Project Settings → Tags and Layers → name an unused layer e.g. 'ZoneWall' (say index 8).\n" +
             "  2. Set this field to that index (8).\n" +
             "  3. On each RandomWalker that SHOULD be confined, set obstacleLayers to include that layer.\n" +
             "  4. Walkers that shouldn't be confined leave it out of their mask and walk through freely.\n" +
             "Other physics objects (camera, props) are never affected because the walls are triggers.")]
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
        bc.isTrigger = true;   // RandomWalker's obstacle probe uses QueryTriggerInteraction.Collide,
                               // so triggers are detected without blocking other physics objects.
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

        Vector3 c1 = new Vector3(minX, y, minZ);
        Vector3 c2 = new Vector3(maxX, y, minZ);
        Vector3 c3 = new Vector3(maxX, y, maxZ);
        Vector3 c4 = new Vector3(minX, y, maxZ);

        // Floor rectangle — bright.
        Gizmos.color = baseCol;
        Gizmos.DrawLine(c1, c2);
        Gizmos.DrawLine(c2, c3);
        Gizmos.DrawLine(c3, c4);
        Gizmos.DrawLine(c4, c1);

        // Top rectangle — dim, hints at the ceiling of the illusory wall.
        Color topCol = baseCol; topCol.a *= 0.25f;
        Gizmos.color = topCol;
        Vector3 t1 = c1 + Vector3.up * wallHeight;
        Vector3 t2 = c2 + Vector3.up * wallHeight;
        Vector3 t3 = c3 + Vector3.up * wallHeight;
        Vector3 t4 = c4 + Vector3.up * wallHeight;
        Gizmos.DrawLine(t1, t2);
        Gizmos.DrawLine(t2, t3);
        Gizmos.DrawLine(t3, t4);
        Gizmos.DrawLine(t4, t1);

        // Vertical light strips along each edge, plus thicker corner pillars.
        int strips = Mathf.Max(2, lightStripsPerEdge);
        for (int i = 0; i <= strips; i++)
        {
            float t = (float)i / strips;
            float xL = Mathf.Lerp(minX, maxX, t);
            float zL = Mathf.Lerp(minZ, maxZ, t);
            DrawFadingVertical(new Vector3(xL, y, minZ), wallHeight, baseCol);
            DrawFadingVertical(new Vector3(xL, y, maxZ), wallHeight, baseCol);
            DrawFadingVertical(new Vector3(minX, y, zL), wallHeight, baseCol);
            DrawFadingVertical(new Vector3(maxX, y, zL), wallHeight, baseCol);
        }
        DrawFadingVertical(c1, wallHeight, baseCol);
        DrawFadingVertical(c2, wallHeight, baseCol);
        DrawFadingVertical(c3, wallHeight, baseCol);
        DrawFadingVertical(c4, wallHeight, baseCol);
    }

    void DrawFadingVertical(Vector3 bottom, float height, Color baseColor)
    {
        int segs = Mathf.Max(2, stripFadeSegments);
        for (int s = 0; s < segs; s++)
        {
            float t0 = (float)s / segs;
            float t1 = (float)(s + 1) / segs;
            float alpha = baseColor.a * Mathf.Pow(1f - t0, fadePower);
            Color col = baseColor; col.a = alpha;
            Gizmos.color = col;
            Gizmos.DrawLine(
                bottom + Vector3.up * (t0 * height),
                bottom + Vector3.up * (t1 * height));
        }
    }
}
