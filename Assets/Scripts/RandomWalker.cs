using UnityEngine;

/// <summary>
/// Simple random walker for an avatar. The agent always moves forward and
/// occasionally chooses a new heading. It will avoid obstacles using a forward
/// raycast and optionally stay inside a rectangular area.
///
/// Usage:
/// - Attach to your avatar GameObject.
/// - Ensure the GameObject has an Animator (with a float parameter named "Forward" or a bool "isWalking").
/// - Add a CharacterController or let the script add one at runtime.
/// </summary>
[RequireComponent(typeof(Animator))]
public class RandomWalker : MonoBehaviour
{
    [Header("Motion")]
    public float forwardSpeed = 1.6f;         // meters/sec
    public float turnSpeedDegrees = 120f;     // degrees/sec when rotating towards target

    [Header("Behavior timing")]
    public float minChangeInterval = 1.5f;    // seconds (minimum time between heading changes — average ~2s)
    public float maxChangeInterval = 2.5f;    // seconds (maximum time between heading changes)
    public float maxTurnAngle = 135f;         // when choosing a random heading, choose +/- this angle (wider = more aggressive direction changes)
    [Tooltip("Probability per change of picking a completely random heading (0-360°) instead of an incremental ±maxTurnAngle. Helps break circular wandering patterns.")]
    [Range(0f, 1f)] public float fullRandomHeadingChance = 0.25f;

    [Header("Obstacle avoidance")]
    public float obstacleDetectDistance = 1.0f; // raycast distance to detect obstacles in front
    public LayerMask obstacleLayers = ~0;       // layers considered obstacles (default: everything)
    public float obstacleSphereRadius = 0.25f;  // radius for spherecast to detect thin/low colliders
    public bool includeTriggerColliders = true; // include trigger colliders in detection
    [Tooltip("Number of directions probed when forward is blocked. Higher = smoother corner exits at small cost.")]
    public int avoidanceProbeRays = 12;
    [Tooltip("Multiplier on obstacleDetectDistance used for direction probing. Larger = picks emptier corridors.")]
    public float avoidanceProbeRangeMul = 2.0f;

    [Header("Optional bounds (leave empty to disable)")]
    public Transform areaCenter;               // center of allowed area
    public Vector3 areaSize = new Vector3(10f, 0f, 10f); // x/z size (y unused)

    // Animator parameter names (customize to match your controller)
    public string forwardParam = "Forward";  // float param: 0..1 for blending
    public string isWalkingParam = "isWalking"; // optional bool param

    // Internals
    Animator anim;
    CharacterController cc;
    float nextChangeTime = 0f;
    Quaternion targetRotation;
    // Stuck detection
    [Header("Stuck detection")]
    public float stuckThreshold = 0.01f; // meters: movement smaller than this per frame considered stuck
    public float stuckTimeToTurn = 1.5f; // seconds before forcing a 180 turn
    public float stuckCheckWindow = 0.6f; // seconds over which to measure displacement
    public float stuckWindowMoveThreshold = 0.04f; // meters displacement over window considered stuck
    public float backupDistance = 0.18f; // meters to nudge backward when forcing a turn
    private float stuckTimer = 0f;
    private Vector3 lastPosition;
    private float lastStuckCheckTime = 0f;
    private Vector3 lastStuckCheckPosition;

    [Header("Debug Gizmos")]
    public bool showGizmos = false;
    public Color gizmoSphereColor = new Color(1f, 0.5f, 0f, 0.6f);
    public Color gizmoRayColor = Color.yellow;
    public Color gizmoAreaColor = Color.cyan;
    public Color gizmoStuckColor = Color.red;
    public float gizmoSphereDrawRadius = 0.04f;

