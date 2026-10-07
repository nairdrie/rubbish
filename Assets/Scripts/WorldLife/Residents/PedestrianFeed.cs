using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package C ↔ D seam (Docs/WorldLife.md §4.1, §7.1, §8.3). Every reference C makes to package D lives in this
/// file:
/// <list type="bullet">
/// <item><b>Pedestrians.</b> D's <see cref="PedestrianDirector"/> (slot Schedule, before C) draws each resident out on
/// foot near the camera with <see cref="ResidentDirector.Show"/>. <see cref="Feed"/> then adds what that call doesn't
/// carry: the door / car pop at the start and end of an excursion, and a gossip partner to look at.</item>
/// <item><b>Walk-outs.</b> <see cref="IsOut"/> (the bag carrier must be someone at home) and <see cref="Suppress"/> (D
/// doesn't draw the carrier from the timetable during the walk). D also sets <see cref="WalkOutPresenter.CarrierPicker"/>.</item>
/// <item><b>Cars.</b> <see cref="CarScenes"/> listens to <see cref="TrafficDirector.DriverPoppedOut"/> (a shunted car's
/// driver gets out as a full puppet, yells and shakes a fist for 3 s, gets back in) and
/// <see cref="TrafficDirector.ParkedCarShunted"/> (the owner storms out of the front door at their parked car).</item>
/// </list>
/// </summary>
public static class PedestrianFeed
{
    public static int Fed { get; private set; }

    public static void Feed(ResidentDirector dir, WorldLifeContext ctx)
    {
        var pd = PedestrianDirector.Instance;
        Fed = 0;
        if (pd == null || !pd.IsAwake) return;
        var list = pd.Out;
        int frame = Time.frameCount;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (!p.relevant) continue;
            var a = dir.Find(p.id);
            if (a == null || a.owner != AgentOwner.External || a.reqFrame != frame) continue;   // D didn't draw them this frame
            Fed++;
            a.pop = p.pop;
            if (p.partner.IsValid)
            {
                var other = pd.Find(p.partner);
                if (other != null) a.Look(other.position);
            }
            else a.hasLook = false;
        }
    }

    /// <summary>Is this resident out on one of D's excursions right now (not home to carry a bag out)?</summary>
    public static bool IsOut(LifeId id)
    {
        var pd = PedestrianDirector.Instance;
        return pd != null && pd.IsAwake && pd.Find(id) != null;
    }

    /// <summary>Keep D from drawing this resident from the timetable until then (they're C's puppet for the walk-out).</summary>
    public static void Suppress(LifeId id, double untilHours)
    {
        var pd = PedestrianDirector.Instance;
        if (pd != null && pd.IsAwake) pd.Suppress(id, untilHours);
    }
}

/// <summary>
/// The car scenes (§8.3): a shunted car's driver pops out beside it as a full puppet ("MY MINIVAN!", "That's a LEASE!"),
/// shakes a fist for 3 s and gets back in; when a PARKED car is shunted, its owner storms out of the front door to it,
/// fist up ("HEY!! THAT'S MY CAR!"), and goes back in. Presentation only (E decides and charges the shunt).
/// </summary>
public sealed class CarScenes
{
    public const float PopSeconds = 0.3f, StormSpeed = 2.6f, Standoff = 1.8f;

    sealed class S
    {
        public LifeId who;
        public bool parked;
        public Vector3 door, at, face;
        public float t, walkOut, fistEnd, walkBack;
        public string line;
        public ResidentAgent agent;
        public bool waiting;        // a parked car's owner stays in while a truck is still rolling up to it (fixer pass)
        public float waitUntil;
        public LifeId car;          // the shunted car (its shell may have been shoved aside since)
    }

    readonly ResidentDirector dir;
    readonly List<S> active = new List<S>(4);
    readonly Stack<S> free = new Stack<S>(4);
    bool hooked;
    public int DriverScenes { get; private set; }
    public int OwnerScenes { get; private set; }
    public int Active => active.Count;

    /// <summary>The district byte of a resident (their lines are in that district's voice).</summary>
    static byte DistrictOf(LifeId who) => ResidentCast.IsValid(who) ? ResidentCast.Style(who).district : (byte)0;

    public CarScenes(ResidentDirector dir) { this.dir = dir; }

    public void Wake()
    {
        if (hooked) return;
        TrafficDirector.DriverPoppedOut += OnDriverPoppedOut;
        TrafficDirector.ParkedCarShunted += OnParkedCarShunted;
        hooked = true;
    }

