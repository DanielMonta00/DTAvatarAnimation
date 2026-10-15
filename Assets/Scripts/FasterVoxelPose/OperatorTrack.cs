using System;
using System.Collections.Generic;
using UnityEngine;

// Follows ONE person through the stream of estimates a pose model produces, so that something can be moved with them (PoseEstimateFollower).
// No scene code in here: it takes the people of each estimate and the scene time they were captured at, and answers where the person is
// now. That keeps it testable outside the editor.
//
// What the estimates look like (measured, see the README): the torso point is good to 3-4 cm, but the list of people is not clean. A
// person can come twice (a second, fading skeleton on the same body, for a few estimates), numbered differently every time the model loses
// and finds the person again, and now and then a skeleton stands where nobody is. So the track follows a BODY, not an id:
//
//   * skeletons closer than `mergeRadius` are one body (two people cannot stand inside each other); the better one (quality) stands for it;
//   * the body with the followed id is the person while it is where the prediction says; otherwise a body inside the matching gate
//     replaces it, but only once the same place has been seen in `confirmEstimates` estimates in a row (a ghost lasts one);
//   * a body that was seen at least `separation` away from the followed one at the same time is somebody else: it is taken only if it
//     is practically on the prediction, so a neighbour walking past while the person's estimate drops out is not mistaken for the followed person.
//
//   Searching  no one yet. The body nearest to `searchCentre` (inside `acquireRadius`, if set; the sharper one on a tie) is taken once it
//              has been there for `confirmEstimates` estimates. Bodies already followed by another follower (`claimed`) are not candidates.
//   Tracking   a constant-velocity Kalman filter per horizontal axis; each estimate that matches the body is a measurement.
//   Lost       no estimate matched for `holdSeconds`: the filter stops extrapolating and keeps its last position. A body that shows up
//              near it again is taken back; after `forgetSeconds` the track is dropped and the search starts again.
//
// The filter is what makes real time possible: an estimate describes the scene as it was when the images were taken (tens to hundreds
// of milliseconds ago). PredictXZ(now) moves the filtered position forward by velocity x age, so the output is where the person is now
// rather than where they were; the more regular the walk, the better it works (a start, a stop or a turn costs a few centimetres).
public sealed class OperatorTrack
{
    public enum State { Searching, Tracking, Lost }

    public struct Candidate
    {
        public int id;           // the estimator's id for this skeleton (stable while that estimator tracks it, new when it loses and finds the person again)
        public Vector3 pos;      // world, metres: the point of the body that is followed (the caller chooses it)
        public float quality;    // 0..1, how sure the estimator is of this skeleton (its 2D support, or its score); 0 = not known
    }

    // ---------------- settings (change them any time) ----------------

    public float measurementNoise = 0.06f;      // metres, 1 sigma of one estimate's horizontal position of the followed point (measured on the recorded session: 3-4 cm; set above it)
    public float maneuverAccel = 1.5f;          // m/s^2: how hard the person can speed up, stop or turn (bigger follows changes faster, smooths less)
    public float maxSpeed = 3f;                 // m/s, a person walking never exceeds this; used to cap the velocity
    public float holdSeconds = 1f;              // Tracking -> Lost when no estimate matched for this long (counted from the arrival of the last one that did)
    public float holdIntervals = 2.5f;          // ... and for at least this many intervals between estimates: a slow pipeline is not a lost person
    public float forgetSeconds = 8f;            // Lost -> Searching after this long
    public float acquireRadius = 0f;            // Searching: only people within this of the search centre (<= 0: anyone)
    public int confirmEstimates = 2;            // a new body (searching, or a new one in place of the followed one) must be seen this many estimates in a row
    public float baseGate = 0.5f;               // matching gate = baseGate + gateGrowth * seconds since the last match, at most reacquireRadius
    public float gateGrowth = 1.5f;             // m/s
    public float reacquireRadius = 3f;
    public float idGate = 1.5f;                 // the followed body is accepted within this of the prediction
    public float mergeRadius = 0.35f;           // skeletons closer than this are one body
    public float separation = 0.6f;             // bodies at least this far from the followed one, seen at the same time, are other people
    public float strangerGate = 0.3f;           // another person is only taken for the followed one within this of the prediction
    public float switchMargin = 0.5f;           // a body that is not the followed one has to be this much nearer to the prediction to replace it
    public float qualityWeight = 0.5f;          // metres of preference per unit of quality when choosing between bodies
    public float velocityStepGain = 2.5f;       // one estimate changes the velocity by at most maneuverAccel x this x the time since the last one

