using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Package F5 (Docs/Multiplayer.md §14.5), an additive hook on N2's HandsNet: a yard dog yanks a dangling bag out of a
// player's hands. The host takes the item as a flight it simulates itself (the same move as a holder disconnecting), so it
// stays ONE networked object the whole time: every machine sees it leave the hands and follow the host's stream while the
// dog runs with it, anyone can grab it back out of the dog's mouth (an ordinary grab of a flying item), and when the dog
// lets go it falls and settles at rest like any drop. Nothing is created or destroyed: bags are conserved.
public partial class HandsNet
{
    /// <summary>
    /// Host: take a networked trash bag <paramref name="client"/> is holding (not mid-heave, not in a tug). Returns the host's
    /// copy, now in a host-simulated flight, or null when there's nothing to take (or this isn't an active co-op host).
    /// </summary>
    public Carryable HostYank(ulong client, string why = "dog")
    {
        if (!IsServer || !active || !hooked) return null;
        if (TugOf(client).HasValue) return null;
        foreach (var nc in items.Values)
        {
            if (nc == null || !(nc.Item is TrashBag bag) || bag.HasBurst || !nc.HeldBy(client) || nc.WindowOpen || nc.PendingGrab) continue;
            if (nc.Item.State == CarryState.Consumed) return null;
            var s = Snapshot(nc);
            s.phase = (byte)HandsPhase.Flight;
            s.holder = Unity.Netcode.NetworkManager.ServerClientId;
            s.other = None;
            s.flags = 0;
            s.vel = Vector3.zero;
            HostSet(nc, s);
            NetAudit.Event("hands", new Dictionary<string, object>
            {
                { "op", "yank" }, { "id", nc.Id }, { "client", client }, { "why", why ?? "" }, { "key", nc.Desc.key.ToString() },
            });
            return nc.Item;
        }
        return null;
    }

    /// <summary>Whether a player has a trash bag that a yard dog can chase.</summary>
    public bool IsHoldingBag(ulong client)
    {
        foreach (var nc in items.Values)
            if (nc != null && nc.Item is TrashBag bag && !bag.HasBurst && nc.HeldBy(client)) return true;
        var carrier = CarrierOf(client);
        return carrier != null && carrier.Held is TrashBag localBag && !localBag.HasBurst;
    }

    /// <summary>Host: is this item one of ours, flying under the host's own simulation right now (e.g. in a dog's mouth)?</summary>
    public bool HostFlies(Carryable c)
    {
        if (!IsServer || c == null || c.NetId == 0) return false;
        var nc = Get(c.NetId);
        return nc != null && nc.St.Phase == HandsPhase.Flight && nc.St.holder == Unity.Netcode.NetworkManager.ServerClientId;
    }
}
