using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Scavenger presentation that lives in the world or on the map. All of it is LOCAL: it reads authored data every machine
// already has (PickupStop.wealth, the baked map) and this player's own calling levels, draws its own objects, and never
// touches a shared material, a networked object or anybody else's screen.

/// <summary>Nose for Money: coins glinting over the roofs of rich houses in smelling range.</summary>
sealed class ScavNoseFx
{
    sealed class Marker { public Transform root; public Transform[] coins; public float phase; public Vector3 roof; }

    static readonly Color Gold = new Color(1f, 0.82f, 0.22f);
    readonly Dictionary<PickupStop, Marker> markers = new Dictionary<PickupStop, Marker>();
    readonly Dictionary<PickupStop, Vector3> roofs = new Dictionary<PickupStop, Vector3>();
    readonly HashSet<string> sniffed = new HashSet<string>();
    readonly List<PickupStop> gone = new List<PickupStop>();
    float nextScan;
    int builtLevel;

    public void Tick(Vector3 me, int level, Transform parent)
    {
        if (level <= 0) { Clear(); return; }
        if (level != builtLevel) { Clear(); builtLevel = level; }
        if (Time.time >= nextScan) { nextScan = Time.time + 0.5f; Scan(me, level, parent); }
        float t = Time.time;
        foreach (var m in markers.Values)
        {
            if (m.root == null) continue;
            m.root.position = m.roof + Vector3.up * (0.25f * Mathf.Sin(t * 1.7f + m.phase));
            for (int i = 0; i < m.coins.Length; i++)
                if (m.coins[i] != null) m.coins[i].localRotation = Quaternion.Euler(90f, 0f, 0f) * Quaternion.Euler(0f, 0f, t * 140f + i * 55f + m.phase * 40f);
        }
    }

    void Scan(Vector3 me, int level, Transform parent)
    {
        float range = ScavengerRules.NoseRange(level);
        var stops = PickupStop.All;
        for (int i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            if (stop == null) continue;
            Vector3 d = stop.transform.position - me; d.y = 0f;
            int tier = d.sqrMagnitude <= range * range ? ScavengerRules.RichTier(stop.wealth, stop.District.tier) : 0;
            bool want = tier > 0 && stop.DistrictUnlocked;
            if (want && !markers.ContainsKey(stop)) Add(stop, level >= 2 ? tier : 1, parent);
            else if (!want && markers.ContainsKey(stop)) Remove(stop);
        }
        gone.Clear();
        foreach (var kv in markers) if (kv.Key == null) gone.Add(kv.Key);
        foreach (var stop in gone) Remove(stop);
    }

    Vector3 RoofOf(PickupStop stop)
    {
        if (roofs.TryGetValue(stop, out var roof)) return roof;
        var route = stop.GetComponent<CoastalHouseRoute>();
        var building = route != null ? route.buildingRoot : null;
        bool any = false; var bounds = new Bounds();
        if (building != null)
            foreach (var r in building.GetComponentsInChildren<Renderer>())
            {
                if (r == null || r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
            }
        if (any && bounds.size.y < 40f) roof = new Vector3(bounds.center.x, bounds.max.y + 1.1f, bounds.center.z);
        else if (route != null && route.door.sqrMagnitude > 0.01f) roof = route.door + Vector3.up * 6.5f;
        else roof = stop.transform.position - stop.transform.forward * 9f + Vector3.up * 7f;
        roofs[stop] = roof;
        return roof;
    }

    void Add(PickupStop stop, int coins, Transform parent)
    {
        var mat = ShopModKit.Emissive("mat/scav_nose_coin", Gold, 2.4f);
        var m = new Marker { roof = RoofOf(stop), phase = (PickupVisuals.StableHash(stop.stopId) & 0xFF) * 0.05f, coins = new Transform[coins] };
        m.root = new GameObject("Nose for Money " + stop.stopId).transform;
        m.root.SetParent(parent, false);
        m.root.position = m.roof;
        for (int i = 0; i < coins; i++)
            m.coins[i] = ShopModKit.Prim(PrimitiveType.Cylinder, "Coin", m.root, mat, new Vector3((i - (coins - 1) * 0.5f) * 0.85f, 0f, 0f), new Vector3(0.62f, 0.035f, 0.62f), new Vector3(90f, 0f, 0f));
        markers[stop] = m;
        Juice.PopIn(m.root, 0.3f);
        if (sniffed.Add(stop.stopId))
        {
            Sfx.Play("sniff", null, 0.5f, 1.15f);
            GameHUD.FloatingText(m.roof + Vector3.up * 0.9f, coins >= 3 ? "*sniff* ...LOADED" : coins == 2 ? "*sniff* ...money" : "*sniff*", Gold, 1.1f);
            Fx.Sparkle(m.root, Gold, 1.5f);
        }
        if (sniffed.Count > 500) sniffed.Clear();
    }

    void Remove(PickupStop stop)
    {
        if (markers.TryGetValue(stop, out var m) && m.root != null) Object.Destroy(m.root.gameObject);
        markers.Remove(stop);
    }

    public void Clear()
    {
        foreach (var m in markers.Values) if (m.root != null) Object.Destroy(m.root.gameObject);
        markers.Clear(); roofs.Clear(); builtLevel = 0;
    }
}

/// <summary>
/// The Itch: honest kerb bags go grey and dull to YOU, and front doors start to glow. Bags are tinted through a
/// MaterialPropertyBlock on their own renderers (cleared again when the bag is picked up, leaves range or the Itch is
/// gone); the door glow is a separate object with this effect's own material instance.
/// </summary>
sealed class ScavItchFx
{
    const float BagRange = 26f, DoorRange = 42f;
    static readonly Color DullGrey = new Color(0.34f, 0.34f, 0.36f), Warm = new Color(1f, 0.62f, 0.18f, 0.5f);
    static readonly int BaseColor = Shader.PropertyToID("_BaseColor"), LegacyColor = Shader.PropertyToID("_Color"), Smoothness = Shader.PropertyToID("_Smoothness");