    const double InitialVelocitySigma = 1.0;    // m/s, what a new track knows about the person's velocity
    const double InnovationSigmas = 3.0;        // an estimate farther than this many (expected) sigmas from the prediction counts as if it were exactly this far
    const float ConfirmRadius = 0.6f;           // the "same place" of a body being confirmed over several estimates
    const float LockedQualitySlack = 0.15f;     // the followed id keeps representing its body unless another skeleton on it is this much sharper

    // ---------------- state ----------------

    struct Axis { public double p, v, p00, p01, p11; }   // position, velocity and their covariance
    struct Body { public Candidate rep; public bool hasLocked; }

    Axis ax, az;
    float height;
    int pendingCount; Vector2 pendingPos;                   // Searching: the body being confirmed
    int switchCount; Vector2 switchPos;                     // Tracking / Lost: a new body being confirmed as the same person
    readonly HashSet<int> strangers = new HashSet<int>();   // ids seen away from the followed person at the same time: other people
    readonly List<Body> bodies = new List<Body>();

    public State Current { get; private set; } = State.Searching;
    public int LockedId { get; private set; } = -1;
    public double LastMeasurementTime { get; private set; }
    public int Measurements { get; private set; }
    public int Acquisitions { get; private set; }
    public int Losses { get; private set; }
    public int Reacquisitions { get; private set; }
    public int PendingCount => pendingCount;

    // The followed point exactly as the newest matched estimate had it (world XZ): what you would use with no filter at all.
    public Vector2 LastMeasurement { get; private set; }

    // The filtered position and velocity at LastMeasurementTime (world XZ, m and m/s) and the followed point's height.
    public Vector2 Position => new Vector2((float)ax.p, (float)az.p);
    public Vector2 Velocity => new Vector2((float)ax.v, (float)az.v);
    public float Height => height;

    // How old the newest matched estimate's picture is: capture time to now. This is what the position is extrapolated over.
    public double Staleness(double now) => Math.Max(0.0, now - LastMeasurementTime);

    // How long ago the newest matched estimate ARRIVED. This is what decides Lost: the pipeline's own delay (capture -> arrival, 0.2 s on a
    // free GPU, 0.5-1.4 s measured on a loaded one) is not time the person was missing. Judged from the capture time, a pipeline whose delay
    // plus interval went over holdSeconds made the follower flicker between Lost and Tracking while the model was seeing the person at every estimate.
    public double SinceMatchArrived(double now) => Math.Max(0.0, now - lastMatchArrival);

    // The time between two estimates (smoothed), and how long without a match counts as lost.
    public double EstimateInterval => arrivalInterval;
    public double LostAfterSeconds => Math.Max(holdSeconds, holdIntervals * arrivalInterval);

    double lastArrival = double.NaN, lastMatchArrival, arrivalInterval, currentArrival;

    public void Reset()
    {
        Current = State.Searching;
        LockedId = -1;
        pendingCount = 0; switchCount = 0;
        strangers.Clear();
        lastArrival = double.NaN; arrivalInterval = 0.0;
    }

    void NoteArrival(double arrival)
    {
        currentArrival = arrival;
        if (!double.IsNaN(lastArrival))
        {
            double gap = arrival - lastArrival;
            if (gap > 0.0 && gap < 5.0)      // a longer silence is a stream that stopped and restarted, not an interval
            {
                gap = Math.Min(gap, 1.5);    // one hiccup must not make the next loss slow to notice
                arrivalInterval = arrivalInterval <= 0.0 ? gap : arrivalInterval + 0.2 * (gap - arrivalInterval);
            }
        }
        lastArrival = arrival;
    }

