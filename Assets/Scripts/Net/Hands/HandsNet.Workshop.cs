public partial class HandsNet
{
    /// <summary>Host-only exact held identity/sequence gate. A replay, shared grip or pending ownership cannot donate twice.</summary>
    public bool TryWorkshopHeld(ulong client, uint id, uint sequence, out LooseItem item)
    {
        item = null;
        if (!IsServer || !Active) return false;
        var carry = Get(id); var body = BodyOf(client); var player = PlayerBody.For(client);
        if (carry == null || carry.Announced || carry.PendingGone || carry.PendingGrab || carry.WindowOpen ||
            carry.St.seq != sequence || !carry.HeldBy(client) || !(carry.Item is LooseItem loose) ||
            loose.State == CarryState.Consumed || body.State != BodyState.Normal || body.carrying != None ||
            player == null || player.Player == null || player.Player.IsDriving || player.Player.IsPlayingChess ||
            CompactorCube.IsCubed(client) || CompactorCube.IsSuctioning(client) || TugOf(client).HasValue ||
            player.Carrier != null && player.Carrier.ExternalControl) return false;
        item = loose; return true;
    }
}
