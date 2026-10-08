using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// Mouse and keyboard controls, read from the input system every frame so they work on whichever display the Game view shows
// (IMGUI only sees Display 1, which is why the bar is uGUI too). The Game view has to be focused for the editor to deliver the input.
//
//   mouse   left click  pause / play        wheel  step one frame (down = forward, up = back; the first notch pauses)
//           left drag   scrub the history (one frame per `dragPixelsPerFrame` pixels; drag left = back in time)
//   keys    Space pause / play   Left / Right step (hold to repeat)   R rewind   Home / End first / newest frame   M the bar
//
// A click on the transport bar (on every display, see FasterVoxelPoseLive.Bar.cs) belongs to its buttons and is not also read as
// "pause"; the buttons work whether or not the gestures on the picture are enabled.
public partial class FasterVoxelPoseLive
{
    // drag state
    bool dragging, dragMoved;
    Vector2 dragStart;
    int dragStartCursor;
    float wheelAccum;
    float repeatLeftAt, repeatRightAt;
    bool prevLeftButton;
    readonly bool[] prevKey = new bool[5]; // space, R, Home, End, M
    const float RepeatDelay = 0.4f, RepeatInterval = 0.07f;

    void PollInput()
    {
        if (!isActiveAndEnabled) return;
#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            Vector2 wheel = mouse.scroll.ReadValue();
            bool held = mouse.leftButton.isPressed;
            // Edges from isPressed between two frames as well as the input system's own "this frame" flags: those are tied to
            // its update steps and are not raised when the editor does not have focus.
            bool pressed = mouse.leftButton.wasPressedThisFrame || (held && !prevLeftButton);
            bool released = mouse.leftButton.wasReleasedThisFrame || (!held && prevLeftButton);
            prevLeftButton = held;
            HandleMouse(mouse.position.ReadValue(), pressed, released, held, wheel.y);
        }

        Keyboard kb = Keyboard.current;
        if (kb != null && enableHotkeys)
        {
            if (KeyEdge(0, kb.spaceKey.isPressed || kb.spaceKey.wasPressedThisFrame)) TogglePause();
            if (KeyEdge(1, kb.rKey.isPressed || kb.rKey.wasPressedThisFrame)) ToggleRewind();
            if (KeyEdge(2, kb.homeKey.isPressed || kb.homeKey.wasPressedThisFrame)) GoToStart();
            if (KeyEdge(3, kb.endKey.isPressed || kb.endKey.wasPressedThisFrame)) GoToNewest();
            if (KeyEdge(4, kb.mKey.isPressed || kb.mKey.wasPressedThisFrame)) showControls = !showControls;
            if (Repeats(!prevRight && kb.rightArrowKey.isPressed || kb.rightArrowKey.wasPressedThisFrame, kb.rightArrowKey.isPressed, ref repeatRightAt)) StepForward();
            if (Repeats(!prevLeft && kb.leftArrowKey.isPressed || kb.leftArrowKey.wasPressedThisFrame, kb.leftArrowKey.isPressed, ref repeatLeftAt)) StepBackward();
            prevRight = kb.rightArrowKey.isPressed; prevLeft = kb.leftArrowKey.isPressed;
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        HandleMouse(Input.mousePosition, Input.GetMouseButtonDown(0), Input.GetMouseButtonUp(0), Input.GetMouseButton(0), Input.mouseScrollDelta.y);
        if (enableHotkeys)
        {
            if (Input.GetKeyDown(KeyCode.Space)) TogglePause();
            if (Input.GetKeyDown(KeyCode.R)) ToggleRewind();
            if (Input.GetKeyDown(KeyCode.Home)) GoToStart();
            if (Input.GetKeyDown(KeyCode.End)) GoToNewest();
            if (Input.GetKeyDown(KeyCode.M)) showControls = !showControls;
            if (Repeats(Input.GetKeyDown(KeyCode.RightArrow), Input.GetKey(KeyCode.RightArrow), ref repeatRightAt)) StepForward();
            if (Repeats(Input.GetKeyDown(KeyCode.LeftArrow), Input.GetKey(KeyCode.LeftArrow), ref repeatLeftAt)) StepBackward();
        }
#endif
    }

    bool prevRight, prevLeft;

    // True on the frame a key goes down (isPressed after not being pressed).
    bool KeyEdge(int slot, bool down)
    {
        bool edge = down && !prevKey[slot];
        prevKey[slot] = down;
        return edge;
    }

    // Press fires at once; held past RepeatDelay it fires every RepeatInterval.
    static bool Repeats(bool pressed, bool held, ref float nextAt)
    {
        float now = Time.unscaledTime;
        if (pressed) { nextAt = now + RepeatDelay; return true; }
        if (held && now >= nextAt) { nextAt = now + RepeatInterval; return true; }
        return false;
    }

    // `pos` is in screen pixels, origin bottom-left (the input system's convention). Public so a test can feed it events.
    public void HandleMouse(Vector2 pos, bool pressed, bool released, bool held, float wheel)
    {
        bool inside = pos.x >= 0f && pos.y >= 0f && pos.x <= Screen.width && pos.y <= Screen.height;
        HandleBar(pos, pressed, released, held, inside);
        if (!enableMouse) { dragging = false; return; }

        bool onBar = inside && barLayout.Contains(pos);

        if (pressed && inside && !onBar)
        {
            dragging = true; dragMoved = false;
            dragStart = pos;
            dragStartCursor = history.Count > 0 ? cursor : 0;
        }

        if (dragging && held)
        {
            float dx = pos.x - dragStart.x;
            if (!dragMoved && Mathf.Abs(dx) > 6f)
            {
                dragMoved = true;
                if (!paused) Pause();
            }
            if (dragMoved && history.Count > 0)
                Scrub(Mathf.Clamp(dragStartCursor + Mathf.RoundToInt(dx / dragPixelsPerFrame), 0, history.Count - 1));
        }

        if (dragging && released)
        {
            if (!dragMoved && inside && !onBar) TogglePause(); // a click, not a drag
            dragging = false;
        }
        else if (dragging && !held && !pressed) dragging = false; // the button went up somewhere we did not see

        if (inside && !onBar && wheel != 0f)
        {
            // Windows reports 120 per notch, other sources 1: count notches either way; touchpads accumulate.
            wheelAccum += Mathf.Abs(wheel) >= 20f ? wheel / 120f : wheel;
            while (wheelAccum <= -1f) { wheelAccum += 1f; StepForward(); }
            while (wheelAccum >= 1f) { wheelAccum -= 1f; StepBackward(); }
        }
    }
}