    void NoteStranger(int id)
    {
        if (strangers.Count > 512) strangers.Clear();      // ids only grow: a long session must not grow the set without end
        strangers.Add(id);
    }

    // ---------------- a new estimate ----------------

    // The people of an estimate captured at scene time t. Returns whether it gave a measurement of the followed person (in Searching:
    // whether it started one). searchCentre: where to look for someone while nobody is followed (the object's own position).
    // arrival: the scene time the estimate came in (default: t, as if there were no delay); see SinceMatchArrived.
    public bool Update(IReadOnlyList<Candidate> candidates, double t, Vector2 searchCentre, Func<int, bool> claimed, double arrival = double.NaN)
    {
        if (Current != State.Searching && t < LastMeasurementTime - 1e-3) Reset();    // the clock went back (a rewind): the track means nothing now
        NoteArrival(double.IsNaN(arrival) ? t : arrival);
        MergeBodies(candidates, claimed);
        return Current == State.Searching ? Search(t, searchCentre) : Associate(t);
    }

    // Skeletons on top of each other are one body; the sharper one stands for it, the followed id keeps it unless another is clearly sharper.
    void MergeBodies(IReadOnlyList<Candidate> cands, Func<int, bool> claimed)
    {
        bodies.Clear();
        bool following = Current != State.Searching;
        for (int i = 0; i < cands.Count; i++)
        {
            Candidate c = cands[i];
            bool isLocked = following && c.id == LockedId;
            if (!isLocked && claimed != null && claimed(c.id)) continue;
            int k = -1;
            for (int j = 0; j < bodies.Count; j++)
                if (Dist(bodies[j].rep.pos, c.pos) <= mergeRadius) { k = j; break; }
            if (k < 0) { bodies.Add(new Body { rep = c, hasLocked = isLocked }); continue; }

            Body b = bodies[k];
            b.hasLocked |= isLocked;
            float cq = c.quality + (isLocked ? LockedQualitySlack : 0f);
            float bq = b.rep.quality + (following && b.rep.id == LockedId ? LockedQualitySlack : 0f);
            if (cq > bq) b.rep = c;
            bodies[k] = b;
        }
    }

    bool Search(double t, Vector2 centre)
    {
        int best = -1; float bestCost = float.MaxValue;
        for (int i = 0; i < bodies.Count; i++)
        {
            float d = Dist(bodies[i].rep.pos, centre);
            if (acquireRadius > 0f && d > acquireRadius) continue;
            float cost = d - qualityWeight * bodies[i].rep.quality;
            if (cost < bestCost) { bestCost = cost; best = i; }
        }
        if (best < 0) { pendingCount = 0; return false; }

        Candidate c = bodies[best].rep;
        var here = new Vector2(c.pos.x, c.pos.z);
        if (pendingCount > 0 && Vector2.Distance(here, pendingPos) <= ConfirmRadius) pendingCount++;
        else pendingCount = 1;
        pendingPos = here;
        if (pendingCount < Mathf.Max(1, confirmEstimates)) return false;

        Lock(c, t);
        return true;
    }

    void Lock(Candidate c, double t)
    {
        double r2 = (double)measurementNoise * measurementNoise;
        ax = new Axis { p = c.pos.x, v = 0, p00 = r2, p01 = 0, p11 = InitialVelocitySigma * InitialVelocitySigma };
        az = new Axis { p = c.pos.z, v = 0, p00 = r2, p01 = 0, p11 = InitialVelocitySigma * InitialVelocitySigma };
        height = c.pos.y;
        LockedId = c.id;
        LastMeasurementTime = t;
        lastMatchArrival = currentArrival;
        LastMeasurement = new Vector2(c.pos.x, c.pos.z);
        Current = State.Tracking;
        pendingCount = 0; switchCount = 0;
        strangers.Clear();
        Measurements++;
        Acquisitions++;
    }

