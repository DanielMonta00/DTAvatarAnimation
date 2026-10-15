using System.Globalization;
using System.Text;
using UnityEngine;

// The people side of FasterVoxelPoseLite for the eyes: the skeletons the model found and a status line, drawn OVER each camera's display
// (FvpDisplayLayer: a screen overlay on the display, positioned like FasterVoxelPose's), never into the images the model reads. The mesh
// is rebuilt only when an estimate arrives or something moves, so this is nothing next to the estimate itself (measured in the README).
public sealed partial class FasterVoxelPoseLite
{
    const float MaxLagCompensation = 1.0f;     // the estimate is brought forward by what the person moved since it was captured, but only this far
    const float FadeAfterSeconds = 1.0f;       // an estimate older than this is drawn fainter, and gone after twice that

    FvpDisplayLayer display;
    float nextDisplaySync, nextStatusText;
    string statusText = "";

    static readonly Vector2[] uvBuf = new Vector2[FvpSkeleton.Count];
    static readonly bool[] okBuf = new bool[FvpSkeleton.Count];
    static Color[] estColors;

    // For tests: the overlay drawn on camera `index`'s display (null before the first frame, or when it is not drawn).
    public FvpOverlay DisplayOverlayOf(int index) => display != null && index >= 0 && index < display.Count ? display.Views[index].overlay : null;

    void LateUpdate()
    {
        if (!Application.isPlaying) return;
        long start = Stamp();
        UpdateDisplay();
        frameTicks += Stamp() - start;
    }

    void UpdateDisplay()
    {
        if ((!drawSkeletons && !drawStatus) || cameras == null || cameras.Count == 0)
        {
            display?.SetVisible(false);
            return;
        }
        if (display == null) display = new FvpDisplayLayer("FVP lite overlay", 170);
        if (Time.unscaledTime >= nextDisplaySync)
        {
            nextDisplaySync = Time.unscaledTime + 0.5f;
            display.Sync(cameras, null);
        }

        FvpLiteEstimate e = latest;
        double age = e != null ? Time.timeAsDouble - e.captureTime : double.MaxValue;
        bool drawPeople = drawSkeletons && IsReady && e != null && age < 2.0 * FadeAfterSeconds;
        float alpha = age <= FadeAfterSeconds ? 1f : Mathf.Lerp(1f, 0.25f, (float)((age - FadeAfterSeconds) / FadeAfterSeconds));
        float lag = e != null ? Mathf.Clamp((float)age, 0f, MaxLagCompensation) : 0f;

        if (drawStatus && Time.unscaledTime >= nextStatusText)
        {
            nextStatusText = Time.unscaledTime + 0.25f;
            statusText = BuildStatusLine();
        }
        var statusColor = new Color(1f, 0.85f, 0.45f, 1f);

        display.SetVisible(true);
        foreach (FvpDisplayLayer.View v in display.Views)
        {
            if (v.cam == null) continue;                    // destroyed since the last Sync
            v.overlay.SetDisplay(v.cam.targetDisplay);
            FvpOverlayGraphic g = v.overlay.graphic;
            g.lineWidth = skeletonLineWidth;
            g.dotRadius = skeletonDotRadius;

            g.Begin();
            if (drawPeople)
            {
                for (int i = 0; i < e.people.Count; i++) DrawSkeleton(v, e.people[i], lag, alpha);
                if (drawPhantoms)
                    for (int i = 0; i < e.phantoms.Count; i++) DrawSkeleton(v, e.phantoms[i], lag, alpha * 0.35f);
            }
            g.alpha = 1f;
            g.End();

            v.SetStatus(drawStatus ? statusText : "", statusColor);
        }
    }

