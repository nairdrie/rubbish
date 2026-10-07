using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// UV Treasure Headlamp (headlamp L2, Docs/Progression.md §6.4) on the local player: at night, curb bags that hold a
/// valuable glow faintly purple within 15 m, with <see cref="UpgradeEffects.UvDetect"/> chance (35%), deterministic by
/// bagId (UpgradeEffects.DetectRoll, salt "uv"); never a false positive. The glow is a separate renderer-only shell
/// that follows the bag (never parented to it, so the bag's bounds and physics are untouched), plus a small purple
/// light on the nearest two.
/// </summary>
[DisallowMultipleComponent]
public class UvLamp : MonoBehaviour
{
    public const string Salt = "uv";
    public const float Range = 15f;
    const float OnAtNight01 = 0.45f;   // same dusk threshold as the headlamp
    const int MaxLights = 2;

    static readonly Color Uv = new Color(0.62f, 0.25f, 1f);

    class Glow
    {
        public TrashBag bag;
        public Transform shell;
        public Light light;
        public Material mat;
        public float born;
    }

    PlayerController pc;
    readonly Dictionary<TrashBag, Glow> glows = new Dictionary<TrashBag, Glow>();
    readonly HashSet<string> revealed = new HashSet<string>();
    readonly List<TrashBag> tmp = new List<TrashBag>();
    float nextScanAt;

    /// <summary>Bags glowing right now (tests).</summary>
    public int GlowingCount => glows.Count;

    void Awake() => pc = GetComponent<PlayerController>();

    void OnDisable() => ClearAll();

    /// <summary>Would the UV lamp light up this bag at night? (valuable inside AND the deterministic roll hits)</summary>
    public static bool WouldGlow(BagContents c)
    {
        float chance = UpgradeEffects.UvDetect;
        if (chance <= 0f || c == null || string.IsNullOrEmpty(c.bagId)) return false;
        return ShopModKit.HasValuable(c) && UpgradeEffects.DetectRoll(c.bagId, Salt, chance);
    }

    void Update()
    {
        if (pc == null || !pc.IsOwner) return;
        bool want = UpgradeEffects.UvDetect > 0f && GameClock.Night01 >= OnAtNight01 && !pc.IsDriving && !pc.IsPlayingChess;
        if (!want) { if (glows.Count > 0) ClearAll(); return; }
        if (Time.time >= nextScanAt)
        {
            nextScanAt = Time.time + 0.3f;
            Scan();
        }
    }

    void Scan()
    {
        Vector3 me = transform.position;
        tmp.Clear();
        foreach (var stop in PickupStop.All)
        {
            if (stop == null || (stop.transform.position - me).sqrMagnitude > (Range + 8f) * (Range + 8f)) continue;
            var bags = stop.SpawnedBags;
            for (int i = 0; i < bags.Count; i++)
            {
                var b = bags[i];
                if (b == null || b.State != CarryState.Resting || b.HasBurst) continue;
                if ((b.WorldCenter - me).sqrMagnitude > Range * Range) continue;
                if (!WouldGlow(b.Contents)) continue;
                tmp.Add(b);
            }
        }
        // drop glows that no longer qualify
        var gone = new List<TrashBag>();
        foreach (var kv in glows) if (kv.Key == null || !tmp.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var b in gone) Remove(b);
        foreach (var b in tmp) if (!glows.ContainsKey(b)) Add(b);

        // the nearest two get a real light
        tmp.Sort((a, c) => (a.WorldCenter - me).sqrMagnitude.CompareTo((c.WorldCenter - me).sqrMagnitude));
        for (int i = 0; i < tmp.Count; i++)
            if (glows.TryGetValue(tmp[i], out var g) && g.light != null) g.light.enabled = i < MaxLights;
    }

    void Add(TrashBag b)
    {
        var g = new Glow { bag = b, born = Time.time };
        var lb = b.LocalBounds();
        Vector3 size = Vector3.Scale(lb.size, b.transform.lossyScale);
        g.mat = new Material(ShopModKit.Glow("mat/shop_uv_glow", new Color(Uv.r, Uv.g, Uv.b, 0.3f)));
        g.shell = ShopModKit.Prim(PrimitiveType.Sphere, "UV Glow", null, g.mat, b.WorldCenter, size * 1.35f + Vector3.one * 0.1f);
        g.shell.gameObject.name = "UV Glow " + (b.Contents != null ? b.Contents.bagId : "");
        var lgo = new GameObject("UV Light");
        lgo.transform.SetParent(g.shell, false);
        g.light = lgo.AddComponent<Light>();
        g.light.type = LightType.Point;
        g.light.color = Uv;
        g.light.range = 2.6f;
        g.light.intensity = 1.6f;
        g.light.shadows = LightShadows.None;
        g.light.enabled = false;
        glows[b] = g;
        Juice.PopIn(g.shell, 0.3f);
        string id = b.Contents != null ? b.Contents.bagId : null;
        if (id != null && revealed.Add(id))
        {
            Fx.Sparkle(b.JuiceTarget, Uv, 1.2f);
            Sfx.Play("sparkle", b.WorldCenter, 0.3f, 0.8f);
            GameHUD.FloatingText(b.WorldCenter + Vector3.up * 0.9f, "* uv *", Uv, 0.75f);
        }
    }

    void Remove(TrashBag b)
    {
        if (!glows.TryGetValue(b, out var g)) return;
        glows.Remove(b);
        if (g.shell != null) Destroy(g.shell.gameObject);
        if (g.mat != null) Destroy(g.mat);
    }

    void ClearAll()
    {
        foreach (var g in glows.Values)
        {
            if (g.shell != null) Destroy(g.shell.gameObject);
            if (g.mat != null) Destroy(g.mat);
        }
        glows.Clear();
    }

    void LateUpdate()
    {
        if (glows.Count == 0) return;
        float pulse = 0.18f + 0.14f * (0.5f + 0.5f * Mathf.Sin(Time.time * 2.6f));
        foreach (var g in glows.Values)
        {
            if (g.bag == null || g.shell == null) continue;
            g.shell.position = g.bag.WorldCenter;
            g.shell.rotation = g.bag.transform.rotation;
            if (g.mat != null)
            {
                var c = new Color(Uv.r, Uv.g, Uv.b, pulse);
                g.mat.SetColor("_BaseColor", c);
                g.mat.SetColor("_Color", c);
            }
            if (g.light != null && g.light.enabled) g.light.intensity = 1.2f + 1.2f * pulse;
        }
    }
}
