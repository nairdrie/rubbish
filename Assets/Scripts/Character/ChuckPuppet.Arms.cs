using UnityEngine;

/// <summary>
/// Chuck's arms (Docs/Character.md sections 11 "ArmSplay" and 12 "Carry pose"): splay the animated arms out around the
/// belly (binary-searched clearance, raised instantly, relaxed smoothly), windmill in free fall, fling on release, and
/// bear-hug carried bags with analytic two-bone IK. All world-space and scale-safe; no allocations.
/// </summary>
public partial class ChuckPuppet
{
    struct Arm
    {
        public Transform upper, lower, hand, mitt, thumb;
        public float sign;      // -1 left, +1 right (rotating by sign*θ about root.forward moves the elbow out)
        public float splay;     // smoothed splay (deg)
    }

    Arm armL, armR;
    Transform upperLegL, lowerLegL, footL, upperLegR, lowerLegR, footR;
    RideRider rideRider;
    Transform rideTruck;
    int rideHold = -1;
    float ridePoseW, rideLat, rideBrake, rideSide, rideShuffle;
    // Clearance ellipsoids (rig metres): 0 belly, 1 shirt, 2 seat (the splay pushes lowered arms out of these),
    // 3 head ball, 4 cap crown (raised arms are swung out of these; the jump / in-air clips put the mitts on his face).
    const int BodyShapes = 3, AllShapes = 5;
    static readonly int[] ShapeParts = { ChuckPartsData.Overalls_Belly, ChuckPartsData.Shirt_Chest, ChuckPartsData.Overalls_Seat, ChuckPartsData.Head_Ball, ChuckPartsData.Cap_Crown };
    Transform[] ellT;
    Vector3[] ellS;
    readonly Matrix4x4[] ellM = new Matrix4x4[AllShapes];

    [Header("Arms: head clearance")]
    [Tooltip("Clearance margin (rig m) between the head/cap and the elbow, forearm and mitt.")]
    public float headMargin = 0.07f;
    [Tooltip("Largest extra outward swing (deg) the head-clearance search may add to a raised arm.")]
    public float headMaxExtra = 70f;

    void InitArms()
    {
        armL = new Arm { upper = upperArmL, lower = lowerArmL, hand = handL, mitt = P(ChuckPartsData.Glove_Mitt_L), thumb = P(ChuckPartsData.Glove_Thumb_L), sign = -1f, splay = splayBase };
        armR = new Arm { upper = upperArmR, lower = lowerArmR, hand = handR, mitt = P(ChuckPartsData.Glove_Mitt_R), thumb = P(ChuckPartsData.Glove_Thumb_R), sign = 1f, splay = splayBase };
        if (animator != null && animator.isHuman && animator.avatar != null)
        {
            upperLegL = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            lowerLegL = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            footL = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            upperLegR = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            lowerLegR = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            footR = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        }
        ellT = new Transform[AllShapes];
        ellS = new Vector3[AllShapes];
        for (int k = 0; k < AllShapes; k++) ellT[k] = P(ShapeParts[k]);
        RefreshArmShapes();
    }

    /// <summary>Clearance sizes follow the current rest scales (variants resize the cap crown).</summary>
    void RefreshArmShapes()
    {
        if (ellS == null || restScale == null) return;
        for (int k = 0; k < AllShapes; k++) ellS[k] = restScale[ShapeParts[k]];
    }

    void PoseArms(float dt)
    {
        for (int k = 0; k < AllShapes; k++) ellM[k] = ellT[k].worldToLocalMatrix;
        Vector3 axis = transform.forward;

        float free = 1f - carryW;
        float yawn = fidgetSplay; // stretch-yawn: up to +52 on top of the base
        SplayArm(ref armL, axis, dt, free, yawn, 0f);
        SplayArm(ref armR, axis, dt, free, yawn, Mathf.PI);

        if (carryW > 0.001f && carryHasTarget) CarryIK();
        if (fidgetPatW > 0.001f) TummyPatIK();
    }