    public void Sleep()
    {
        if (hooked)
        {
            TrafficDirector.DriverPoppedOut -= OnDriverPoppedOut;
            TrafficDirector.ParkedCarShunted -= OnParkedCarShunted;
            hooked = false;
        }
        for (int i = active.Count - 1; i >= 0; i--) Finish(i);
    }

    void OnDriverPoppedOut(CarAgent car, LifeId driver, Vector3 side)
    {
        if (car == null || !ResidentCast.IsValid(driver) || dir.IsBusy(driver) || active.Count >= 4)
        {
            if (car != null && dir.NearCamera(side, 60f)) GameHUD.FloatingText(side + Vector3.up * 2.1f, ResidentLines.DriverFor(DistrictOf(driver))[0], ResidentLines.Yell, 1.1f);
            return;
        }
        var s = Take(driver, false);
        s.at = side; s.door = side;
        s.face = car.Position;
        s.walkOut = PopSeconds; s.fistEnd = PopSeconds + WorldLifeTuning.Hits.DriverFistSeconds; s.walkBack = s.fistEnd;
        var dl = ResidentLines.DriverFor(DistrictOf(driver));
        s.line = dl[(int)(LifeHash.Mix(driver.Packed + (ulong)DriverScenes) % (ulong)dl.Length)];
        DriverScenes++;
    }

    void OnParkedCarShunted(LifeId car, LifeId owner, string stopId, Vector3 carPos)
    {
        var ctx = dir.Ctx;
        int house = ctx != null ? ctx.Graph.HouseIndex(stopId) : -1;
        if (house < 0 || !ResidentCast.IsValid(owner) || dir.IsBusy(owner) || active.Count >= 4)
        {
            if (dir.NearCamera(carPos, 60f)) GameHUD.FloatingText(carPos + Vector3.up * 2.4f, ResidentLines.OwnerFor(DistrictOf(owner))[0], ResidentLines.Yell, 1.1f);
            return;
        }
        var g = ctx.Graph;
        var s = Take(owner, true);
        s.door = g.nodes[g.houses[house].door].pos;
        s.car = car;
        Aim(s, carPos);
        var ol = ResidentLines.OwnerFor(DistrictOf(owner));
        s.line = ol[(int)(LifeHash.Mix(owner.Packed + (ulong)OwnerScenes) % (ulong)ol.Length)];
        // Fixer pass: the owner waits indoors while the truck that did it is still rolling up the driveway (the same truck
        // used to flatten them on the way out), at most TrafficDirector.DriverWaitMax s, then storms out.
        s.waiting = true;
        s.waitUntil = Time.time + TrafficDirector.DriverWaitMax;
        OwnerScenes++;
    }

    S Take(LifeId who, bool parked)
    {
        var s = free.Count > 0 ? free.Pop() : new S();
        s.who = who; s.parked = parked; s.t = 0f; s.agent = null; s.waiting = false;
        active.Add(s);
        return s;
    }

