using UnityEngine;

[ExecuteInEditMode]
public class DrawTransform : MonoBehaviour
{
    [Header("Axis Settings")]
    public bool drawInGameMode = true;
    public bool drawInEditMode = true;
    
    [Header("Axis Lengths")]
    public float axisLength = 1.0f;
    public float arrowSize = 0.2f;
    
    [Header("Colors")]
    public Color xAxisColor = Color.red;
    public Color yAxisColor = Color.green;
    public Color zAxisColor = Color.blue;
    
    [Header("Display Options")]
    public bool drawLabels = true;
    public float labelDistance = 1.1f;
    public GUIStyle labelStyle;
    
    private void OnDrawGizmos()
    {
        // Check if we should draw based on current mode
        bool shouldDraw = Application.isPlaying ? drawInGameMode : drawInEditMode;
        if (!shouldDraw) return;
        
        // Draw axes in the global (world) reference frame at the object's position
        Vector3 origin = transform.position;

        // Draw X axis (Red, world right)
        DrawAxis(origin, Vector3.right, xAxisColor, "X");

        // Draw Y axis (Green, world up)
        DrawAxis(origin, Vector3.up, yAxisColor, "Y");

        // Draw Z axis (Blue, world forward)
        DrawAxis(origin, Vector3.forward, zAxisColor, "Z");
    }
    
   private void DrawAxis(Vector3 origin, Vector3 direction, Color color, string label)
{
    Gizmos.color = color;

    Vector3 endPoint = origin + direction * axisLength;
    Gizmos.DrawLine(origin, endPoint);

    if (arrowSize > 0f)
    {
        // Use a safe method for perpendicular vectors
        Vector3 perp1 = Vector3.Cross(direction, Vector3.up);
        if (perp1.sqrMagnitude < 0.0001f)
            perp1 = Vector3.Cross(direction, Vector3.right);

        perp1.Normalize();
        Vector3 perp2 = Vector3.Cross(direction, perp1).normalized;

        Vector3 tip = endPoint;
        Vector3 basePoint = endPoint - direction * arrowSize;

        Gizmos.DrawLine(tip, basePoint + perp1 * arrowSize * 0.5f);
        Gizmos.DrawLine(tip, basePoint - perp1 * arrowSize * 0.5f);
        Gizmos.DrawLine(tip, basePoint + perp2 * arrowSize * 0.5f);
        Gizmos.DrawLine(tip, basePoint - perp2 * arrowSize * 0.5f);
    }

#if UNITY_EDITOR
    if (drawLabels && labelStyle != null)
    {
        Vector3 labelPos = origin + direction * axisLength * labelDistance;
        UnityEditor.Handles.Label(labelPos, label, labelStyle);
    }
#endif
}
    
    // Optional: Draw the axes using GL lines for better visibility in Game Mode
    private void OnRenderObject()
    {
        // This method provides more persistent drawing in Game Mode
        if (!drawInGameMode || !Application.isPlaying) return;
        
        // You would need to set up GL lines here for more advanced rendering
        // This requires additional setup with materials and shaders
    }
}