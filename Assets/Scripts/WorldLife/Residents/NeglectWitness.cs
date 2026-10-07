using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §7.2): neglected piles get a witness. While a house
/// <see cref="PickupStop.IsNeglected"/> and the camera is within 60 m, one household member walks out, plants a
/// hand-lettered sign ("IT'S BEEN 2 DAYS", "SMELL THAT?", "HOA NOTIFIED") beside the pile and stands there with arms
/// crossed, tapping a foot. Clear the curb: "FINALLY!" and they go back in. A Pile Goblin moves in: they yelp and flee.
/// A free, readable neglect cue; local presentation from this machine's camera (it may differ between machines).
/// </summary>
public sealed class NeglectWitness
{
    public const int MaxWitnesses = 3;
    const float LeaveDistance = 75f;

    public enum Mode : byte { Out, Stand, Finally, Back, Flee, Done }

    public sealed class W
    {
        public int house;
        public PickupStop stop;
        public LifeId who;
        public ResidentAgent agent;
        public readonly WalkPath path = new WalkPath();
        public readonly WalkPath back = new WalkPath();
        public Mode mode;
        public float t, nextLine;
        public PuppetFx.Sign sign;
        public Vector3 standPos, signPos;
        public float standYaw;
    }

    readonly ResidentDirector dir;
    readonly List<W> active = new List<W>(MaxWitnesses);
    readonly Stack<W> free = new Stack<W>(MaxWitnesses);
    /// <summary>A house whose witness just went back in waits this long before sending another one out.</summary>
    public const float RestSeconds = 45f;
    readonly Dictionary<string, float> cooldown = new Dictionary<string, float>();
    float nextScan;
    public int Started { get; private set; }
    public int Finallys { get; private set; }
    public int Fled { get; private set; }
    public IReadOnlyList<W> Active => active;

    public NeglectWitness(ResidentDirector dir) { this.dir = dir; }

