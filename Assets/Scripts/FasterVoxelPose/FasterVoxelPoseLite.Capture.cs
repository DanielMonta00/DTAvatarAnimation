using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// The picture side of FasterVoxelPoseLite: take the cameras' images at the end of a rendered frame, get them to the server.
public sealed partial class FasterVoxelPoseLite
{
    // Blit with these and the GPU hands the rows back top-down, which is what the network wants (a plain blit comes back bottom-up).
    static readonly Vector2 FlipScale = new Vector2(1f, -1f), FlipOffset = new Vector2(0f, 1f);

    AsyncGPUReadbackRequest[] syncRequests;

    bool WantsCapture()
    {
        if (problem != null || slots == null || !IsReady) return false;
        if (inFlight != null || building != null) return false;
        if (Time.timeAsDouble <= lastCaptureTime + 1e-9) return false;       // the scene has not advanced (paused): the answer would be the same
        return !(maxFps > 0f && Time.realtimeSinceStartup - lastCaptureReal < 1f / maxFps);
    }

    // SRP: all cameras of the frame (and their lens-distortion remaps) are done when this fires. The Scene view is rendered in a
    // context of its own, so wait for the one that holds our cameras.
    void OnEndContextRendering(ScriptableRenderContext context, List<Camera> rendered)
    {
        if (cameras == null || cameras.Count == 0 || cameras[0] == null || !rendered.Contains(cameras[0])) return;
        OnFrameRendered();
    }

    IEnumerator EndOfFrameLoop()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();
            OnFrameRendered();
        }
    }

    // The cameras of this frame are drawn. An answer that arrived while it was being drawn is used now, and the next frame is taken
    // right away: the server waits for the shortest possible time.
    void OnFrameRendered()
    {
        if (!Application.isPlaying) return;
        long start = Stamp();
        PumpClient();
        if (WantsCapture()) captureArmed = true;
        frameTicks += Stamp() - start;
        if (captureArmed && slots != null) CaptureNow();
    }

    void CaptureNow()
    {
        captureArmed = false;
        if (slots == null || inFlight != null || building != null || !configAcked) return;

        // Not ready yet: a lens output that was just built is black until its camera has rendered into it; a shadow camera that was just
        // made renders for the first time next frame. Try again then.
        bool wait = false;
        foreach (ViewSlot s in slots)
        {
            if (s.Distorted) { if (!s.calib.OutputRendered) wait = true; }
            else if (EnsureShadow(s)) wait = true;
        }
        if (wait) return;

        long t0 = Stamp();
        var f = new LiteFrame
        {
            id = ++frameCounter,
            captureTime = Time.timeAsDouble,
            captureReal = Time.realtimeSinceStartup,
            captureStartTicks = t0,
            raw = new byte[slots.Length][],
        };
        int gen = generation;
        building = f;
        readbackPending = slots.Length;
        readbackFailed = false;
        lastCaptureTime = f.captureTime;
        lastCaptureReal = f.captureReal;

        if (readback == ReadbackMode.Blocking)
        {
            if (syncRequests == null || syncRequests.Length != slots.Length) syncRequests = new AsyncGPUReadbackRequest[slots.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                Graphics.Blit(SourceTexture(slots[i]), slots[i].small, FlipScale, FlipOffset);
                syncRequests[i] = AsyncGPUReadback.Request(slots[i].small, 0, TextureFormat.RGB24);
            }
            // The GPU has all the requests queued; they finish together, right after this frame's render.
            for (int i = 0; i < slots.Length; i++) syncRequests[i].WaitForCompletion();
            for (int i = 0; i < slots.Length; i++) Consume(f, i, syncRequests[i]);
        }
        else
        {
            for (int i = 0; i < slots.Length; i++)
            {
                Graphics.Blit(SourceTexture(slots[i]), slots[i].small, FlipScale, FlipOffset);
                int view = i;
                AsyncGPUReadback.Request(slots[i].small, 0, TextureFormat.RGB24, req => OnReadback(gen, f, view, req));
            }
        }
    }

    void OnReadback(int gen, LiteFrame f, int view, AsyncGPUReadbackRequest req)
    {
        if (gen != generation || building != f) { ReleaseBuffers(f); return; }
        Consume(f, view, req);
    }

    void Consume(LiteFrame f, int view, AsyncGPUReadbackRequest req)
    {
        if (req.hasError) readbackFailed = true;
        else
        {
            NativeArray<byte> data = req.GetData<byte>();
            if (data.Length != frameW * frameH * 3) readbackFailed = true;
            else
            {
                byte[] buf = FvpBufferPool.Rent(data.Length);
                data.CopyTo(buf);
                f.raw[view] = buf;
            }
        }
        if (--readbackPending == 0) FinishBuild(f);
    }

    bool failedWarned;

    void FinishBuild(LiteFrame f)
    {
        building = null;
        f.captureEndTicks = Stamp();
        f.captureMs = (float)TicksToMs(f.captureEndTicks - f.captureStartTicks);
        if (readbackFailed)
        {
            if (!failedWarned) { Debug.LogWarning("[FasterVoxelPoseLite] a camera image could not be read back; frame skipped.", this); failedWarned = true; }
            ReleaseBuffers(f);
            return;
        }
        failedWarned = false;

        inFlight = f;
        inFlightSince = Time.realtimeSinceStartup;
        var parts = new List<ArraySegment<byte>>(f.raw.Length);
        foreach (byte[] b in f.raw) parts.Add(new ArraySegment<byte>(b, 0, b.Length));
        string json = FvpProtocol.BuildFrameJson(f.id, configId, f.raw.Length, frameW, frameH, minScore);
        // done runs on the sender thread once the bytes are written (or at once when the link is down): the buffers go back to the pool then.
        if (!client.Send(json, parts, () => { System.Threading.Volatile.Write(ref f.sentTicks, Stamp()); ReleaseBuffers(f); }))
            inFlight = null;
    }

    static void ReleaseBuffers(LiteFrame f)
    {
        byte[][] raw = f.raw;
        if (raw == null) return;
        for (int i = 0; i < raw.Length; i++)
            if (raw[i] != null) { FvpBufferPool.Return(raw[i]); raw[i] = null; }
    }

    // The image the camera produces: its lens-distorted output, or (no distortion) a shadow camera's render.
    Texture SourceTexture(ViewSlot s)
    {
        if (s.Distorted)
        {
            s.calib.RenderDistorted();          // make sure it holds this frame
            return s.calib.DistortedTexture;
        }
        EnsureShadow(s);
        return s.shadowRT;
    }

    // Makes the shadow camera of a plain / undistorted view if it has none. True when it was made just now (it has not rendered yet).
    bool EnsureShadow(ViewSlot s)
    {
        if (s.shadow != null) return false;
        var go = new GameObject($"_FVP_LiteShadowCam_{s.cam.name.Trim()}") { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(s.cam.transform, false);
        s.shadow = go.AddComponent<Camera>();
        s.shadow.CopyFrom(s.cam);
        if (s.calib != null) s.shadow.projectionMatrix = s.cam.projectionMatrix;      // keep the calibrated K
        else s.shadow.aspect = (float)frameW / frameH;                                  // the pinhole K sent to the server assumes the sent frame's aspect
        s.shadowRT = new RenderTexture(frameW, frameH, 24, RenderTextureFormat.ARGB32) { name = "FVP lite shadow" };
        s.shadowRT.Create();
        s.shadow.targetTexture = s.shadowRT;
        s.shadow.depth = s.cam.depth + 100f;       // after the source camera, same frame state
        return true;
    }
}
