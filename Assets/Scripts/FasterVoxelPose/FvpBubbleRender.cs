using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// The protection bubble on a pose camera's display, drawn AFTERWARDS: the real objects themselves (their meshes, materials and tint, lit by the
// scene's lights, glass and all), seen through the same projection and the same lens distortion as the camera, into a texture of their own that
// is shown over the display. The camera's own picture (the one the pose model reads) never contains the bubble: a second, hidden camera that
// sees nothing but the bubble renders it, so this costs the bubble's meshes and two full-frame passes at the resolution chosen, not a second
// render of the scene.
//
//   pose camera   --> IdealTexture --> DistortedTexture --> the model (raw)          [CalibratedCamera, untouched]
//   bubble camera --> its own ideal texture --> its own distorted texture (RGBA)     [this]   ... shown over the display by FvpDisplayLayer
//
// How the second camera gets the bubble and nothing else: for the duration of ITS render only (between the pipeline's begin and end of that
// camera) the bubble's GameObjects are moved to a private layer that only it renders, and moved back. No other camera ever renders while they
// are there, so the Scene view and every other camera see them where they always were. (Graphics.DrawMesh for one camera does not work here:
// measured, the render pipeline draws nothing for a request that names a camera, and a request without one is drawn by every camera that
// includes the layer, the Scene view among them. Camera.SetTargetBuffers, to borrow the pose camera's depth, is ignored too.)
//
// Hiding behind things. The second camera has no scene, so the glass would be drawn over everything, including the operator inside the bubble,
// whose body hides the far side of the glass in the real render (the red shell has both faces drawn). The objects that must hide the bubble
// (SetOccluders: the operator's avatar, a box in front of it) are moved to the same layer too, with a depth-only material, so that the glass is
// depth-tested against them. Anything not listed does not hide it.
//
// The result is premultiplied (alpha-blended objects in a texture cleared to transparent): see FvpPremultipliedOver.
public sealed class FvpBubbleRender : IDisposable
{
    public const string CompositeShaderName = "Hidden/FasterVoxelPose/PremultipliedOver";
    public const string DepthShaderName = "Hidden/FasterVoxelPose/DepthOnly";

    readonly CalibratedCamera calib;
    readonly Camera poseCam;
    readonly int layer;
    GameObject go;
    Camera cam;
    LensDistortionRenderer lens;
    Matrix4x4 builtProjection;
    int builtW, builtH;
    bool hooked;

    Renderer[] objects = Array.Empty<Renderer>();
    int[] savedLayers = Array.Empty<int>();
    Renderer[] occluders = Array.Empty<Renderer>();
    int[] savedOccluderLayers = Array.Empty<int>();
    List<Material>[] savedOccluderMaterials = Array.Empty<List<Material>>();
    readonly List<Material> depthList = new List<Material>();
    Material depthMaterial;
    bool swapped;

    // The bubble, distorted like the camera's own image, premultiplied RGBA. Null until the camera's output exists.
    public RenderTexture Output => lens != null ? lens.DistortedTexture : null;
    public Camera BubbleCamera => cam;
    public bool IsBuilt => lens != null && cam != null;

    // For tests and diagnostics: the bubble camera's own texture before the lens remap, how many times the camera finished rendering, and how
    // many objects (bubble, occluders) were moved to its layer in the last of them.
    public RenderTexture Ideal => lens != null ? lens.IdealTexture : null;
    public int RenderedFrames { get; private set; }
    public int ObjectsShown { get; private set; }
    public int OccludersShown { get; private set; }

    // The highest layer without a name: nothing in the scene is on it.
    static int pickedLayer = -1;
    static int FreeLayer()
    {
        if (pickedLayer >= 0) return pickedLayer;
        for (int i = 31; i >= 8; i--)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return pickedLayer = i;
        return pickedLayer = 31;
    }

    public FvpBubbleRender(CalibratedCamera calibrated)
    {
        calib = calibrated;
        poseCam = calibrated.GetComponent<Camera>();
        layer = FreeLayer();
    }

    // Makes sure the second camera and its textures match the pose camera (a calibration that was reloaded, an output that changed size).
    // `scale` is the resolution of the bubble's textures relative to the camera's output: 1 = the same, 0.5 = a quarter of the pixels.
    // False while that is not possible (no render pipeline, no lens output yet, shader missing).
    public bool Prepare(float scale)
    {
        if (calib == null || poseCam == null || !calib.IsApplied || !calib.DistortionActive || calib.DistortedTexture == null) return false;
        if (GraphicsSettings.currentRenderPipeline == null) return false;      // the "camera begins / finished" callbacks it relies on belong to the pipeline

        int w = Mathf.Max(16, Mathf.RoundToInt(calib.DistortedTexture.width * scale));
        int h = Mathf.Max(16, Mathf.RoundToInt(calib.DistortedTexture.height * scale));
        Matrix4x4 projection = poseCam.projectionMatrix;
        if (lens != null && projection == builtProjection && w == builtW && h == builtH && cam != null) return true;
        return Build(projection, w, h);
    }

