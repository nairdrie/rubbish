using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The street front of The Lucky Raccoon (Docs/Casino.md §2): the neon sign, the bulb marquee, the padded double doors,
/// the red carpet and rope posts, the roof searchlights and the light spilling from the doorway. It dresses the centre
/// bay of the decorative storefront parcel <see cref="CasinoLayout.FrontSiteId"/> on Coupon Street.
/// <para>Built at runtime on every machine, once per town load, from the prefab <c>Setup_Casino</c> bakes into
/// <c>Resources/Casino/CasinoFront</c> (like <see cref="DumpsterRuntime"/> and the Scavenger's fence): the baked town is
/// not touched and needs no bake. It has no colliders (nothing to walk into, nothing for traffic, kids or pathing to
/// notice), no NetworkObject and no networking: lights and music are local cosmetics.</para>
/// </summary>
public class CasinoFront : MonoBehaviour
{
    public static CasinoFront Instance { get; private set; }

    [Tooltip("Centre of the street door at ground level; +Z points INTO the building.")]
    public Transform door;
    [Tooltip("Pieces that lie or stand on the apron (carpet, rope runs): snapped to the ground and pitched to its slope.")]
    public Transform[] onGround = new Transform[0];
    [Tooltip("Searchlight pivots on the roof (sweep at night).")]
    public Transform[] beams = new Transform[0];
    public Renderer[] beamRenderers = new Renderer[0];
    [ColorUsage(true, true)] public Color beamColor = new Color(1f, 0.93f, 0.75f, 0.009f);
    [Tooltip("The warm pool from the doorway onto the sidewalk.")]
    public Light doorLight;
    public float doorLightDay = 0.25f, doorLightNight = 2.4f;
    [Tooltip("Where the muffled music comes from.")]
    public Transform musicAnchor;

    /// <summary>1 right after someone used the door, easing to 0: the music leaks out (see <see cref="CasinoMusic"/>).</summary>
    public static float DoorOpen01 => Mathf.Clamp01(1f - (Time.unscaledTime - doorOpenedAt) / 1.8f);
    static float doorOpenedAt = -100f;
    public static void PulseDoor() => doorOpenedAt = Time.unscaledTime;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    const int GroundMask = 1 << 6;
    MaterialPropertyBlock block;
    Quaternion[] beamRest;

    void OnEnable()
    {
        Instance = this;
        beamRest = new Quaternion[beams.Length];
        for (int i = 0; i < beams.Length; i++) beamRest[i] = beams[i] != null ? beams[i].localRotation : Quaternion.identity;
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        float night = Application.isPlaying ? GameClock.Night01 : previewNight;
        float fx = Application.isPlaying ? Mathf.Lerp(0.45f, 1f, Mathf.Clamp01(GameSettings.ScreenFx01)) : 1f;
        float t = Time.time;
        // Searchlights: two slow crossing sweeps (about one pass every 9 s: nothing flashes).
        for (int i = 0; i < beams.Length; i++)
        {
            var b = beams[i];
            if (b == null) continue;
            bool on = night > 0.04f;
            if (b.gameObject.activeSelf != on) b.gameObject.SetActive(on);
            if (!on) continue;
            float sweep = Mathf.Sin(t * 0.7f + i * 2.3f) * 34f, nod = Mathf.Sin(t * 0.43f + i * 1.1f) * 9f;
            b.localRotation = beamRest[i] * Quaternion.Euler(nod, 0f, sweep);
        }
        if (block == null) block = new MaterialPropertyBlock();
        for (int i = 0; i < beamRenderers.Length; i++)
        {
            var r = beamRenderers[i];
            if (r == null) continue;
            var c = beamColor;
            c.a *= night * fx;
            r.GetPropertyBlock(block);
            block.SetColor(BaseColorId, c);
            r.SetPropertyBlock(block);
        }
        if (doorLight != null) doorLight.intensity = Mathf.Lerp(doorLightDay, doorLightNight, night);
    }

    /// <summary>Edit-mode preview renders: how much night to show.</summary>
    public static float previewNight = 1f;
    /// <summary>Edit-mode preview renders: pose the beams and the door light once.</summary>
    public void PreviewTick() { if (beamRest == null || beamRest.Length != beams.Length) OnEnable(); Update(); }

    // ------------------------------------------------------------------ placement

    /// <summary>Where a player stands after leaving: on the carpet in front of the door, facing the street.</summary>
    public static Pose StreetPose
    {
        get
        {
            Vector3 p; Quaternion intoBuilding;
            var f = Instance;
            if (f != null && f.door != null) { p = f.door.TransformPoint(new Vector3(0f, 0f, -1.9f)); intoBuilding = f.door.rotation; }
            else if (havePose) { p = lastDoor + lastRot * new Vector3(0f, 0f, -1.9f); intoBuilding = lastRot; }
            else { p = CasinoLayout.FrontFallback + new Vector3(0f, 0f, -1.9f); intoBuilding = Quaternion.identity; }
            p.y = GroundY(p, p.y);
            return new Pose(p + Vector3.up * 0.05f, FlowUtil.Upright(intoBuilding * Quaternion.Euler(0f, 180f, 0f)));
        }
    }

    static bool havePose, missingWarned;
    static Vector3 lastDoor;
    static Quaternion lastRot = Quaternion.identity;
    static int tries;

