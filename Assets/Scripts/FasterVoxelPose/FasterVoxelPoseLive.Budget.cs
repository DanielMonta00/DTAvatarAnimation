using UnityEngine;

// The GPU is shared between Unity and the inference servers, and Unity is the greedy one: a scene rendered as fast as the display
// allows keeps the 3D engine at 100 % (measured: 96 % in a paused game at 59 fps on the laptop RTX 5000 Ada, at its power limit),
// and every network then waits for its turn: the same inference that takes 40 ms on a free GPU takes 150-500 ms next to it.
// What is drawn does not need 59 images a second (the cameras of the dataset run at 30), and a paused scene needs hardly any, so
// the component caps Unity's frame rate and hands the rest of the GPU to the networks.
public partial class FasterVoxelPoseLive
{
    [Header("GPU budget")]
    [Tooltip("Frame rate Unity is held to while the scene runs (Application.targetFrameRate; V-Sync is switched off for it). The servers get the GPU time Unity does not use, so estimates come faster and sooner. 0 = leave Unity alone.")]
    [Min(0)] public int liveFrameRate = 30;
    [Tooltip("The same while paused: a frozen scene needs few frames (the overlay, the bar and the status card). Mouse and key activity brings the live rate back for a second so scrubbing stays smooth. 0 = leave Unity alone.")]
    [Min(0)] public int pausedFrameRate = 15;

    [Tooltip("Every running model (ViTPose...) takes every captured frame: the next frame is captured only when all of them have answered the last, so the slowest model sets the pace and none of them skips frames. Off: each model takes a frame whenever it is idle, and a slow one misses some.")]
    public bool modelsInLockstep = true;

    [Tooltip("Windows scheduling class of the inference servers, CPU and GPU. Unity and the servers share one GPU and one CPU; a class above Unity's lets the servers' kernels and threads go first. Applies when the server is (re)started. 'Normal' leaves them as they are.")]
    public FvpServerProcess.Priority serverPriority = FvpServerProcess.Priority.Normal;

    int appliedRate = -1, previousTarget, previousVSync;
    bool budgetHeld;
    float lastActivityReal = -10f;

    // Anything the user does with the pointer or the keyboard (called from the input code).
    void NoteActivity() => lastActivityReal = Time.unscaledTime;

    public int AppliedFrameRate => appliedRate;

    void ApplyBudget()
    {
        bool idlePaused = paused && Time.unscaledTime - lastActivityReal > 1f;
        int want = idlePaused ? pausedFrameRate : liveFrameRate;
        if (want == appliedRate) return;
        if (!budgetHeld) { previousTarget = Application.targetFrameRate; previousVSync = QualitySettings.vSyncCount; budgetHeld = true; }
        if (want > 0)
        {
            QualitySettings.vSyncCount = 0;          // V-Sync would override the target
            Application.targetFrameRate = want;
        }
        else
        {
            Application.targetFrameRate = previousTarget;
            QualitySettings.vSyncCount = previousVSync;
        }
        appliedRate = want;
    }

    void ReleaseBudget()
    {
        if (!budgetHeld) return;
        Application.targetFrameRate = previousTarget;
        QualitySettings.vSyncCount = previousVSync;
        budgetHeld = false;
        appliedRate = -1;
    }
}
