using UnityEngine;

/// <summary>
/// The Scrapazon Night Shift Headlamp: a little lamp strapped to the local player's forehead with a spot light
/// that clicks on (with a derpy flicker) when it gets dark and off at dawn. Added at runtime to
/// PlayerController.Local by <see cref="ShopEffectsRunner"/> once "headlamp" is owned. Off while driving
/// (the player is hidden in the cab; the truck has its own headlights).
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1010)] // after ChuckPuppet (1000) has leaned the chest/head and popped the cap this frame
public class ShopHeadlamp : MonoBehaviour
{
    const float OnAtNight01 = 0.45f;     // dusk threshold (GameClock.Night01)
    const float MaxIntensity = 11f;
    const float Range = 22f;

    static readonly Color LampColor = new Color(1f, 0.93f, 0.72f);

    PlayerController player;
    Transform head;
    Transform mount;     // optional "HeadlampMount" child (Chuck's cap crown); used instead of the head bone
    float nextHeadSearch;
    GameObject rig;
    GameObject visual;   // strap + housing + lens (not the beam): hidden while the character hides its cap from a close camera
    Transform housing, lens;
    Light spot;
    Material lensMat;
    bool lit;
    bool builtUv;
    float level;         // 0..1 displayed brightness
    float flickerUntil;
    int lastNightDay = -1;

    void Awake() => player = GetComponent<PlayerController>();

    void OnDestroy()
    {
        if (rig != null) Destroy(rig);
        if (lensMat != null) Destroy(lensMat);
    }

    void LateUpdate()
    {
        if (!ShopEffects.HasHeadlamp)
        {
            if (rig != null && rig.activeSelf) rig.SetActive(false);
            return;
        }
        bool uv = UpgradeEffects.UvDetect > 0f; // headlamp L2: UV Treasure Headlamp (the bag glow itself is UvLamp's)
        if (rig != null && uv != builtUv)
        {
            Destroy(rig);
            if (lensMat != null) Destroy(lensMat);
            rig = null;
        }
        if (rig == null) { builtUv = uv; BuildRig(); if (uv && Application.isPlaying) Juice.PopIn(rig.transform, 0.3f); }
        if (!rig.activeSelf) rig.SetActive(true);

        FollowHead();

        bool driving = player != null && (player.IsDriving || player.currentTruck != null);
        bool want = GameClock.Night01 >= OnAtNight01 && !driving;
        if (want != lit) Toggle(want, driving);

        float target = lit ? 1f : 0f;
        level = Mathf.MoveTowards(level, target, Time.unscaledDeltaTime * (lit ? 3f : 5f));
        float shown = level;
        if (Time.unscaledTime < flickerUntil) shown *= Mathf.PerlinNoise(Time.unscaledTime * 30f, 0.3f) > 0.45f ? 1f : 0.1f;
        spot.intensity = MaxIntensity * shown;
        spot.enabled = shown > 0.01f;
        if (lensMat != null)
        {
            var c = Color.Lerp(new Color(0.35f, 0.33f, 0.25f), LampColor * 4f, shown);
            lensMat.SetColor("_EmissionColor", c * shown);
            lensMat.SetColor("_BaseColor", Color.Lerp(new Color(0.7f, 0.7f, 0.65f), LampColor, shown));
        }
    }

    void Toggle(bool on, bool driving)
    {
        lit = on;
        if (driving) return; // silent while hidden in the cab
        Vector3 at = rig.transform.position;
        Sfx.Play("click", at, 0.7f, on ? 1.3f : 0.9f);
        if (housing != null) Juice.Punch(housing, 0.35f, 0.2f);
        if (on)
        {
            flickerUntil = Time.unscaledTime + 0.45f;
            int day = GameClock.DayIndex;
            if (day != lastNightDay)
            {
                lastNightDay = day;
                GameHUD.FloatingText(at + Vector3.up * 0.4f, "*click* headlamp on", LampColor, 0.8f);
            }
        }
    }

    // ---------------- rig ----------------

