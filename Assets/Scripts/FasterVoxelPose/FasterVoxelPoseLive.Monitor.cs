using System;
using UnityEngine;

// The monitor strip drawn over the Game view (IMGUI, so it works whatever the input backend and needs no canvas):
// one tile per camera showing the images the network was given, with the estimated skeletons projected back over
// them through the same camera model that was sent to the server, then the transport bar and a status line.
//
// Keys (Game view focused): Space pause / play, Left / Right step, R rewind, Home / End first / newest frame, M monitor.
//
// IMGUI only draws on the main display, i.e. the one the Game view shows when "Display 1" is selected.
public partial class FasterVoxelPoseLive
{
    FvpFrame texFrame;
    GUIStyle labelStyle, shadowStyle, statusStyle;
    static readonly Color Dim = new Color(0f, 0f, 0f, 0.62f);

    // ---------------- hotkeys ----------------

    void HandleHotkeys()
    {
        if (!enableHotkeys) return;
        Event e = Event.current;
        if (e.type != EventType.KeyDown) return;
        switch (e.keyCode)
        {
            case KeyCode.Space: TogglePause(); break;
            case KeyCode.RightArrow: StepForward(); break;
            case KeyCode.LeftArrow: StepBackward(); break;
            case KeyCode.R: ToggleRewind(); break;
            case KeyCode.Home: GoToStart(); break;
            case KeyCode.End: GoToNewest(); break;
            case KeyCode.M: showMonitor = !showMonitor; break;
            default: return;
        }
        e.Use();
    }

    // ---------------- drawing ----------------

    void OnGUI()
    {
        if (!Application.isPlaying || !isActiveAndEnabled) return;
        HandleHotkeys();

        if (!showMonitor)
        {
            if (GUI.Button(new Rect(6, 6, 46, 20), "FVP")) showMonitor = true;
            return;
        }
        if (labelStyle == null) MakeStyles();

        bool repaint = Event.current.type == EventType.Repaint;
        int n = slots != null ? slots.Length : Mathf.Max(1, cameras != null ? cameras.Count : 1);
        const float pad = 6f;
        float aspect = frameW > 0 ? (float)frameH / frameW : 9f / 16f;
        float tw = Mathf.Min(tileWidth, (Screen.width - pad * (n + 1)) / n);
        float th = tw * aspect;
        float barW = n * tw + (n - 1) * pad;
        float barTop = pad + th + 4f;
        float panelH = th + 4f + 26f + 4f + 18f + 4f + 36f + pad;

        if (repaint)
        {
            GUI.color = Dim;
            GUI.DrawTexture(new Rect(0, 0, barW + 2 * pad, panelH + pad), Texture2D.whiteTexture);
            GUI.color = Color.white;
            EnsureTextures(displayed);
        }

        // Controls first, and the same calls in every event type: IMGUI hands out control ids in call order, so
        // anything that only runs on Repaint (tiles, labels) must come after them.
        float x = pad, h = 26f, bw = 46f;
        if (Button(ref x, barTop, bw, h, "|<", false, "First frame (Home)")) GoToStart();
        if (Button(ref x, barTop, bw, h, "<<", autoDir < 0, "Rewind (R)")) ToggleRewind();
        if (Button(ref x, barTop, bw, h, "<", false, "Step back (Left)")) StepBackward();
        if (Button(ref x, barTop, bw + 14f, h, paused ? "Play" : "Pause", paused, "Pause / resume (Space)")) TogglePause();
        if (Button(ref x, barTop, bw, h, ">", false, "Step forward (Right)")) StepForward();
        if (Button(ref x, barTop, bw, h, ">>", autoDir > 0, "Replay forward through the history")) ToggleReplay();
        if (Button(ref x, barTop, bw, h, ">|", false, "Newest frame (End)")) GoToNewest();
        x += 10f;
        if (Button(ref x, barTop, 60f, h, "Names", showJointNames, "Joint names on the tiles")) showJointNames = !showJointNames;
        if (Button(ref x, barTop, 52f, h, "Hide", false, "Hide the monitor (M)")) showMonitor = false;

        float sy = barTop + h + 4f;
        int last = Mathf.Max(0, history.Count - 1);
        GUI.enabled = history.Count > 1;
        GUI.changed = false;
        float v = GUI.HorizontalSlider(new Rect(pad, sy + 3f, barW, 14f), Mathf.Min(cursor, last), 0, Mathf.Max(1, last));
        if (GUI.changed) Scrub(Mathf.Clamp(Mathf.RoundToInt(v), 0, last));
        GUI.enabled = true;

        if (!repaint) return;

        for (int i = 0; i < n; i++)
            DrawTile(new Rect(pad + i * (tw + pad), pad, tw, th), i);

        float ty = sy + 22f;
        GUI.Label(new Rect(pad, ty, barW, 18f), StatusLine(), statusStyle);
        GUI.Label(new Rect(pad, ty + 18f, barW, 18f), HelpLine(), statusStyle);
    }

    bool Button(ref float x, float y, float w, float h, string text, bool active, string tooltip)
    {
        Color prev = GUI.backgroundColor;
        if (active) GUI.backgroundColor = new Color(1f, 0.75f, 0.25f);
        bool hit = GUI.Button(new Rect(x, y, w, h), new GUIContent(text, tooltip));
        GUI.backgroundColor = prev;
        x += w + 4f;
        return hit;
    }

