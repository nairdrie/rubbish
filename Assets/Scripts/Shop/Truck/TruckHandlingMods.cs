using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// truck_handling 0-3 (Docs/Progression.md §6.1), applied at runtime from <see cref="UpgradeEffects.Handling"/>:
///   friction  : each WheelCollider's sideways / forward curve stiffness = stock × tier multiplier (copied
///               WheelFrictionCurve structs; the stock curves are captured once and never lost);
///   CoG       : centreOfGravityOffset -1.0 → -1.3 (auto centre of mass + offset; same maths as TruckControllerV2.Start);
///   anti-tip  : L2+, below 12 m/s, a counter-roll assist once the body rolls past 25°;
///   drift     : L3, hold Space while driving = handbrake drift (rear sideways stiffness ×0.45, rear brake 1,500 Nm).
/// Physics only acts where this machine simulates the truck (non-kinematic body). Runs after TruckControllerV2's
/// FixedUpdate so the drift brake wins. Added by <see cref="ShopEffectsRunner"/> to every truck.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)]
public class TruckHandlingMods : MonoBehaviour
{
    const float AntiTipStartDeg = 25f;
    const float AntiTipMaxDeg = 110f;
    const float DriftRearSidewaysMult = 0.45f;
    const float DriftRearBrake = 1500f;

    struct Wheel
    {
        public WheelCollider wc;
        public bool rear;
        public WheelFrictionCurve fwd0, side0;
    }

    readonly List<Wheel> wheels = new List<Wheel>();
    TruckControllerV2 truck;
    UpgradeEffects.HandlingTier Tier => UpgradeEffects.HandlingAtLevel(BusinessUpgradeNet.LevelForTruck(truck, ShopIds.TruckHandling));
    Rigidbody body;
    bool comTouched, comReady;
    int appliedLevel = -1;
    bool drifting, driftHeld;
    float driftStartedAt, nextSmokeAt, nextSqueakAt, lastAssistFx;
    bool assisting;

    /// <summary>True while a handbrake drift is on (for juice / other mods).</summary>
    public bool Drifting => drifting;

    void Awake()
    {
        truck = GetComponent<TruckControllerV2>();
        body = GetComponent<Rigidbody>();
        foreach (var wc in GetComponentsInChildren<WheelCollider>(true))
        {
            var ctl = wc.GetComponent<WheelControl>();
            bool rear = ctl != null ? !ctl.steerable : ShopModKit.ToTruckMetres(transform, wc.transform.position).z < 1.5f;
            wheels.Add(new Wheel { wc = wc, rear = rear, fwd0 = wc.forwardFriction, side0 = wc.sidewaysFriction });
        }
    }

    IEnumerator Start()
    {
        // TruckControllerV2.Start sets the centre of mass once; let it run first, then take over idempotently.
        yield return null;
        yield return null;
        comReady = true;
        Apply();
    }

    void OnEnable()
    {
        GameSession.UpgradeChanged += OnUpgradeChanged;
        BusinessUpgradeNet.Changed += Apply;
        Apply();
    }

    void OnDisable()
    {
        GameSession.UpgradeChanged -= OnUpgradeChanged;
        BusinessUpgradeNet.Changed -= Apply;
        drifting = false;
    }

    void OnDestroy()
    {
        // Removed from a live truck: stock grip back.
        if (truck == null || !truck.gameObject.activeInHierarchy) return;
        foreach (var w in wheels)
        {
            if (w.wc == null) continue;
            w.wc.forwardFriction = w.fwd0;
            w.wc.sidewaysFriction = w.side0;
        }
        if (comTouched && body != null) SetCom(truck.centreOfGravityOffset);
    }

    void OnUpgradeChanged(string id, int level)
    {
        if (id == ShopIds.TruckHandling) Apply();
    }

    /// <summary>Re-apply the grip and centre of gravity for the current level. Idempotent.</summary>
    public void Apply()
    {
        var h = Tier;
        int level = Mathf.Clamp(BusinessUpgradeNet.LevelForTruck(truck, ShopIds.TruckHandling), 0, 3);
        SetGrip(h, false);
        if (comReady && body != null && (level > 0 || comTouched))
        {
            SetCom(h.cogOffset);
            comTouched = level > 0;
        }
        if (appliedLevel >= 0 && level > appliedLevel && Application.isPlaying)
        {
            Vector3 p = transform.position + Vector3.up * 1.2f;
            Fx.Dust(p, 1.1f);
            Sfx.Play("squeak", p, 0.6f, 0.7f);
        }
        appliedLevel = level;
    }