    // ------------------------------------------------------------------ truck riding (F1)

    /// <summary>Called after the Animator and truck pinning. The same path is used for every network copy and by
    /// PreviewTick; no gameplay transform or network state is written.</summary>
    void PoseRide(float dt)
    {
        rideTruck = null;
        rideHold = -1;
        rideLat = rideBrake = rideShuffle = 0f;
        if (previewMode && previewRideTruck != null && previewRideHold >= 0 && previewRideHold < RideHolds.Count)
        {
            rideTruck = previewRideTruck;
            rideHold = previewRideHold;
        }
        else if (!previewMode)
        {
            if (rideRider == null) rideRider = GetComponent<RideRider>();
            if (rideRider != null && rideRider.TryGetRide(out var rt, out int hold) && rt != null &&
                hold >= 0 && hold < RideHolds.Count)
            {
                rideTruck = rt.transform;
                rideHold = hold;
                // Sway with the truck as this machine shows it (cosmetic; riders don't lean or lose grip any more).
                rideLat = rt.SwayAccel.x;
                rideBrake = rt.SwayAccel.y;
                rideShuffle = rideRider.ShuffleWeight;
            }
        }

        ridePoseW = Mathf.MoveTowards(ridePoseW, rideTruck != null ? 1f : 0f, dt / 0.18f);
        if (rideTruck == null || ridePoseW <= 0.001f) return;

        // Small knee bend, while keeping the boots at the Animator's foot positions on the step. The pelvis and
        // shoulders come toward the truck instead of standing bolt upright like the idle clip.
        if (upperLegL != null && lowerLegL != null && footL != null &&
            upperLegR != null && lowerLegR != null && footR != null)
        {
            Vector3 leftAt = footL.position, rightAt = footR.position;
            Quaternion leftRot = footL.rotation, rightRot = footR.rotation;
            hips.position += (rideTruck.forward * 0.025f - Vector3.up * 0.055f) * ridePoseW;
            Vector3 kneeFwd = transform.forward * 0.35f;
            TwoBone(upperLegL, lowerLegL, footL, leftAt,
                upperLegL.position + kneeFwd - transform.right * 0.08f, 0.995f);
            TwoBone(upperLegR, lowerLegR, footR, rightAt,
                upperLegR.position + kneeFwd + transform.right * 0.08f, 0.995f);
            footL.rotation = leftRot;
            footR.rotation = rightRot;
        }

        // A seat switch eases the ladder-side twist across instead of popping it at the new hold.
        float sideTarget = RideHolds.IsRung(rideHold) ? RideHolds.Side(rideHold) : 0f;
        rideSide = previewMode ? sideTarget : Mathf.MoveTowards(rideSide, sideTarget, dt / 0.3f);
        float side = rideSide;
        float roll = side * 4f - Mathf.Clamp(rideLat * 1.3f, -6f, 6f);
        float pitch = 6f + Mathf.Clamp(rideBrake * 0.55f, 0f, 4f);
        if (spine != null)
            spine.rotation = Quaternion.AngleAxis((-side * 7f) * ridePoseW, Vector3.up) *
                Quaternion.AngleAxis(roll * 0.45f * ridePoseW, transform.forward) *
                Quaternion.AngleAxis(pitch * ridePoseW, transform.right) * spine.rotation;
        chest.rotation = Quaternion.AngleAxis((-side * 8f) * ridePoseW, Vector3.up) *
            Quaternion.AngleAxis(roll * 0.55f * ridePoseW, transform.forward) *
            Quaternion.AngleAxis(5f * ridePoseW, transform.right) * chest.rotation;
        head.rotation = Quaternion.AngleAxis(-4f * ridePoseW, transform.right) * head.rotation;
    }

