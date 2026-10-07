using UnityEngine;

/// <summary>
/// Put on a trigger collider (child of a receiver). Physically thrown/dropped carryables that enter it
/// are handed to the receiver found on this object or its parents. (Part of the Carry contract; lives
/// in its own file so it can be saved on prefabs.)
/// Refused items are reported to receivers implementing ICarryReceiverFeedback (rate-limited per item)
/// so they can buzz / bounce them back out.
/// </summary>
[RequireComponent(typeof(Collider))]
public class CarryReceiverTrigger : MonoBehaviour
{
    [Tooltip("Optional explicit receiver (a MonoBehaviour implementing ICarryReceiver). Defaults to the first one on this object or its parents.")]
    public MonoBehaviour receiverOverride;

    [Tooltip("Only take things a player just threw or dropped (not script-spawned props that happen to land here, " +
             "e.g. bags being unloaded out of the truck).")]
    public bool onlyPlayerReleased;

    /// <summary>Seconds after a player drop during which the item still counts as 'player released'.</summary>
    public const float PlayerReleaseWindow = 3f;

    ICarryReceiver receiver;

    /// <summary>The receiver this trigger feeds (may be null if none was found).</summary>
    public ICarryReceiver Receiver
    {
        get
        {
            if (receiver == null || (receiver is Object o && o == null)) receiver = FindReceiver();
            return receiver;
        }
    }

    void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        receiver = FindReceiver();
    }

    ICarryReceiver FindReceiver()
    {
        if (receiverOverride is ICarryReceiver ro) return ro;
        foreach (var mb in GetComponentsInParent<MonoBehaviour>(true))
            if (mb is ICarryReceiver r) return r;
        return null;
    }

    void OnTriggerEnter(Collider other)
    {
        var r = Receiver;
        if (r == null || other == null) return;
        var c = other.GetComponentInParent<Carryable>();
        if (c == null || c.State == CarryState.Carried || c.State == CarryState.Consumed || c.IsInScriptedFlight) return;
        // Co-op (N2): only the machine that simulates a networked item detects receiver hits; a guest asks the host.
        if (c.NetRemote) return;
        if (onlyPlayerReleased && c.State != CarryState.Thrown &&
            (c.LastHolder == null || Time.time - c.LastReleaseTime > PlayerReleaseWindow)) return;
        if (Carryable.TryNetDeliver(c, r)) return;
        if (r.CanAccept(c, out string reason))
        {
            r.Accept(c);
            return;
        }
        if (Time.time - c.lastRejectFeedbackTime < 0.6f) return;
        c.lastRejectFeedbackTime = Time.time;
        if (r is ICarryReceiverFeedback fb) fb.OnRejected(c, reason, true);
    }
}