    bool Build(Matrix4x4 projection, int w, int h)
    {
        Shader shader = calib.distortionShader != null ? calib.distortionShader : Shader.Find(CalibratedCamera.DistortionShaderName);
        if (shader == null) return false;

        DisposeLens();
        var dist = new double[calib.distCoeffs.Length];
        for (int i = 0; i < dist.Length; i++) dist[i] = calib.distCoeffs[i];
        // a twin of the camera's own lens chain: the same remap grid and margins, so what is drawn with the camera's projection lands where the camera's own pixels do
        lens = new LensDistortionRenderer(calib.fx, calib.fy, calib.cx, calib.cy, calib.imageWidth, calib.imageHeight, dist, shader);
        lens.EnsureOutput(w, h);

        if (go == null)
        {
            go = new GameObject("_FVP_BubbleCam_" + poseCam.name.Trim()) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(poseCam.transform, false);          // follows the pose camera wherever CalibratedCamera puts it
            cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);           // transparent: only the bubble leaves anything in the texture
            cam.cullingMask = 1 << layer;                              // the private layer the bubble is moved to while this camera renders
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.depth = poseCam.depth + 0.5f;
            cam.enabled = false;
        }
        cam.targetTexture = null;
        cam.projectionMatrix = projection;
        cam.targetTexture = lens.IdealTexture;
        builtProjection = projection; builtW = w; builtH = h;

        if (!hooked)
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
            hooked = true;
        }
        return true;
    }

    // The second camera renders (and the remap runs) only while there is a bubble to show.
    public void SetActive(bool on)
    {
        if (cam != null && cam.enabled != on) cam.enabled = on;
        if (!on) RestoreAll();
    }

    // The objects that make up the bubble (the renderers under the followed object). Their GameObjects are borrowed for this camera's render.
    public void SetObjects(IReadOnlyList<Renderer> list)
    {
        if (Same(list, objects)) return;
        RestoreAll();
        objects = Copy(list);
        savedLayers = new int[objects.Length];
    }

    // The objects that hide the bubble behind them (the operator's avatar, a box in front of it): drawn depth-only into the bubble camera's texture.
    // `depthShader` null = found by name. With no depth shader there are no occluders.
    public void SetOccluders(IReadOnlyList<Renderer> list, Shader depthShader)
    {
        if (depthMaterial == null)
        {
            if (depthShader == null) depthShader = Shader.Find(DepthShaderName);
            if (depthShader != null) depthMaterial = new Material(depthShader) { hideFlags = HideFlags.HideAndDontSave };
        }
        if (depthMaterial == null) list = null;
        if (Same(list, occluders)) return;
        RestoreAll();
        occluders = Copy(list);
        savedOccluderLayers = new int[occluders.Length];
        savedOccluderMaterials = new List<Material>[occluders.Length];
        for (int i = 0; i < occluders.Length; i++) savedOccluderMaterials[i] = new List<Material>(4);
    }

    static bool Same(IReadOnlyList<Renderer> list, Renderer[] current)
    {
        int n = list == null ? 0 : list.Count;
        if (n != current.Length) return false;
        for (int i = 0; i < n; i++) if (!ReferenceEquals(current[i], list[i])) return false;
        return true;
    }

    static Renderer[] Copy(IReadOnlyList<Renderer> list)
    {
        if (list == null || list.Count == 0) return Array.Empty<Renderer>();
        var a = new Renderer[list.Count];
        for (int i = 0; i < a.Length; i++) a[i] = list[i];
        return a;
    }

    void OnBeginCamera(ScriptableRenderContext context, Camera camera)
    {
        if (camera != cam) { if (swapped) RestoreAll(); return; }       // whatever happens, no other camera finds them on the private layer / with depth-only materials
        int n = 0;
        for (int i = 0; i < objects.Length; i++)
        {
            Renderer r = objects[i];
            if (r == null) continue;
            savedLayers[i] = r.gameObject.layer;
            r.gameObject.layer = layer;
            n++;
        }
        int m = 0;
        for (int i = 0; i < occluders.Length; i++)
        {
            Renderer r = occluders[i];
            if (r == null) continue;
            savedOccluderLayers[i] = r.gameObject.layer;
            r.GetSharedMaterials(savedOccluderMaterials[i]);
            depthList.Clear();
            for (int k = 0; k < savedOccluderMaterials[i].Count; k++) depthList.Add(depthMaterial);
            r.SetSharedMaterials(depthList);
            r.gameObject.layer = layer;
            m++;
        }
        swapped = true;
        ObjectsShown = n; OccludersShown = m;
    }

    void OnEndCamera(ScriptableRenderContext context, Camera camera)
    {
        if (camera != cam) return;
        RestoreAll();
        RenderedFrames++;
        if (lens != null) lens.Render();
    }

    void RestoreAll()
    {
        if (!swapped) return;
        swapped = false;
        for (int i = 0; i < objects.Length && i < savedLayers.Length; i++)
            if (objects[i] != null) objects[i].gameObject.layer = savedLayers[i];
        for (int i = 0; i < occluders.Length && i < savedOccluderLayers.Length; i++)
        {
            Renderer r = occluders[i];
            if (r == null) continue;
            r.gameObject.layer = savedOccluderLayers[i];
            r.SetSharedMaterials(savedOccluderMaterials[i]);
        }
    }

    void DisposeLens()
    {
        if (cam != null) cam.targetTexture = null;      // never leave the camera pointing at a released texture
        lens?.Dispose();
        lens = null;
    }

    public void Dispose()
    {
        RestoreAll();
        if (hooked)
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            hooked = false;
        }
        DisposeLens();
        if (depthMaterial != null)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(depthMaterial); else UnityEngine.Object.DestroyImmediate(depthMaterial);
            depthMaterial = null;
        }
        if (go != null)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(go); else UnityEngine.Object.DestroyImmediate(go);
            go = null; cam = null;
        }
    }
}
