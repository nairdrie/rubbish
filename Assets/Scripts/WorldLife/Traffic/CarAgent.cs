using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// WorldLife package D (Docs/WorldLife.md §3.2, §4.2, §7.3, §8.3): one household car SHOWN on this machine: a pooled
/// shell (the Suburbs pack mesh with the house's tint, a kinematic box collider, a little driver's head, brake and head
/// lights by material swap, a body-bounce spring) driven along its <see cref="CarPath"/> by <see cref="TrafficDirector"/>.
/// It is the <see cref="ILifeBody"/> package E's sensor hits (registered in <see cref="LifeBodies"/> while shown) and it
/// presents what the authority decides: the cartoon shunt, pulling over for the horn, honking back.
/// Plain C# object (no MonoBehaviour update cost); presentation only, nothing here pays or hurts.
/// </summary>
public sealed class CarAgent : ILifeBody
{
    // ------------------------------------------------------------------ identity (set on assignment)

    public LifeId id;
    public int car = -1;            // roster car index (−1 = a debug car)
    public LifeId driver;
    public string stopId = "";
    public HouseholdCar household;  // the static parked twin (null in sandboxes / debug cars)

    // ------------------------------------------------------------------ driving state (TrafficDirector writes)

    public CarPath path;
    /// <summary>The trip (index into the day) and whether this is the out (true) or in leg.</summary>
    public int trip = -1;
    public bool outbound;
    /// <summary>Drawn arc on the path and drawn speed along it (m/s).</summary>
    public float s, v;
    public float yaw;
    /// <summary>Lateral offset from the path (m, + = right): overtakes (−5), pulling over (+1.4).</summary>
    public float lateral;
    /// <summary>A world offset that decays (the shunt's slide), m.</summary>
    public Vector3 slide;
    public Vector3 position;
    public bool parkedShell;        // standing in for a shunted parked car
    public int lodTier;             // 0 full, 1 lite, 2 far (15 Hz)
    public float distance;
    public float nextFarUpdate;

    // Following / stuck / honk / overtake.
    public float gapAhead = float.PositiveInfinity;
    public byte blockKind;          // TrafficDirector.Block*
    public float stuckTime, lastHonk = -99f, lastScreech = -99f;
    public int honkCount;
    public int overtake;            // 0 none, 1 active
    public float otStart, otEnd;    // arc range of the overtake
    public float otRampIn = 7f;     // swing-out length (m)
    public float otLateral = 5f;    // how far into the other lane the pass goes (m; just enough to clear the truck)
    public TruckControllerV2 otTruck;
    // Courtesy (fixer pass): a car that has waited behind a truck asks the oncoming lane to hold at the far end of its
    // overtake corridor; an oncoming car holding for it points at it with yieldingTo (the overtake's clear check skips it).
    public float courtesyUntil;
    public Vector3 courtesyFrom, courtesyTo;
    public CarAgent yieldingTo;
    public float courtesyHeld;      // seconds this car has held back for someone (it stops after TrafficDirector.CourtesyMaxHold)
    public bool yielding;
    public float holdUntil;         // pull-over / shunt hold (Time.time)
    public float pullOverUntil;
    public bool inBox;              // inside an intersection box right now
    public float boxEnd;            // arc where the current/next box ends

    // Shunt presentation.
    public float shuntT = -1f;      // seconds since the shunt (−1 = none)
    public float shuntYaw, shuntYawKick, shuntRoll, alarmUntil, colliderOffUntil, nextAlarmBeep;
    public float shuntImpactSpeed;
    public Vector3 shuntSlide, shuntSlideFrom;
    public bool crooked;
    public bool driverOut, driverPopped;
    public float driverPopT;        // shunt time (s) the driver got out (they wait while the truck is still rolling past)

    // ------------------------------------------------------------------ the shell

    public GameObject go;
    public Transform root, bodyPivot, model, driverHead;
    public MeshFilter mf;
    public MeshRenderer mr;
    public BoxCollider box;
    public Rigidbody rb;
    public Renderer[] driverRenderers;
    public Vector3 halfExtents = new Vector3(0.95f, 0.75f, 2.1f);
    Material[][] looks;             // 4 variants: [headlights on?][brake on?], + [4] every light off (parked)
    int lookIndex = -1;

    // Spring state.
    float pitch, pitchVel, roll, rollVel, bob, bobVel;
    float lastV, lastYaw;

    public bool Shown => go != null && go.activeSelf;

    // ------------------------------------------------------------------ ILifeBody

    public LifeId Id => id;
    public LifeBodyKind BodyKind => LifeBodyKind.Car;
    public Vector3 Position => position;
    public Quaternion Rotation => Quaternion.Euler(0f, yaw, 0f);
    public Vector3 HalfExtents => halfExtents;
    public bool Hittable => Shown && shuntT < 0f && Time.time >= immuneUntil;
    public string StopId => stopId;
    public float immuneUntil;

    /// <summary>Set by TrafficDirector: presents decided hits (shunt, pull over, honk back).</summary>
    public Action<CarAgent, NpcHit> onHit;

