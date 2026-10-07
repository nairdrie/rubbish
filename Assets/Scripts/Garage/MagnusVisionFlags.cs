using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Magnus's X-Ray Googly Eyes (magnus_vision, Docs/Progression.md §6.3): treasure-bearing bags on the tipping floor (and
/// riding the Bag Belt) get a gold glint and a bobbing "!" — 60% of them at L1, all of them at L2, where the glint takes
/// the rarity colour of the best thing inside. The roll is deterministic per bag id (UpgradeEffects.DetectRoll), so every
/// machine flags the same bags. "Next bag!" and the belt take flagged bags first (<see cref="IsFlagged"/>).
/// Markers are separate objects that follow their bag (never children: a child renderer would change the bag's bounds).
/// </summary>
public class MagnusVisionFlags : MonoBehaviour
{
    public GarageRig rig;
    public float scanInterval = 0.5f;

    sealed class Marker
    {
        public TrashBag bag;
        public GameObject go;
        public Transform bang;
        public TextMeshPro text;
        public Renderer glint;
        public Color color;
        public float nextSparkle;
    }

    readonly Dictionary<TrashBag, Marker> markers = new Dictionary<TrashBag, Marker>();
    readonly List<TrashBag> dead = new List<TrashBag>();
    float nextScan;
    MaterialPropertyBlock mpb;

    /// <summary>Does Magnus's vision flag this bag right now? (Pure: upgrade level + bag contents + bag id.)</summary>
    public static bool IsFlagged(BagContents c)
    {
        return IsFlagged(c, UpgradeEffects.Level(ShopIds.MagnusVision));
    }

    /// <summary>Flag roll using the upgrade level of a particular garage rather than this machine's save.</summary>
    public static bool IsFlagged(BagContents c, int visionLevel)
    {
        float chance = visionLevel >= 2 ? 1f : visionLevel >= 1 ? 0.6f : 0f;
        if (chance <= 0f || c == null || !HasValuable(c, out _)) return false;
        return UpgradeEffects.DetectRoll(c.bagId, "magnus_vision", chance);
    }

    /// <summary>Has a valuable inside; <paramref name="best"/> = the best rarity in there.</summary>
    public static bool HasValuable(BagContents c, out Rarity best)
    {
        best = Rarity.Common;
        bool any = false;
        if (c == null || c.items == null) return false;
        foreach (var it in c.items)
        {
            var d = it != null ? it.Def : null;
            if (d == null || d.category != ItemCategory.Valuable) continue;
            if (!any || d.rarity > best) best = d.rarity;
            any = true;
        }
        return any;
    }

    void Awake()
    {
        if (rig == null) rig = GetComponentInParent<GarageRig>();
    }

    void Update()
    {
        if (Time.time >= nextScan)
        {
            nextScan = Time.time + scanInterval;
            Scan();
        }
        foreach (var m in markers.Values) Follow(m);
    }

    void Scan()
    {
        var compactor = rig != null ? rig.compactor : null;
        int visionLevel = DepotMachineContext.Level(this, ShopIds.MagnusVision);
        bool on = visionLevel > 0;
        var seen = new HashSet<TrashBag>();
        if (on)
        {
            foreach (var b in FindObjectsByType<TrashBag>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (b == null || b.Contents == null || b.State == CarryState.Consumed || b.HasBurst || b.SourceStop != null) continue;
                bool onFloorOrBelt = GaragePile.InPile(compactor, b.WorldCenter) || OnLine(b);
                if (!onFloorOrBelt || !IsFlagged(b.Contents, visionLevel)) continue;
                seen.Add(b);
                if (!markers.ContainsKey(b)) markers[b] = MakeMarker(b);
            }
        }
        dead.Clear();
        foreach (var kv in markers) if (kv.Key == null || !seen.Contains(kv.Key) || kv.Key.State == CarryState.Carried) dead.Add(kv.Key);
        foreach (var b in dead)
        {
            if (markers.TryGetValue(b, out var m) && m.go != null) Destroy(m.go);
            markers.Remove(b);
        }
    }

    bool OnLine(TrashBag b)
    {
        int lot = DepotMachineContext.LotOf(this);
        foreach (var l in ConveyorLine.Lines) if (l != null && DepotMachineContext.LotOf(l) == lot && l.Owns(b)) return true;
        foreach (var t in ConveyorTray.Trays) if (t != null && DepotMachineContext.LotOf(t) == lot && t.Owns(b)) return true;
        return false;
    }

    Marker MakeMarker(TrashBag bag)
    {
        HasValuable(bag.Contents, out var best);
        bool l2 = DepotMachineContext.Level(this, ShopIds.MagnusVision) >= 2;
        Color c = l2 ? RarityInfo.Color(best) : SortingKit.Gold;
        var go = new GameObject("VisionFlag");
        go.transform.SetParent(transform, true);
        var m = new Marker { bag = bag, go = go, color = c };
        var bang = new GameObject("Bang").transform;
        bang.SetParent(go.transform, false);
        m.bang = bang;
        m.text = SortingKit.MakeText(bang, "Text", "!", 4f, c);
        m.text.rectTransform.sizeDelta = new Vector2(0.4f, 0.6f);
        var glint = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(glint.GetComponent<Collider>());
        glint.name = "Glint";
        glint.transform.SetParent(go.transform, false);
        glint.transform.localScale = Vector3.one * 0.12f;
        m.glint = glint.GetComponent<Renderer>();
        m.glint.sharedMaterial = SortingKit.RuntimeMaterial("mat/garage_glint", Color.white, true);
        mpb ??= new MaterialPropertyBlock();
        mpb.SetColor("_BaseColor", c);
        m.glint.SetPropertyBlock(mpb);
        Juice.PopIn(go.transform, 0.25f);
        Sfx.Play("sparkle", bag.WorldCenter, 0.35f, 1.4f);
        return m;
    }

    void Follow(Marker m)
    {
        if (m.bag == null || m.go == null) return;
        Vector3 top = SortingKit.RendererBounds(m.bag.gameObject).max;
        Vector3 c = m.bag.WorldCenter;
        float t = Time.time;
        m.go.transform.position = new Vector3(c.x, top.y + 0.35f + Mathf.Sin(t * 3f) * 0.06f, c.z);
        var cam = Camera.main;
        if (cam != null) m.bang.rotation = Quaternion.LookRotation(m.bang.position - cam.transform.position);
        float pulse = 0.1f + Mathf.Abs(Mathf.Sin(t * 4f)) * 0.06f;
        m.glint.transform.localScale = Vector3.one * pulse;
        m.glint.transform.localPosition = new Vector3(0f, -0.25f, 0f);
        if (t >= m.nextSparkle)
        {
            m.nextSparkle = t + 1.6f + (PickupVisuals.StableHash(m.bag.name) & 7) * 0.1f;
            Fx.Sparkle(m.bag.transform, m.color, 0.6f);
        }
    }

    void OnDisable()
    {
        foreach (var m in markers.Values) if (m.go != null) Destroy(m.go);
        markers.Clear();
    }
}
