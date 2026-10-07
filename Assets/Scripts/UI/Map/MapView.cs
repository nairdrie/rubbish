using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One live view of the baked town map (Docs/UIPass.md §7.3–§7.4), shared by the <see cref="Minimap"/> and the
/// <see cref="FullMap"/>: the PNG on a viewport-sized RawImage whose <c>uvRect</c> follows the centre (one quad, no render
/// camera), plus overlays drawn from data — locked-district hatch, curb pips coloured by freshness (shapes in colour-blind
/// mode), the planned route as gold dots, the next stop's pulsing ring, the depot badge, gates and exits, and the beacons
/// (trucks, crew, you).
/// <para>Hierarchy: <c>viewport</c> (masked by the caller) → <c>rot</c> (centred; rotates for heading-up) → the map image and
/// <c>pan</c> (offset so the centre sits at the viewport centre) → overlay layers. Overlays are placed in pan space at
/// (world − map origin) × pixels-per-metre, so per frame only the view (pan, rotation, zoom) and the pulses change.</para>
/// <para>Pips rebuild only on <see cref="GameSession.CurbTrashChanged"/> / <see cref="GameSession.DayStarted"/> (and once a
/// second as a fallback for age colours); everything is pooled (0 B/frame after warm-up).</para>
/// </summary>
public sealed class MapView
{
    public readonly RectTransform Viewport, Rot, Pan;
    readonly RawImage image;
    readonly Image nightTint;
    readonly RectTransform hatchRoot, routeRoot, pipRoot, iconRoot, beaconRoot, labelRoot;
    readonly bool minimap;
    TownMapData data;

    Vector2 center;          // world XZ at the viewport centre
    float k = 2f;            // UI px per metre
    float rotation;          // degrees (heading-up)
    float laidK = -1f;
    Vector2 viewSize;

    // ------------------------------------------------------------------ pools

    sealed class Pip
    {
        public RectTransform rt;
        public Image outline, fill, stink;
        public int house;
        public bool neglected;
        public float size;
    }

    sealed class Icon
    {
        public RectTransform rt;
        public Image img;
        public TextMeshProUGUI text;
        public Vector2 xz;
        public float heading;
        public bool upright;
    }

    readonly List<Pip> pips = new List<Pip>(96);
    int pipCount;
    readonly List<Image> dots = new List<Image>(256);
    int dotCount;
    readonly List<Vector2> routePts = new List<Vector2>(64);
    readonly List<Icon> gates = new List<Icon>(4), exits = new List<Icon>(4), hatches = new List<Icon>(4);
    Icon depot, ring, chevron, youCone, youOutline, you;
    readonly List<Icon> beacons = new List<Icon>(8);
    readonly List<(TextMeshProUGUI t, Vector2 xz, float angle, float size, MapLabelKind kind)> labels = new List<(TextMeshProUGUI, Vector2, float, float, MapLabelKind)>(64);

    bool pipsDirty = true;
    float nextPipRefresh;
    string ringStop;
    bool hasRing;
    Vector2 ringXZ;

    /// <summary>Pips on the map right now (subscribed houses in unlocked districts with bags out): the UP2-4 check.</summary>
    public int PipCount => pipCount;
    public float PixelsPerMeter => k;
    public Vector2 Center => center;
    public float Rotation => rotation;
    public TownMapData Data => data;
    public bool HasData => data != null && data.texture != null;
    /// <summary>The next-stop ring's position inside the viewport (local, from its centre), when shown.</summary>
    public bool RingShown => hasRing && ring != null && ring.rt.gameObject.activeSelf;

