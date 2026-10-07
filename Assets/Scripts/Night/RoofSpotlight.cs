using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A roof spotlight on every truck (F4, Docs/Multiplayer.md §14.4), on at night while the truck is crewed (and 30 s after). A rider on the truck's side ladder rung (F1's
/// <see cref="Riding.RidersOn"/>, solo or co-op) aims it with the mouse for the driver; their aim goes to every machine through
/// <see cref="NightNet"/> (8 Hz, 2 bytes). Nobody aiming: it sweeps ahead and locks onto the nearest raccoon. Raccoons caught in
/// the beam are illuminated, not repelled: a stock truck must not automatically clear the raccoon encounter.
/// </summary>
[DisallowMultipleComponent]
public class RoofSpotlight : MonoBehaviour
{
    /// <summary>Truck-local mount on the cab roof.</summary>
    public static readonly Vector3 MountLocal = new Vector3(0f, 3.05f, 2.75f);
    public const float Range = 42f, Angle = 22f, DazzleRange = 26f, DazzleAngle = 12f;
    static readonly Color BeamColor = new Color(1f, 0.97f, 0.85f);

    static readonly List<RoofSpotlight> all = new List<RoofSpotlight>();
    public static IReadOnlyList<RoofSpotlight> All => all;

    public TruckControllerV2 Truck { get; private set; }
    Transform pivot, head;
    Light spot;
    float yaw, pitch = -8f;          // truck space, degrees
    float targetYaw, targetPitch = -8f;
    float remoteAimUntil;
    ulong remoteAimer = ulong.MaxValue;
    float nextSend, sweepT;
    byte sentYaw, sentPitch;
    float level;
    float lastCrewed = -999f;
    const float LingerSeconds = 30f;
    bool aimingLocally;

    /// <summary>The local player is steering this light right now.</summary>
    public bool AimingLocally => aimingLocally;
    public bool Lit => level > 0.5f;

    public static void Ensure(TruckControllerV2 t)
    {
        if (t == null || t.GetComponentInChildren<RoofSpotlight>(true) != null) return;
        var go = new GameObject("RoofSpotlight");
        go.transform.SetParent(t.transform, false);
        go.transform.localPosition = MountLocal;
        var s = go.AddComponent<RoofSpotlight>();
        s.Truck = t;
        s.Build();
    }

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() => all.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    void Build()
    {
        var dark = CritterLook.Mat(new Color(0.18f, 0.18f, 0.2f), 0.5f);
        var chrome = CritterLook.Mat(new Color(0.75f, 0.75f, 0.78f), 0.85f);
        CritterLook.Part(transform, PrimitiveType.Cylinder, "Base", Vector3.zero, new Vector3(0.28f, 0.05f, 0.28f), dark);
        pivot = CritterLook.Node(transform, "Pivot", new Vector3(0f, 0.16f, 0f));
        head = CritterLook.Node(pivot, "Head", Vector3.zero);
        CritterLook.Part(head, PrimitiveType.Cylinder, "Can", Vector3.zero, new Vector3(0.3f, 0.2f, 0.3f), chrome, new Vector3(90f, 0f, 0f));
        CritterLook.Part(head, PrimitiveType.Sphere, "Glass", new Vector3(0f, 0f, 0.2f), new Vector3(0.27f, 0.27f, 0.05f), CritterLook.Mat(BeamColor, 0.9f, 2.5f), default, false);
        var lg = new GameObject("Spot");
        lg.transform.SetParent(head, false);
        lg.transform.localPosition = new Vector3(0f, 0f, 0.25f);
        spot = lg.AddComponent<Light>();
        spot.type = LightType.Spot;
        spot.color = BeamColor;
        spot.range = Range;
        spot.spotAngle = Angle;
        spot.innerSpotAngle = Angle * 0.6f;
        spot.shadows = LightShadows.None;
        spot.enabled = false;
    }