    public void PlayHit(in NpcHit hit)
    {
        var h = onHit;
        if (h != null) h(this, hit);
    }

    // ------------------------------------------------------------------ build / assign

    static readonly Color DriverHatFallback = new Color(0.2f, 0.2f, 0.25f);

    /// <summary>Make an empty shell under <paramref name="parent"/> (once per pool slot).</summary>
    public static CarAgent CreateShell(Transform parent, int slot)
    {
        var a = new CarAgent();
        a.go = new GameObject("Car (pool " + slot + ")");
        a.go.SetActive(false);
        a.root = a.go.transform;
        a.root.SetParent(parent, false);
        a.rb = a.go.AddComponent<Rigidbody>();
        a.rb.isKinematic = true;
        a.rb.useGravity = false;
        a.rb.interpolation = RigidbodyInterpolation.None;
        a.box = a.go.AddComponent<BoxCollider>();
        var bp = new GameObject("Body").transform;
        bp.SetParent(a.root, false);
        a.bodyPivot = bp;
        var mgo = new GameObject("Model");
        a.model = mgo.transform;
        a.model.SetParent(bp, false);
        a.mf = mgo.AddComponent<MeshFilter>();
        a.mr = mgo.AddComponent<MeshRenderer>();
        a.mr.shadowCastingMode = ShadowCastingMode.On;

        // A driver's head through the windscreen: skull, nose, hair or hat (3 renderers, §3.2).
        var head = new GameObject("Driver").transform;
        head.SetParent(bp, false);
        a.driverHead = head;
        a.driverRenderers = new Renderer[3];
        a.driverRenderers[0] = Part(head, PrimitiveType.Sphere, "Skull", Vector3.zero, new Vector3(0.34f, 0.36f, 0.34f));
        a.driverRenderers[1] = Part(head, PrimitiveType.Sphere, "Nose", new Vector3(0f, -0.02f, 0.19f), new Vector3(0.11f, 0.1f, 0.14f));
        a.driverRenderers[2] = Part(head, PrimitiveType.Cylinder, "Hat", new Vector3(0f, 0.17f, 0f), new Vector3(0.3f, 0.06f, 0.3f));
        return a;
    }

    static Renderer Part(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale)
    {
        var p = GameObject.CreatePrimitive(type);
        UnityEngine.Object.Destroy(p.GetComponent<Collider>());
        p.name = name;
        p.transform.SetParent(parent, false);
        p.transform.localPosition = pos;
        p.transform.localScale = scale;
        var r = p.GetComponent<MeshRenderer>();
        r.shadowCastingMode = ShadowCastingMode.Off;
        return r;
    }

    /// <summary>Dress the shell as a car: mesh, tinted materials (with their light variants), scale and collider.</summary>
    public void Assign(LifeId carId, int carIndex, HouseholdCar hc, Mesh mesh, Material[][] lookVariants, Vector3 scale, float noseYaw, Bounds meshBounds, LifeId driverId, string stop)
    {
        id = carId; car = carIndex; household = hc; driver = driverId; stopId = stop ?? "";
        mf.sharedMesh = mesh;
        looks = lookVariants;
        lookIndex = -1;
        model.localRotation = Quaternion.Euler(0f, noseYaw, 0f);
        model.localScale = scale;
        model.localPosition = Vector3.zero;
        // The mesh's box in the car frame (nose = +Z): rotate the scaled bounds by noseYaw.
        Vector3 c = Vector3.Scale(meshBounds.center, scale), e = Vector3.Scale(meshBounds.extents, scale);
        Quaternion q = Quaternion.Euler(0f, noseYaw, 0f);
        Vector3 ex = q * new Vector3(e.x, 0f, 0f), ez = q * new Vector3(0f, 0f, e.z);
        Vector3 he = new Vector3(Mathf.Abs(ex.x) + Mathf.Abs(ez.x), e.y, Mathf.Abs(ex.z) + Mathf.Abs(ez.z));
        Vector3 cc = q * c;
        box.center = new Vector3(cc.x, cc.y, cc.z);
        box.size = he * 2f;
        box.enabled = true;
        halfExtents = new Vector3(he.x, he.y, he.z);
        // Driver at the front-left seat (left-hand drive: right-hand traffic).
        driverHead.localPosition = new Vector3(-he.x * 0.38f, Mathf.Min(1.18f, he.y * 2f - 0.42f), he.z * 0.12f);
        bool tall = he.y * 2f > 1.7f;
        driverHead.localScale = Vector3.one * (tall ? 1f : 0.92f);
        pitch = pitchVel = roll = rollVel = bob = bobVel = 0f;
        shuntT = -1f; crooked = false; driverOut = false; driverPopped = false; alarmUntil = 0f; colliderOffUntil = 0f; immuneUntil = 0f;
        shuntYaw = shuntYawKick = shuntRoll = shuntImpactSpeed = 0f; shuntHop = shuntPitchTarget = 0f; shuntSlide = shuntSlideFrom = Vector3.zero;
        lateral = 0f; slide = Vector3.zero; overtake = 0; otTruck = null; otLateral = 5f; courtesyUntil = 0f; yieldingTo = null; courtesyHeld = 0f; driverPopT = 0f; stuckTime = 0f; honkCount = 0; lastHonk = -99f; lastScreech = -99f; holdUntil = 0f; pullOverUntil = 0f;
        yielding = false; inBox = false; gapAhead = float.PositiveInfinity; blockKind = 0; parkedShell = false;
        StyleDriver(driverId);
    }

