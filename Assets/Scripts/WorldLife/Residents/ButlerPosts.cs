using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §3.5): the Bluffs butlers. The static "Butler" prop NeighborhoodBuilder puts at
/// every estate's curb is hidden while WorldLife is awake (its renderers and its body collider; restored on sleep) and
/// a butler puppet stands at <see cref="StreetGraph.House.butlerPost"/> in its place. At a Butler Drop (07:00 and 17:00
/// ± 10 min, <see cref="CurbTrashSpawner.ButlerDropTime"/>) he bows, LIFTS THE SILVER CLOCHE and the bag(s) appear on
/// his platter with a sparkle; he sets them down (the real bags spawn in their curb slots) and bows again. No walking.
/// Deterministic from the record's minute-quantised time, like the walk-outs. The existing BUTLER DROP toast still fires.
/// </summary>
public sealed class ButlerPosts
{
    public const float BowEnd = 0.8f, LiftEnd = 1.8f, SetEnd = 2.9f, MaterializeAt = 2.5f, End = 3.7f;

    public sealed class Post
    {
        public int house;
        public string stopId;
        public Pose pose;
        public LifeId butler;
        public PickupStop stop;
        public Transform staticButler;
        public Renderer[] staticRenderers;
        public Collider[] staticColliders;
        public ResidentAgent agent;
        // the reveal
        public bool revealing;
        public double T;
        public CurbBag rec0, rec1;
        public bool done0, done1;
        public Transform sack0, sack1;
        public bool sparkled, said;
        public int reveals;
        public float pause;
        public bool truckNear;
        public float bowT = -1f;
        public bool searched;
        public bool HasBag0 => rec0 != null && !done0;
        public bool HasBag1 => rec1 != null && !done1;
    }

    readonly ResidentDirector dir;
    readonly List<Post> posts = new List<Post>(8);
    public IReadOnlyList<Post> Posts => posts;
    public int RevealsStarted { get; private set; }
    public int RevealsDone { get; private set; }
    public int Materialized { get; private set; }
    public readonly Dictionary<string, int> RevealsByStop = new Dictionary<string, int>();
    static readonly Collider[] probe = new Collider[24];

    public ButlerPosts(ResidentDirector dir) { this.dir = dir; }

    public void Wake(WorldLifeContext ctx)
    {
        posts.Clear();
        var g = ctx.Graph;
        for (int h = 0; h < (g.houses?.Length ?? 0); h++)
        {
            var hs = g.houses[h];
            if (hs.butlerPost.position == Vector3.zero) continue;
            var hh = ResidentCast.HouseholdOf(h);
            LifeId butler = LifeId.None;
            for (int m = 0; m < hh.count; m++)
                if (ResidentCast.Get(hh.first + m).archetype == ResidentArchetype.Butler) { butler = LifeId.Resident(hh.first + m); break; }
            if (!butler.IsValid) continue;
            posts.Add(new Post { house = h, stopId = hs.stopId, pose = hs.butlerPost, butler = butler });
        }
    }

    /// <summary>The builder's static butler at this post (static-batched; never moved or cloned): hide it.</summary>
    static void FindStaticButler(Post p)
    {
        int n = Physics.OverlapSphereNonAlloc(p.pose.position + Vector3.up * 1.3f, 0.8f, probe, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var t = probe[i].transform;
            while (t != null && t.name != "Butler") t = t.parent;
            if (t == null) continue;
            p.staticButler = t;
            p.staticRenderers = t.GetComponentsInChildren<Renderer>(true);
            p.staticColliders = t.GetComponentsInChildren<Collider>(true);
            SetStatic(p, false);
            return;
        }
    }

    static void SetStatic(Post p, bool visible)
    {
        if (p.staticRenderers != null) foreach (var r in p.staticRenderers) if (r != null) r.enabled = visible;
        if (p.staticColliders != null) foreach (var c in p.staticColliders) if (c != null) c.enabled = visible;
    }

    public void Sleep()
    {
        foreach (var p in posts)
        {
            if (p.revealing && !WorldLifeRunner.Leaving) MaterializeAll(p);
            ClearSacks(p);
            SetStatic(p, true);
        }
        posts.Clear();
    }

    Post PostFor(int house)
    {
        for (int i = 0; i < posts.Count; i++) if (posts[i].house == house) return posts[i];
        return null;
    }

    // ------------------------------------------------------------------ the PickupStop hook (Bluffs records)

