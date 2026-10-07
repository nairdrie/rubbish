using UnityEngine;

/// <summary>Shared camera aim and physical reach checks. Low fences are reachable over; solid walls are not.</summary>
public static class PoleTargeting
{
    public static float AimScore(Ray aim, Vector3 point, float radius = 0.35f)
    {
        Vector3 offset = point - aim.origin;
        float along = Vector3.Dot(offset, aim.direction.normalized);
        if (along <= 0f) return float.PositiveInfinity;
        float miss = (offset - aim.direction.normalized * along).magnitude;
        float allowance = Mathf.Max(0.2f, radius) + along * 0.34f; // generous ~19 degree aim assist
        if (miss > allowance) return float.PositiveInfinity;
        return miss / allowance + along * 0.004f;
    }

    public static bool WithinReach(Vector3 from, Vector3 to, float range) =>
        IsFinite(from) && IsFinite(to) && range > 0f && (to - from).sqrMagnitude <= range * range;

    static bool IsFinite(Vector3 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
        && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

    public static bool ClearPath(Vector3 from, Vector3 to, Transform actor, Transform target)
    {
        if (ClearSegment(from, to, actor, target)) return true;
        // The pole's advertised over-fence reach. Check every leg so roofs and tall walls still block it.
        float top = Mathf.Max(from.y, to.y) + 1.35f;
        Vector3 upFrom = new Vector3(from.x, top, from.z), upTo = new Vector3(to.x, top, to.z);
        return ClearSegment(from, upFrom, actor, target) && ClearSegment(upFrom, upTo, actor, target)
            && ClearSegment(upTo, to, actor, target);
    }

    public static bool ClearSegment(Vector3 from, Vector3 to, Transform actor, Transform target)
    {
        if (!IsFinite(from) || !IsFinite(to)) return false;
        Vector3 delta = to - from;
        if (delta.sqrMagnitude < 0.0001f) return true;
        foreach (var hit in Physics.RaycastAll(from, delta.normalized, delta.magnitude,
                     Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            var t = hit.collider.transform;
            if (actor != null && t.IsChildOf(actor) || target != null && t.IsChildOf(target)) continue;
            if (hit.collider.GetComponentInParent<Carryable>() != null || hit.collider.GetComponentInParent<PlayerController>() != null ||
                hit.collider.GetComponentInParent<Critter>() != null) continue;
            return false;
        }
        return true;
    }
}