    public MapView(RectTransform parent, Vector2 size, bool forMinimap)
    {
        minimap = forMinimap;
        Viewport = FeelUi.Rect("MapViewport", parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        Rot = FeelUi.Rect("Rot", Viewport, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        var imgRt = FeelUi.Rect("Map", Rot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        image = imgRt.gameObject.AddComponent<RawImage>();
        image.raycastTarget = false;
        image.color = Color.white;
        nightTint = FeelUi.Img(imgRt, "Night", null, new Color(0.08f, 0.1f, 0.3f, 0f));
        Pan = FeelUi.Rect("Pan", Rot, new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), Vector2.zero, Vector2.zero);
        hatchRoot = Layer("Hatch");
        routeRoot = Layer("Route");
        pipRoot = Layer("Pips");
        iconRoot = Layer("Icons");
        labelRoot = Layer("Labels");
        beaconRoot = Layer("Beacons");
        viewSize = size;
        if (Application.isPlaying)
        {
            GameSession.CurbTrashChanged += MarkPips;
            GameSession.DayStarted += OnDay;
        }
    }

    public void Dispose()
    {
        GameSession.CurbTrashChanged -= MarkPips;
        GameSession.DayStarted -= OnDay;
    }

    void MarkPips() => pipsDirty = true;
    void OnDay(int d) => pipsDirty = true;

    RectTransform Layer(string name)
    {
        var rt = FeelUi.Rect(name, Pan, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
        return rt;
    }

    /// <summary>(Re)bind to the baked map (null hides the map image).</summary>
    public void Bind(TownMapData d)
    {
        if (d == data) return;
        data = d;
        image.texture = d != null ? d.texture : null;
        image.enabled = image.texture != null;
        BuildStatic();
        pipsDirty = true;
        laidK = -1f;
    }

    // ------------------------------------------------------------------ view

    /// <summary>Show <paramref name="centerXZ"/> at the viewport centre at <paramref name="pxPerMeter"/>, rotated by
    /// <paramref name="rotationDeg"/> (the world yaw that should point up; 0 = north-up).</summary>
    public void SetView(Vector2 centerXZ, float pxPerMeter, float rotationDeg, Vector2 size)
    {
        center = centerXZ;
        k = Mathf.Max(0.05f, pxPerMeter);
        rotation = rotationDeg;
        if (size != viewSize)
        {
            viewSize = size;
            Viewport.sizeDelta = size;
            Rot.sizeDelta = size;
            image.rectTransform.sizeDelta = size;
        }
        Rot.localRotation = Quaternion.Euler(0f, 0f, rotation);
        if (data == null) return;
        Vector2 b0 = data.bounds.min, bs = data.bounds.size;
        Vector2 half = size * 0.5f / k;
        image.uvRect = new Rect((center.x - half.x - b0.x) / bs.x, (center.y - half.y - b0.y) / bs.y, half.x * 2f / bs.x, half.y * 2f / bs.y);
        Pan.anchoredPosition = -(center - b0) * k;
        if (Mathf.Abs(k - laidK) > laidK * 0.004f) Relayout();
    }

    /// <summary>Night: a 15% navy tint over the map (§7.3).</summary>
    public void SetNight(float night01) => nightTint.color = new Color(0.08f, 0.1f, 0.3f, 0.15f * Mathf.Clamp01(night01));

    /// <summary>World XZ → pan-space UI position.</summary>
    public Vector2 ToPan(Vector2 xz) => data == null ? Vector2.zero : (xz - data.bounds.min) * k;

    /// <summary>World XZ → viewport-local position (from the viewport centre), after rotation.</summary>
    public Vector2 ToViewport(Vector2 xz)
    {
        Vector2 d = (xz - center) * k;
        float a = rotation * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
        return new Vector2(d.x * c - d.y * s, d.x * s + d.y * c);
    }

    /// <summary>Viewport-local position (from the centre) → world XZ (inverse of <see cref="ToViewport"/>).</summary>
    public Vector2 FromViewport(Vector2 local)
    {
        float a = -rotation * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
        var d = new Vector2(local.x * c - local.y * s, local.x * s + local.y * c);
        return center + d / k;
    }

    // ------------------------------------------------------------------ static overlays (gates, exits, depot, labels, hatch)

    void BuildStatic()
    {
        foreach (var i in gates) FeelRunner.SafeDestroy(i.rt.gameObject);
        foreach (var i in exits) FeelRunner.SafeDestroy(i.rt.gameObject);
        foreach (var i in hatches) FeelRunner.SafeDestroy(i.rt.gameObject);
        foreach (var l in labels) FeelRunner.SafeDestroy(l.t.gameObject);
        gates.Clear(); exits.Clear(); hatches.Clear(); labels.Clear();
        if (depot != null) { FeelRunner.SafeDestroy(depot.rt.gameObject); depot = null; }
        if (data == null) return;

        foreach (var dist in data.districts)
        {
            if (dist.id == Districts.MildlyPleasant) continue;
            var h = NewIcon(hatchRoot, "Hatch " + dist.id, MapIcons.Hatch, new Color(0.12f, 0.1f, 0.18f, 0.35f), Vector2.zero, false);
            h.img.type = Image.Type.Tiled;
            h.img.pixelsPerUnitMultiplier = 2.5f;
            h.xz = dist.rect.center;
            h.text = null;
            h.rt.gameObject.name = dist.id;
            hatches.Add(h);
            if (dist.hasGate)
            {
                var g = NewIcon(iconRoot, "Gate " + dist.id, MapIcons.Padlock, Color.white, new Vector2(minimap ? 24f : 30f, minimap ? 24f : 30f), true);
                g.xz = dist.gate;
                g.rt.gameObject.name = dist.id;
                Backing(g, minimap ? 30f : 38f);
                gates.Add(g);
            }
        }
        foreach (var p in data.pois)
        {
            if (p.kind == MapPoiKind.Gate)
            {
                bool duplicate = false;
                foreach (var old in gates) if ((old.xz - p.pos).sqrMagnitude < .01f) { duplicate = true; break; }
                if (duplicate) continue;
                var gate = NewIcon(iconRoot, "Gate " + p.id, MapIcons.Padlock, Color.white,
                    new Vector2(minimap ? 24f : 30f, minimap ? 24f : 30f), true);
                gate.xz = p.pos; gate.rt.gameObject.name = p.id;
                Backing(gate, minimap ? 30f : 38f); gates.Add(gate);
            }
            else if (p.kind == MapPoiKind.Exit)
            {
                var e = NewIcon(iconRoot, "Exit " + p.id, MapIcons.ExitArrow, new Color(1f, 1f, 1f, 0.95f), new Vector2(minimap ? 18f : 24f, minimap ? 18f : 24f), false);
                e.xz = p.pos;
                e.heading = p.heading;
                Backing(e, minimap ? 24f : 32f);
                exits.Add(e);
            }
            else if (p.kind == MapPoiKind.GarageDoor && depot == null)
            {
                depot = NewIcon(iconRoot, "Depot", FeelUi.Rounded, MapBeacon.TruckOlive, new Vector2(minimap ? 28f : 34f, minimap ? 28f : 34f), true);
                depot.img.type = Image.Type.Sliced;
                depot.img.pixelsPerUnitMultiplier = 3f;
                depot.xz = p.pos;
                depot.text = FeelUi.Text(depot.rt, "R", minimap ? 22f : 26f, Color.white, TextAlignmentOptions.Center);
                depot.text.text = "R";
            }
        }
        if (!minimap)
        {
            foreach (var l in data.labels)
            {
                bool district = l.kind == MapLabelKind.District;
                var t = FeelUi.Text(labelRoot, "Label", l.size, district ? new Color(0.12f, 0.1f, 0.18f, 0.55f) : Color.white, TextAlignmentOptions.Center,
                                    district ? FontStyles.Bold | FontStyles.Italic : FontStyles.Bold);
                if (district)
                {
                    // big tilted district names in the display font, ink (its own material: atlases must match)
                    if (UiTheme.DisplayFont != null) t.font = UiTheme.DisplayFont;
                    var ink = UiTheme.InkFor(t.font);
                    if (ink != null) t.fontSharedMaterial = ink;
                    t.characterSpacing = 6f;
                }
                t.text = l.text;
                t.rectTransform.anchorMin = t.rectTransform.anchorMax = Vector2.zero;
                t.rectTransform.sizeDelta = new Vector2(t.GetPreferredValues(l.text, 100000f, 0f).x + 8f, l.size * 1.4f);
                labels.Add((t, l.pos, l.angle, l.size, l.kind));
            }
        }
        // the next-stop ring, chevron and "you" on top
        if (ring == null)
        {
            ring = NewIcon(iconRoot, "NextRing", UiKit.Sprites.Ring, UiTheme.Gold, new Vector2(minimap ? 34f : 40f, minimap ? 34f : 40f), true);
            chevron = NewIcon(iconRoot, "NextChevron", UiKit.Sprites.Chevron, UiTheme.Gold, new Vector2(22f, 22f), true);
            youCone = NewIcon(beaconRoot, "YouCone", MapIcons.Cone, new Color(1f, 1f, 1f, 0.2f), new Vector2(minimap ? 120f : 140f, minimap ? 120f : 140f), false);
            youOutline = NewIcon(beaconRoot, "YouOutline", MapIcons.ArrowOutline, Color.white, new Vector2(minimap ? 34f : 40f, minimap ? 34f : 40f), false);
            you = NewIcon(beaconRoot, "You", MapIcons.Arrow, UiTheme.Tangerine, new Vector2(minimap ? 34f : 40f, minimap ? 34f : 40f), false);
            ring.rt.gameObject.SetActive(false);
            chevron.rt.gameObject.SetActive(false);
            SetYouVisible(false);
        }
        else
        {
            ring.rt.SetAsLastSibling();
            chevron.rt.SetAsLastSibling();
        }
        laidK = -1f;
    }

    static void Backing(Icon i, float size)
    {
        var b = FeelUi.Rect("Back", i.rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
        b.SetAsFirstSibling();
        var img = b.gameObject.AddComponent<Image>();
        img.sprite = FeelUi.Circle;
        img.color = new Color(0.11f, 0.09f, 0.17f, 0.85f);
        img.raycastTarget = false;
        // the icon itself sits on top of its own backing: move the icon graphic to a child
    }

    Icon NewIcon(RectTransform parent, string name, Sprite sprite, Color color, Vector2 size, bool upright)
    {
        var i = new Icon { upright = upright };
        i.rt = FeelUi.Rect(name, parent, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, size);
        var g = FeelUi.Rect("G", i.rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        i.img = g.gameObject.AddComponent<Image>();
        i.img.sprite = sprite;
        i.img.color = color;
        i.img.raycastTarget = false;
        return i;
    }

    /// <summary>Re-place everything that depends on the zoom (after k changed).</summary>
    void Relayout()
    {
        laidK = k;
        if (data == null) return;
        foreach (var h in hatches)
        {
            int di = data.DistrictIndex(h.rt.gameObject.name);
            if (di < 0) continue;
            var r = data.districts[di].rect;
            h.rt.pivot = Vector2.zero;
            h.rt.anchoredPosition = ToPan(r.min);
            h.rt.sizeDelta = r.size * k;
            h.img.rectTransform.sizeDelta = r.size * k;
            h.img.rectTransform.anchoredPosition = Vector2.zero;
            h.img.rectTransform.anchorMin = Vector2.zero; h.img.rectTransform.anchorMax = Vector2.one;
            h.img.rectTransform.offsetMin = h.img.rectTransform.offsetMax = Vector2.zero;
        }
        foreach (var g in gates) g.rt.anchoredPosition = ToPan(g.xz);
        foreach (var e in exits) e.rt.anchoredPosition = ToPan(e.xz);
        if (depot != null) depot.rt.anchoredPosition = ToPan(depot.xz);
        foreach (var l in labels)
        {
            l.t.rectTransform.anchoredPosition = ToPan(l.xz);
            l.t.rectTransform.localRotation = Quaternion.Euler(0f, 0f, l.angle);
        }
        for (int i = 0; i < pipCount; i++) PlacePip(pips[i]);
        LayoutRouteDots();
    }

    // ------------------------------------------------------------------ per frame

    /// <summary>
    /// Refresh the live layers. <paramref name="you"/> = the local player (world pos + yaw) when known; <paramref name="nextStop"/> =
    /// the HUD arrow's house (same target as the route panel), or null.
    /// </summary>
    public void Tick(float time, bool youKnown, Vector3 youPos, float youYaw, string nextStop, bool garage)
    {
        if (data == null) return;
        // locked districts: hatch + padlock; unlocked: no hatch, an open gate
        foreach (var h in hatches)
        {
            bool locked = !Districts.IsUnlocked(Districts.Get(h.rt.gameObject.name));
            if (h.rt.gameObject.activeSelf != locked) h.rt.gameObject.SetActive(locked);
        }
        foreach (var g in gates)
        {
            bool locked = !Districts.IsUnlocked(Districts.Get(g.rt.gameObject.name));
            var sp = locked ? MapIcons.Padlock : MapIcons.GateOpen;
            if (g.img.sprite != sp) g.img.sprite = sp;
            g.img.color = locked ? new Color(1f, 0.84f, 0.3f) : new Color(0.55f, 1f, 0.6f);
        }

        // pips: rebuild on curb changes (and once a second, so ages tick from fresh → late without an event)
        float now = Time.unscaledTime;
        if (pipsDirty || !Application.isPlaying || now >= nextPipRefresh)
        {
            pipsDirty = false;
            nextPipRefresh = now + 1f;
            RebuildPips();
        }
        for (int i = 0; i < pipCount; i++)
        {
            var p = pips[i];
            if (!p.neglected) continue;
            float s = 1f + 0.25f * Mathf.Sin(time * Mathf.PI * 3f); // 1.5 Hz
            p.stink.rectTransform.localScale = new Vector3(s, s, 1f);
            p.stink.color = new Color(0.62f, 0.85f, 0.25f, 0.55f + 0.35f * Mathf.Sin(time * Mathf.PI * 3f));
        }

        // counter-rotate upright things so they read at any heading
        var counter = Quaternion.Euler(0f, 0f, -rotation);
        foreach (var g in gates) g.rt.localRotation = counter;
        if (depot != null) depot.rt.localRotation = counter;
        foreach (var e in exits) e.rt.localRotation = Quaternion.Euler(0f, 0f, -e.heading);
        if (GameSettings.ColorblindMarkers || !Application.isPlaying)
            for (int i = 0; i < pipCount; i++) pips[i].rt.localRotation = counter;

        // next stop (§7.3): a gold ring pulsing at 1.5 Hz + a bouncing chevron, on the same house as the HUD arrow
        int hi = data.HouseIndex(nextStop);
        hasRing = hi >= 0;
        ring.rt.gameObject.SetActive(hasRing);
        chevron.rt.gameObject.SetActive(hasRing);
        if (hasRing)
        {
            ringXZ = data.houses[hi].curb;
            ring.rt.anchoredPosition = ToPan(ringXZ);
            float pulse = 1f + 0.18f * Mathf.Sin(time * Mathf.PI * 3f);
            ring.rt.localScale = new Vector3(pulse, pulse, 1f);
            ring.rt.localRotation = counter;
            chevron.rt.localRotation = counter * Quaternion.Euler(0f, 0f, -90f); // points down at the house
            float bounce = 22f + 6f * Mathf.Abs(Mathf.Sin(time * 5f));
            chevron.rt.anchoredPosition = ToPan(ringXZ) + (Vector2)(counter * new Vector3(0f, bounce, 0f));
            int last = iconRoot.childCount - 1;
            if (chevron.rt.GetSiblingIndex() != last) { ring.rt.SetAsLastSibling(); chevron.rt.SetAsLastSibling(); }
        }
        ringStop = nextStop;

        TickBeacons(time, youKnown, youPos, youYaw);
    }

    /// <summary>The next-stop ring's RectTransform (checks: its drawn position vs the projected target).</summary>
    public RectTransform RingRect => ring != null ? ring.rt : null;

    /// <summary>Where the ring is inside the viewport (from the centre), for the rim clamp and the 2 px check.</summary>
    public Vector2 RingViewport => ToViewport(ringXZ);
    public Vector2 RingXZ => ringXZ;

    void RebuildPips()
    {
        pipCount = 0;
        bool live = Application.isPlaying;
        for (int i = 0; i < data.houses.Count; i++)
        {
            var h = data.houses[i];
            int bags;
            double age;
            DistrictDef d;
            if (live)
            {
                if (!Progression.IsSubscribed(h.stopId)) continue;
                d = Districts.ForStopId(h.stopId);
                if (!Districts.IsUnlocked(d)) continue;
                var st = FeelWorld.At(h.stopId);
                bags = st.bags;
                age = st.oldestAge;
            }
            else
            {
                // edit-mode preview: a deterministic spread of fake curb trash
                d = Districts.ForStopId(h.stopId);
                if (!Districts.IsUnlocked(d)) continue;
                uint hh = (uint)(i * 2654435761u);
                bags = (int)(hh % 7u) - 1;
                age = (hh >> 8) % 140 / 10.0;
            }
            if (bags <= 0) continue;
            var p = PipAt(pipCount++);
            p.house = i;
            p.size = bags <= 2 ? 10f : bags <= 4 ? 13f : 16f;
            if (!minimap) p.size += 2f;
            var stage = CurbTrash.FreshnessStage(d, age);
            bool cb = live && GameSettings.ColorblindMarkers;
            p.fill.sprite = cb ? FeelUi.Glyph(stage) : FeelUi.Circle;
            p.outline.sprite = p.fill.sprite;
            p.fill.color = CurbTrash.FreshnessColor(d, age);
            p.neglected = Districts.IsNeglected(d, age);
            p.stink.gameObject.SetActive(p.neglected);
            p.rt.gameObject.SetActive(true);
            PlacePip(p);
        }
        for (int i = pipCount; i < pips.Count; i++) pips[i].rt.gameObject.SetActive(false);
    }

    Pip PipAt(int i)
    {
        while (pips.Count <= i)
        {
            var p = new Pip();
            p.rt = FeelUi.Rect("Pip", pipRoot, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(16f, 16f));
            p.stink = FeelUi.Img(p.rt, "Stink", FeelUi.Stink, new Color(0.62f, 0.85f, 0.25f, 0.8f));
            p.stink.rectTransform.offsetMin = new Vector2(-4f, 6f);
            p.stink.rectTransform.offsetMax = new Vector2(4f, 20f);
            p.outline = FeelUi.Img(p.rt, "Outline", FeelUi.Circle, Color.white);
            p.outline.rectTransform.offsetMin = new Vector2(-2.5f, -2.5f);
            p.outline.rectTransform.offsetMax = new Vector2(2.5f, 2.5f);
            p.fill = FeelUi.Img(p.rt, "Fill", FeelUi.Circle, Color.green);
            pips.Add(p);
        }
        return pips[i];
    }

    void PlacePip(Pip p)
    {
        if (p.house < 0 || p.house >= data.houses.Count) return;
        p.rt.anchoredPosition = ToPan(data.houses[p.house].curb);
        p.rt.sizeDelta = new Vector2(p.size, p.size);
    }

    // ------------------------------------------------------------------ the planned route (gold dots)

    /// <summary>The planned route as world XZ points (you → stop 1 → stop 2 …), already following roads when known.</summary>
    public void SetRoute(List<Vector2> pts)
    {
        routePts.Clear();
        if (pts != null) routePts.AddRange(pts);
        LayoutRouteDots();
    }

    void LayoutRouteDots()
    {
        dotCount = 0;
        if (data != null && routePts.Count >= 2)
        {
            float step = Mathf.Max(4f, 11f / k);           // 6 px dots every 4 m (spaced out when zoomed far out)
            float carry = step * 0.5f;
            for (int i = 1; i < routePts.Count && dotCount < 400; i++)
            {
                Vector2 a = routePts[i - 1], b = routePts[i];
                float len = Vector2.Distance(a, b);
                float t = carry;
                while (t <= len && dotCount < 400)
                {
                    var dot = DotAt(dotCount++);
                    dot.rectTransform.anchoredPosition = ToPan(Vector2.Lerp(a, b, len > 0f ? t / len : 0f));
                    t += step;
                }
                carry = t - len;
            }
        }
        for (int i = dotCount; i < dots.Count; i++) if (dots[i].gameObject.activeSelf) dots[i].gameObject.SetActive(false);
    }

    Image DotAt(int i)
    {
        while (dots.Count <= i)
        {
            var rt = FeelUi.Rect("Dot", routeRoot, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(6f, 6f));
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = FeelUi.Circle;
            img.color = UiTheme.Gold;
            img.raycastTarget = false;
            dots.Add(img);
        }
        var d = dots[i];
        if (!d.gameObject.activeSelf) d.gameObject.SetActive(true);
        return d;
    }

    public int RouteDots => dotCount;

    // ------------------------------------------------------------------ beacons

    void SetYouVisible(bool on)
    {
        if (you == null) return;
        if (you.rt.gameObject.activeSelf != on)
        {
            you.rt.gameObject.SetActive(on);
            youOutline.rt.gameObject.SetActive(on);
            youCone.rt.gameObject.SetActive(on);
        }
    }

    /// <summary>Preview beacons (edit mode): (world pos, yaw, tint, kind).</summary>
    public readonly List<(Vector3 pos, float yaw, Color tint, MapBeaconKind kind)> PreviewBeacons = new List<(Vector3, float, Color, MapBeaconKind)>();

    void TickBeacons(float time, bool youKnown, Vector3 youPos, float youYaw)
    {
        int n = 0;
        var local = Application.isPlaying ? PlayerController.Local : null; // coop:allow(local) HUD/camera only: which arrow is "you"
        if (Application.isPlaying)
        {
            var all = MapBeacon.All;
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == null || !b.isActiveAndEnabled) continue;
                if (b.kind == MapBeaconKind.Player && b.Player != null && b.Player == local) continue; // drawn as "you"
                if (b.kind == MapBeaconKind.Player && b.Player != null && b.Player.IsDriving) continue; // in the truck
                if (b.kind == MapBeaconKind.Player) b.RefreshPlayerTint();
                var t = b.Target;
                PlaceBeacon(BeaconAt(n++), b.kind, b.tint, new Vector2(t.position.x, t.position.z), t.eulerAngles.y);
            }
        }
        else
        {
            foreach (var pb in PreviewBeacons) PlaceBeacon(BeaconAt(n++), pb.kind, pb.tint, new Vector2(pb.pos.x, pb.pos.z), pb.yaw);
        }
        for (int i = n; i < beacons.Count; i++) if (beacons[i].rt.gameObject.activeSelf) beacons[i].rt.gameObject.SetActive(false);

        SetYouVisible(youKnown);
        if (youKnown)
        {
            var p = ToPan(new Vector2(youPos.x, youPos.z));
            var rot = Quaternion.Euler(0f, 0f, -youYaw);
            you.rt.anchoredPosition = youOutline.rt.anchoredPosition = youCone.rt.anchoredPosition = p;
            you.rt.localRotation = youOutline.rt.localRotation = youCone.rt.localRotation = rot;
            you.img.color = local != null ? MapBeacon.PlayerColor(local) : UiTheme.Tangerine;
            if (you.rt.GetSiblingIndex() != beaconRoot.childCount - 1) you.rt.SetAsLastSibling();
        }
    }

    Icon BeaconAt(int i)
    {
        while (beacons.Count <= i)
        {
            var b = NewIcon(beaconRoot, "Beacon", MapIcons.Arrow, Color.white, new Vector2(26f, 26f), false);
            var outline = FeelUi.Rect("Outline", b.rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(30f, 30f));
            outline.SetAsFirstSibling();
            var oi = outline.gameObject.AddComponent<Image>();
            oi.sprite = MapIcons.ArrowOutline;
            oi.color = new Color(0.11f, 0.09f, 0.17f, 0.9f);
            oi.raycastTarget = false;
            beacons.Add(b);
        }
        var r = beacons[i];
        if (!r.rt.gameObject.activeSelf) r.rt.gameObject.SetActive(true);
        return r;
    }

    void PlaceBeacon(Icon b, MapBeaconKind kind, Color tint, Vector2 xz, float yaw)
    {
        b.rt.anchoredPosition = ToPan(xz);
        b.rt.localRotation = Quaternion.Euler(0f, 0f, -yaw);
        var outline = b.rt.GetChild(0).GetComponent<Image>();
        switch (kind)
        {
            case MapBeaconKind.Truck:
                b.img.sprite = MapIcons.Truck;
                b.img.color = tint;
                b.img.rectTransform.sizeDelta = new Vector2(minimap ? 30f : 36f, minimap ? 30f : 36f);
                outline.sprite = MapIcons.Truck;
                outline.rectTransform.sizeDelta = new Vector2(minimap ? 36f : 42f, minimap ? 36f : 42f);
                break;
            case MapBeaconKind.Car:
                b.img.sprite = FeelUi.Circle;
                b.img.color = new Color(0.7f, 0.7f, 0.74f, 0.9f);
                b.img.rectTransform.sizeDelta = new Vector2(7f, 7f);
                outline.sprite = FeelUi.Circle;
                outline.rectTransform.sizeDelta = new Vector2(9f, 9f);
                break;
            default:
                b.img.sprite = MapIcons.Arrow;
                b.img.color = tint;
                b.img.rectTransform.sizeDelta = new Vector2(minimap ? 24f : 28f, minimap ? 24f : 28f);
                outline.sprite = MapIcons.ArrowOutline;
                outline.rectTransform.sizeDelta = new Vector2(minimap ? 28f : 32f, minimap ? 28f : 32f);
                break;
        }
    }

    /// <summary>Full map only: street labels show at ≥ 1.5× zoom; district and park names always.</summary>
    public void SetLabelVisibility(bool streets)
    {
        foreach (var l in labels)
        {
            bool on = l.kind != MapLabelKind.Street || streets;
            if (l.t.gameObject.activeSelf != on) l.t.gameObject.SetActive(on);
        }
    }

    /// <summary>Counter-rotate labels? (Full map is north-up; labels keep their road angle.)</summary>
    public int LabelCount => labels.Count;
}