    void FollowHead()
    {
        if (head == null && Time.unscaledTime >= nextHeadSearch)
        {
            nextHeadSearch = Time.unscaledTime + 1f;
            head = FindHead();
            mount = FindMount();
        }
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        if (mount != null)
        {
            // The character marks where the lamp goes (e.g. on the cap crown, riding the cap pop): no extra offsets.
            rig.transform.position = mount.position;
            // Camera squeezed into his head (tight shop corners): the character deactivates its cap, so the lamp body
            // goes too (the beam stays on). SetActive only: the truck/chess code owns renderer.enabled.
            bool show = mount.gameObject.activeInHierarchy;
            if (visual != null && visual.activeSelf != show)
            {
                visual.SetActive(show);
                if (show)
                {
                    // Rare toggle: match the cap's renderers (the character just re-synced them to the body, which the
                    // truck / chess / sort station may have hidden meanwhile).
                    var capR = mount.parent != null ? mount.parent.GetComponentInChildren<Renderer>() : null;
                    bool vis = capR != null ? capR.enabled : !(player != null && (player.IsDriving || player.IsPlayingChess));
                    foreach (var r in visual.GetComponentsInChildren<Renderer>()) r.enabled = vis;
                }
            }
        }
        else
        {
            Vector3 basePos = head != null ? head.position : transform.position + Vector3.up * 1.62f;
            rig.transform.position = basePos + Vector3.up * 0.13f + fwd * 0.1f;
        }
        rig.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        spot.transform.localRotation = Quaternion.Euler(12f, 0f, 0f); // aim a bit down at the curb
    }

    Transform FindMount()
    {
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (t.name == "HeadlampMount") return t;
        return null;
    }

    Transform FindHead()
    {
        var anim = GetComponentInChildren<Animator>();
        if (anim != null && anim.isHuman && anim.avatar != null)
        {
            var h = anim.GetBoneTransform(HumanBodyBones.Head);
            if (h != null) return h;
        }
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToLowerInvariant();
            if (n == "head" || n.EndsWith(":head") || n == "head_jnt" || n == "bip01 head") return t;
        }
        return null;
    }

    void BuildRig()
    {
        rig = new GameObject("Scrapazon Headlamp");
        rig.transform.SetParent(transform, false);

        var bodyMat = ShopMaterials.Get("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
        lensMat = new Material(ShopMaterials.Get("mat/shop_headlamp_lens", LampColor, 0.8f));
        lensMat.EnableKeyword("_EMISSION");
        lensMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

        visual = new GameObject("Visual");
        visual.transform.SetParent(rig.transform, false);

        // strap band around the forehead
        var strap = ShopMaterials.Primitive(PrimitiveType.Cylinder, "Strap", visual.transform, bodyMat);
        strap.localPosition = new Vector3(0f, -0.02f, -0.1f);
        strap.localScale = new Vector3(0.2f, 0.018f, 0.2f);

        housing = ShopMaterials.Primitive(PrimitiveType.Cylinder, "Housing", visual.transform, bodyMat);
        housing.localRotation = Quaternion.Euler(90f, 0f, 0f);
        housing.localScale = new Vector3(0.085f, 0.025f, 0.085f);

        lens = ShopMaterials.Primitive(PrimitiveType.Sphere, "Lens", visual.transform, lensMat);
        lens.localPosition = new Vector3(0f, 0f, 0.025f);
        lens.localScale = new Vector3(0.07f, 0.07f, 0.03f);

        if (builtUv)
        {
            // UV Treasure Headlamp: a ring of purple LEDs around the lens and a little antenna (very scientific).
            var uvMat = ShopModKit.Emissive("mat/shop_uv_led", new Color(0.62f, 0.25f, 1f), 3f);
            for (int i = 0; i < 6; i++)
            {
                float a = i * 60f * Mathf.Deg2Rad;
                ShopModKit.Prim(PrimitiveType.Sphere, "UV LED", visual.transform, uvMat,
                    new Vector3(Mathf.Cos(a) * 0.05f, Mathf.Sin(a) * 0.05f, 0.022f), Vector3.one * 0.018f);
            }
            ShopModKit.Prim(PrimitiveType.Cylinder, "Antenna", visual.transform, bodyMat, new Vector3(0.04f, 0.06f, -0.01f), new Vector3(0.008f, 0.04f, 0.008f));
            ShopModKit.Prim(PrimitiveType.Sphere, "Antenna Tip", visual.transform, uvMat, new Vector3(0.04f, 0.1f, -0.01f), Vector3.one * 0.016f);
        }

        var lightGo = new GameObject("Beam");
        lightGo.transform.SetParent(rig.transform, false);
        lightGo.transform.localPosition = new Vector3(0f, 0f, 0.05f);
        spot = lightGo.AddComponent<Light>();
        spot.type = LightType.Spot;
        spot.color = LampColor;
        spot.range = Range;
        spot.spotAngle = 68f;
        spot.innerSpotAngle = 38f;
        spot.intensity = 0f;
        spot.shadows = LightShadows.None;
        spot.enabled = false;

        // Respect the "hide the body while driving" toggles: match the player's current renderer state.
        bool hidden = player != null && player.IsDriving;
        foreach (var r in rig.GetComponentsInChildren<Renderer>()) r.enabled = !hidden;
    }
}