    public bool Offer(PickupStop stop, CurbBag rec, int house)
    {
        var p = PostFor(house);
        var ctx = dir.Ctx;
        if (p == null || ctx == null) return false;
        p.stop = stop;
        if ((p.pose.position - ctx.Viewer).sqrMagnitude > WorldLifeTuning.Lod.WalkOutDistance * WorldLifeTuning.Lod.WalkOutDistance) return false;
        double T = Math.Floor(rec.putOutAtHours * 60.0) / 60.0;
        if (p.revealing)
        {
            if (p.T == T && p.rec1 == null && (ctx.Hours - T) * GameClock.SecondsPerGameHour < LiftEnd) { p.rec1 = rec; p.done1 = false; return true; }
            return false;
        }
        if ((ctx.Hours - T) * GameClock.SecondsPerGameHour >= MaterializeAt) return false;
        p.revealing = true; p.T = T; p.rec0 = rec; p.rec1 = null; p.done0 = p.done1 = false; p.sparkled = p.said = false; p.pause = 0f;
        p.reveals++;
        RevealsStarted++;
        RevealsByStop[p.stopId] = (RevealsByStop.TryGetValue(p.stopId, out int k) ? k : 0) + 1;
        return true;
    }

    // ------------------------------------------------------------------ tick

    public void Tick(WorldLifeContext ctx, float dt)
    {
        float showR = WorldLifeTuning.Lod.LiteDistance + 5f;
        for (int i = 0; i < posts.Count; i++)
        {
            var p = posts[i];
            // The static butler is found (and hidden) on the first tick, once the scene's colliders are all in.
            if (!p.searched) { p.searched = true; FindStaticButler(p); }
            bool near = (p.pose.position - ctx.Viewer).sqrMagnitude < showR * showR;
            if (!near && !p.revealing) { if (p.agent != null) { dir.ReleaseInternal(p.agent, p); p.agent = null; } continue; }
            if (p.stop == null) p.stop = PickupStop.Find(p.stopId);
            float yaw = p.pose.rotation.eulerAngles.y;
            PersonPose pose = PersonPose.Stand;
            float poseT = 0f;
            if (p.revealing) TickReveal(ctx, p, dt, ref pose, ref poseT, ref yaw);
            else IdleNod(p, ref pose, ref poseT, dt);
            var a = dir.ShowInternal(p.butler, AgentOwner.Butler, p, p.stopId, p.pose.position, yaw, pose, poseT, 1f, false, true);
            p.agent = a;
            if (a == null) continue;
            if (p.truckNear && !p.revealing) a.Look(NearestTruck(p.pose.position));
            SyncSacks(p, a, ctx);
        }
    }

    /// <summary>A polite bow when a truck rolls up (once per approach).</summary>
    void IdleNod(Post p, ref PersonPose pose, ref float poseT, float dt)
    {
        Vector3 tp = NearestTruck(p.pose.position);
        bool near = tp != Vector3.zero && (tp - p.pose.position).sqrMagnitude < 12f * 12f;
        if (near && !p.truckNear) p.bowT = 0f;
        p.truckNear = near;
        if (p.bowT >= 0f)
        {
            p.bowT += dt;
            pose = PersonPose.Bow; poseT = p.bowT / 0.9f;
            if (p.bowT >= 0.9f) p.bowT = -1f;
        }
    }

    static Vector3 NearestTruck(Vector3 at)
    {
        var trucks = TruckControllerV2.All;
        Vector3 best = Vector3.zero; float bd = float.MaxValue;
        for (int i = 0; i < trucks.Count; i++)
        {
            if (trucks[i] == null) continue;
            float d = (trucks[i].transform.position - at).sqrMagnitude;
            if (d < bd) { bd = d; best = trucks[i].transform.position; }
        }
        return best;
    }

