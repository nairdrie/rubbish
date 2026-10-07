using System;

public partial class HandsNet
{
    /// <summary>Host machine-only release of body links before a compactor takes over the owner-authored transform.</summary>
    internal void HostPrepareForCompactor(ulong client, float seconds)
    {
        if (!IsServer || !IsSpawned) return;
        HostEnsureBody(client);
        var b = BodyOf(client);
        if (b.State == BodyState.Carried) HostClearCarrier(b.other, client);
        if (b.carrying != None) HostDropPlayer(client, b.carrying);
        CancelTugsOf(client);
        b = BodyOf(client);
        b.state = (byte)BodyState.Normal;
        b.other = None;
        b.carrying = None;
        b.until = 0;
        b.immuneUntil = Math.Max(b.immuneUntil, ServerNow + seconds);
        b.noGrabUntil = Math.Max(b.noGrabUntil, ServerNow + seconds);
        HostSetBody(b);
        punchCombos.Remove(client);
        wriggles.Remove(client);
    }
}
