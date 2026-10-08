using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The minimap (Docs/UIPass.md §7.3): a round galvanised <b>bin lid</b> in the bottom-left HUD zone (200 / 250 / 300 px by the
/// setting, stepped down by <see cref="HudLayout"/> when the left column needs the room), with a street-label pill under it
/// ("Maple St · Mildly Pleasant Acres"). Heading-up by default (on foot: the camera's yaw; driving: the truck's), north-up in
/// Settings; the lid handle at the top is the heading notch and an "N" tab orbits the rim. Visible radius 45 m on foot,
/// growing to 90 m between 0 and 15 m/s while driving (smoothed). Shows roads and district tints (the baked PNG), curb pips
/// by freshness, the planned route, the next stop (the same house as the HUD arrow), the depot, gates, exits, trucks, crew
/// and you. The next stop, the depot and the truck stick to the rim with a distance when they're off the lid.
/// <para>A nested Canvas under FeelHUD, so its per-frame rebatch doesn't dirty the HUD canvas. Shown only while a
/// <see cref="TownMapAnchor"/> is active (hidden in the garage, in Station mode, while the full map is open, in a solo pause
/// and in menus; FeelHud decides). Ticks under the <c>Minimap.Tick</c> profiler marker.</para>
/// </summary>
public sealed class Minimap
{
    public const float RadiusOnFoot = 45f, RadiusDriving = 90f, SpeedForMaxZoom = 15f;
    const float RimInset = 14f;

    static readonly Unity.Profiling.ProfilerMarker TickMarker = new Unity.Profiling.ProfilerMarker("Minimap.Tick");

    public RectTransform Root { get; private set; }
    RectTransform lid, maskRt, frameRt, handleRt, tabRt, pillRt, flyRt, rimRoot;
    Canvas nested;
    CanvasGroup group;
    MapView view;
    Image fly;
    TextMeshProUGUI pillText, tabText;
    string pillShown, pillStreet;
    DistrictDef pillDistrict;
    float alpha, radius = RadiusOnFoot, speedSmoothed, diameter = 250f;
    Vector3 lastCenter;
    bool hasLast;
    readonly List<Vector2> routeXZ = new List<Vector2>(32);
    readonly List<PickupStop> planned = new List<PickupStop>(4);
    float nextRoutePlan;
    int routeKey;
    bool routeKeySet;

    sealed class RimIcon
    {
        public RectTransform rt;
        public Image img, back;
        public TextMeshProUGUI label;
        public int shownDist = -1;
    }
    RimIcon rimStop, rimDepot, rimTruck;

    // ---- preview (edit mode)
    public struct PreviewState
    {
        public Vector3 center;
        public float heading, speed;
        public bool driving;
        public string nextStop;
        public List<Vector3> route;
        public float night;
        public float city;
    }
    PreviewState? previewState;

    /// <summary>The minimap is on screen.</summary>
    public bool Visible => alpha > 0.01f && Root != null && Root.gameObject.activeSelf;
    /// <summary>The lid + pill block (top-down HUD reference units).</summary>
    public Rect BlockRect { get; private set; }
    public MapView View => view;
    /// <summary>The lid and the street pill as drawn (the overlap audit measures these, not just the zone).</summary>
    public RectTransform LidRect => lid;
    public RectTransform PillRect => pillRt != null && pillRt.gameObject.activeSelf ? pillRt : null;
    /// <summary>The visible radius right now (m): 45 on foot … 90 at 15 m/s.</summary>
    public float Radius => radius;
    public float Diameter => diameter;
    /// <summary>The smoothed speed (m/s) the zoom follows (from the centre's motion).</summary>
    public float Speed => speedSmoothed;
    /// <summary>The next-stop ring's offset from the lid centre (px, after rotation), for the 2 px check.</summary>
    public Vector2 RingOffset => view != null ? view.RingViewport : Vector2.zero;
    public bool RingShown => view != null && view.RingShown;

    public static Minimap Create(RectTransform hudRoot)
    {
        var m = new Minimap();
        m.Build(hudRoot);
        return m;
    }

    void Build(RectTransform hudRoot)
    {
        Root = FeelUi.Rect("Minimap", hudRoot, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(250f, 290f));
        nested = Root.gameObject.AddComponent<Canvas>(); // nested: the lid's per-frame changes rebatch only this canvas
        nested.overrideSorting = false;
        group = Root.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;

        lid = FeelUi.Rect("Lid", Root, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(125f, -125f), new Vector2(250f, 250f));
        // drop shadow under the lid
        var sh = FeelUi.Img(lid, "Shadow", FeelUi.Circle, new Color(0f, 0f, 0f, 0.4f));
        sh.rectTransform.offsetMin = new Vector2(5f, -7f);
        sh.rectTransform.offsetMax = new Vector2(5f, -7f);
        // the map, masked by a circle a touch inside the lid ring
        maskRt = FeelUi.Rect("Mask", lid, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(220f, 220f));
        var maskImg = maskRt.gameObject.AddComponent<Image>();
        maskImg.sprite = FeelUi.Circle;
        maskImg.color = MapRaster.Wild;
        maskImg.raycastTarget = false;
        maskRt.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        view = new MapView(maskRt, new Vector2(220f, 220f), true);

        frameRt = FeelUi.Rect("Frame", lid, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(250f, 250f));
        var frame = frameRt.gameObject.AddComponent<Image>();
        frame.sprite = MapIcons.Lid;
        frame.raycastTarget = false;
        handleRt = FeelUi.Rect("Handle", lid, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, -8f), new Vector2(64f, 30f));
        var handleShadow = FeelUi.Img(handleRt, "Shadow", MapIcons.Handle, new Color(0.12f, 0.12f, 0.16f, 0.8f));
        handleShadow.rectTransform.offsetMin = new Vector2(1.5f, -2.5f);
        handleShadow.rectTransform.offsetMax = new Vector2(1.5f, -2.5f);
        FeelUi.Img(handleRt, "Bar", MapIcons.Handle, new Color(0.8f, 0.83f, 0.87f)); // drawn after (over) its shadow

        rimRoot = FeelUi.Rect("Rim", lid, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        rimStop = MakeRim("Stop", UiKit.Sprites.Ring, UiTheme.Gold);
        rimDepot = MakeRim("Depot", FeelUi.Rounded, MapBeacon.TruckOlive);
        var r = FeelUi.Text(rimDepot.img.rectTransform, "R", 18f, Color.white, TextAlignmentOptions.Center);
        r.text = "R";
        rimTruck = MakeRim("Truck", MapIcons.Truck, MapBeacon.TruckOlive);

        // the "N" tab orbits the rim
        tabRt = FeelUi.Rect("NTab", lid, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(30f, 30f));
        var tab = tabRt.gameObject.AddComponent<Image>();
        tab.sprite = MapIcons.Tab;
        tab.color = UiTheme.StampRed;
        tab.raycastTarget = false;
        tabText = FeelUi.Text(tabRt, "N", 20f, Color.white, TextAlignmentOptions.Center);
        tabText.text = "N";

        // a fly lands on the lid when the city's under 35% clean
        flyRt = FeelUi.Rect("Fly", lid, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(18f, 18f));
        fly = flyRt.gameObject.AddComponent<Image>();
        fly.sprite = MapIcons.Fly;
        fly.color = new Color(0.12f, 0.1f, 0.14f, 0.95f);
        fly.raycastTarget = false;
        flyRt.gameObject.SetActive(false);

        // the street-label pill under the lid
        pillRt = FeelUi.Rect("Street", Root, new Vector2(0f, 1f), new Vector2(0.5f, 1f), new Vector2(125f, -256f), new Vector2(240f, HudLayout.MinimapPillH));
        FeelUi.Panel(pillRt, FeelUi.PanelColor, 0.5f, new Vector2(3f, -4f));
        pillText = FeelUi.Text(pillRt, "Text", 19f, Color.white, TextAlignmentOptions.Center);
        Root.gameObject.SetActive(false);
    }

    RimIcon MakeRim(string name, Sprite sprite, Color color)
    {
        var ri = new RimIcon();
        ri.rt = FeelUi.Rect("Rim" + name, rimRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(24f, 24f));
        var back = FeelUi.Img(ri.rt, "Back", FeelUi.Circle, new Color(0.11f, 0.09f, 0.17f, 0.92f));
        back.rectTransform.offsetMin = new Vector2(-5f, -5f);
        back.rectTransform.offsetMax = new Vector2(5f, 5f);
        ri.back = back;
        var irt = FeelUi.Rect("Icon", ri.rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(22f, 22f));
        ri.img = irt.gameObject.AddComponent<Image>();
        ri.img.sprite = sprite;
        ri.img.color = color;
        ri.img.raycastTarget = false;
        if (sprite == FeelUi.Rounded) { ri.img.type = Image.Type.Sliced; ri.img.pixelsPerUnitMultiplier = 3f; }
        ri.label = FeelUi.Text(ri.rt, "Dist", 18f, Color.white, TextAlignmentOptions.Center);
        ri.label.rectTransform.anchorMin = ri.label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        ri.label.rectTransform.sizeDelta = new Vector2(140f, 22f);
        ri.rt.gameObject.SetActive(false);
        return ri;
    }

    /// <summary>Edit-mode preview: draw this state instead of the live game.</summary>
    public void SetPreview(PreviewState p) => previewState = p;

    public void OnSceneChanged()
    {
        hasLast = false;
        speedSmoothed = 0f;
        routeKeySet = false;
    }

    public void Dispose() => view?.Dispose();

    /// <summary>Checks: count the bytes allocated inside Minimap.Tick / FullMap.Tick (UP2-6). Off by default.</summary>
    public static bool MeasureAllocs;
    /// <summary>Bytes allocated inside the last Tick (while <see cref="MeasureAllocs"/>).</summary>
    public static long LastTickAlloc { get; private set; }

    /// <summary>Lay out in the HUD's minimap zone and refresh (FeelHud calls this every frame).</summary>
    public void Tick(HudLayout.Frame layout, bool visible, bool label, float dt, bool preview)
    {
        long a0 = MeasureAllocs ? System.GC.GetAllocatedBytesForCurrentThread() : 0;
        TickInner(layout, visible, label, dt, preview);
        if (MeasureAllocs) LastTickAlloc = System.GC.GetAllocatedBytesForCurrentThread() - a0;
    }

    void TickInner(HudLayout.Frame layout, bool visible, bool label, float dt, bool preview)
    {
        using (TickMarker.Auto())
        {
            var data = TownMapData.Load();
            bool on = visible && data != null && data.texture != null;
            alpha = Mathf.MoveTowards(alpha, on ? 1f : 0f, dt * 6f);
            group.alpha = alpha;
            bool active = alpha > 0.001f;
            if (Root.gameObject.activeSelf != active) Root.gameObject.SetActive(active);
            BlockRect = layout.minimap;
            if (!active) { hasLast = false; return; }
            view.Bind(data);

            // ---- geometry: the lid in the zone, the pill under it
            diameter = Mathf.Max(HudLayout.MinimapSmallest, layout.minimapDiameter);
            HudLayout.Place(Root, layout.minimap.width > 0f ? layout.minimap : new Rect(HudLayout.Margin, 0f, diameter, diameter));
            lid.sizeDelta = new Vector2(diameter, diameter);
            lid.anchoredPosition = new Vector2(diameter * 0.5f, -diameter * 0.5f);
            float inner = diameter * (MapIcons.LidInner * 2f) + 2f;
            maskRt.sizeDelta = new Vector2(inner, inner);
            frameRt.sizeDelta = new Vector2(diameter, diameter);
            handleRt.sizeDelta = new Vector2(diameter * 0.26f, diameter * 0.12f);
            pillRt.gameObject.SetActive(label);
            pillRt.anchoredPosition = new Vector2(diameter * 0.5f, -(diameter + HudLayout.MinimapPillGap));

            // ---- where we are and which way is up
            Vector3 centerW;
            float heading, speed;
            bool driving, northUp = !preview && GameSettings.MinimapNorthUp;
            string nextStop;
            float night, city;
            var pc = preview ? null : PlayerController.Local; // coop:allow(local) HUD/camera only
            if (previewState.HasValue && preview)
            {
                var p = previewState.Value;
                centerW = p.center; heading = p.heading; speed = p.speed; driving = p.driving; nextStop = p.nextStop;
                night = p.night; city = p.city;
                routeXZ.Clear();
                if (p.route != null) foreach (var v in p.route) routeXZ.Add(new Vector2(v.x, v.z));
                view.SetRoute(routeXZ);
            }
            else
            {
                if (pc == null) return;
                driving = pc.IsDriving && pc.currentTruck != null;
                var body = driving ? pc.currentTruck.transform : pc.transform;
                centerW = body.position;
                var cam = Camera.main;
                heading = driving ? body.eulerAngles.y : cam != null ? cam.transform.eulerAngles.y : body.eulerAngles.y;
                // speed from the centre's motion (works for guests too, whose truck body is kinematic)
                if (hasLast && dt > 0.0001f)
                {
                    var dv = centerW - lastCenter; dv.y = 0f;
                    float sp = dv.magnitude / dt;
                    if (sp > 60f) sp = 0f; // a teleport / scene move, not speed
                    speedSmoothed = Mathf.Lerp(speedSmoothed, sp, 1f - Mathf.Exp(-3f * dt));
                }
                lastCenter = centerW;
                hasLast = true;
                speed = speedSmoothed;
                var hud = FeelRunner.Get() != null ? FeelRunner.Get().Hud : null;
                var target = hud != null && hud.Route != null ? hud.Route.Target : null;
                nextStop = target != null ? target.stopId : null;
                night = GameClock.Night01;
                city = FeelWorld.IsLive ? FeelWorld.Cleanliness : 100f;
                PlanRoute(pc, centerW, target, hud != null && hud.Route != null ? hud.Route.Chain : null);
            }

            // ---- zoom: 45 m on foot → 90 m at 15 m/s driving
            float want = driving ? Mathf.Lerp(RadiusOnFoot, RadiusDriving, Mathf.Clamp01(speed / SpeedForMaxZoom)) : RadiusOnFoot;
            radius = preview ? want : Mathf.Lerp(radius, want, 1f - Mathf.Exp(-4f * dt));
            float k = inner * 0.5f / radius;
            float rot = northUp ? 0f : heading;
            view.SetView(new Vector2(centerW.x, centerW.z), k, rot, new Vector2(inner, inner));
            view.SetNight(night);
            float time = Time.unscaledTime;
            view.Tick(time, true, centerW, heading, nextStop, false);

            // ---- rim: the N tab, the handle notch, off-lid targets
            Vector2 north = Rotate(new Vector2(0f, 1f), rot);
            float rr = diameter * 0.5f - diameter * 0.03f;
            tabRt.anchoredPosition = north * rr;
            tabRt.sizeDelta = Vector2.one * Mathf.Clamp(diameter * 0.12f, 24f, 34f);
            handleRt.gameObject.SetActive(!northUp);
            TickRim(data, centerW, rot, k, inner * 0.5f, driving, nextStop);

            // ---- the fly (city under 35% clean)
            bool flyOn = city < 35f;
            if (flyRt.gameObject.activeSelf != flyOn) flyRt.gameObject.SetActive(flyOn);
            if (flyOn)
            {
                float a = time * 1.7f;
                flyRt.anchoredPosition = new Vector2(Mathf.Cos(a) * rr * 0.92f + 6f * Mathf.Sin(time * 23f), Mathf.Sin(a * 1.3f) * rr * 0.92f + 5f * Mathf.Cos(time * 19f));
                flyRt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(time * 30f) * 25f);
            }

            // ---- the street-label pill (rebuilt only when the street or district changes: no per-frame strings)
            if (label)
            {
                string street = data.StreetAt(centerW, 14f);
                var d = Districts.AtPosition(centerW);
                if (!ReferenceEquals(street, pillStreet) || !ReferenceEquals(d, pillDistrict) || pillShown == null)
                {
                    pillStreet = street;
                    pillDistrict = d;
                    // "Maple St · Mildly Pleasant Acres" if it fits the lid's width, else just the street (the district is on
                    // its welcome card and the full map), else trimmed; the pill never leaves the minimap block
                    float maxW = diameter;
                    pillText.fontSize = 19f;
                    string s = string.IsNullOrEmpty(street) ? d.name : $"{street} · {d.name}";
                    if (pillText.GetPreferredValues(s, 100000f, 0f).x > maxW - 24f && !string.IsNullOrEmpty(street)) s = street;
                    pillShown = s;
                    pillText.text = FeelUi.Fit(pillText, s, maxW - 24f);
                    float w = Mathf.Min(maxW, pillText.GetPreferredValues(pillText.text, 100000f, 0f).x + 28f);
                    pillRt.sizeDelta = new Vector2(Mathf.Max(120f, w), HudLayout.MinimapPillH);
                }
            }
        }
    }

    static Vector2 Rotate(Vector2 v, float deg)
    {
        float a = deg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
        return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }

    /// <summary>Next stop, depot and truck: when off the lid they stick to the rim with a distance ("Depot 240 m").</summary>
    void TickRim(TownMapData data, Vector3 centerW, float rot, float k, float innerR, bool driving, string nextStop)
    {
        var c = new Vector2(centerW.x, centerW.z);
        // next stop
        int hi = data.HouseIndex(nextStop);
        Rim(rimStop, hi >= 0, hi >= 0 ? data.houses[hi].curb : c, c, k, innerR, null);
        // depot (the garage door)
        bool depotKnown = DepotMapLocations.TryLocalPosition(data, out var depotXZ);
        Rim(rimDepot, depotKnown, depotXZ, c, k, innerR, "Depot");
        // the truck: only while on foot and more than 40 m away (MP §2.5's "arrow to the truck")
        bool truckOn = false;
        Vector2 truckXZ = c;
        if (!driving && Application.isPlaying)
        {
            float best = float.MaxValue;
            var all = MapBeacon.All;
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == null || b.kind != MapBeaconKind.Truck) continue;
                var p = b.Target.position;
                float d = (new Vector2(p.x, p.z) - c).sqrMagnitude;
                if (d < best) { best = d; truckXZ = new Vector2(p.x, p.z); truckOn = true; }
            }
            if (truckOn && best < 40f * 40f) truckOn = false;
        }
        Rim(rimTruck, truckOn, truckXZ, c, k, innerR, "Truck");
    }

    void Rim(RimIcon ri, bool known, Vector2 targetXZ, Vector2 c, float k, float innerR, string name)
    {
        var local = view.ToViewport(targetXZ);
        bool off = known && local.magnitude > innerR - 6f;
        if (ri.rt.gameObject.activeSelf != off) ri.rt.gameObject.SetActive(off);
        if (!off) return;
        var dir = local.normalized;
        float r = innerR + (diameter * 0.5f - innerR) * 0.5f;
        ri.rt.anchoredPosition = dir * r;
        int dist = Mathf.RoundToInt(Vector2.Distance(targetXZ, c));
        if (dist != ri.shownDist)
        {
            ri.shownDist = dist;
            // TMP's SetText(format, value) formats into its own buffer: no garbage per change
            if (name == "Depot") ri.label.SetText("Depot {0} m", dist);
            else if (name == "Truck") ri.label.SetText("Truck {0} m", dist);
            else ri.label.SetText("{0} m", dist);
        }
        // the label sits just inside the lid, towards the centre
        ri.label.rectTransform.anchoredPosition = -dir * 30f;
    }

    // ------------------------------------------------------------------ planned route (gold dots along the roads)

    static LifeAStar astar;
    static StreetGraph astarGraph;
    readonly List<int> pathNodes = new List<int>(128);

    void PlanRoute(PlayerController pc, Vector3 from, PickupStop first, IReadOnlyList<PickupStop> chain)
    {
        float now = Time.unscaledTime;
        if (now < nextRoutePlan) return;
        nextRoutePlan = now + 0.5f;
        // the same plan as the HUD's route rows (no second planner): [0] = the arrow's house
        planned.Clear();
        if (first != null) planned.Add(first);
        if (chain != null)
            for (int i = 0; i < chain.Count && planned.Count < 3; i++)
                if (chain[i] != null && !planned.Contains(chain[i])) planned.Add(chain[i]);
        // re-route only when the start node or the stops changed (an int key: no garbage)
        var g = StreetGraph.Load();
        int startNode = g != null && g.NodeCount > 0 ? g.Nearest(from, LaneMask) : -1;
        int key = startNode;
        for (int i = 0; i < planned.Count; i++) key = key * 31 + planned[i].GetInstanceID();
        key = key * 31 + planned.Count;
        if (key == routeKey && routeKeySet) return;
        routeKey = key;
        routeKeySet = true;

        routeXZ.Clear();
        if (planned.Count > 0)
        {
            routeXZ.Add(new Vector2(from.x, from.z));
            Vector3 at = from;
            int atNode = startNode;
            for (int i = 0; i < planned.Count; i++)
            {
                Vector3 to = planned[i].transform.position;
                AppendRoad(g, ref atNode, at, to);
                routeXZ.Add(new Vector2(to.x, to.z));
                at = to;
            }
        }
        view.SetRoute(routeXZ);
    }

    static readonly int LaneMask = 1 << (int)StreetGraph.NodeKind.Lane;

    /// <summary>Follow the street graph's road lanes from <paramref name="a"/> to <paramref name="b"/> when it exists (else a straight line).</summary>
    void AppendRoad(StreetGraph g, ref int fromNode, Vector3 a, Vector3 b)
    {
        if (g == null || g.NodeCount == 0 || fromNode < 0) return;
        if (!ReferenceEquals(g, astarGraph) || astar == null) { astar = new LifeAStar(g); astarGraph = g; }
        int toNode = g.Nearest(b, LaneMask);
        if (toNode < 0) return;
        pathNodes.Clear();
        if (astar.Route(fromNode, toNode, LifeRouteMode.Car, -1, -1, pathNodes, null) < 0) { fromNode = toNode; return; }
        for (int i = 0; i < pathNodes.Count; i++)
        {
            var p = g.nodes[pathNodes[i]].pos;
            routeXZ.Add(new Vector2(p.x, p.z));
        }
        fromNode = toNode;
    }

    /// <summary>The route points (world XZ) the dots were laid along (tests).</summary>
    public IReadOnlyList<Vector2> RoutePoints => routeXZ;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { astar = null; astarGraph = null; MeasureAllocs = false; }
}