    void PoseRideGrip()
    {
        if (rideTruck == null || rideHold < 0) return;
        // A snag uses the ordinary carry/hug pose. Release the rail smoothly while the bag is in hand, then re-grip; the same
        // while shuffling to another hold after a seat switch.
        float gripW = ridePoseW * (1f - carryW) * (1f - rideShuffle);
        if (gripW <= 0.001f) return;
        GripArm(ref armL, RideHolds.Point(rideTruck, RideHolds.GripMetres(rideHold, true)), gripW);
        GripArm(ref armR, RideHolds.Point(rideTruck, RideHolds.GripMetres(rideHold, false)), gripW);
    }

    void GripArm(ref Arm a, Vector3 mittTarget, float w)
    {
        Quaternion u0 = a.upper.localRotation, l0 = a.lower.localRotation, h0 = a.hand.localRotation;
        Vector3 pole = a.upper.position + transform.right * (0.25f * a.sign) - transform.forward * 0.08f;
        for (int iter = 0; iter < 2; iter++)
        {
            Vector3 wrist = mittTarget - (a.mitt.position - a.hand.position);
            TwoBone(a.upper, a.lower, a.hand, wrist, pole, 0.995f);
        }
        ThumbUp(a);
        a.upper.localRotation = Quaternion.Slerp(u0, a.upper.localRotation, w);
        a.lower.localRotation = Quaternion.Slerp(l0, a.lower.localRotation, w);
        a.hand.localRotation = Quaternion.Slerp(h0, a.hand.localRotation, w);
    }

    /// <summary>
    /// Frontal-plane elevation of the upper arm in degrees: 0 hanging, 90 straight out to the side, 180 straight up,
    /// past 180 leaning in over the head. Rotating the upper arm by sign·θ about root.forward adds θ to it.
    /// </summary>
    float ArmElevation(in Arm a)
    {
        Vector3 ua = a.lower.position - a.upper.position;
        return Mathf.Atan2(Vector3.Dot(ua, transform.right) * a.sign, -Vector3.Dot(ua, transform.up)) * Mathf.Rad2Deg;
    }

    void SplayArm(ref Arm a, Vector3 axis, float dt, float free, float yawn, float flailPhase)
    {
        Vector3 S = a.upper.position;
        Vector3 E = a.lower.position, W = a.hand.position, M = a.mitt.position;

        // The splay pushes LOWERED arms out around the belly. On a raised arm (jump / in-air clips) the same rotation
        // would swing the mitt in over his head, so it fades out between 60 and 100 degrees of elevation.
        float elev = ArmElevation(a);
        float lowK = Mathf.Clamp01((100f - elev) / 40f);
        float baseSplay = splayBase * lowK;

        // Smallest extra splay in [0, max] that clears belly/chest/seat (4 binary-search steps).
        float extra = 0f;
        bool needForearm = false;
        if (!ArmClear(S, E, W, M, axis, a.sign, baseSplay))
        {
            if (!ArmClear(S, E, W, M, axis, a.sign, baseSplay + splayMaxExtra))
            {
                extra = splayMaxExtra;
                needForearm = true;
            }
            else
            {
                float lo = 0f, hi = splayMaxExtra;
                for (int i = 0; i < 4; i++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (ArmClear(S, E, W, M, axis, a.sign, baseSplay + mid)) hi = mid; else lo = mid;
                }
                extra = hi;
            }
        }
        float target = baseSplay + extra;
        // Raise instantly (never show a clipping frame), relax smoothly.
        a.splay = target >= a.splay ? target : Mathf.Lerp(a.splay, target, 1f - Mathf.Exp(-12f * dt));

        float angle = (a.splay + (Mathf.Max(0f, sSplayKick.x) + yawn) * lowK) * free;
        if (flailW > 0f)
        {
            // Straighten the elbows (the in-air clip has fists up by the ears) so the windmill is big and loopy.
            Vector3 fore = a.hand.position - a.lower.position, upper = a.lower.position - a.upper.position;
            if (fore.sqrMagnitude > 1e-6f && upper.sqrMagnitude > 1e-6f)
                a.lower.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(fore, upper), 0.75f * flailW) * a.lower.rotation;

            // Free-fall windmill: drive the elevation between 45 and 135 degrees (never over the head), left and right
            // half a cycle apart, whatever the in-air clip is doing underneath.
            float ph = clock * (2f * Mathf.PI * 5f) + flailPhase;
            float flailAngle = (90f + 45f * Mathf.Sin(ph)) - elev;
            angle = Mathf.Lerp(angle, flailAngle, flailW);
        }
        if (Mathf.Abs(angle) > 0.01f) a.upper.rotation = Quaternion.AngleAxis(a.sign * angle, axis) * a.upper.rotation;
        if (flailW > 0f)
        {
            // ...plus a forward/back swing a quarter-cycle out of step, so the mitts trace loops.
            float swing = 30f * flailW * Mathf.Cos(clock * (2f * Mathf.PI * 5f) + flailPhase);
            a.upper.rotation = Quaternion.AngleAxis(swing, transform.right) * a.upper.rotation;
        }