    public void Tick(WorldLifeContext ctx, float dt)
    {
        if (Time.time >= nextScan) { nextScan = Time.time + 1f; Scan(ctx); }
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var w = active[i];
            if (w.stop == null) { Finish(i); continue; }
            Step(ctx, w, dt);
            if (w.mode == Mode.Done) Finish(i);
        }
    }

    void Scan(WorldLifeContext ctx)
    {
        if (active.Count >= MaxWitnesses) return;
        var stops = PickupStop.All;
        float r2 = WorldLifeTuning.Lod.WitnessDistance * WorldLifeTuning.Lod.WitnessDistance;
        for (int i = 0; i < stops.Count && active.Count < MaxWitnesses; i++)
        {
            var s = stops[i];
            if (s == null || (s.transform.position - ctx.Viewer).sqrMagnitude > r2) continue;
            if (!s.IsNeglected || Watching(s) || GoblinAtPile(s)) continue;   // nobody walks out to a goblin
            if (cooldown.TryGetValue(s.stopId, out float until) && Time.time < until) continue;
            var d = s.District;
            if (d != null && d.butlerDrops) continue;   // the Bluffs have their butler standing there anyway
            int house = ctx.Graph.HouseIndex(s.stopId);
            if (house < 0 || dir.WalkOuts.FindJobAt(house) != null) continue;
            var nodes = HouseRoutes.DoorToCurb(ctx.Graph, house);
            if (nodes == null || nodes.Length == 0) continue;
            var hh = ResidentCast.HouseholdOf(house);
            LifeId who = LifeId.None;
            for (int m = 0; m < hh.count; m++)
            {
                var id = LifeId.Resident(hh.first + m);
                if (!dir.IsBusy(id)) { who = id; break; }
            }
            if (!who.IsValid) continue;
            Start(ctx, s, house, nodes, who);
        }
    }

    bool Watching(PickupStop s)
    {
        for (int i = 0; i < active.Count; i++) if (active[i].stop == s) return true;
        return false;
    }

    void Start(WorldLifeContext ctx, PickupStop s, int house, int[] nodes, LifeId who)
    {
        var w = free.Count > 0 ? free.Pop() : new W();
        w.house = house; w.stop = s; w.who = who; w.agent = null; w.mode = Mode.Out; w.t = 0f; w.sign = null;
        Vector3 fwd = s.transform.forward; fwd.y = 0f; fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        // Beside the pile, on the house side, facing the street, on the side away from the wheelie bin (so the sign shows).
        if (s.bin != null && Vector3.Dot(s.bin.position - s.PileCenter, right) > 0f) right = -right;
        w.standPos = s.PileCenter + right * 1.45f - fwd * 0.35f;
        w.signPos = s.PileCenter + right * 2.25f - fwd * 0.1f;
        w.standYaw = PuppetKit.YawOf(fwd);
        w.path.Clear();
        var g = ctx.Graph;
        for (int i = 0; i < nodes.Length; i++) w.path.Add(g.nodes[nodes[i]].pos);
        w.path.Add(w.standPos);
        w.nextLine = Time.time + 4f;
        active.Add(w);
        Started++;
    }

    void Step(WorldLifeContext ctx, W w, float dt)
    {
        var a = w.agent;
        if (a != null && a.Interrupted) { Show(w, a.pos, a.yaw, PersonPose.Stand, 1f); return; }
        w.t += dt;
        bool far = (w.stop.transform.position - ctx.Viewer).sqrMagnitude > LeaveDistance * LeaveDistance;
        Vector3 dirv;
        switch (w.mode)
        {
            case Mode.Out:
            {
                float s = w.t * WorldLifeTuning.Walk.Waddle - WorldLifeTuning.Walk.Waddle * 0.4f;
                if (s >= w.path.Length) { w.mode = Mode.Stand; w.t = 0f; PlantSign(w); goto case Mode.Stand; }
                Vector3 p = w.path.At(Mathf.Max(0f, s), out dirv);
                Show(w, p, PuppetKit.YawOf(dirv), PersonPose.Walk, Mathf.Clamp01(w.t / 0.4f));
                if (far) StartBack(w, false);
                break;
            }
            case Mode.Stand:
            {
                a = Show(w, w.standPos, w.standYaw, PersonPose.FootTap, 1f);
                if (a != null) a.Look(ctx.Viewer);
                if (!w.stop.IsNeglected || !w.stop.HasTrash)
                {
                    w.mode = Mode.Finally; w.t = 0f; Finallys++;
                    if (a != null) dir.Say(a, ResidentLines.Finally, ResidentLines.Happy, true);
                    break;
                }
                if (GoblinAtPile(w.stop))
                {
                    Fled++;
                    if (a != null) { dir.Say(a, ResidentLines.Goblin, ResidentLines.Yell, true); a.KickEyes(6f); }
                    StartBack(w, true);
                    break;
                }
                if (Time.time >= w.nextLine && a != null)
                {
                    w.nextLine = Time.time + 9f;
                    if (dir.NearCamera(w.standPos, 30f)) dir.Say(a, ResidentLines.Tapping, ResidentLines.Hey, false);
                }
                if (far) StartBack(w, false);
                break;
            }
            case Mode.Finally:
                a = Show(w, w.standPos, w.standYaw, PersonPose.Wave, 1f);
                if (w.t >= 1.4f) StartBack(w, false);
                break;
            case Mode.Back:
            case Mode.Flee:
            {
                float speed = w.mode == Mode.Flee ? 3.3f : WorldLifeTuning.Walk.Waddle;
                float s = w.t * speed;
                if (s < w.back.Length)
                {
                    Vector3 p = w.back.At(s, out dirv);
                    Show(w, p, PuppetKit.YawOf(dirv), w.mode == Mode.Flee ? PersonPose.Panic : PersonPose.Walk, 1f);
                }
                else
                {
                    float k = (s - w.back.Length) / speed / WalkOutPresenter.PopInSeconds;
                    w.back.At(w.back.Length, out dirv);
                    Show(w, w.back.End, PuppetKit.YawOf(dirv), PersonPose.Stand, 1f - Mathf.Clamp01(k));
                    if (k >= 1f) w.mode = Mode.Done;
                }
                break;
            }
        }
    }

    void StartBack(W w, bool flee)
    {
        Vector3 from = w.agent != null ? w.agent.pos : w.standPos;
        w.back.BuildReturn(w.path, w.path.Project(from), from);
        if (w.back.Count < 2) w.back.Add(w.path.Start);
        w.mode = flee ? Mode.Flee : Mode.Back;
        w.t = 0f;
        if (w.sign != null) { dir.Fx.ReturnSign(w.sign); w.sign = null; }
    }

    void PlantSign(W w)
    {
        if (w.sign != null) return;
        var h = PuppetDice.From(LifeHash.Str(w.stop.stopId), (int)GameClock.TotalHours);
        w.sign = dir.Fx.TakeSign(h.Pick(ResidentLines.SignText));
        if (w.sign == null) return;
        Vector3 fwd = w.stop.transform.forward; fwd.y = 0f;
        float gy = w.signPos.y;
        if (Physics.Raycast(w.signPos + Vector3.up * 1.5f, Vector3.down, out var g, 3f, 1 << 6, QueryTriggerInteraction.Ignore)) gy = g.point.y;
        w.sign.t.SetPositionAndRotation(new Vector3(w.signPos.x, gy, w.signPos.z), Quaternion.LookRotation(-fwd.normalized, Vector3.up) * Quaternion.Euler(0f, 180f, 0f));
        Juice.PopIn(w.sign.t, 0.3f);
        Sfx.Play("thud", w.signPos, 0.4f, 1.3f);
    }

    static bool GoblinAtPile(PickupStop s)
    {
        var all = Critter.All;
        Vector3 c = s.PileCenter;
        for (int i = 0; i < all.Count; i++)
        {
            var cr = all[i];
            if (cr is PileGoblin && cr != null && !cr.Despawning && (cr.transform.position - c).sqrMagnitude < 3.5f * 3.5f) return true;
        }
        return false;
    }

    ResidentAgent Show(W w, Vector3 p, float yaw, PersonPose pose, float pop)
    {
        var a = dir.ShowInternal(w.who, AgentOwner.Witness, w, w.stop.stopId, p, yaw, pose, 0f, pop, false, false);   // arms crossed: no prop
        w.agent = a;
        return a;
    }

    public void OnActEnded(W w, ResidentAgent a)
    {
        if (w.mode == Mode.Stand || w.mode == Mode.Out) StartBack(w, false);
    }

    void Finish(int i)
    {
        var w = active[i];
        if (w.stop != null) cooldown[w.stop.stopId] = Time.time + RestSeconds;
        if (w.sign != null) { dir.Fx.ReturnSign(w.sign); w.sign = null; }
        if (w.agent != null) dir.ReleaseInternal(w.agent, w);
        w.agent = null; w.stop = null;
        active.RemoveAt(i);
        free.Push(w);
    }

    public void Clear()
    {
        for (int i = active.Count - 1; i >= 0; i--) Finish(i);
        cooldown.Clear();
    }
}
