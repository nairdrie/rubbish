using System.Collections.Generic;
using UnityEngine;

public enum ZoneMode { OnFoot, Driving, Either }

/// <summary>
/// Makes an IInteractable available whenever the local player (or the truck they're driving) is
/// inside a box volume — no line of sight needed. Use for doors you drive through, unload levers,
/// compactor buttons, etc. Checked by PlayerController every frame via CollectCandidates.
/// </summary>
public class InteractionZone : MonoBehaviour
{
    [Tooltip("Box center in this object's local space.")]
    public Vector3 center = new Vector3(0, 1.5f, 0);
    [Tooltip("Box size in this object's local space.")]
    public Vector3 size = new Vector3(4, 3, 4);
    public ZoneMode mode = ZoneMode.Either;
    [Tooltip("Candidate score. Line-of-sight hits score 0..2, the truck's Exit option 0.25.")]
    public float score = 1f;
    [Tooltip("Optional IInteractable to expose. Defaults to the first IInteractable on this GameObject.")]
    public MonoBehaviour targetOverride;

    static readonly List<InteractionZone> active = new List<InteractionZone>();
    public static IReadOnlyList<InteractionZone> Active => active;

    IInteractable target;
    public IInteractable Target
    {
        get
        {
            if (target == null)
                target = targetOverride as IInteractable ?? GetComponent<IInteractable>();
            return target;
        }
    }

    void OnEnable() { if (!active.Contains(this)) active.Add(this); }
    void OnDisable() { active.Remove(this); }

    public bool Contains(Vector3 worldPos)
    {
        Vector3 local = transform.InverseTransformPoint(worldPos) - center;
        Vector3 half = size * 0.5f;
        return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
    }

    public bool Matches(bool driving) =>
        mode == ZoneMode.Either || (driving ? mode == ZoneMode.Driving : mode == ZoneMode.OnFoot);

    /// <summary>Adds a candidate for every zone the player (or their truck) is inside.</summary>
    public static void CollectCandidates(PlayerController player, List<InteractionCandidate> results)
    {
        if (player == null) return;
        bool driving = player.IsDriving && player.currentTruck != null;
        Vector3 refPos = driving ? player.currentTruck.transform.position : player.transform.position;
        for (int i = 0; i < active.Count; i++)
        {
            var z = active[i];
            if (z == null || !z.isActiveAndEnabled || !z.Matches(driving)) continue;
            var t = z.Target;
            if (t == null || !t.CanInteract()) continue;
            if (!z.Contains(refPos)) continue;
            results.Add(new InteractionCandidate { Target = t, Score = z.score });
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => active.Clear();

    void OnDrawGizmos()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = mode == ZoneMode.Driving ? new Color(1f, 0.5f, 0f, 0.35f)
                     : mode == ZoneMode.OnFoot ? new Color(0f, 0.8f, 1f, 0.35f)
                     : new Color(0.6f, 1f, 0.2f, 0.35f);
        Gizmos.DrawWireCube(center, size);
    }
}
