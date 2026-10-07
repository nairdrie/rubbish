using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Headlamps for everyone at night (F4, Docs/Multiplayer.md §14.4). Every player copy gets a little strap lamp that clicks on
/// at dusk (off while driving or indoors). Cheap by design: at most one spot light per player, and only this machine's own
/// player plus the nearest <see cref="MaxRemoteLights"/> others within <see cref="RemoteLightRange"/> m get a real light; the
/// rest show just the glowing lens (no light). The Scrapazon Headlamp (<see cref="ShopHeadlamp"/>) replaces
/// your own when you own it.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1011)]
public class PlayerHeadlamp : MonoBehaviour
{
    public const float OnAtNight01 = 0.45f;
    public const int MaxRemoteLights = 2;
    public const float RemoteLightRange = 35f;
    static readonly Color LampColor = new Color(1f, 0.92f, 0.7f);

    static readonly List<PlayerHeadlamp> all = new List<PlayerHeadlamp>();
    public static IReadOnlyList<PlayerHeadlamp> All => all;
    /// <summary>Real lights on this machine right now (perf census).</summary>
    public static int LitCount { get; private set; }

    PlayerController pc;
    Transform mount, head;
    float nextSearch;
    int searches;
    GameObject rig;
    Light spot;
    Material lensMat;
    float level;
    bool wantLight;
    public bool IsLocal => pc != null && NightPlayers.IsLocal(pc);
    public bool On { get; private set; }

    void Awake() => pc = GetComponent<PlayerController>();
    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() { all.Remove(this); if (rig != null) rig.SetActive(false); }

    void OnDestroy()
    {
        if (rig != null) Destroy(rig);
        if (lensMat != null) Destroy(lensMat);
    }

    /// <summary>The manager's light budget (decided every half second).</summary>
    public void SetWantLight(bool v) => wantLight = v;

    void LateUpdate()
    {
        if (pc == null) return;
        bool shopLamp = IsLocal && ShopEffects.HasHeadlamp; // the Scrapazon one is brighter: let it do the job
        bool driving = pc.IsDriving || pc.currentTruck != null;
        bool indoors = NightPlayers.IsIndoors(pc);
        bool want = !shopLamp && GameClock.Night01 >= OnAtNight01 && !driving && !indoors && !pc.IsPlayingChess;
        if (!want && rig == null) { On = false; return; }
        if (rig == null) Build();
        if (want != On)
        {
            On = want;
            if (want && IsLocal && Time.timeSinceLevelLoad > 2f) Sfx.Play("click", rig.transform.position, 0.5f, 1.3f);
        }
        level = Mathf.MoveTowards(level, On ? 1f : 0f, Time.deltaTime * 3f);
        bool show = level > 0.01f;
        if (rig.activeSelf != show) rig.SetActive(show);
        if (!show) { if (spot.enabled) spot.enabled = false; return; }
        Follow();
        bool real = IsLocal || wantLight;
        spot.enabled = real;
        spot.intensity = (IsLocal ? 7f : 5f) * level;
        if (lensMat != null) lensMat.SetColor("_EmissionColor", LampColor * (3f * level));
    }

    void Follow()
    {
        if ((mount == null && head == null) && Time.unscaledTime >= nextSearch && searches < 6)
        {
            searches++;
            nextSearch = Time.unscaledTime + 1f;
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.name == "HeadlampMount") { mount = t; break; }
            if (mount == null)
            {
                var anim = GetComponentInChildren<Animator>();
                if (anim != null && anim.isHuman && anim.avatar != null) head = anim.GetBoneTransform(HumanBodyBones.Head);
            }
        }
        Vector3 fwd = CritterWorld.Flat(transform.forward);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 p = mount != null ? mount.position + fwd * 0.05f
                  : head != null ? head.position + Vector3.up * 0.13f + fwd * 0.1f
                  : transform.position + Vector3.up * 1.55f + fwd * 0.12f;
        rig.transform.SetPositionAndRotation(p, Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(14f, 0f, 0f));
    }

    void Build()
    {
        rig = new GameObject("Night Headlamp");
        rig.transform.SetParent(transform, false);
        var body = CritterLook.Mat(new Color(0.15f, 0.15f, 0.18f), 0.4f);
        lensMat = new Material(CritterLook.Mat(LampColor, 0.8f, 2f));
        CritterLook.Part(rig.transform, PrimitiveType.Cylinder, "Housing", Vector3.zero, new Vector3(0.08f, 0.022f, 0.08f), body, new Vector3(90f, 0f, 0f), false);
        CritterLook.Part(rig.transform, PrimitiveType.Sphere, "Lens", new Vector3(0f, 0f, 0.022f), new Vector3(0.065f, 0.065f, 0.025f), lensMat, default, false);
        var lightGo = new GameObject("Beam Light");
        lightGo.transform.SetParent(rig.transform, false);
        lightGo.transform.localPosition = new Vector3(0f, 0f, 0.05f);
        spot = lightGo.AddComponent<Light>();
        spot.type = LightType.Spot;
        spot.color = LampColor;
        spot.range = 18f;
        spot.spotAngle = 62f;
        spot.innerSpotAngle = 34f;
        spot.shadows = LightShadows.None;
        spot.renderMode = LightRenderMode.ForcePixel;
        spot.enabled = false;
        // No beam mesh: the real light (yours + the 2 nearest) and the glowing lens read well enough at night, and cost nothing.
    }

    // ---------------------------------------------------------------- the light budget

    static readonly List<PlayerHeadlamp> ranked = new List<PlayerHeadlamp>();

    /// <summary>Manager (NightRunner, every 0.5 s): give real lights to the nearest remote lamps.</summary>
    public static void Budget(Vector3 eye)
    {
        ranked.Clear();
        int lit = 0;
        foreach (var h in all)
        {
            if (h == null) continue;
            if (h.IsLocal) { h.wantLight = true; if (h.On) lit++; continue; }
            h.wantLight = false;
            if (h.On && (h.transform.position - eye).sqrMagnitude < RemoteLightRange * RemoteLightRange) ranked.Add(h);
        }
        ranked.Sort((a, b) => (a.transform.position - eye).sqrMagnitude.CompareTo((b.transform.position - eye).sqrMagnitude));
        for (int i = 0; i < ranked.Count && i < MaxRemoteLights; i++) { ranked[i].wantLight = true; lit++; }
        LitCount = lit;
    }
}
