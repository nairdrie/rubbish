using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Compactor cube (F3, Docs/Multiplayer.md §14.3): a player shoved / thrown / jumped into a shop's hopper when someone hits SMASH
/// pops out as a squished cartoon cube that waddles for <see cref="Seconds"/> s. Harmless: no damage, no pay, never traps (the
/// cube is popped out onto the bale landing outside the machine), and a cube can't be compacted again until it's back to normal.
/// The machine that pressed SMASH detects who was in the chamber; in co-op the host records it (<see cref="BagTellsNet"/>) and
/// every machine draws the cube (<see cref="PlayerTells"/>). Solo keeps a local record.
/// </summary>
public static class CompactorCube
{
    public const float Seconds = 25f;
    public const float SuctionSeconds = 2.6f;

    static readonly Dictionary<ulong, double> soloCubes = new Dictionary<ulong, double>();
    sealed class SuctionBatch
    {
        public Compactor machine;
        public readonly List<ulong> victims = new List<ulong>();
    }
    static readonly Dictionary<uint, SuctionBatch> suctionBatches = new Dictionary<uint, SuctionBatch>();
    static uint nextSuction;

    /// <summary>Raised on every machine when someone becomes a cube or stops being one.</summary>
    public static event System.Action Changed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        soloCubes.Clear();
        suctionBatches.Clear();
        nextSuction = 0;
        Changed = null;
    }

    internal static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (System.Exception e) { Debug.LogException(e); }
    }

    static BagTellsNet Net => BagTellsNet.Instance != null && BagTellsNet.Instance.IsSpawned ? BagTellsNet.Instance : null;

    /// <summary>The shared clock cubes are timed on (server time in co-op).</summary>
    public static double Now => Net != null ? Net.ServerNow : Time.timeAsDouble;

    /// <summary>When this client stops being a cube (≤ <see cref="Now"/> = not a cube).</summary>
    public static double CubedUntil(ulong client)
    {
        var n = Net;
        if (n != null) return n.CubedUntil(client);
        return soloCubes.TryGetValue(client, out var t) ? t : 0;
    }

    public static bool IsCubed(ulong client) => CubedUntil(client) > Now;

    /// <summary>Seconds of cube left for this client (0 when not a cube).</summary>
    public static float Remaining(ulong client) => Mathf.Max(0f, (float)(CubedUntil(client) - Now));

    /// <summary>Only this shop's physical occupants: not their crew elsewhere, another shop or the street.</summary>
    public static bool SameShop(Vector3 machine, Vector3 player, bool oneWorld)
    {
        int lot = DepotRowLayout.InteriorAt(machine);
        if (lot >= 0) return DepotRowLayout.InteriorAt(player) == lot;
        // Legacy solo ShopScene is one room. Never turn a street machine into a world-wide vacuum.
        if (oneWorld) return false;
        Vector3 d = player - machine;
        return Mathf.Abs(d.x) <= 24f && Mathf.Abs(d.z) <= 24f && Mathf.Abs(d.y) <= 12f;
    }

    public static bool IsSuctioning(ulong client)
    {
        var p = PlayerTells.For(client);
        var pull = p != null ? p.GetComponent<CompactorPlayerPull>() : null;
        return pull != null && pull.IsActive;
    }

    /// <summary>All occupants, regardless of business affiliation; parked drivers/riders/chess players are included.</summary>
    public static List<PlayerTells> PlayersInShop(Compactor c, List<PlayerTells> into)
    {
        into.Clear();
        if (c == null) return into;
        foreach (var p in PlayerTells.All)
            if (p != null && p.Player != null && !IsCubed(p.ClientId) && !IsSuctioning(p.ClientId) &&
                SameShop(c.transform.position, p.transform.position, DepotRowWorld.Active)) into.Add(p);
        return into;
    }

    /// <summary>Host/solo only. Capture/revalidate occupants once, start owner-driven pulls, return a one-use slam token.</summary>
    public static uint BeginSuction(Compactor c, List<PlayerTells> victims, float seconds = SuctionSeconds)
    {
        var n = Net;
        if (c == null || victims == null || (n != null && !n.IsServer)) return 0;
        var batch = new SuctionBatch { machine = c };
        foreach (var p in victims)
            if (p != null && p.Player != null && !IsCubed(p.ClientId) && !IsSuctioning(p.ClientId) &&
                SameShop(c.transform.position, p.transform.position, DepotRowWorld.Active) && !batch.victims.Contains(p.ClientId))
                batch.victims.Add(p.ClientId);
        if (batch.victims.Count == 0) return 0;
        uint token = ++nextSuction;
        if (token == 0) token = ++nextSuction;
        suctionBatches[token] = batch;
        seconds = Mathf.Clamp(seconds, 0.5f, 6f);
        Vector3 at = c.chamber != null ? c.chamber.position : c.transform.position;
        for (int i = 0; i < batch.victims.Count; i++)
        {
            ulong target = batch.victims[i];
            Vector3 landing = Landing(c, i, batch.victims.Count);
            if (n != null) n.BeginCompactorPull(token, target, at, landing, seconds);
            else CompactorPlayerPull.Begin(PlayerTells.For(target), token, at, landing, Now + seconds);
        }
        return token;
    }

    /// <summary>Consume exactly the matching host batch at slam; late replicated owner poses cannot reject the cube.</summary>
    public static void FinishSuction(Compactor c, uint token)
    {
        var n = Net;
        if (c == null || token == 0 || (n != null && !n.IsServer) || !suctionBatches.TryGetValue(token, out var batch) || batch.machine != c) return;
        suctionBatches.Remove(token);
        for (int i = 0; i < batch.victims.Count; i++)
        {
            ulong target = batch.victims[i];
            var p = PlayerTells.For(target);
            if (p == null || IsCubed(target)) continue;
            Vector3 landing = Landing(c, i, batch.victims.Count);
            if (n != null) n.FinishCompactorPull(token, target, landing);
            else { soloCubes[target] = Now + Seconds; RaiseChanged(); CompactorPlayerPull.Finish(p, token, landing, true); }
        }
    }

    /// <summary>Interrupted machines always release their occupants; an old cancellation cannot end a newer pull.</summary>
    public static void CancelSuction(uint token)
    {
        var n = Net;
        if (token == 0 || (n != null && !n.IsServer) || !suctionBatches.TryGetValue(token, out var batch)) return;
        suctionBatches.Remove(token);
        for (int i = 0; i < batch.victims.Count; i++)
        {
            ulong target = batch.victims[i];
            if (n != null) n.CancelCompactorPull(token, target);
            else CompactorPlayerPull.Cancel(PlayerTells.For(target), token);
        }
    }

    static Vector3 Landing(Compactor c, int index, int count)
    {
        Vector3 at = c.baleLanding != null ? c.baleLanding.position : c.transform.position + c.transform.forward * 1.8f;
        // Give each waddle-box its own landing lane, instead of four overlapping CharacterControllers.
        return at + c.transform.right * ((index - (count - 1) * 0.5f) * 0.9f);
    }

    /// <summary>Players standing in this compactor's chamber right now (not already cubes).</summary>
    public static List<PlayerTells> PlayersIn(Compactor c, List<PlayerTells> into)
    {
        into.Clear();
        if (c == null || c.chamber == null) return into;
        var ch = c.chamber;
        Vector3 half = Vector3.Scale(c.chamberSize * 0.5f, new Vector3(Mathf.Abs(ch.lossyScale.x), Mathf.Abs(ch.lossyScale.y), Mathf.Abs(ch.lossyScale.z)));
        foreach (var p in PlayerTells.All)
        {
            if (p == null || p.Player == null || p.Player.IsDriving) continue;
            if (IsCubed(p.ClientId) || IsSuctioning(p.ClientId)) continue;
            // Feet or chest inside the chamber (with a bit of slack: a body is wider than its pivot).
            if (Inside(ch, half, p.transform.position + Vector3.up * 0.3f) || Inside(ch, half, p.transform.position + Vector3.up * 0.9f))
                into.Add(p);
        }
        return into;
    }

    static bool Inside(Transform ch, Vector3 half, Vector3 world)
    {
        Vector3 l = Quaternion.Inverse(ch.rotation) * (world - ch.position);
        return Mathf.Abs(l.x) <= half.x + 0.35f && Mathf.Abs(l.z) <= half.z + 0.35f && l.y >= -half.y - 0.4f && l.y <= half.y + 1.2f;
    }

    /// <summary>True when somebody (not yet a cube) is in this compactor's chamber.</summary>
    public static bool AnyPlayerIn(Compactor c)
    {
        if (c == null || PlayerTells.All.Count == 0) return false;
        return PlayersIn(c, scratch).Count > 0;
    }

    static readonly List<PlayerTells> scratch = new List<PlayerTells>();

    /// <summary>SMASH came down on these players (called by the machine that pressed it, right at the slam).</summary>
    public static void Squish(Compactor c, List<PlayerTells> victims)
    {
        if (c == null || victims == null || victims.Count == 0 || (Net != null && !Net.IsServer)) return;
        Vector3 at = c.chamber != null ? c.chamber.position : c.transform.position;
        for (int i = 0; i < victims.Count; i++)
        {
            var p = victims[i];
            if (p == null || IsCubed(p.ClientId)) continue;
            var n = Net;
            Vector3 landing = Landing(c, i, victims.Count);
            if (n != null) n.CubeFromMachine(p.ClientId, at, landing);
            else
            {
                soloCubes[p.ClientId] = Now + Seconds;
                RaiseChanged();
                if (p.IsLocal) OwnerPopOut(landing);
            }
        }
        var local = PlayerTells.LocalTells;
        if (local == null || !SameShop(c.transform.position, local.transform.position, DepotRowWorld.Active)) return;
        // Everyone at the machine sees it: SQUISH.
        Sfx.Play("squeak", at, 1f, 0.5f);
        Sfx.Play("pop", at, 0.9f, 0.6f);
        GameHUD.FloatingText(at + Vector3.up * 1.4f, victims.Count > 1 ? "DOUBLE CUBE!" : "SQUISH!", new Color(1f, 0.6f, 0.9f), 1.6f);
        Fx.Burst(at + Vector3.up * 0.6f, new Color(1f, 0.75f, 0.9f), 26, 5f);
        Juice.Shake(0.3f, 0.25f);
        if (victims.Count > 0 && Time.unscaledTime - lastToast > 6f)
        {
            lastToast = Time.unscaledTime;
            GameHUD.Toast(victims[0].IsLocal ? "You've been compacted. You're a cube now. Waddle with pride." : "Someone got compacted! Look at that cube go.", ToastStyle.Rare, 3f);
        }
        NetAudit.Event("tells", new Dictionary<string, object> { { "op", "squish" }, { "n", victims.Count } });
    }

    static float lastToast = -99f;

    /// <summary>Owner: you're a cube: pop out of the machine onto the floor (never trapped).</summary>
    internal static void OwnerPopOut(Vector3 at)
    {
        var me = PlayerTells.LocalTells;
        if (me == null) return;
        me.PopOutAsCube(at);
    }
}
