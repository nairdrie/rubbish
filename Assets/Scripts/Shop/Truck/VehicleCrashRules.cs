using UnityEngine;

/// <summary>Bounded cartoon vehicle response. No ownership, timing, saves or random rolls.</summary>
public static class VehicleCrashRules
{
    public const float MinimumClosingSpeed = 2.2f, MaximumClosingSpeed = 45f, PairCooldown = .7f;
    public const float MaximumKick = 6f, MaximumYawKick = .65f, ParkingYieldSeconds = .85f;

    public static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    public static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

    public static float KickSpeed(float closingSpeed) => !Finite(closingSpeed) || closingSpeed < MinimumClosingSpeed
        ? 0f : Mathf.Clamp(closingSpeed * .28f, .8f, MaximumKick);

    public static Vector3 TruckKick(Vector3 away, float closingSpeed, float ownMass, float otherMass)
    {
        if (!Finite(away) || !Finite(ownMass) || !Finite(otherMass)) return Vector3.zero;
        away.y = 0f;
        if (away.sqrMagnitude < .001f) return Vector3.zero;
        float kick = KickSpeed(closingSpeed);
        if (kick <= 0f) return Vector3.zero;
        float share = 2f * Mathf.Max(1f, otherMass) / (Mathf.Max(1f, ownMass) + Mathf.Max(1f, otherMass));
        return away.normalized * Mathf.Min(MaximumKick, kick * share) +
               Vector3.up * Mathf.Clamp((closingSpeed - 5f) * .025f, 0f, .55f);
    }

    public static bool TrucksNear(Vector3 a, Quaternion ar, Vector3 b, Quaternion br, float slack)
        => TrucksNearBounds(a, ar, TruckControllerV2.BodyBoxMetres, b, br, TruckControllerV2.BodyBoxMetres, slack);

    public static bool TrucksNearBounds(Vector3 a, Quaternion ar, Bounds aBox, Vector3 b, Quaternion br, Bounds bBox, float slack)
    {
        if (!Finite(a) || !Finite(b)) return false;
        Vector3 ac = a + ar * aBox.center, bc = b + br * bBox.center;
        if (Mathf.Abs(ac.y - bc.y) > aBox.extents.y + bBox.extents.y + slack) return false;
        Vector3 d = bc - ac;
        Vector3 ax = ar * Vector3.right, az = ar * Vector3.forward, bx = br * Vector3.right, bz = br * Vector3.forward;
        return !Separated(ax, d, ax, az, bx, bz, aBox.extents, bBox.extents, slack) &&
               !Separated(az, d, ax, az, bx, bz, aBox.extents, bBox.extents, slack) &&
               !Separated(bx, d, ax, az, bx, bz, aBox.extents, bBox.extents, slack) &&
               !Separated(bz, d, ax, az, bx, bz, aBox.extents, bBox.extents, slack);
    }

    public static bool CarNearTruck(Vector3 car, Vector3 halfExtents, Vector3 truck, Quaternion rotation)
        => CarNearTruckBounds(car, halfExtents, truck, rotation, TruckControllerV2.BodyBoxMetres);

    public static bool CarNearTruckBounds(Vector3 car, Vector3 halfExtents, Vector3 truck, Quaternion rotation, Bounds box)
    {
        if (!Finite(car) || !Finite(halfExtents) || !Finite(truck)) return false;
        Vector3 p = Quaternion.Inverse(rotation) * (car + Vector3.up * halfExtents.y - truck) - box.center;
        // Rounded car footprint plus bounded interpolation slack; height rejects separate storeys/interior copies.
        float radius = Mathf.Min(2.8f, new Vector2(halfExtents.x, halfExtents.z).magnitude);
        return Mathf.Abs(p.x) <= box.extents.x + radius + .6f && Mathf.Abs(p.z) <= box.extents.z + radius + .8f &&
               Mathf.Abs(p.y) <= box.extents.y + halfExtents.y + .5f;
    }

    static bool Separated(Vector3 axis, Vector3 d, Vector3 ax, Vector3 az, Vector3 bx, Vector3 bz, Vector3 ae, Vector3 be, float slack)
    {
        axis.y = 0f;
        if (axis.sqrMagnitude < .01f) return false;
        axis.Normalize();
        float radius = ae.x * Mathf.Abs(Vector3.Dot(axis, ax)) + be.x * Mathf.Abs(Vector3.Dot(axis, bx)) +
                       ae.z * Mathf.Abs(Vector3.Dot(axis, az)) + be.z * Mathf.Abs(Vector3.Dot(axis, bz));
        return Mathf.Abs(Vector3.Dot(d, axis)) > radius + slack;
    }

    public static bool MaySnapTraffic(bool shunting, bool holding, float slideSquared, float viewerDistance) =>
        !shunting && !holding && slideSquared < .01f && viewerDistance > WorldLifeTuning.Lod.LiteDistance;

    public static bool MayReleaseParkedCrash(bool leaving, float farSeconds, bool shunting, bool holding) =>
        !shunting && !holding && (leaving || farSeconds > 10f);

    /// <summary>A reported car must lie on its scheduled lane/overtake corridor within a bounded timetable window.</summary>
    public static bool TryRoutePoint(Vector3 point, Vector3[] route, float[] arc, float minArc, float maxArc, out float foundArc)
    {
        foundArc = 0f;
        if (!Finite(point) || route == null || arc == null || route.Length < 2 || arc.Length != route.Length ||
            !Finite(minArc) || !Finite(maxArc) || maxArc < minArc) return false;
        float best = 7.5f * 7.5f; // five-metre overtakes plus the bounded recovery slide, NOT a free world-space radius
        bool found = false;
        for (int i = 0; i + 1 < route.Length; i++)
        {
            if (arc[i + 1] < minArc || arc[i] > maxArc || !Finite(route[i]) || !Finite(route[i + 1])) continue;
            Vector3 d = route[i + 1] - route[i];
            float length = arc[i + 1] - arc[i];
            if (length <= .001f || d.sqrMagnitude < .0001f) continue;
            float lo = Mathf.Clamp01((minArc - arc[i]) / length), hi = Mathf.Clamp01((maxArc - arc[i]) / length);
            float u = Mathf.Clamp(Vector3.Dot(point - route[i], d) / d.sqrMagnitude, lo, hi);
            Vector3 at = route[i] + d * u;
            if (Mathf.Abs(point.y - at.y) > 2f) continue;
            Vector3 offset = point - at; offset.y = 0f;
            if (offset.sqrMagnitude > best) continue;
            best = offset.sqrMagnitude; foundArc = Mathf.Lerp(arc[i], arc[i + 1], u); found = true;
        }
        return found;
    }

    public static float CarSlideDistance(float speed) => Finite(speed) ? Mathf.Clamp(speed * .32f, 1.1f, 6f) : 0f;
    public static float CarYawKick(float speed) => Finite(speed) ? Mathf.Lerp(24f, 135f, Mathf.InverseLerp(2.5f, 22f, speed)) : 0f;
    public static float CarSlideFraction(float seconds)
    {
        float u = Mathf.Clamp01(seconds / 1.15f);
        return 1f - (1f - u) * (1f - u);
    }
    public static float CarYaw(float seconds, float kick)
    {
        if (seconds <= 0f) return 0f;
        if (seconds < .6f) return kick * Mathf.Sin(seconds / .6f * Mathf.PI * .5f);
        float u = Mathf.Clamp01((seconds - .6f) / 2.8f);
        return kick * (1f - u * u * (3f - 2f * u));
    }
}
