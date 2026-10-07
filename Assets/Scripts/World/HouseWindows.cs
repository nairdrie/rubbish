using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// House windows (and porch lights) for the day/night cycle, driven by <see cref="DayNightSky"/>. The Suburbs
/// houses share their materials, so instead of touching assets we find every renderer with an "M_windows"
/// slot at runtime and swap that slot between the original glass and a few shared runtime materials:
/// warm, cozy-orange, flickery blue TV, and one PARTY house cycling colours ("woo!"). Each house gets a
/// deterministic little life: lights on around dusk, bedtime somewhere 21:30–1:00 (a few night owls stay up),
/// early birds light up before sunrise. Porch lights ("M_emissive" slot) stay on dusk to dawn.
/// </summary>
[DisallowMultipleComponent]
public class HouseWindows : MonoBehaviour
{
    [Header("Look")]
    public Color warmBase = new Color(1f, 0.84f, 0.5f);
    [ColorUsage(false, true)] public Color warmEmission = new Color(2.6f, 1.75f, 0.7f);
    [ColorUsage(false, true)] public Color cozyEmission = new Color(2.4f, 1.25f, 0.45f);
    [ColorUsage(false, true)] public Color tvEmission = new Color(0.7f, 1.0f, 2.1f);
    [ColorUsage(false, true)] public Color porchEmission = new Color(4.2f, 2.9f, 1.1f);

    [Header("Neighbours' habits")]
    [Range(0f, 1f)] public float tvShare = 0.22f;
    [Range(0f, 1f)] public float nightOwlShare = 0.14f;
    [Range(0f, 1f)] public float earlyBirdShare = 0.3f;
    public bool partyHouse = true;

    public static HouseWindows Current { get; private set; }
    public int HouseCount => houses.Count;
    public int LitCount { get; private set; }

    enum Kind { Warm, Cozy, Tv, Party }

    class House
    {
        public MeshRenderer renderer;
        public int windowSlot = -1, porchSlot = -1;
        public Material origWindow, origPorch;
        public Vector3 pos, top;
        public Kind kind;
        public float onHour, bedHour, wakeHour = -1f, wakeOffHour = -1f, porchOffHour;
        public bool lit, porchLit;
        public bool applied;
    }

    readonly List<House> houses = new List<House>();
    Material warm, cozy, tv, party, porch;
    bool scanned;
    float lastSfx, nextWoo, tvCut, tvLevel = 1f;
    Color tvColor;

    void OnEnable() { Current = this; }
    void OnDisable() { if (Current == this) Current = null; }

    void OnDestroy()
    {
        RestoreOriginals();
        DestroyObj(warm); DestroyObj(cozy); DestroyObj(tv); DestroyObj(party); DestroyObj(porch);
    }