    void TickReveal(WorldLifeContext ctx, Post p, float dt, ref PersonPose pose, ref float poseT, ref float yaw)
    {
        if (p.agent != null && p.agent.Interrupted) p.pause += dt;
        float t = (float)((ctx.Hours - p.T) * GameClock.SecondsPerGameHour) - p.pause;
        if (p.stop != null)
        {
            Vector3 toPile = p.stop.PileCenter - p.pose.position; toPile.y = 0f;
            if (toPile.sqrMagnitude > 0.01f) yaw = PuppetKit.YawOf(toPile);
        }
        if (t >= MaterializeAt && (p.HasBag0 || p.HasBag1)) MaterializeAll(p);
        if (t < BowEnd) { pose = PersonPose.Bow; poseT = t / BowEnd; }
        else if (t < LiftEnd)
        {
            pose = PersonPose.ClocheLift; poseT = (t - BowEnd) / (LiftEnd - BowEnd);
            if (!p.sparkled && poseT > 0.25f && p.agent != null && p.agent.rig != null)
            {
                p.sparkled = true;
                Vector3 at = p.agent.rig.handR.position + Vector3.up * 0.3f;
                Fx.Burst(at, new Color(1f, 0.92f, 0.5f), 14, 2.5f);
                Sfx.Play("sparkle", at, 0.8f, 1.2f);
                if (!p.said && dir.NearCamera(at, 40f)) { p.said = true; dir.Say(p.agent, ResidentLines.Butler, ResidentLines.Chat, true); }
            }
        }
        else if (t < SetEnd) { pose = PersonPose.ClocheSet; poseT = (t - LiftEnd) / (SetEnd - LiftEnd); }
        else if (t < End) { pose = PersonPose.Bow; poseT = (t - SetEnd) / (End - SetEnd); }
        else
        {
            if (p.HasBag0 || p.HasBag1) MaterializeAll(p);
            p.revealing = false;
            RevealsDone++;
        }
        // The cloche: lifted off the platter during the reveal (relative to the platter).
        if (p.agent != null && p.agent.rig != null)
        {
            var cl = p.agent.rig.t[(int)PartSlot.Prop2];
            float lift = pose == PersonPose.ClocheLift ? Mathf.SmoothStep(0f, 1f, poseT) : pose == PersonPose.ClocheSet ? 1f
                       : (t >= SetEnd && t < End ? 1f - Mathf.Clamp01((t - SetEnd) / 0.5f) : 0f);
            // Lifted up and out to his right, tipped open toward the street, so the face and the reveal stay in view.
            cl.localPosition = ResidentLook.ClochePos + ResidentLook.PlatterUp * (0.32f * lift) + new Vector3(0.36f * lift, 0f, 0f);
            cl.localRotation = Quaternion.Euler(ResidentLook.PlatterTilt, 0f, 0f) * Quaternion.Euler(0f, 0f, -55f * lift);
        }
    }

    void SyncSacks(Post p, ResidentAgent a, WorldLifeContext ctx)
    {
        float t = p.revealing ? (float)((ctx.Hours - p.T) * GameClock.SecondsPerGameHour) - p.pause : 0f;
        bool show = p.revealing && a.rig != null && a.act == ResidentAgent.Act.None && t > BowEnd + 0.25f;
        Sack(ref p.sack0, show && p.HasBag0, p.rec0, a, p.HasBag1 ? -0.17f : 0f);
        Sack(ref p.sack1, show && p.HasBag1, p.rec1, a, 0.17f);
    }

    static void Sack(ref Transform sack, bool want, CurbBag rec, ResidentAgent a, float x)
    {
        if (!want)
        {
            if (sack != null) { UnityEngine.Object.Destroy(sack.gameObject); sack = null; }
            return;
        }
        if (sack == null)
        {
            // On the platter: the platter is part Prop in hand space (tilted back to level), so the sack sits on it.
            // The platter part is scaled (0.62, 0.012, 0.62): undo that so the sack keeps its shape (~0.32 m tall).
            var holder = PuppetKit.Node(a.rig.t[(int)PartSlot.Prop], "PlatterSack");
            TrashBag.BuildSackVisual(rec.contents, holder);
            holder.localPosition = new Vector3(x / 0.62f, 1f, 0f);
            holder.localScale = new Vector3(0.58f / 0.62f, 0.58f / 0.012f, 0.58f / 0.62f);
            sack = holder;
            Juice.PopIn(sack, 0.3f);
        }
    }

    void MaterializeAll(Post p)
    {
        if (p.stop == null) p.stop = PickupStop.Find(p.stopId);
        if (p.stop != null)
        {
            if (p.HasBag0 && p.stop.MaterializeRecord(p.rec0, null, Vector3.zero, false) != null) Materialized++;
            if (p.HasBag1 && p.stop.MaterializeRecord(p.rec1, null, Vector3.zero, false) != null) Materialized++;
            Vector3 c = p.stop.PileCenter + Vector3.up * 0.4f;
            Fx.Burst(c, new Color(1f, 0.92f, 0.5f), 12, 2f);
        }
        p.done0 = p.rec0 != null; p.done1 = p.rec1 != null;
        ClearSacks(p);
    }

    static void ClearSacks(Post p)
    {
        if (p.sack0 != null) { UnityEngine.Object.Destroy(p.sack0.gameObject); p.sack0 = null; }
        if (p.sack1 != null) { UnityEngine.Object.Destroy(p.sack1.gameObject); p.sack1 = null; }
    }

    // ------------------------------------------------------------------ interruptions

    public void OnHit(Post p, ResidentAgent a, in NpcHit h)
    {
        if ((h.kind == NpcHitKind.Flatten || h.kind == NpcHitKind.ShortFlop) && p.revealing)
        {
            MaterializeAll(p);
            p.revealing = false;
        }
        if (h.kind == NpcHitKind.Flatten) dir.Say(a, ResidentLines.ButlerHit, ResidentLines.Yell, true);
    }
}