        if (needForearm && free > 0.01f)
        {
            // Forearm fallback: bend the elbow out too, up to 20 deg, until the mitt clears.
            Vector3 E2 = a.lower.position, W2 = a.hand.position, M2 = a.mitt.position;
            float f = 20f;
            if (ForearmClear(E2, W2, M2, axis, a.sign, 0f)) f = 0f;
            else
            {
                float lo = 0f, hi = 20f;
                for (int i = 0; i < 3; i++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (ForearmClear(E2, W2, M2, axis, a.sign, mid)) hi = mid; else lo = mid;
                }
                f = hi;
            }
            if (f > 0.01f) a.lower.rotation = Quaternion.AngleAxis(a.sign * f * free, axis) * a.lower.rotation;
        }

        ClearHead(ref a, axis);
    }

    /// <summary>
    /// Last word on the arm: if the elbow, forearm or mitt is inside the (margin-inflated) head or cap, swing the whole
    /// arm about the shoulder by the smallest angle that clears it without burying it in the belly. First choice: rotate
    /// the deepest offending point straight away from the head (raised arms go up-and-out in the jump / in-air clips;
    /// an arm reaching forward beside the ducked JumpStart head swings out sideways). Fallback: the frontal swing either
    /// way; last resort: the full swing plus folding the forearm. Runs every frame on whatever the clip + splay +
    /// windmill produced; allocation-free.
    /// </summary>
    void ClearHead(ref Arm a, Vector3 fwdAxis)
    {
        Vector3 S = a.upper.position, E = a.lower.position, W = a.hand.position, M = a.mitt.position;
        if (ArmOK(S, E, W, M, Quaternion.identity, false)) return;

        Vector3 away = AwayAxis(S, E, W, M);
        float d = float.NaN;
        Vector3 ax = away;
        if (away != Vector3.zero) d = SmallestClearSwing(S, E, W, M, away, headMaxExtra, true, false);
        if (float.IsNaN(d)) { ax = fwdAxis; d = SmallestClearSwing(S, E, W, M, fwdAxis, headMaxExtra, true, true); }
        if (float.IsNaN(d) && away != Vector3.zero) { ax = away; d = SmallestClearSwing(S, E, W, M, away, headMaxExtra, false, false); }
        if (float.IsNaN(d)) { ax = fwdAxis; d = SmallestClearSwing(S, E, W, M, fwdAxis, headMaxExtra, false, true); }
        bool forearm = float.IsNaN(d);
        if (forearm) { ax = away != Vector3.zero ? away : fwdAxis; d = headMaxExtra; }
        a.upper.rotation = Quaternion.AngleAxis(d, ax) * a.upper.rotation;
        if (!forearm) return;

        Vector3 E2 = a.lower.position, W2 = a.hand.position, M2 = a.mitt.position;
        Vector3 away2 = AwayAxis(E2, E2, W2, M2);
        if (away2 == Vector3.zero) return;
        float f = SmallestClearSwing(E2, E2, W2, M2, away2, 110f, false, false);
        a.lower.rotation = Quaternion.AngleAxis(float.IsNaN(f) ? 110f : f, away2) * a.lower.rotation;
    }

    /// <summary>
    /// Rotation axis (through the pivot) that moves the arm point deepest inside the head/cap straight away from that
    /// shape's centre; zero if the point sits on the line from the pivot through the centre.
    /// </summary>
    Vector3 AwayAxis(Vector3 pivot, Vector3 E, Vector3 W, Vector3 M)
    {
        Vector3 P = E, C = ellT[BodyShapes].position;
        float worst = float.MaxValue;
        Vector3 mid = 0.5f * (E + W);
        for (int k = BodyShapes; k < AllShapes; k++)
        {
            Vector3 c = ellT[k].position;
            float v;
            if ((v = EllNorm(k, E, headMargin)) < worst) { worst = v; P = E; C = c; }
            if ((v = EllNorm(k, mid, headMargin)) < worst) { worst = v; P = mid; C = c; }
            if ((v = EllNorm(k, W, headMargin)) < worst) { worst = v; P = W; C = c; }
            if ((v = EllNorm(k, M, headMargin + 0.01f)) < worst) { worst = v; P = M; C = c; }
        }
        Vector3 r = P - pivot;
        if (r.sqrMagnitude < 1e-6f) return Vector3.zero;
        Vector3 dir = P - C;
        dir -= r * (Vector3.Dot(dir, r) / r.sqrMagnitude);
        Vector3 n = Vector3.Cross(r, dir);
        return n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.zero;
    }

    /// <summary>
    /// Smallest swing (deg about axis through the pivot; positive only, or either way when bothSides) in [-max, max]
    /// that clears the head (and the body when withBody), 5 binary-search steps per side; NaN if none does.
    /// </summary>
    float SmallestClearSwing(Vector3 pivot, Vector3 E, Vector3 W, Vector3 M, Vector3 axis, float max, bool withBody, bool bothSides)
    {
        float best = float.NaN;
        for (int k = bothSides ? 0 : 1; k < 2; k++)
        {
            float dir = k == 0 ? -1f : 1f;
            if (!ArmOK(pivot, E, W, M, Quaternion.AngleAxis(dir * max, axis), withBody)) continue;
            float lo = 0f, hi = max;
            for (int i = 0; i < 5; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (ArmOK(pivot, E, W, M, Quaternion.AngleAxis(dir * mid, axis), withBody)) hi = mid; else lo = mid;
            }
            if (float.IsNaN(best) || hi < Mathf.Abs(best)) best = dir * hi;
        }
        return best;
    }

    /// <summary>
    /// After rotating the arm by q about the pivot: do the elbow, mid-forearm, wrist and mitt clear the head/cap
    /// (headMargin), and, when withBody, the belly/chest/seat (elbow/mitt margins)?
    /// </summary>
    bool ArmOK(Vector3 pivot, Vector3 E, Vector3 W, Vector3 M, Quaternion q, bool withBody)
    {
        Vector3 e = pivot + q * (E - pivot), w = pivot + q * (W - pivot), m = pivot + q * (M - pivot);
        float mg = headMargin;
        if (Inside(e, mg, BodyShapes, AllShapes) || Inside(0.5f * (e + w), mg, BodyShapes, AllShapes)
            || Inside(w, mg, BodyShapes, AllShapes) || Inside(m, mg + 0.01f, BodyShapes, AllShapes)) return false;
        if (!withBody) return true;
        return !Inside(e, elbowMargin, 0, BodyShapes) && !Inside(w, elbowMargin, 0, BodyShapes) && !Inside(m, mittMargin, 0, BodyShapes);
    }

    /// <summary>Normalised ellipsoid radius of p for shape k inflated by m (&lt; 1 = inside).</summary>
    float EllNorm(int k, Vector3 p, float m)
    {
        Vector3 d = ellM[k].MultiplyPoint3x4(p);
        Vector3 s = ellS[k];
        float x = d.x * s.x / (0.5f * s.x + m);
        float y = d.y * s.y / (0.5f * s.y + m);
        float z = d.z * s.z / (0.5f * s.z + m);
        return x * x + y * y + z * z;
    }

    bool ArmClear(Vector3 S, Vector3 E, Vector3 W, Vector3 M, Vector3 axis, float sign, float deg)
    {
        Quaternion q = Quaternion.AngleAxis(sign * deg, axis);
        return !Inside(S + q * (E - S), elbowMargin, 0, BodyShapes) && !Inside(S + q * (W - S), elbowMargin, 0, BodyShapes) && !Inside(S + q * (M - S), mittMargin, 0, BodyShapes);
    }

    bool ForearmClear(Vector3 E, Vector3 W, Vector3 M, Vector3 axis, float sign, float deg)
    {
        Quaternion q = Quaternion.AngleAxis(sign * deg, axis);
        return !Inside(E + q * (W - E), elbowMargin, 0, BodyShapes) && !Inside(E + q * (M - E), mittMargin, 0, BodyShapes);
    }

    /// <summary>Is p inside any of the margin-inflated ellipsoids [from, to)? (Σ (d·s)² / (0.5·s + m)² &lt; 1, rig metres.)</summary>
    bool Inside(Vector3 p, float m, int from, int to)
    {
        for (int k = from; k < to; k++)
            if (EllNorm(k, p, m) < 1f) return true;
        return false;
    }

    // ------------------------------------------------------------------ carry: the bear hug

    void CarryIK()
    {
        if (sharedGripPose) { SharedGripIK(); return; }
        Vector3 P = transform.TransformPoint(holdLocal);
        Vector3 up = Vector3.up;
        Vector3 f = transform.forward; f.y = 0f;
        f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        Vector3 r = new Vector3(f.z, 0f, -f.x);
        float halfW = Mathf.Clamp(holdExtents.x, 0.15f, 0.32f);
        float halfD = Mathf.Clamp(holdExtents.z, 0.14f, 0.30f);
        // Mitts grab the bag's upper back flanks.
        Vector3 back = -f * (0.70f * halfD) + up * 0.05f;
        Vector3 mL = P - r * (0.85f * halfW) + back;
        Vector3 mR = P + r * (0.85f * halfW) + back;
        HugArm(ref armL, mL, r, f, up);
        HugArm(ref armR, mR, r, f, up);
    }

    void SharedGripIK()
    {
        Vector3 toward = sharedGripTarget - transform.position;
        toward.y = 0f;
        if (toward.sqrMagnitude < 0.001f) return;
        toward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, toward);
        if (Vector3.Dot(transform.forward, toward) > 0.65f)
        {
            // Facing the bag: two hands around its near flank, with no single-carrier back offset.
            HugArm(ref armL, sharedGripTarget - right * 0.10f, right, toward, Vector3.up);
            HugArm(ref armR, sharedGripTarget + right * 0.10f, right, toward, Vector3.up);
        }
        else
        {
            // Side-by-side travel/pulling away: the nearer hand grips; the other keeps its walking/balance pose.
            // Reaching both arms across/behind the body made the old bear-hug IK look like a scarecrow.
            bool left = (armL.upper.position - sharedGripTarget).sqrMagnitude <= (armR.upper.position - sharedGripTarget).sqrMagnitude;
            if (left) HugArm(ref armL, sharedGripTarget, transform.right, transform.forward, Vector3.up);
            else HugArm(ref armR, sharedGripTarget, transform.right, transform.forward, Vector3.up);
        }
    }

    void HugArm(ref Arm a, Vector3 mittTarget, Vector3 r, Vector3 f, Vector3 up)
    {
        Quaternion u0 = a.upper.localRotation, l0 = a.lower.localRotation, h0 = a.hand.localRotation;
        Vector3 pole = a.upper.position + r * (0.30f * a.sign) - up * 0.10f - f * 0.05f;
        for (int iter = 0; iter < 2; iter++)
        {
            Vector3 wrist = mittTarget - (a.mitt.position - a.hand.position);
            TwoBone(a.upper, a.lower, a.hand, wrist, pole, 0.97f);
            ThumbUp(a);
        }
        float w = carryW;
        a.upper.localRotation = Quaternion.Slerp(u0, a.upper.localRotation, w);
        a.lower.localRotation = Quaternion.Slerp(l0, a.lower.localRotation, w);
        a.hand.localRotation = Quaternion.Slerp(h0, a.hand.localRotation, w);
    }

    /// <summary>Analytic two-bone IK in world space (bone lengths measured this frame, so scale-safe).</summary>
    static void TwoBone(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole, float maxReach)
    {
        Vector3 S = upper.position, E = lower.position, H = hand.position;
        float l1 = (E - S).magnitude, l2 = (H - E).magnitude;
        if (l1 < 1e-4f || l2 < 1e-4f) return;
        Vector3 toT = target - S;
        float d = toT.magnitude;
        if (d < 1e-4f) return;
        Vector3 dir = toT / d;
        d = Mathf.Clamp(d, Mathf.Abs(l1 - l2) + 1e-3f, maxReach * (l1 + l2));
        float cosA = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2f * l1 * d), -1f, 1f);
        float sinA = Mathf.Sqrt(1f - cosA * cosA);
        Vector3 pd = pole - S;
        pd -= dir * Vector3.Dot(pd, dir);
        if (pd.sqrMagnitude < 1e-6f) pd = Vector3.Cross(dir, Vector3.right);
        pd.Normalize();
        Vector3 elbow = S + dir * (l1 * cosA) + pd * (l1 * sinA);
        upper.rotation = Quaternion.FromToRotation(E - S, elbow - S) * upper.rotation;
        Vector3 E2 = lower.position, H2 = hand.position;
        Vector3 goal = S + dir * d;
        lower.rotation = Quaternion.FromToRotation(H2 - E2, goal - E2) * lower.rotation;
    }

    /// <summary>Roll the hand about the forearm axis so the rubber thumb points up (palm then faces the bag).</summary>
    static void ThumbUp(Arm a)
    {
        Vector3 axis = a.hand.position - a.lower.position;
        if (axis.sqrMagnitude < 1e-6f) return;
        axis.Normalize();
        Vector3 tv = a.thumb.position - a.mitt.position;
        tv -= axis * Vector3.Dot(tv, axis);
        Vector3 uv = Vector3.up - axis * axis.y;
        if (tv.sqrMagnitude < 1e-6f || uv.sqrMagnitude < 1e-6f) return;
        float ang = Vector3.SignedAngle(tv, uv, axis);
        a.hand.rotation = Quaternion.AngleAxis(ang * 0.8f, axis) * a.hand.rotation;
    }

    // ------------------------------------------------------------------ idle fidget: tummy pat (right hand)

    void TummyPatIK()
    {
        Vector3 f = transform.forward; f.y = 0f;
        f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        Vector3 r = new Vector3(f.z, 0f, -f.x);
        // Root-local rig point on the belly front, pats 3 cm in and out.
        Vector3 target = transform.TransformPoint(new Vector3(0.14f, 1.02f, 0.33f + fidgetPatOut));
        var a = armR;
        Quaternion u0 = a.upper.localRotation, l0 = a.lower.localRotation, h0 = a.hand.localRotation;
        Vector3 pole = a.upper.position + r * 0.35f - Vector3.up * 0.25f - f * 0.10f;
        for (int iter = 0; iter < 2; iter++)
        {
            Vector3 wrist = target - (a.mitt.position - a.hand.position);
            TwoBone(a.upper, a.lower, a.hand, wrist, pole, 0.98f);
        }
        float w = fidgetPatW;
        a.upper.localRotation = Quaternion.Slerp(u0, a.upper.localRotation, w);
        a.lower.localRotation = Quaternion.Slerp(l0, a.lower.localRotation, w);
        a.hand.localRotation = Quaternion.Slerp(h0, a.hand.localRotation, w);
    }
}