    void SetGrip(UpgradeEffects.HandlingTier h, bool drift)
    {
        foreach (var w in wheels)
        {
            if (w.wc == null) continue;
            var f = w.fwd0; f.stiffness = w.fwd0.stiffness * h.forwardStiffness;
            var s = w.side0; s.stiffness = w.side0.stiffness * h.sidewaysStiffness * (drift && w.rear ? DriftRearSidewaysMult : 1f);
            w.wc.forwardFriction = f;
            w.wc.sidewaysFriction = s;
        }
    }

    /// <summary>Automatic centre of mass + (0, offset, 0): exactly what TruckControllerV2.Start does with its own offset.</summary>
    void SetCom(float offset)
    {
        body.ResetCenterOfMass();
        var com = body.centerOfMass;
        com.y += offset;
        body.centerOfMass = com;
    }

    bool Simulating => body != null && !body.isKinematic;

    void Update()
    {
        driftHeld = false;
        if (truck == null || !truck.IsLocalPlayerSeated) return;
        if (PlayerController.IsUIBlocking || SceneController.IsTransitioning) return;
        if (!Tier.drift) return;
        driftHeld = GameControls.Held(KeyCode.Space, GameControls.Action.Drift);
    }

    void FixedUpdate()
    {
        if (!Simulating || truck == null) return;
        var h = Tier;
        float speed = body.linearVelocity.magnitude;

        // ---- drift (L3): handbrake the rear axles and let them slide
        bool want = driftHeld && h.drift && speed > 2.5f;
        if (want != drifting)
        {
            drifting = want;
            SetGrip(h, drifting);
            if (drifting)
            {
                driftStartedAt = Time.time;
                Vector3 p = transform.position + Vector3.up * 3.6f;
                GameHUD.FloatingText(p, ShopModKit.Pick(DriftWords), new Color(1f, 0.4f, 0.85f), 1.3f);
                Sfx.Play("squeak", transform.position, 0.8f, 0.5f);
            }
            else if (Time.time - driftStartedAt > 1.6f)
            {
                GameHUD.FloatingText(transform.position + Vector3.up * 3.8f, $"{Time.time - driftStartedAt:0.0}s DRIFT! (it smells)", new Color(1f, 0.55f, 0.9f), 1.2f);
                Sfx.Play("combo", transform.position, 0.6f, 1.1f);
            }
        }
        if (drifting)
        {
            foreach (var w in wheels)
                if (w.rear && w.wc != null) { w.wc.brakeTorque = DriftRearBrake; w.wc.motorTorque = 0f; }
            if (Time.time >= nextSmokeAt)
            {
                nextSmokeAt = Time.time + 0.07f;
                foreach (var w in wheels)
                    if (w.rear && w.wc != null && w.wc.GetGroundHit(out var hit))
                        Fx.Dust(hit.point + Vector3.up * 0.1f, 0.55f);
            }
            if (Time.time >= nextSqueakAt)
            {
                nextSqueakAt = Time.time + 0.22f;
                Sfx.Play("squeak", transform.position, 0.35f, ShopModKit.Range(0.35f, 0.5f));
            }
        }

        // ---- anti-tip assist (L2+)
        assisting = false;
        if (h.antiTipBelow > 0f && speed < h.antiTipBelow)
        {
            Vector3 fwd = transform.forward;
            Vector3 upInPlane = Vector3.ProjectOnPlane(Vector3.up, fwd);
            if (upInPlane.sqrMagnitude > 1e-4f)
            {
                float roll = Vector3.SignedAngle(upInPlane.normalized, transform.up, fwd); // + = rolled one way
                float a = Mathf.Abs(roll);
                if (a > AntiTipStartDeg && a < AntiTipMaxDeg)
                {
                    assisting = true;
                    float k = Mathf.Clamp01((a - AntiTipStartDeg) / 25f);
                    float rollRate = Vector3.Dot(body.angularVelocity, fwd);
                    // Counter-roll angular acceleration (rad/s²) toward upright, damped by the current roll rate.
                    float accel = -Mathf.Sign(roll) * (2.5f + 7.5f * k) - rollRate * 2.2f;
                    body.AddTorque(fwd * accel, ForceMode.Acceleration);
                    if (Time.time - lastAssistFx > 2.5f)
                    {
                        lastAssistFx = Time.time;
                        GameHUD.FloatingText(transform.position + Vector3.up * 4f, ShopModKit.Pick(TonyWords), new Color(0.6f, 0.9f, 1f), 1f);
                        Sfx.Play("squeak", transform.position, 0.7f, 0.65f);
                    }
                }
            }
        }
    }

    /// <summary>True during a FixedUpdate in which the anti-tip assist pushed back.</summary>
    public bool Assisting => assisting;

    static readonly string[] DriftWords = { "TOKYO DRIFT!", "GARBAGE DRIFT!", "SKRRRRRT", "DRIFT KING (OF TRASH)" };
    static readonly string[] TonyWords = { "TONY SAYS NO", "anti-tip!", "nuh-uh, physics", "*suspension noises*" };
}