    void DrawSkeleton(FvpDisplayLayer.View v, FvpPerson p, float lag, float alpha)
    {
        if (estColors == null)
        {
            estColors = new Color[FvpSkeleton.Count];
            for (int j = 0; j < FvpSkeleton.Count; j++) estColors[j] = FvpSkeleton.EstimateColor(j);
        }
        FvpOverlayGraphic g = v.overlay.graphic;
        g.alpha = alpha;
        Vector3 shift = p.velocity * lag;
        for (int j = 0; j < FvpSkeleton.Count; j++)
        {
            okBuf[j] = p.IsValid(j) && FvpDisplayLayer.TryProject(v, p.joints[j] + shift, out uvBuf[j])
                       && uvBuf[j].x > -0.5f && uvBuf[j].x < 1.5f && uvBuf[j].y > -0.5f && uvBuf[j].y < 1.5f;
        }
        for (int k = 0; k < FvpSkeleton.Edges.GetLength(0); k++)
        {
            int a = FvpSkeleton.Edges[k, 0], b = FvpSkeleton.Edges[k, 1];
            if (okBuf[a] && okBuf[b]) g.AddLine(uvBuf[a], uvBuf[b], Color.Lerp(estColors[a], estColors[b], 0.5f));
        }
        for (int j = 0; j < FvpSkeleton.Count; j++)
            if (okBuf[j]) g.AddDot(uvBuf[j], estColors[j]);
    }

    // The Scene view (and the Game view with Gizmos on): the estimate as 3D lines and joints, orange like FasterVoxelPose's, so that it can be
    // compared with the green ground-truth skeleton the recorder / FasterVoxelPose gizmos draw from the avatar's own joints.
    void OnDrawGizmos()
    {
        FvpLiteEstimate e = latest;
        if (!drawSceneGizmos || !Application.isPlaying || e == null || !isActiveAndEnabled) return;
        double age = Time.timeAsDouble - e.captureTime;
        if (age > 2.0 * FadeAfterSeconds) return;
        float lag = Mathf.Clamp((float)age, 0f, MaxLagCompensation);
        float alpha = age <= FadeAfterSeconds ? 1f : 0.35f;
        for (int i = 0; i < e.people.Count; i++) DrawGizmoSkeleton(e.people[i], lag, alpha);
        if (drawPhantoms)
            for (int i = 0; i < e.phantoms.Count; i++) DrawGizmoSkeleton(e.phantoms[i], lag, alpha * 0.35f);
    }

    void DrawGizmoSkeleton(FvpPerson p, float lag, float alpha)
    {
        Vector3 shift = p.velocity * lag;
        for (int k = 0; k < FvpSkeleton.Edges.GetLength(0); k++)
        {
            int a = FvpSkeleton.Edges[k, 0], b = FvpSkeleton.Edges[k, 1];
            if (!p.IsValid(a) || !p.IsValid(b)) continue;
            Color c = Color.Lerp(FvpSkeleton.EstimateColor(a), FvpSkeleton.EstimateColor(b), 0.5f);
            c.a *= alpha;
            Gizmos.color = c;
            Gizmos.DrawLine(p.joints[a] + shift, p.joints[b] + shift);
        }
        for (int j = 0; j < FvpSkeleton.Count; j++)
        {
            if (!p.IsValid(j)) continue;
            Color c = FvpSkeleton.EstimateColor(j);
            c.a *= alpha;
            Gizmos.color = c;
            Gizmos.DrawSphere(p.joints[j] + shift, j == FvpSkeleton.MidHip ? 0.055f : 0.035f);
        }
    }

    string BuildStatusLine()
    {
        if (!IsReady) return "FasterVoxelPoseLite: " + status;
        var sb = new StringBuilder(128);
        sb.Append("FasterVoxelPoseLite  ").Append(estimateFps.ToString("F1", CultureInfo.InvariantCulture)).Append("/s");
        FvpLiteEstimate e = latest;
        if (e != null)
            sb.Append("  ").Append(e.LatencyMs.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms (server ")
              .Append(e.totalMs.ToString("F0", CultureInfo.InvariantCulture)).Append(")  ").Append(e.people.Count).Append(e.people.Count == 1 ? " person" : " people");
        if (FrameRateCap > 0) sb.Append("  Unity held to ").Append(FrameRateCap).Append(" fps");
        return sb.ToString();
    }

    void DestroyDisplay()
    {
        display?.Destroy();
        display = null;
    }
}