    void StyleDriver(LifeId who)
    {
        Color skin = new Color(0.86f, 0.68f, 0.55f), hat = DriverHatFallback;
        bool hatOn = true;
        if (who.IsValid && ResidentCast.IsValid(who))
        {
            var st = ResidentCast.Style(who);
            skin = st.Skin;
            hatOn = st.hat != PuppetHat.None || st.hairKind != PuppetHair.Bald;
            hat = st.hat != PuppetHat.None ? st.hatColor : st.hair;
        }
        driverRenderers[0].sharedMaterial = CritterLook.Mat(skin, 0.25f);
        driverRenderers[1].sharedMaterial = CritterLook.Mat(skin * 0.92f, 0.25f);
        driverRenderers[2].sharedMaterial = CritterLook.Mat(hat, 0.3f);
        driverRenderers[2].enabled = hatOn;
    }

    public void Show(bool on)
    {
        if (go.activeSelf != on) go.SetActive(on);
    }

    // ------------------------------------------------------------------ per-frame presentation

    /// <summary>Lights by material swap: headlights from dusk to dawn, brake lights while slowing / yielding (flashing
    /// for the alarm). <paramref name="parked"/>: a parked car's stand-in, whose tail lights are off too.</summary>
    public void SetLights(bool headlights, bool brake, bool parked = false)
    {
        if (looks == null) return;
        int i = parked && !headlights && !brake && looks.Length > 4 ? 4 : (headlights ? 0 : 2) + (brake ? 1 : 0);
        if (i == lookIndex) return;
        lookIndex = i;
        mr.sharedMaterials = looks[i];
    }

    /// <summary>Place the shell: chassis at <paramref name="pos"/>, facing <paramref name="yawDeg"/>, with the body
    /// springs (full LOD only) from the speed and turn rate.</summary>
    public void Pose(Vector3 pos, float yawDeg, float dt, bool full)
    {
        position = pos;
        yaw = yawDeg;
        root.SetPositionAndRotation(pos, Quaternion.Euler(0f, yawDeg, 0f));
        if (!full || dt <= 0f)
        {
            bodyPivot.localPosition = Vector3.zero;
            bodyPivot.localRotation = Quaternion.Euler(shuntPitchTarget, 0f, crooked ? shuntRoll : 0f);
            lastV = v; lastYaw = yawDeg;
            return;
        }
        float accel = (v - lastV) / dt;
        float yawRate = Mathf.DeltaAngle(lastYaw, yawDeg) / dt;
        lastV = v; lastYaw = yawDeg;
        // Nose dips under braking, squats under throttle; leans out of turns.
        float pitchTarget = Mathf.Clamp(-accel * 0.9f, -4f, 4f) + shuntPitchTarget;
        float rollTarget = Mathf.Clamp(-yawRate * Mathf.Abs(v) * 0.012f, -5f, 5f) + (crooked ? shuntRoll : 0f);
        Spring(ref pitch, ref pitchVel, pitchTarget, 60f, 9f, dt);
        Spring(ref roll, ref rollVel, rollTarget, 50f, 8f, dt);
        Spring(ref bob, ref bobVel, shuntHop, 80f, 10f, dt);
        bodyPivot.localPosition = new Vector3(0f, bob, 0f);
        bodyPivot.localRotation = Quaternion.Euler(pitch, 0f, roll);
    }

    public float shuntPitchTarget, shuntHop;

    static void Spring(ref float x, ref float vel, float target, float k, float damp, float dt)
    {
        vel += ((target - x) * k - vel * damp) * dt;
        x += vel * dt;
    }

    public void SetDriverVisible(bool on)
    {
        if (driverHead.gameObject.activeSelf != on) driverHead.gameObject.SetActive(on);
    }

    public void SetCollider(bool on)
    {
        if (box.enabled != on) box.enabled = on;
    }
}

/// <summary>
/// A household car standing in its driveway, as an <see cref="ILifeBody"/> so package E's sensor can shunt parked cars
/// too (§8.3). Registered while the car is home and its static twin is showing.
/// </summary>
public sealed class ParkedCarBody : ILifeBody
{
    public LifeId id;
    public int car;
    public HouseholdCar household;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 halfExtents;
    public string stopId = "";
    public float immuneUntil;
    public Action<ParkedCarBody, NpcHit> onHit;
    public bool registered;

    public LifeId Id => id;
    public LifeBodyKind BodyKind => LifeBodyKind.Car;
    public Vector3 Position => position;
    public Quaternion Rotation => rotation;
    public Vector3 HalfExtents => halfExtents;
    public bool Hittable => Time.time >= immuneUntil;
    public string StopId => stopId;

    public void PlayHit(in NpcHit hit)
    {
        var h = onHit;
        if (h != null) h(this, hit);
    }
}
