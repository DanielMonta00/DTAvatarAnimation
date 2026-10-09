using UnityEngine;

// The corner panels of one model on ONE display: its status card (FvpHud, top-left) and its heatmap tiles (FvpHeatPanel, top-right),
// on a screen-space canvas of their own. FasterVoxelPose keeps its panels in the canvas of its transport bar; every other model
// uses one of these per display, and the hub decides which model's are visible.
public sealed class FvpModelPanels
{
    public readonly Canvas canvas;
    public readonly int display;
    public readonly FvpHud hud;
    public readonly FvpHeatPanel heat;
    readonly GameObject root;

    public FvpModelPanels(string model, int display, int sortingOrder)
    {
        this.display = display;
        root = new GameObject($"{model} panels display {display + 1}") { hideFlags = HideFlags.HideAndDontSave };
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        canvas.targetDisplay = display;
        canvas.pixelPerfect = true;
        hud = new FvpHud(root.transform);
        heat = new FvpHeatPanel(root.transform);
    }

    public void Destroy()
    {
        if (root == null) return;
        if (Application.isPlaying) Object.Destroy(root); else Object.DestroyImmediate(root);
    }
}