    public void Tick(float dt)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var s = active[i];
            if (s.waiting)
            {
                if (Time.time < s.waitUntil && TruckOnTheWay(s)) { OwnersWaited++; continue; }
                s.waiting = false;
                // Storm out to where the car is NOW (G's shove moves a shunted car out of the truck's path).
                var body = LifeBodies.Find(s.car);
                if (body != null) Aim(s, body.Position);
            }
            float prev = s.t;
            s.t += dt;
            if (s.agent != null && s.agent.Interrupted) { Show(s, s.agent.pos, s.agent.yaw, PersonPose.Stand, 1f); continue; }
            Vector3 p; float yaw; PersonPose pose; float pop = 1f;
            Vector3 toFace = s.face - s.at; toFace.y = 0f;
            float faceYaw = toFace.sqrMagnitude > 0.01f ? PuppetKit.YawOf(toFace) : 0f;
            if (s.t < PopSeconds && !s.parked) { p = s.at; yaw = faceYaw; pose = PersonPose.Stand; pop = s.t / PopSeconds; }
            else if (s.t < s.walkOut)
            {
                // storming out of the front door to the car
                float k = s.walkOut > PopSeconds ? Mathf.Clamp01((s.t - PopSeconds) / (s.walkOut - PopSeconds)) : 1f;
                p = Vector3.Lerp(s.door, s.at, k);
                Vector3 d = s.at - s.door; d.y = 0f;
                yaw = d.sqrMagnitude > 0.01f ? PuppetKit.YawOf(d) : faceYaw;
                pose = PersonPose.Jog;
                pop = Mathf.Clamp01(s.t / PopSeconds);
            }
            else if (s.t < s.fistEnd)
            {
                p = s.at; yaw = faceYaw; pose = PersonPose.FistShake;
                if (prev < s.walkOut && s.agent != null) dir.Say(s.agent, s.line, ResidentLines.Yell, true);
            }
            else if (s.parked && s.t < s.walkBack)
            {
                float k = Mathf.Clamp01((s.t - s.fistEnd) / Mathf.Max(0.01f, s.walkBack - s.fistEnd));
                p = Vector3.Lerp(s.at, s.door, k);
                Vector3 d = s.door - s.at; d.y = 0f;
                yaw = d.sqrMagnitude > 0.01f ? PuppetKit.YawOf(d) : faceYaw;
                pose = PersonPose.Walk;
            }
            else if (s.t < s.walkBack + PopSeconds)
            {
                p = s.parked ? s.door : s.at; yaw = faceYaw; pose = PersonPose.Stand;
                pop = 1f - Mathf.Clamp01((s.t - s.walkBack) / PopSeconds);
            }
            else { Finish(i); continue; }
            Show(s, p, yaw, pose, pop);
        }
    }

    /// <summary>The owner's spot: <see cref="Standoff"/> m short of the car on the line from the front door, and never
    /// inside a truck's box (fixer pass: a truck that stopped where the car stood had the owner shake a fist from inside
    /// its bodywork). The timeline (walk out, fist, walk back) follows from it.</summary>
    static void Aim(S s, Vector3 carPos)
    {
        Vector3 d = carPos - s.door; d.y = 0f;
        float len = d.magnitude;
        Vector3 dir = len > 1e-3f ? d / len : Vector3.forward;
        float along = Mathf.Max(0f, len - Standoff);
        Vector3 at = s.door + dir * along;
        for (int guard = 0; guard < 40 && along > 0f && InsideTruck(at, 0.7f); guard++)
        {
            along = Mathf.Max(0f, along - 0.3f);
            at = s.door + dir * along;
        }
        at.y = carPos.y;
        s.at = at;
        s.face = carPos;
        float walk = Vector3.Distance(s.door, s.at) / StormSpeed;
        s.walkOut = PopSeconds + walk;
        s.fistEnd = s.walkOut + WorldLifeTuning.Hits.DriverFistSeconds;
        s.walkBack = s.fistEnd + Vector3.Distance(s.door, s.at) / WorldLifeTuning.Walk.Waddle;
    }

    /// <summary>Is this point inside any truck's body box, padded by <paramref name="pad"/> m (flat)?</summary>
    static bool InsideTruck(Vector3 p, float pad)
    {
        var trucks = TruckControllerV2.All;
        var bb = TruckControllerV2.BodyBoxMetres;
        for (int i = 0; i < trucks.Count; i++)
        {
            var tk = trucks[i];
            if (tk == null || !tk.isActiveAndEnabled) continue;
            var rb = tk.Body;
            Vector3 pos = rb != null ? rb.position : tk.transform.position;
            Quaternion rot = rb != null ? rb.rotation : tk.transform.rotation;
            Vector3 l = Quaternion.Inverse(rot) * (p - pos) - bb.center;
            if (Mathf.Abs(l.x) <= bb.extents.x + pad && Mathf.Abs(l.z) <= bb.extents.z + pad) return true;
        }
        return false;
    }

    /// <summary>Is a truck still rolling near the owner's way from the door to their car?</summary>
    static bool TruckOnTheWay(S s)
    {
        const float R = TrafficDirector.DriverWaitRadius, V = TrafficDirector.DriverWaitSpeed;
        return TrafficDirector.TruckRollingNear(s.at, R, V) || TrafficDirector.TruckRollingNear((s.door + s.at) * 0.5f, R, V)
               || TrafficDirector.TruckRollingNear(s.door, R, V);
    }

    /// <summary>Frames an owner spent waiting indoors for a rolling truck (tests).</summary>
    public int OwnersWaited { get; private set; }

    void Show(S s, Vector3 p, float yaw, PersonPose pose, float pop)
    {
        s.agent = dir.ShowInternal(s.who, AgentOwner.Scene, s, "", p, yaw, pose, 0f, pop, false, false);
    }

    void Finish(int i)
    {
        var s = active[i];
        if (s.agent != null) dir.ReleaseInternal(s.agent, s);
        s.agent = null;
        active.RemoveAt(i);
        free.Push(s);
    }
}