    void Awake()
    {
        anim = GetComponent<Animator>();
        if (anim == null) Debug.LogError("RandomWalker requires an Animator component.");

        cc = GetComponent<CharacterController>();
        if (cc == null)
        {
            // add a basic CharacterController so Move works; user may prefer Rigidbody-based locomotion
            cc = gameObject.AddComponent<CharacterController>();
            // set a reasonable default capsule size if zero
            if (cc.height <= 0.01f) cc.height = 1.8f;
            if (cc.radius <= 0.01f) cc.radius = 0.3f;
            cc.center = new Vector3(0f, cc.height * 0.5f, 0f);
        }

        targetRotation = transform.rotation;
        ScheduleNextChange();
        lastPosition = transform.position;
        lastStuckCheckPosition = transform.position;
        lastStuckCheckTime = Time.time;
    }

    void ScheduleNextChange()
    {
        nextChangeTime = Time.time + Random.Range(minChangeInterval, maxChangeInterval);
    }

    void Update()
    {
        // Always move forward
        Vector3 forward = transform.forward;

        // Obstacle avoidance: detect blockage via spherecast + low/high rays.
        // When blocked, probe the full circle to find the most open heading
        // instead of using Vector3.Reflect — reflection oscillates in concave
        // corners (wall A's reflection aims into wall B, which reflects back).
        bool avoided = false;
        RaycastHit hit;
        Vector3 rayOrigin = transform.position + Vector3.up * 0.4f;
        QueryTriggerInteraction qti = includeTriggerColliders ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;

        bool blocked = Physics.SphereCast(rayOrigin, obstacleSphereRadius, forward, out hit, obstacleDetectDistance, obstacleLayers, qti);
        if (!blocked)
        {
            Vector3 lowOrigin = transform.position + Vector3.up * 0.15f;
            Vector3 highOrigin = transform.position + Vector3.up * (cc != null ? Mathf.Min(cc.height * 0.9f, 1.6f) : 1.6f);
            blocked = Physics.Raycast(lowOrigin, forward, out hit, obstacleDetectDistance, obstacleLayers, qti)
                   || Physics.Raycast(highOrigin, forward, out hit, obstacleDetectDistance, obstacleLayers, qti);
        }

        if (blocked)
        {
            Vector3 escape;
            if (FindClearestDirection(rayOrigin, forward, qti, out escape))
                targetRotation = Quaternion.LookRotation(escape, Vector3.up);
            else
                // Fully surrounded — flip 180 as last resort.
                targetRotation = Quaternion.Euler(0f, transform.eulerAngles.y + 180f, 0f);
            ScheduleNextChange();
            avoided = true;
        }

        // Area bounds: if areaCenter is set and agent is outside, turn back toward center
        if (!avoided && areaCenter != null)
        {
            Vector3 local = transform.position - areaCenter.position;
            float halfX = Mathf.Abs(areaSize.x) * 0.5f;
            float halfZ = Mathf.Abs(areaSize.z) * 0.5f;
            if (local.x > halfX || local.x < -halfX || local.z > halfZ || local.z < -halfZ)
            {
                // turn toward area center
                Vector3 toCenter = (areaCenter.position - transform.position);
                toCenter.y = 0f;
                if (toCenter.sqrMagnitude > 0.001f)
                {
                    targetRotation = Quaternion.LookRotation(toCenter.normalized, Vector3.up);
                    ScheduleNextChange();
                }
            }
        }

        // Random heading change. Fires on schedule even when avoidance just
        // fired this frame — without this, walking along a zone wall keeps
        // re-triggering avoidance and the random change never gets to run,
        // producing circular loops along the perimeter. If the random heading
        // happens to point into a wall, next frame's avoidance corrects it.
        if (Time.time >= nextChangeTime)
        {
            float newY;
            if (Random.value < fullRandomHeadingChance)
                newY = Random.Range(0f, 360f);            // total reset — breaks loops
            else
                newY = transform.eulerAngles.y + Random.Range(-maxTurnAngle, maxTurnAngle);
            targetRotation = Quaternion.Euler(0f, newY, 0f);
            ScheduleNextChange();
        }

        // Smoothly rotate toward target rotation
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeedDegrees * Time.deltaTime);

