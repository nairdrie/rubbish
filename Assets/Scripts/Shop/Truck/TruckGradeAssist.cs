using UnityEngine;

/// <summary>
/// How a truck deals with hills (Docs/CityAuthoring.md, "Terrain relief"). The town's streets now climb and fall, and
/// the truck is a light front-drive box on wheel colliders: left alone it stalls on the Bluffs road, rolls away when
/// you let go of the stick and runs off downhill with no engine braking. Pure numbers; <see cref="TruckGradeAssist"/>
/// applies them.
/// </summary>
public static class TruckGradeRules
{
    /// <summary>Share of the hill's pull that is cancelled while the driver is on the throttle or brake: a hill slows
    /// the climb and quickens the descent, but as if it were about a third as steep.</summary>
    public const float DrivenCancel = 0.65f;
    /// <summary>With the stick centred the hill's pull is cancelled outright: the truck coasts to a stop exactly as it
    /// does on the flat, and stays where it stopped.</summary>
    public const float CoastCancel = 1f;
    /// <summary>Extra weight on the wheels while they are on the ground (share of the truck's own): grip for climbing,
    /// braking and cornering on a slope.</summary>
    public const float Downforce = 0.3f;
    /// <summary>A wheel that has just left the road over a crest is pulled back down this hard (m/s^2 on top of
    /// gravity), while the body is still within <see cref="StickHeight"/> of the ground.</summary>
    public const float StickAccel = 9f, StickHeight = 0.9f;
    /// <summary>Slopes gentler than this (sine of the angle) are left alone.</summary>
    public const float MinSlope = 0.012f;
    /// <summary>Past top speed (downhill) the engine holds the truck back: m/s^2 per m/s over, capped.</summary>
    public const float OverspeedGain = 2.5f, OverspeedMax = 6f;

    public static float Cancel(bool neutral) => neutral ? CoastCancel : DrivenCancel;

    /// <summary>Deceleration (m/s^2) for a truck moving at <paramref name="speed"/> along the road when its top speed is
    /// <paramref name="maxSpeed"/>: 0 at or under it. A little slack so the limiter and this never fight on the flat.</summary>
    public static float OverspeedDecel(float speed, float maxSpeed)
    {
        float over = Mathf.Abs(speed) - maxSpeed * 1.04f;
        return over <= 0f ? 0f : Mathf.Min(OverspeedMax, over * OverspeedGain);
    }

    /// <summary>The part of gravity that pulls along a slope with the given ground normal.</summary>
    public static Vector3 SlopePull(Vector3 gravity, Vector3 normal)
    {
        if (normal.sqrMagnitude < 1e-6f) return Vector3.zero;
        normal.Normalize();
        return gravity - normal * Vector3.Dot(gravity, normal);
    }
}

/// <summary>On every truck (added by <see cref="TruckGradeRuntime"/>). Acts only on the machine that simulates the
/// truck's physics, so the result replicates like the rest of its motion.</summary>
[DisallowMultipleComponent]
public sealed class TruckGradeAssist : MonoBehaviour
{
    TruckControllerV2 truck;
    Rigidbody body;
    WheelCollider[] wheels;

    /// <summary>Last measured slope along the truck's nose, in percent (positive = climbing). For the HUD and QA.</summary>
    public float GradePercent { get; private set; }

    void Awake()
    {
        truck = GetComponent<TruckControllerV2>();
        body = GetComponent<Rigidbody>();
        wheels = GetComponentsInChildren<WheelCollider>(true);
    }

    void FixedUpdate()
    {
        if (truck == null || body == null || body.isKinematic) return;
        if (truck.IsSpawned && !truck.IsOwner) return;

        Vector3 normal = Vector3.zero; int grounded = 0, total = 0;
        foreach (var wheel in wheels)
        {
            if (wheel == null || !wheel.enabled) continue;
            total++;
            if (wheel.GetGroundHit(out var hit)) { normal += hit.normal; grounded++; }
        }
        if (total == 0) return;
        Vector3 gravity = Physics.gravity;
        // As the truck's own FixedUpdate sees it: parked the moment the driver gets out, even on a guest's machine that
        // still shows them in the seat until the server's answer lands.
        bool driven = truck.AnyDriver;

        if (grounded == 0)
        {
            // Just over a crest: stay with the road instead of sailing off it.
            if (driven && TownRelief.Covers(body.position) && body.position.y - TownRelief.Height(body.position) < TruckGradeRules.StickHeight + 0.6f)
                body.AddForce(Vector3.down * TruckGradeRules.StickAccel, ForceMode.Acceleration);
            return;
        }
        normal /= grounded;
        if (normal.y < 0.5f) return;                                   // on its side or against a wall: not a road
        Vector3 pull = TruckGradeRules.SlopePull(gravity, normal);
        GradePercent = -Vector3.Dot(pull.normalized, transform.forward) * pull.magnitude / Mathf.Max(0.01f, gravity.magnitude) * 100f;

        Vector2 input = Vector2.zero;
        if (truck.InputOverride != null) input = truck.InputOverride();
        else if (truck.IsLocalPlayerSeated || truck.IsLocalDriver) input = GameControls.DriveAxes();
        bool neutral = Mathf.Abs(input.y) <= TruckCoastRules.InputDeadZone;

        if (driven)
        {
            if (pull.magnitude > TruckGradeRules.MinSlope * gravity.magnitude)
                body.AddForce(-pull * TruckGradeRules.Cancel(neutral), ForceMode.Acceleration);
            body.AddForce(-normal * (gravity.magnitude * TruckGradeRules.Downforce * grounded / total), ForceMode.Acceleration);
        }

        // Engine braking: the torque limiter only stops the engine pushing; it never held the truck back downhill.
        Vector3 along = Vector3.ProjectOnPlane(body.linearVelocity, normal);
        float decel = TruckGradeRules.OverspeedDecel(along.magnitude, truck.maxSpeed);
        if (decel > 0f && along.sqrMagnitude > 0.01f) body.AddForce(-along.normalized * decel, ForceMode.Acceleration);
    }
}

/// <summary>Keeps a <see cref="TruckGradeAssist"/> on every truck (created at startup, like the dumpster lift's).</summary>
public sealed class TruckGradeRuntime : MonoBehaviour
{
    static TruckGradeRuntime instance;
    float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("[Truck grade assist]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<TruckGradeRuntime>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 1f;
        var trucks = TruckControllerV2.All;
        for (int i = 0; i < trucks.Count; i++)
            if (trucks[i] != null && trucks[i].GetComponent<TruckGradeAssist>() == null) trucks[i].gameObject.AddComponent<TruckGradeAssist>();
    }
}