    static float GroundY(Vector3 p, float fallback)
    {
        float relief = TownRelief.Height(p.x, p.z);
        Vector3 from = new Vector3(p.x, Mathf.Max(relief, fallback) + 3f, p.z);
        if (Physics.Raycast(from, Vector3.down, out var hit, 8f, GroundMask, QueryTriggerInteraction.Ignore)) return hit.point.y;
        return TownRelief.Flat ? fallback : relief;
    }

    /// <summary>What you SEE underfoot. The parcel's paving finish is a collider-less skin draped 3 cm over the relief,
    /// while the terrain collider lies a few centimetres under it: lay visuals on the relief, not on the collider.</summary>
    static float VisualGroundY(Vector3 p, float fallback)
    {
        float y = GroundY(p, fallback);
        if (!TownRelief.Flat) y = Mathf.Max(y, TownRelief.Height(p.x, p.z) + 0.04f);
        return y;
    }
    static CoastalInfillSite parcel;

    /// <summary>Build the front when the town is up and it is missing (called twice a second by <see cref="CasinoRunner"/>).</summary>
    public static void Ensure()
    {
        if (Instance != null || NetBoot.Quitting) return;
        if (!DepotRowWorld.IsSceneLoaded(DepotRowWorld.TownSceneName)) { tries = 0; return; }
        if (PickupStop.All.Count == 0) return; // the town is still coming up
        var map = TownMapData.Load();
        if (map == null || map.layoutId != CoastalCityLayout.LayoutId) return; // not the coastal town: no Coupon Street

        var prefab = Resources.Load<GameObject>(CasinoLayout.FrontResource);
        if (prefab == null)
        {
            if (!missingWarned)
            {
                missingWarned = true;
                Debug.LogWarning("[Casino] No Resources/" + CasinoLayout.FrontResource + " prefab (run Rubbish/Setup/Casino): no street front.");
            }
            return;
        }

        if (!TryLocate(out Vector3 pos, out Quaternion rot, out Scene scene, out bool found) && ++tries < 6) return; // give a slow load a few seconds
        if (!found) Debug.LogWarning("[Casino] Storefront parcel '" + CasinoLayout.FrontSiteId + "' not found: the front stands at its fallback spot.");

        var go = Instantiate(prefab, pos, rot);
        go.name = "The Lucky Raccoon (street front)";
        if (scene.IsValid() && scene.isLoaded) SceneManager.MoveGameObjectToScene(go, scene); // dies with the town
        var front = go.GetComponent<CasinoFront>();
        if (front != null) front.SnapToGround();
        havePose = true; lastDoor = pos; lastRot = rot;
        Debug.Log($"[Casino] Street front up at ({pos.x:0.0}, {pos.y:0.00}, {pos.z:0.0}).");
    }

    /// <summary>Where the front goes: the centre bay of the storefront parcel (its entrance tells us where the shop face
    /// and the building's base really are), or the fallback spot. Returns false while the parcel is missing.</summary>
    public static bool TryLocate(out Vector3 pos, out Quaternion rot, out Scene scene, out bool found)
    {
        CoastalInfillSite site = null;
        var sites = FindObjectsByType<CoastalInfillSite>(FindObjectsSortMode.None);
        for (int i = 0; i < sites.Length; i++)
            if (sites[i] != null && sites[i].siteId == CasinoLayout.FrontSiteId) { site = sites[i]; break; }
        found = site != null;
        parcel = site;
        if (site == null)
        {
            rot = Quaternion.identity;
            pos = CasinoLayout.FrontFallback;
            pos.y = TownRelief.Height(pos.x, pos.z);
            scene = default;
            return false;
        }
        rot = site.transform.rotation;
        pos = site.transform.TransformPoint(new Vector3(0f, 0f, CasinoLayout.FrontFaceDepth));
        scene = site.gameObject.scene;
        float best = float.MaxValue;
        foreach (Transform child in site.transform)
        {
            if (child.name != "Recessed glazed entrance") continue;
            Vector3 local = site.transform.InverseTransformPoint(child.position);
            if (Mathf.Abs(local.x) >= best) continue;
            best = Mathf.Abs(local.x);
            // InfillStorefront: the entrance box is centred 1.5 m up and 0.06 m in front of the shop face.
            pos = site.transform.TransformPoint(new Vector3(local.x, local.y - 1.5f, local.z + 0.06f));
        }
        return true;
    }

    /// <summary>The apron in front of the shop follows the relief: lay the carpet and the rope runs on it.</summary>
    public void SnapToGround()
    {
        Physics.SyncTransforms();
        foreach (var t in onGround)
        {
            if (t == null) continue;
            // Each piece is modelled along its local -Z (toward the street) from its pivot; "Far" marks its street end.
            var far = t.Find("Far");
            Vector3 a = t.position, b = far != null ? far.position : a;
            float ya = VisualGroundY(a, a.y), yb = VisualGroundY(b, b.y);
            t.position = new Vector3(a.x, ya, a.z);
            float run = new Vector2(b.x - a.x, b.z - a.z).magnitude;
            if (run > 0.2f)
            {
                float pitch = Mathf.Atan2(yb - ya, run) * Mathf.Rad2Deg;
                t.localRotation = Quaternion.Euler(Mathf.Clamp(pitch, -12f, 12f), 0f, 0f);
            }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        parcel = null;
        havePose = false;
        missingWarned = false;
        tries = 0;
        doorOpenedAt = -100f;
    }
}