    // Which body of this estimate is the followed person?
    bool Associate(double t)
    {
        double dt = Math.Max(0.0, t - LastMeasurementTime);
        Vector2 expected = ExtrapolateTo(dt);
        float gate = Mathf.Min(reacquireRadius, baseGate + gateGrowth * (float)dt);

        // the followed body, and what stands away from it at the same time is somebody else
        int lockedBody = -1;
        for (int i = 0; i < bodies.Count; i++) if (bodies[i].hasLocked) { lockedBody = i; break; }
        float lockedD = lockedBody >= 0 ? Dist(bodies[lockedBody].rep.pos, expected) : 0f;
        bool lockedPlausible = lockedBody >= 0 && lockedD <= Mathf.Max(gate, idGate);
        if (lockedPlausible)
            for (int i = 0; i < bodies.Count; i++)
                if (i != lockedBody && Dist(bodies[i].rep.pos, bodies[lockedBody].rep.pos) >= separation) NoteStranger(bodies[i].rep.id);

        int best = lockedPlausible ? lockedBody : -1;
        float bestCost = lockedPlausible ? lockedD - qualityWeight * bodies[lockedBody].rep.quality : float.MaxValue;
        bool bestIsNew = false;
        for (int i = 0; i < bodies.Count; i++)
        {
            if (i == lockedBody) continue;
            Candidate c = bodies[i].rep;
            float d = Dist(c.pos, expected);
            bool stranger = strangers.Contains(c.id);
            if (d > (stranger ? strangerGate : gate)) continue;
            float cost = d + switchMargin - qualityWeight * c.quality;      // somebody else has to be this much closer than the followed body to replace it
            if (cost < bestCost) { bestCost = cost; best = i; bestIsNew = !stranger; }
        }

        if (best < 0) { switchCount = 0; return false; }
        Candidate m = bodies[best].rep;
        if (best == lockedBody || !bestIsNew) switchCount = 0;
        else if (!ConfirmNewBody(new Vector2(m.pos.x, m.pos.z))) return false;     // a new body, not yet confirmed: no measurement from this estimate

        Propagate(ref ax, dt);
        Propagate(ref az, dt);
        double vx0 = ax.v, vz0 = az.v;
        Correct(ref ax, m.pos.x);
        Correct(ref az, m.pos.z);
        LimitVelocityChange(vx0, vz0, dt);
        CapVelocity();
        height += 0.5f * (m.pos.y - height);

        LockedId = m.id;
        LastMeasurementTime = t;
        lastMatchArrival = currentArrival;
        LastMeasurement = new Vector2(m.pos.x, m.pos.z);
        Measurements++;
        if (Current == State.Lost) { Current = State.Tracking; Reacquisitions++; }
        return true;
    }

    // The same new place in `confirmEstimates` estimates in a row.
    bool ConfirmNewBody(Vector2 here)
    {
        if (switchCount > 0 && Vector2.Distance(here, switchPos) <= ConfirmRadius) switchCount++;
        else switchCount = 1;
        switchPos = here;
        if (switchCount < Mathf.Max(1, confirmEstimates)) return false;
        switchCount = 0;
        return true;
    }

    // ---------------- the filter ----------------

    // Moves one axis forward by dt seconds: position by velocity (for at most holdSeconds: a velocity older than that is not to be
    // trusted any further), covariance by the white-noise-acceleration model with spectral density maneuverAccel^2.
    void Propagate(ref Axis a, double dt)
    {
        if (dt <= 0.0) return;
        if (dt > holdSeconds)
        {
            a.p += a.v * holdSeconds;
            a.v = 0.0;
            a.p01 = 0.0;
            a.p11 = InitialVelocitySigma * InitialVelocitySigma;
        }
        else a.p += a.v * dt;

        double q = (double)maneuverAccel * maneuverAccel;
        double dt2 = dt * dt, dt3 = dt2 * dt;
        double n00 = a.p00 + 2.0 * dt * a.p01 + dt2 * a.p11 + q * dt3 / 3.0;
        double n01 = a.p01 + dt * a.p11 + q * dt2 / 2.0;
        double n11 = a.p11 + q * dt;
        a.p00 = n00; a.p01 = n01; a.p11 = n11;
    }