        // Move using CharacterController so collisions are handled
        Vector3 motion = transform.forward * forwardSpeed * Time.deltaTime;
        // small gravity to keep grounded if using CharacterController
        motion += Physics.gravity * Time.deltaTime;
        CollisionFlags colFlags = CollisionFlags.None;
        if (cc != null)
        {
            cc.Move(motion);
            colFlags = cc.collisionFlags;
        }
        else
        {
            Vector3 prev = transform.position;
            transform.position += motion;
            colFlags = CollisionFlags.None; // no character controller info
        }

    // Drive animator: normalized forward speed (optional)
        if (anim != null)
        {
            // if user has a float param named forwardParam, set it to 1.0 (always walking)
            if (!string.IsNullOrEmpty(forwardParam) && anim.HasParameterHash(Animator.StringToHash(forwardParam)))
            {
                anim.SetFloat(forwardParam, 1f);
            }
            // set walking boolean if present
            if (!string.IsNullOrEmpty(isWalkingParam) && anim.HasParameterHash(Animator.StringToHash(isWalkingParam)))
            {
                anim.SetBool(isWalkingParam, true);
            }
        }

        // --- Improved stuck detection ---
        // Use a time-window displacement check so small frame jitter doesn't mask being stuck.
        float now = Time.time;
        if (now - lastStuckCheckTime >= stuckCheckWindow)
        {
            float displacement = Vector3.Distance(transform.position, lastStuckCheckPosition);
            if (displacement < stuckWindowMoveThreshold)
            {
                stuckTimer += (now - lastStuckCheckTime);
            }
            else
            {
                // moved sufficiently; decay timer
                stuckTimer = 0f;
            }

            lastStuckCheckPosition = transform.position;
            lastStuckCheckTime = now;
        }

        // If we are physically colliding with sides, consider that a sign of being stuck and accelerate the timer
        if (cc != null && (colFlags & CollisionFlags.Sides) != 0)
        {
            // bump the stuck timer so we react faster when colliding
            stuckTimer += Time.deltaTime * 1.5f;
        }

        // Update lastPosition for legacy uses/debug
        lastPosition = transform.position;