    sealed class Door { public Transform root; public float phase, strength; }
    readonly Dictionary<TrashBag, Renderer[]> dulled = new Dictionary<TrashBag, Renderer[]>();
    readonly Dictionary<PickupStop, Door> doors = new Dictionary<PickupStop, Door>();
    readonly HashSet<TrashBag> seen = new HashSet<TrashBag>();
    readonly List<TrashBag> dropBags = new List<TrashBag>();
    readonly List<PickupStop> dropDoors = new List<PickupStop>();
    MaterialPropertyBlock block;
    Material glow;
    float nextScan;

    public void Tick(Vector3 me, bool on, Transform parent)
    {
        if (!on) { Clear(); return; }
        if (Time.time >= nextScan) { nextScan = Time.time + 0.5f; Scan(me, parent); }
        if (glow != null)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 2.2f);
            var c = new Color(Warm.r, Warm.g, Warm.b, 0.16f + 0.3f * pulse);
            glow.SetColor(BaseColor, c); glow.SetColor(LegacyColor, c);
        }
        foreach (var d in doors.Values)
            if (d.root != null) d.root.localScale = Vector3.one * (1f + 0.08f * Mathf.Sin(Time.time * 2.2f + d.phase)) * d.strength;
    }

    void Scan(Vector3 me, Transform parent)
    {
        seen.Clear();
        var stops = PickupStop.All;
        for (int i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            if (stop == null) continue;
            Vector3 d = stop.transform.position - me; d.y = 0f;
            float d2 = d.sqrMagnitude;
            if (d2 <= BagRange * BagRange)
            {
                var bags = stop.SpawnedBags;
                for (int b = 0; b < bags.Count; b++)
                {
                    var bag = bags[b];
                    if (bag == null || bag.State != CarryState.Resting || bag.HasBurst) continue;
                    seen.Add(bag);
                    Dull(bag);
                }
            }
            bool want = d2 <= DoorRange * DoorRange && stop.DistrictUnlocked && ScavHouses.TryDoor(stop, out _);
            if (want && !doors.ContainsKey(stop)) AddDoor(stop, parent);
            else if (!want && doors.ContainsKey(stop)) RemoveDoor(stop);
        }
        dropBags.Clear();
        foreach (var kv in dulled) if (kv.Key == null || !seen.Contains(kv.Key)) dropBags.Add(kv.Key);
        foreach (var bag in dropBags) Restore(bag);
        dropDoors.Clear();
        foreach (var kv in doors) if (kv.Key == null) dropDoors.Add(kv.Key);
        foreach (var stop in dropDoors) RemoveDoor(stop);
    }

    void Dull(TrashBag bag)
    {
        if (!dulled.TryGetValue(bag, out var renderers))
        {
            renderers = bag.GetComponentsInChildren<Renderer>();
            dulled[bag] = renderers;
        }
        block ??= new MaterialPropertyBlock();
        foreach (var r in renderers)
        {
            if (r == null || r is ParticleSystemRenderer || r is TrailRenderer) continue;
            r.GetPropertyBlock(block);
            block.SetColor(BaseColor, DullGrey); block.SetColor(LegacyColor, DullGrey); block.SetFloat(Smoothness, 0.02f);
            r.SetPropertyBlock(block); // re-applied each scan: the carry highlight clears blocks when it lets go
        }
    }

    void Restore(TrashBag bag)
    {
        if (dulled.TryGetValue(bag, out var renderers) && bag != null)
            foreach (var r in renderers) if (r != null) r.SetPropertyBlock(null);
        dulled.Remove(bag);
    }

    void AddDoor(PickupStop stop, Transform parent)
    {
        if (!ScavHouses.TryDoor(stop, out var at)) return;
        if (glow == null) glow = new Material(ShopModKit.Glow("mat/scav_door_glow", Warm));
        int tier = ScavengerRules.RichTier(stop.wealth, stop.District.tier);
        var d = new Door { phase = (PickupVisuals.StableHash(stop.stopId) & 0xFF) * 0.07f, strength = 0.85f + 0.12f * tier };
        d.root = new GameObject("Itch Door " + stop.stopId).transform;
        d.root.SetParent(parent, false);
        d.root.position = at + Vector3.up * 1.1f;
        ShopModKit.Prim(PrimitiveType.Sphere, "Glow", d.root, glow, Vector3.zero, new Vector3(1.5f, 2.3f, 1.5f));
        ShopModKit.Prim(PrimitiveType.Sphere, "Core", d.root, glow, new Vector3(0f, -0.1f, 0f), new Vector3(0.7f, 1.4f, 0.7f));
        doors[stop] = d;
    }

    void RemoveDoor(PickupStop stop)
    {
        if (doors.TryGetValue(stop, out var d) && d.root != null) Object.Destroy(d.root.gameObject);
        doors.Remove(stop);
    }

    public void Clear()
    {
        if (dulled.Count > 0) { dropBags.Clear(); dropBags.AddRange(dulled.Keys); foreach (var bag in dropBags) Restore(bag); }
        if (doors.Count > 0) { foreach (var d in doors.Values) if (d.root != null) Object.Destroy(d.root.gameObject); doors.Clear(); }
    }

    public void Dispose() { Clear(); if (glow != null) Object.Destroy(glow); glow = null; }
}

