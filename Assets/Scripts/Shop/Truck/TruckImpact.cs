using UnityEngine;

/// <summary>Collision reports only from the physics owner; the host decides a bounded response for BOTH trucks.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-60)]
public sealed class TruckImpact : MonoBehaviour
{
    TruckControllerV2 truck;
    readonly VehicleMotionWindow motion = new VehicleMotionWindow();
    readonly Vector3[] velocityHistory = new Vector3[20];
    readonly float[] sampleTimes = new float[20];
    int sampleHead, sampleCount;
    float yieldUntil, localCooldown;
    public int AppliedImpacts { get; private set; }
    public Vector3 LastKick { get; private set; }

    public static TruckImpact Ensure(TruckControllerV2 truck) => truck == null ? null :
        truck.GetComponent<TruckImpact>() ?? truck.gameObject.AddComponent<TruckImpact>();

    void Awake() { truck = GetComponent<TruckControllerV2>(); }

    void FixedUpdate()
    {
        if (truck == null || truck.Body == null) return;
        Vector3 p = truck.Body.position;
        Vector3 observed = motion.Sample(Time.time, p);
        if (motion.ResetOccurred) sampleCount = sampleHead = 0;
        Vector3 velocity = !truck.Body.isKinematic ? truck.Body.linearVelocity : observed;
        if (!VehicleCrashRules.Finite(velocity) || velocity.magnitude > 50f)
        { sampleCount = sampleHead = 0; return; } // teleports are not crashes
        velocityHistory[sampleHead] = velocity;
        sampleTimes[sampleHead] = Time.time;
        sampleHead = (sampleHead + 1) % velocityHistory.Length;
        sampleCount = Mathf.Min(sampleCount + 1, velocityHistory.Length);
    }

    /// <summary>Recent observed approach speed survives the physics solver stopping a truck at the contact.</summary>
    public float ApproachSpeed(Vector3 direction)
    {
        return Vector3.Dot(RecentVelocity, direction);
    }

    public Vector3 RecentVelocity
    {
        get
        {
            Vector3 best = Vector3.zero;
            for (int i = 0; i < sampleCount; i++)
                if (Time.time - sampleTimes[i] <= .4f && velocityHistory[i].sqrMagnitude > best.sqrMagnitude)
                    best = velocityHistory[i];
            return Vector3.ClampMagnitude(best, 35f);
        }
    }

    void OnCollisionEnter(Collision collision)
    {
        if (truck == null || truck.Body == null || truck.Body.isKinematic || (truck.IsSpawned && !truck.IsOwner)) return;
        var other = collision.collider.GetComponentInParent<TruckControllerV2>();
        if (other == null || other == truck) return;
        var net = HandsNet.Instance;
        if (truck.IsSpawned && other.IsSpawned && net != null && net.IsSpawned)
        {
            net.ReportTruckImpact(truck, other);
            return;
        }
        // An unnetworked preview can simulate both trucks. Only one callback resolves the pair.
        if (truck.IsSpawned || other.IsSpawned || truck.GetInstanceID() > other.GetInstanceID() || Time.time < localCooldown) return;
        var peer = Ensure(other);
        Vector3 normal = FlatDirection(truck, other);
        float closing = ApproachSpeed(normal) + peer.ApproachSpeed(-normal);
        if (closing < VehicleCrashRules.MinimumClosingSpeed) return;
        localCooldown = peer.localCooldown = Time.time + VehicleCrashRules.PairCooldown;
        ApplyPair(truck, other, normal, closing);
    }

    public static Vector3 FlatDirection(TruckControllerV2 a, TruckControllerV2 b)
    {
        Vector3 d = b.Body.position + b.Body.rotation * b.ModelBodyBoxMetres.center
            - a.Body.position - a.Body.rotation * a.ModelBodyBoxMetres.center;
        d.y = 0f;
        return d.sqrMagnitude > .01f ? d.normalized : a.transform.forward;
    }

    public static void ApplyPair(TruckControllerV2 a, TruckControllerV2 b, Vector3 direction, float closing)
    {
        if (a == null || b == null || a.Body == null || b.Body == null) return;
        Apply(a, b, -direction, closing);
        Apply(b, a, direction, closing);
        Vector3 point = (a.Body.worldCenterOfMass + b.Body.worldCenterOfMass) * .5f;
        var camera = Camera.main;
        if (camera != null && Vector3.Distance(camera.transform.position, point) < 55f)
        {
            KnockSfx.Play("crunch", "crunch", point, .85f, .8f);
            GameHUD.FloatingText(point + Vector3.up * 1.2f, closing > 13f ? "KERRUNCH!" : "CLONK!", new Color(1f, .7f, .25f), 1.25f);
        }
    }

    static void Apply(TruckControllerV2 own, TruckControllerV2 other, Vector3 away, float closing)
    {
        var rb = own.Body;
        // Ownership can change between report and delivery: apply only on whoever simulates it NOW.
        if (rb.isKinematic || (own.IsSpawned && !own.IsOwner)) return;
        Vector3 kick = VehicleCrashRules.TruckKick(away, closing, rb.mass, other.Body.mass);
        if (kick.sqrMagnitude < .001f) return;
        // Stationary WheelCollider tire constraints absorbed queued velocity-change forces in live co-op tests.
        // Commit the same bounded delta-v immediately, on the current physics owner only, so the impact yields.
        rb.linearVelocity += kick;
        float side = Vector3.Dot(other.Body.position - rb.position, own.transform.right);
        float yaw = -Mathf.Sign(side) * Mathf.Min(VehicleCrashRules.MaximumYawKick, Mathf.Abs(side) * .09f) * Mathf.Clamp01(closing / 12f);
        Vector3 spin = rb.angularVelocity;
        spin.y = Mathf.Clamp(spin.y + yaw, -1.6f, 1.6f);
        rb.angularVelocity = spin;
        var impact = Ensure(own);
        impact.yieldUntil = Time.time + VehicleCrashRules.ParkingYieldSeconds;
        impact.AppliedImpacts++;
        impact.LastKick = kick;
        if (own.IsLocalPlayerSeated) Juice.Shake(Mathf.Lerp(.14f, .35f, Mathf.InverseLerp(2f, 25f, closing)), .25f);
    }

    public static float ParkingBrakeScale(TruckControllerV2 truck)
    {
        var impact = truck != null ? truck.GetComponent<TruckImpact>() : null;
        return impact != null && Time.time < impact.yieldUntil ? .08f : 1f;
    }
}