    // Measurement update. An innovation far outside what the filter expects (a limb taken for the body, a ghost pulling the match)
    // counts as if it were only InnovationSigmas sigmas away, so one bad estimate has a bounded pull on position AND velocity: it cannot
    // throw the capsule across the room, nor send it running ahead on a velocity that is not there.
    void Correct(ref Axis a, double z)
    {
        double r = (double)measurementNoise * measurementNoise;
        double y = z - a.p;
        double s = a.p00 + r;
        double lim = InnovationSigmas * Math.Sqrt(s);
        if (y > lim) y = lim; else if (y < -lim) y = -lim;
        double k0 = a.p00 / s, k1 = a.p01 / s;
        a.p += k0 * y;
        a.v += k1 * y;
        double n00 = (1.0 - k0) * a.p00, n01 = (1.0 - k0) * a.p01, n11 = a.p11 - k1 * a.p01;
        a.p00 = Math.Max(n00, 1e-9); a.p01 = n01; a.p11 = Math.Max(n11, 1e-9);
    }

    // A person's velocity cannot change faster than they can accelerate: whatever the filter concluded, one estimate moves the velocity by
    // at most maneuverAccel x velocityStepGain x the time since the previous one (at least 80 ms: two estimates of one frame).
    void LimitVelocityChange(double vx0, double vz0, double dt)
    {
        double max = (double)maneuverAccel * velocityStepGain * Math.Max(dt, 0.08);
        double dx = ax.v - vx0, dz = az.v - vz0;
        double d = Math.Sqrt(dx * dx + dz * dz);
        if (d <= max || d <= 0.0) return;
        double k = max / d;
        ax.v = vx0 + dx * k;
        az.v = vz0 + dz * k;
    }

    void CapVelocity()
    {
        double speed = Math.Sqrt(ax.v * ax.v + az.v * az.v);
        if (speed > maxSpeed && speed > 0.0)
        {
            double k = maxSpeed / speed;
            ax.v *= k; az.v *= k;
        }
    }

    // ---------------- time passes ----------------

    // Call every frame with the scene time now: Tracking -> Lost -> Searching by the time since the last match came in.
    public State Tick(double now)
    {
        double since = SinceMatchArrived(now);
        if (Current == State.Tracking && since > LostAfterSeconds) { Current = State.Lost; Losses++; }
        if (Current == State.Lost && since > forgetSeconds) Reset();
        return Current;
    }

    // Where the person is at scene time `now`: the filtered position moved on by velocity x age (at most `maxAhead` seconds, and never
    // past holdSeconds). Meaningful once a person is followed; while Lost it settles at the last guess.
    public Vector2 PredictXZ(double now, float maxAhead)
    {
        double dt = Math.Min(Staleness(now), Math.Min(Math.Max(0f, maxAhead), holdSeconds));
        return ExtrapolateTo(dt);
    }

    Vector2 ExtrapolateTo(double dt)
    {
        double step = Math.Min(Math.Max(0.0, dt), holdSeconds);
        double vx = ax.v, vz = az.v;
        double speed = Math.Sqrt(vx * vx + vz * vz);
        if (speed > maxSpeed && speed > 0.0) { vx *= maxSpeed / speed; vz *= maxSpeed / speed; }
        return new Vector2((float)(ax.p + vx * step), (float)(az.p + vz * step));
    }

    static float Dist(Vector3 a, Vector2 xz)
    {
        float dx = a.x - xz.x, dz = a.z - xz.y;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static float Dist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