    /// <summary>Find the houses again (call after the neighborhood was rebuilt).</summary>
    public void Rescan()
    {
        RestoreOriginals();
        houses.Clear();
        foreach (var root in gameObject.scene.GetRootGameObjects())
        {
            if (!root.activeInHierarchy) continue;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(false))
            {
                var mats = r.sharedMaterials;
                int w = -1, p = -1;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (w < 0 && m.name.StartsWith("M_windows")) w = i;
                    else if (p < 0 && m.name.StartsWith("M_emissive")) p = i;
                }
                if (w < 0) continue;
                if (r.name.StartsWith("SM_streetlamp")) continue;
                var b = r.bounds;
                var h = new House
                {
                    renderer = r, windowSlot = w, porchSlot = p,
                    origWindow = mats[w], origPorch = p >= 0 ? mats[p] : null,
                    pos = b.center, top = new Vector3(b.center.x, b.max.y, b.center.z),
                };
                houses.Add(h);
            }
        }

        // Habits (deterministic per house position).
        House partyPick = null;
        float best = -1f;
        foreach (var h in houses)
        {
            float h1 = Hash01(h.pos, 1), h2 = Hash01(h.pos, 2), h3 = Hash01(h.pos, 3), h4 = Hash01(h.pos, 4), h5 = Hash01(h.pos, 5);
            h.onHour = 18.3f + h1 * 1.7f;
            h.bedHour = h2 < nightOwlShare ? 30f : 21.6f + Mathf.InverseLerp(nightOwlShare, 1f, h2) * 3.5f;
            if (h3 < earlyBirdShare) { h.wakeHour = 4.8f + h4 * 1.2f; h.wakeOffHour = 6.6f + h4 * 0.6f; }
            h.porchOffHour = 6.1f + h4 * 0.5f;
            h.kind = h5 < tvShare ? Kind.Tv : (Hash01(h.pos, 6) < 0.5f ? Kind.Warm : Kind.Cozy);
            if (h5 > best) { best = h5; partyPick = h; }
        }
        if (partyHouse && partyPick != null && houses.Count >= 4)
        {
            partyPick.kind = Kind.Party;
            partyPick.onHour = 19.5f;
            partyPick.bedHour = 26.5f; // parties 'til 2:30 AM
        }

        EnsureMaterials();
        scanned = true;
    }

    /// <summary>Drive every house for <paramref name="hour"/>; animate = play-mode juice (TV flicker, sounds).</summary>
    public void Apply(float hour, float darkness, bool animate, Vector3 viewer)
    {
        if (!scanned) Rescan();
        if (warm == null) return;
        float now = animate ? Time.time : 0f;
        hour = Mathf.Repeat(hour, 24f);
        float hh = hour < 12f ? hour + 24f : hour; // 12..36, so evenings and the small hours are one range

        // Shared animated materials.
        float glow = Mathf.Lerp(0.6f, 1f, darkness);
        warm.SetColor("_EmissionColor", warmEmission * glow);
        cozy.SetColor("_EmissionColor", cozyEmission * glow);
        if (animate && now >= tvCut)
        {
            tvCut = now + Random.Range(0.25f, 1.4f);
            tvLevel = Random.Range(0.5f, 1.25f);
            tvColor = Color.Lerp(Color.white, new Color(Random.Range(0.3f, 1f), Random.Range(0.5f, 1f), 1f), Random.value);
        }
        if (!animate) { tvLevel = 1f; tvColor = Color.white; }
        float tvFlick = animate ? 0.85f + 0.15f * Mathf.PerlinNoise(now * 14f, 0.3f) : 1f;
        tv.SetColor("_EmissionColor", tvEmission * tvLevel * tvFlick * (tvColor * 0.6f + Color.white * 0.4f));
        float hue = animate ? Mathf.Repeat(now * 0.35f, 1f) : 0.85f;
        party.SetColor("_EmissionColor", Color.HSVToRGB(hue, 0.85f, 1f) * 2.6f);
        party.SetColor("_BaseColor", Color.HSVToRGB(hue, 0.5f, 1f));

        int lit = 0;
        House partyLit = null;
        foreach (var h in houses)
        {
            if (h.renderer == null) continue;
            bool on = (hh >= h.onHour && hh < h.bedHour) ||
                      (h.wakeHour >= 0f && hh >= h.wakeHour + 24f && hh < h.wakeOffHour + 24f);
            bool porchOn = hh >= h.onHour && hh < h.porchOffHour + 24f;
            if (on) lit++;
            if (on && h.kind == Kind.Party) partyLit = h;

            if (!h.applied || on != h.lit || porchOn != h.porchLit)
            {
                if (animate && h.applied && on && !h.lit && now - lastSfx > 0.12f && Vector3.Distance(viewer, h.pos) < 28f)
                {
                    lastSfx = now;
                    Sfx.Play("click", h.pos, 0.18f, Random.Range(0.9f, 1.3f));
                }
                h.lit = on;
                h.porchLit = porchOn;
                h.applied = true;
                var mats = h.renderer.sharedMaterials;
                if (h.windowSlot < mats.Length) mats[h.windowSlot] = on ? MaterialFor(h.kind) : h.origWindow;
                if (h.porchSlot >= 0 && h.porchSlot < mats.Length && h.origPorch != null) mats[h.porchSlot] = porchOn ? porch : h.origPorch;
                h.renderer.sharedMaterials = mats;
            }
        }
        LitCount = lit;

        // The party house: occasional "woo!" when you're close.
        if (animate && partyLit != null && now >= nextWoo)
        {
            nextWoo = now + Random.Range(4f, 8f);
            if (Vector3.Distance(viewer, partyLit.pos) < 40f)
            {
                var c = Color.HSVToRGB(Random.value, 0.7f, 1f);
                GameHUD.FloatingText(partyLit.top + Vector3.up * 0.5f + Random.insideUnitSphere * 1.5f, Random.value < 0.5f ? "woo!" : "WOOO!", c, 1.1f);
                Sfx.Play("pop", partyLit.pos, 0.25f, Random.Range(0.8f, 1.4f));
            }
        }
    }

    Material MaterialFor(Kind k)
    {
        switch (k)
        {
            case Kind.Cozy: return cozy;
            case Kind.Tv: return tv;
            case Kind.Party: return party;
            default: return warm;
        }
    }

    void EnsureMaterials()
    {
        if (warm != null) return;
        Material src = null;
        foreach (var h in houses) if (h.origWindow != null) { src = h.origWindow; break; }
        if (src == null) src = RubbishAssets.Material("mat/sky_window_glow");
        if (src == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) return;
            src = new Material(sh);
        }
        warm = Make(src, "Sky_Window_Warm", warmBase, warmEmission);
        cozy = Make(src, "Sky_Window_Cozy", new Color(1f, 0.72f, 0.42f), cozyEmission);
        tv = Make(src, "Sky_Window_TV", new Color(0.55f, 0.68f, 1f), tvEmission);
        party = Make(src, "Sky_Window_Party", new Color(1f, 0.5f, 0.9f), new Color(2f, 0.6f, 2f));
        Material porchSrc = null;
        foreach (var h in houses) if (h.origPorch != null) { porchSrc = h.origPorch; break; }
        porch = Make(porchSrc != null ? porchSrc : src, "Sky_Porch_On", new Color(1f, 0.95f, 0.65f), porchEmission);
    }

    static Material Make(Material src, string name, Color baseColor, Color emission)
    {
        var m = new Material(src) { name = name, hideFlags = HideFlags.DontSave };
        m.SetColor("_BaseColor", baseColor);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.35f);
        m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        m.SetColor("_EmissionColor", emission);
        return m;
    }

    /// <summary>Put the original window/porch materials back (edit-mode preview cleanup).</summary>
    public void RestoreOriginals()
    {
        foreach (var h in houses)
        {
            if (h.renderer == null) continue;
            var mats = h.renderer.sharedMaterials;
            bool changed = false;
            if (h.windowSlot < mats.Length && h.origWindow != null && mats[h.windowSlot] != h.origWindow) { mats[h.windowSlot] = h.origWindow; changed = true; }
            if (h.porchSlot >= 0 && h.porchSlot < mats.Length && h.origPorch != null && mats[h.porchSlot] != h.origPorch) { mats[h.porchSlot] = h.origPorch; changed = true; }
            if (changed) h.renderer.sharedMaterials = mats;
            h.applied = false;
        }
        scanned = false;
    }

    static float Hash01(Vector3 p, int salt)
    {
        unchecked
        {
            int h = Mathf.RoundToInt(p.x * 10f) * 73856093 ^ Mathf.RoundToInt(p.z * 10f) * 19349663 ^ salt * 83492791;
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xFFFF) / 65535f;
        }
    }

    static void DestroyObj(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }
}