/// <summary>Bloodhound for Bling (Nose for Money Lv 2): gold diamonds on the minimap and the paper map over every rich
/// house in a district you can enter. One small layer per map view, laid out again whenever that map zooms.</summary>
sealed class ScavMapPips
{
    sealed class Layer { public MapView view; public RectTransform root; public TownMapData data; public float k = -1f; public readonly List<(RectTransform rt, Vector2 xz)> icons = new List<(RectTransform, Vector2)>(); }
    readonly List<Layer> layers = new List<Layer>();
    float nextCheck;
    int builtFor = -1;

    public void Tick(int noseLevel)
    {
        if (noseLevel < 2) { if (layers.Count > 0) Clear(); return; }
        if (Time.unscaledTime >= nextCheck)
        {
            nextCheck = Time.unscaledTime + 1f;
            var feel = FeelRunner.Get();
            var hudMap = feel != null && feel.Hud != null && feel.Hud.Minimap != null ? feel.Hud.Minimap.View : null;
            var paper = FullMap.Instance != null ? FullMap.Instance.View : null;
            int key = DistrictAccess.TownMask() * 31 + GameClock.Day;
            if (key != builtFor) { Clear(); builtFor = key; }
            Ensure(hudMap, true); Ensure(paper, false);
            layers.RemoveAll(l => { if (l.root != null && l.view != null && l.view.Pan != null) return false; if (l.root != null) Object.Destroy(l.root.gameObject); return true; });
        }
        foreach (var l in layers)
        {
            if (l.root == null || l.view == null) continue;
            if (l.view.Data != l.data) { Object.Destroy(l.root.gameObject); l.root = null; continue; }
            float k = l.view.PixelsPerMeter;
            if (Mathf.Approximately(k, l.k)) continue;
            l.k = k;
            foreach (var icon in l.icons) if (icon.rt != null) icon.rt.anchoredPosition = l.view.ToPan(icon.xz);
        }
    }

    void Ensure(MapView view, bool minimap)
    {
        if (view == null || view.Pan == null || !view.HasData) return;
        foreach (var l in layers) if (l.view == view && l.root != null) return;
        layers.RemoveAll(l => l.view == view);
        var layer = new Layer { view = view, data = view.Data };
        layer.root = FeelUi.Rect("ScavRich", view.Pan, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
        var pipsLayer = view.Pan.Find("Pips");
        if (pipsLayer != null) layer.root.SetSiblingIndex(pipsLayer.GetSiblingIndex()); // under the trash pips, over the paper
        foreach (var h in view.Data.houses)
        {
            var house = CurbTrashSpawner.FindHouse(h.stopId);
            var district = Districts.ForStopId(h.stopId);
            if (house == null || district == null || !DistrictAccess.LocalCanEnter(district)) continue;
            int tier = ScavengerRules.RichTier(house.wealth, district.tier);
            if (tier <= 0) continue;
            float size = (minimap ? 9f : 12f) + tier * (minimap ? 2f : 3f);
            Vector2 xz = h.footprint.width > 0.1f ? h.footprint.center : h.curb;
            var rt = FeelUi.Rect("Rich " + h.stopId, layer.root, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
            var rim = FeelUi.Img(rt, "Rim", UiKit.Sprites.Diamond, UiTheme.Ink);
            rim.rectTransform.offsetMin = new Vector2(-2f, -2f); rim.rectTransform.offsetMax = new Vector2(2f, 2f);
            FeelUi.Img(rt, "Coin", UiKit.Sprites.Diamond, tier >= 3 ? new Color(1f, 0.93f, 0.45f) : tier == 2 ? UiTheme.Gold : new Color(0.93f, 0.72f, 0.25f));
            layer.icons.Add((rt, xz));
        }
        layers.Add(layer);
    }

    public void Clear()
    {
        foreach (var l in layers) if (l.root != null) Object.Destroy(l.root.gameObject);
        layers.Clear(); builtFor = -1;
    }
}