    void MakeStyles()
    {
        labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        shadowStyle = new GUIStyle(labelStyle) { normal = { textColor = Color.black } };
        statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.9f, 0.95f, 1f) } };
    }

    string StatusLine()
    {
        string state = paused ? (autoDir < 0 ? "REWIND" : autoDir > 0 ? "REPLAY" : "PAUSED") : "LIVE";
        if (!IsReady || displayed == null) return $"{state}   {status}";

        FvpFrame f = displayed;
        string where = history.Count > 0 ? $"{cursor + 1}/{history.Count}" : "-";
        return $"{state}   frame {where}  (#{f.id}, t = {f.sceneTime:F2} s)   {f.people.Count} person(s)   network {f.netMs:F0} ms   {estimateFps:F1} estimates/s   {status}";
    }

    string HelpLine()
    {
        if (!string.IsNullOrEmpty(GUI.tooltip)) return GUI.tooltip;
        return enableHotkeys ? "Space pause   Left / Right step   R rewind   Home / End first / newest   M monitor" : "";
    }

    void DrawTile(Rect tile, int view)
    {
        GUI.BeginGroup(tile);
        var local = new Rect(0, 0, tile.width, tile.height);

        Texture shown = slots != null && view < slots.Length ? slots[view].shown : null;
        GUI.color = Color.white;
        if (shown != null) GUI.DrawTexture(local, shown, ScaleMode.StretchToFill, false);
        else
        {
            GUI.color = new Color(0.1f, 0.1f, 0.12f, 1f);
            GUI.DrawTexture(local, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(8, tile.height * 0.5f - 10f, tile.width - 16f, 20f), "waiting for the first estimate...", statusStyle);
        }

        FvpFrame f = displayed;
        if (f != null && f.cameras != null && view < f.cameras.Length)
        {
            FvpCameraModel cam = f.cameras[view];
            float sx = tile.width / cam.width, sy = tile.height / cam.height;
            foreach (FvpPerson person in f.people) DrawPerson(person, cam, sx, sy);
        }

        string name = slots != null && view < slots.Length && slots[view].cam != null ? slots[view].cam.name.Trim() : $"view {view}";
        GUI.Label(new Rect(9, 5, tile.width, 20), name, shadowStyle);
        GUI.Label(new Rect(8, 4, tile.width, 20), name, labelStyle);
        GUI.EndGroup();
    }

    void DrawPerson(FvpPerson p, FvpCameraModel cam, float sx, float sy)
    {
        var pts = new Vector2[FvpSkeleton.Count];
        var ok = new bool[FvpSkeleton.Count];
        for (int j = 0; j < FvpSkeleton.Count; j++)
        {
            Vector3 w = p.joints[j];
            if (cam.Project(w.x, w.y, w.z, out double u, out double v))
            {
                // +0.5: pixel centres sit on integers in the camera model, the tile draws the image edge to edge.
                pts[j] = new Vector2((float)(u + 0.5) * sx, (float)(v + 0.5) * sy);
                ok[j] = true;
            }
        }

        Color c = p.Color;
        for (int e = 0; e < FvpSkeleton.Edges.GetLength(0); e++)
        {
            int a = FvpSkeleton.Edges[e, 0], b = FvpSkeleton.Edges[e, 1];
            if (ok[a] && ok[b]) DrawLine(pts[a], pts[b], c, 2.5f);
        }
        GUI.color = c;
        for (int j = 0; j < FvpSkeleton.Count; j++)
            if (ok[j]) GUI.DrawTexture(new Rect(pts[j].x - 2.5f, pts[j].y - 2.5f, 5f, 5f), Texture2D.whiteTexture);
        GUI.color = Color.white;

        if (ok[FvpSkeleton.Neck])
        {
            Vector2 q = pts[FvpSkeleton.Neck];
            string tag = $"#{p.id}  {p.score:F2}";
            GUI.Label(new Rect(q.x + 7, q.y - 17, 120, 18), tag, shadowStyle);
            GUI.color = c;
            GUI.Label(new Rect(q.x + 6, q.y - 18, 120, 18), tag, labelStyle);
            GUI.color = Color.white;
        }
        if (showJointNames)
            for (int j = 0; j < FvpSkeleton.Count; j++)
                if (ok[j]) GUI.Label(new Rect(pts[j].x + 4, pts[j].y - 7, 90, 16), FvpSkeleton.Names[j], statusStyle);
    }

    // A line as a rotated, stretched pixel: pure IMGUI, no shader or GL state to depend on.
    static void DrawLine(Vector2 a, Vector2 b, Color c, float width)
    {
        Vector2 d = b - a;
        float len = d.magnitude;
        if (len < 0.5f) return;
        Matrix4x4 saved = GUI.matrix;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
        GUI.color = c;
        GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.matrix = saved;
    }

    // ---------------- textures ----------------

    // The tiles show the frame under the cursor: its raw capture while that is still in memory, the JPEG after.
    void EnsureTextures(FvpFrame f)
    {
        if (f == null || slots == null || texFrame == f) return;
        texFrame = f;
        for (int i = 0; i < slots.Length; i++)
        {
            ViewSlot s = slots[i];
            s.shown = null;
            if (f.raw != null && i < f.raw.Length && f.raw[i] != null && f.width == frameW && f.height == frameH)
            {
                FlipRows(f.raw[i], flipScratch, f.width, f.height); // Texture2D rows run bottom-up
                s.display.LoadRawTextureData(flipScratch);
                s.display.Apply(false);
                s.shown = s.display;
            }
            else if (f.jpeg != null && i < f.jpeg.Length && f.jpeg[i] != null)
            {
                if (s.decoded == null) s.decoded = new Texture2D(2, 2, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                if (s.decoded.LoadImage(f.jpeg[i], false)) s.shown = s.decoded;
            }
        }
    }

    static void FlipRows(byte[] src, byte[] dst, int w, int h)
    {
        int stride = w * 3;
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src, (h - 1 - y) * stride, dst, y * stride, stride);
    }
}