    void Update()
    {
        if (Truck == null) { Destroy(gameObject); return; }
        // On at night while the truck is crewed (a driver or a rider), and for a while after they hop out to grab bags.
        if (Truck.HasDriver || Riding.AnyRiders(Truck)) lastCrewed = Time.time;
        bool night = GameClock.Night01 >= 0.45f && Time.time - lastCrewed < LingerSeconds;
        level = Mathf.MoveTowards(level, night ? 1f : 0f, Time.deltaTime * 2f);
        bool show = level > 0.01f;
        spot.enabled = show;
        if (!show) { aimingLocally = false; return; }
        spot.intensity = 26f * level;

        aimingLocally = LocalRiderCanAim();
        if (aimingLocally) AimFromCamera();
        else if (Time.time > remoteAimUntil) AutoAim();

        yaw = Mathf.MoveTowardsAngle(yaw, targetYaw, Time.deltaTime * (aimingLocally ? 400f : 90f));
        pitch = Mathf.MoveTowards(pitch, targetPitch, Time.deltaTime * (aimingLocally ? 300f : 60f));
        pivot.localRotation = Quaternion.Euler(0f, yaw, 0f);
        head.localRotation = Quaternion.Euler(-pitch, 0f, 0f);

        // Illumination only. This free, auto-aiming light is not an automatic scare weapon.
    }

    static readonly List<RiderInfo> riders = new List<RiderInfo>(4);

    /// <summary>This machine's player rides this truck on a side rung (F1's <see cref="Riding.RidersOn"/>).</summary>
    bool LocalRiderCanAim()
    {
        Riding.RidersOn(Truck, riders);
        for (int i = 0; i < riders.Count; i++)
            if (riders[i].isLocal && (riders[i].rung || HitchStep.StepLocal.Length <= 2)) return true;
        return false;
    }

    void AimFromCamera()
    {
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 local = Truck.transform.InverseTransformDirection(cam.transform.forward);
        targetYaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -160f, 160f);
        targetPitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg, -35f, 15f);
        var n = NightNet.Instance;
        if (!NightNet.Streaming || Time.unscaledTime < nextSend) return;
        if (Truck.NetworkObject == null || !Truck.NetworkObject.IsSpawned) return;
        nextSend = Time.unscaledTime + 0.125f;
        byte y = (byte)(Mathf.RoundToInt(Mathf.Repeat(targetYaw, 360f) / 360f * 255f) & 0xFF);
        byte p = (byte)Mathf.RoundToInt(Mathf.InverseLerp(-35f, 15f, targetPitch) * 255f);
        if (y == sentYaw && p == sentPitch) return;
        sentYaw = y; sentPitch = p;
        n.SendSpotAim(Truck.NetworkObject.NetworkObjectId, y, p);
    }

    /// <summary>Someone else's aim arrived.</summary>
    public static void ApplyAim(ulong truckNetId, byte y, byte p, ulong aimer)
    {
        foreach (var s in all)
        {
            if (s == null || s.Truck == null || s.Truck.NetworkObject == null || s.Truck.NetworkObject.NetworkObjectId != truckNetId) continue;
            if (s.aimingLocally) return;
            s.targetYaw = Mathf.DeltaAngle(0f, y / 255f * 360f);
            s.targetPitch = Mathf.Lerp(-35f, 15f, p / 255f);
            s.remoteAimUntil = Time.time + 1f;
            s.remoteAimer = aimer;
            return;
        }
    }

    void AutoAim()
    {
        // Lock onto the nearest raccoon ahead; otherwise a lazy sweep.
        Critter best = null;
        float bestD = 30f;
        foreach (var c in Critter.All)
        {
            if (c == null || c.Despawning || !(c is Raccoon || c is RaccoonKing)) continue;
            Vector3 d = c.transform.position - transform.position;
            float dist = CritterWorld.Flat(d).magnitude;
            if (dist < bestD) { bestD = dist; best = c; }
        }
        if (best != null)
        {
            Vector3 local = Truck.transform.InverseTransformPoint(best.transform.position + Vector3.up * 0.3f) - MountLocal;
            targetYaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -160f, 160f);
            targetPitch = Mathf.Clamp(Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg, -35f, 15f);
            return;
        }
        sweepT += Time.deltaTime;
        targetYaw = Mathf.Sin(sweepT * 0.35f) * 45f;
        targetPitch = -10f;
    }

}