        if (stuckTimer >= stuckTimeToTurn)
        {
            // Small backward nudge to clear the contact, then probe for the
            // most open direction (better than a blind 180 in concave corners).
            Vector3 backup = -transform.forward * backupDistance;
            if (cc != null) cc.Move(backup);
            else            transform.position += backup;

            Vector3 origin = transform.position + Vector3.up * 0.4f;
            QueryTriggerInteraction stuckQti = includeTriggerColliders ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;
            Vector3 escape;
            if (FindClearestDirection(origin, transform.forward, stuckQti, out escape))
            {
                targetRotation = Quaternion.LookRotation(escape, Vector3.up);
            }
            else
            {
                float jitter = Random.Range(-25f, 25f);
                targetRotation = Quaternion.Euler(0f, transform.eulerAngles.y + 180f + jitter, 0f);
            }
            ScheduleNextChange();
            stuckTimer = 0f;
        }
    }

    // Probes `avoidanceProbeRays` headings around the avatar in the xz-plane
    // and returns the world-space direction with the most clearance. This
    // replaces Vector3.Reflect when blocked: reflection alone oscillates in
    // concave corners, while picking the freest direction naturally exits.
    bool FindClearestDirection(Vector3 origin, Vector3 currentForward, QueryTriggerInteraction qti, out Vector3 best)
    {
        int rays = Mathf.Max(4, avoidanceProbeRays);
        float range = obstacleDetectDistance * Mathf.Max(1f, avoidanceProbeRangeMul);
        float bestDist = -1f;
        Vector3 bestDir = currentForward;

        for (int i = 0; i < rays; i++)
        {
            float ang = (360f / rays) * i;
            Vector3 dir = Quaternion.Euler(0f, ang, 0f) * Vector3.forward;

            float dist;
            RaycastHit hit;
            if (Physics.SphereCast(origin, obstacleSphereRadius, dir, out hit, range, obstacleLayers, qti))
                dist = hit.distance;
            else
                dist = range;

            // Light bias toward keeping current heading: equally clear
            // directions that require less turning are preferred. Up to 30%
            // penalty for a full 180° about-face.
            float headingDelta = Vector3.Angle(currentForward, dir); // 0..180
            float bias = 1f - (headingDelta / 180f) * 0.3f;
            float score = dist * bias;

            if (score > bestDist)
            {
                bestDist = score;
                bestDir = dir;
            }
        }

        best = bestDir;
        // Treat anything where the *best* direction is still mostly blocked
        // (less than half a body-length clear) as "no escape found".
        return bestDist > obstacleSphereRadius;
    }

    void OnDrawGizmos()
    {
        if (!showGizmos) return;

        // Basic safety: try to draw relative to transform
        Transform t = this.transform;

        // Draw forward vector
        Gizmos.color = gizmoRayColor;
        Gizmos.DrawLine(t.position + Vector3.up * 0.2f, t.position + Vector3.up * 0.2f + t.forward * obstacleDetectDistance);
        // draw small sphere at origin and endpoint
        Gizmos.color = gizmoSphereColor;
        Gizmos.DrawSphere(t.position + Vector3.up * 0.4f, gizmoSphereDrawRadius);
        Gizmos.DrawSphere(t.position + Vector3.up * 0.4f + t.forward * obstacleDetectDistance, gizmoSphereDrawRadius);

        // Draw spherecast path as series of spheres
        Gizmos.color = new Color(gizmoSphereColor.r, gizmoSphereColor.g, gizmoSphereColor.b, 0.18f);
        int steps = 6;
        for (int i = 0; i <= steps; i++)
        {
            float f = (float)i / (float)steps;
            Vector3 p = t.position + Vector3.up * 0.4f + t.forward * (obstacleDetectDistance * f);
            Gizmos.DrawWireSphere(p, obstacleSphereRadius);
        }

        // low and high ray origins
        Vector3 lowOrigin = t.position + Vector3.up * 0.15f;
        Vector3 highOrigin = t.position + Vector3.up * (cc != null ? Mathf.Min(cc.height * 0.9f, 1.6f) : 1.6f);
        Gizmos.color = gizmoRayColor;
        Gizmos.DrawLine(lowOrigin, lowOrigin + t.forward * obstacleDetectDistance);
        Gizmos.DrawLine(highOrigin, highOrigin + t.forward * obstacleDetectDistance);

        // Draw area bounds if assigned
        if (areaCenter != null)
        {
            Gizmos.color = gizmoAreaColor;
            Vector3 center = areaCenter.position;
            Vector3 size = new Vector3(areaSize.x, 0.05f, areaSize.z);
            Gizmos.DrawWireCube(center + Vector3.up * 0.01f, size);
        }

        // Draw last stuck-check position and timer
        Gizmos.color = stuckTimer > 0f ? gizmoStuckColor : Color.green;
        Gizmos.DrawSphere(lastStuckCheckPosition + Vector3.up * 0.2f, gizmoSphereDrawRadius * 1.5f);

#if UNITY_EDITOR
        // Draw labels with Handles for easier debugging in Editor
        try
        {
            UnityEditor.Handles.color = Color.white;
            UnityEditor.Handles.Label(t.position + Vector3.up * 1.1f, $"stuckTimer={stuckTimer:F2}");
            UnityEditor.Handles.Label(t.position + Vector3.up * 0.9f, $"nextChange={nextChangeTime - Time.time:F1}s");
        }
        catch { }
#endif
    }
}

// Extension method helper for Animator to test parameter existence without throwing
public static class AnimatorExtensions
{
    public static bool HasParameterHash(this Animator animator, int paramHash)
    {
        if (animator == null) return false;
        foreach (var p in animator.parameters)
        {
            if (p.nameHash == paramHash) return true;
        }
        return false;
    }
}
