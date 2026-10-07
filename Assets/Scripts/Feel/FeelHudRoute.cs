using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>A planned stop row on the route panel (also used to fake data in the edit-mode preview).</summary>
public struct RouteRow
{
    public string address;
    public int bags;
    /// <summary>In-game hours since the oldest bag at this curb went out.</summary>
    public float ageHours;
    public float dist;
    /// <summary>The stop's district (its own on-time / neglect windows for the row's color and label); null = starter.</summary>
    public DistrictDef district;
}

/// <summary>
/// The route panel (top-right, under the truck gauge): the big yellow arrow that points to the next stop, plus —
/// in the living world — the live planner: "12 bags out", a LATE / ALL FRESH pill, and the next 3 planned stops
/// with freshness colors ("9 Maple St — 3 bags · 2h left"). Falls back to the legacy "Stops 3/8" look when the
/// world has no random curb trash yet.
///
/// Random trash vs the arrow: the arrow always points exactly where TrashRoute.NextStop says (the same house the
/// in-world beams/markers point at). NextStop already has hysteresis (sticks to the curb you're working, only
/// switches for a clearly better house), so new bags appearing elsewhere don't make it flicker; when it does swing
/// to a new house the arrow pops so you notice. It points at the depot when the truck can't fit another bag,
/// or when every curb is clean and there's a load to dump.
/// </summary>
public sealed class FeelHudRoute
{
    public const float Width = 420f, HeaderHeight = 132f, RowHeight = 32f;
    public const int MaxRows = 3;
    /// <summary>Free truck capacity (kg) below which the route panel sends you back to the depot (≈ one typical bag).</summary>
    public const float TruckFullFreeKg = TruckHudRules.DepotFreeCapacityKg;
    const float PlanInterval = 0.25f;

    sealed class Row
    {
        public RectTransform rt;
        public Image hl, dot;
        public TextMeshProUGUI addr, info;
        public float scale = 1f, vel;
        public string addrShown, infoShown;
    }

    // ---- view
    RectTransform panel, arrowRt, arrowBgRt, listRt, pillRt;
    CanvasGroup group, listGroup, pillGroup;
    Image arrowBg, pill;
    TextMeshProUGUI routeNameText, stopsText, nextText, pillText;
    readonly Row[] rows = new Row[MaxRows];
    float routeAlpha, arrowAngle, arrowVel, stopsScale = 1f, stopsScaleVel, stopsFlash, arrowScale = 1f, arrowScaleVel,
          pillScale = 1f, pillVel, listAlpha, pillAlpha, height = HeaderHeight, heightVel;
    int lastCleared = -1, lastBags = -1, lastLate = -1;
    bool lastComplete, lastFull, lastClean;
    string lastNextStr, lastStopsStr, lastRouteName, lastTargetLabel, lastPillStr;
    float lastNameW = -1f;
    Color flashColor = FeelUi.GoodGreen;
    float width = Width;
    int rowLimit = MaxRows;

    // ---- live gather state
    PickupStop target;
    float nextPlan;
    PickupStop planFor;
    readonly List<PickupStop> planned = new List<PickupStop>();
    readonly List<PickupStop> chain = new List<PickupStop>();
    readonly RouteRow[][] rowArrays = { new RouteRow[0], new RouteRow[1], new RouteRow[2], new RouteRow[3] };
    readonly List<GarageDoorController> doors = new List<GarageDoorController>();
    float nextDoorScan;

    public RectTransform Panel => panel;
    /// <summary>The panel's current (animated) height.</summary>
    public float CurrentHeight => panel != null ? panel.sizeDelta.y : 0f;
    /// <summary>Header + the planned rows it would show (for the right column's budget).</summary>
    public static float HeightFor(int rows) => HeaderHeight + (rows > 0 ? rows * RowHeight + 8f : 0f);
    /// <summary>0..1 how much of the panel is on screen.</summary>
    public float Presence => routeAlpha;
    /// <summary>The house the arrow currently points at (live mode), or null.</summary>
    public PickupStop Target => target;
    /// <summary>The planned chain the rows show ([0] = the arrow's house, up to 3): the minimap draws the same plan.</summary>
    public IReadOnlyList<PickupStop> Chain => chain;

    public static bool HideGuidanceIndoors(bool depotRowIndoors, bool legacyShopScene) => depotRowIndoors || legacyShopScene;

    // ------------------------------------------------------------------ build

    public void Build(RectTransform parent)
    {
        panel = FeelUi.Rect("Route", parent, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-28f, -176f), new Vector2(Width, HeaderHeight));
        panel.localRotation = Quaternion.Euler(0f, 0f, 0.8f);
        group = panel.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        FeelUi.Panel(panel, FeelUi.PanelColor);

        arrowBgRt = FeelUi.Rect("ArrowBg", panel, new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(16f, -HeaderHeight * 0.5f), new Vector2(92f, 92f));
        arrowBg = FeelUi.Img(arrowBgRt, "Circle", FeelUi.Circle, FeelUi.Gold);
        arrowRt = FeelUi.Rect("Arrow", arrowBgRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(66f, 66f));
        var arrowImg = arrowRt.gameObject.AddComponent<Image>();
        arrowImg.sprite = FeelUi.Arrow;
        arrowImg.color = new Color(0.12f, 0.09f, 0.18f);
        arrowImg.raycastTarget = false;

        routeNameText = FeelUi.Text(panel, "RouteName", 21f, new Color(0.75f, 0.75f, 0.92f), TextAlignmentOptions.Left, FontStyles.Normal);
        Place(routeNameText.rectTransform, new Vector2(122f, -10f), new Vector2(284f, 28f));
        // (no TextOverflowModes.Ellipsis anywhere here: see FeelUi.Fit)
        routeNameText.overflowMode = TextOverflowModes.Truncate;

        stopsText = FeelUi.Text(panel, "Stops", 36f, Color.white, TextAlignmentOptions.Left);
        stopsText.overflowMode = TextOverflowModes.Truncate;
        Place(stopsText.rectTransform, new Vector2(122f, -36f), new Vector2(284f, 44f));

        nextText = FeelUi.Text(panel, "Next", 22f, new Color(0.95f, 0.95f, 1f), TextAlignmentOptions.TopLeft, FontStyles.Normal);
        Place(nextText.rectTransform, new Vector2(122f, -80f), new Vector2(284f, 48f));
        nextText.textWrappingMode = TextWrappingModes.Normal;
        nextText.overflowMode = TextOverflowModes.Truncate;

        pillRt = FeelUi.Rect("LatePill", panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -9f), new Vector2(112f, 28f));
        pillGroup = pillRt.gameObject.AddComponent<CanvasGroup>();
        pillGroup.alpha = 0f;
        pill = FeelUi.Img(pillRt, "Body", FeelUi.Rounded, new Color(1f, 0.55f, 0.18f), 0.5f);
        pillText = FeelUi.Text(pillRt, "Text", 18f, Color.white, TextAlignmentOptions.Center);

        listRt = FeelUi.Rect("Plan", panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -HeaderHeight + 2f), new Vector2(Width, MaxRows * RowHeight + 8f));
        listGroup = listRt.gameObject.AddComponent<CanvasGroup>();
        listGroup.alpha = 0f;
        for (int i = 0; i < MaxRows; i++)
        {
            var r = new Row();
            r.rt = FeelUi.Rect("Row" + i, listRt, new Vector2(0f, 1f), new Vector2(0.5f, 1f), new Vector2(Width * 0.5f, -i * RowHeight), new Vector2(Width - 24f, RowHeight - 3f));
            r.hl = FeelUi.Img(r.rt, "Highlight", FeelUi.Rounded, new Color(1f, 1f, 1f, i == 0 ? 0.09f : 0f), 0.35f);
            var dotRt = FeelUi.Rect("Dot", r.rt, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(16f, 0f), new Vector2(15f, 15f));
            r.dot = dotRt.gameObject.AddComponent<Image>();
            r.dot.sprite = FeelUi.Circle;
            r.dot.raycastTarget = false;
            r.addr = FeelUi.Text(r.rt, "Addr", i == 0 ? 22f : 20f, i == 0 ? Color.white : new Color(0.85f, 0.85f, 0.95f), TextAlignmentOptions.Left, i == 0 ? FontStyles.Bold : FontStyles.Normal);
            RowPlace(r.addr.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(32f, 0f), new Vector2(200f, RowHeight));
            r.addr.overflowMode = TextOverflowModes.Truncate; // fitted with "…" by FeelUi.Fit
            r.info = FeelUi.Text(r.rt, "Info", 19f, new Color(0.9f, 0.9f, 1f), TextAlignmentOptions.Right, FontStyles.Normal);
            r.info.overflowMode = TextOverflowModes.Truncate;
            RowPlace(r.info.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), new Vector2(170f, RowHeight));
            rows[i] = r;
        }
    }

    static void Place(RectTransform rt, Vector2 pos, Vector2 size) =>
        RowPlace(rt, new Vector2(0f, 1f), new Vector2(0f, 1f), pos, size);

    static void RowPlace(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    /// <summary>Top-left corner (top-down reference units, HudLayout's right column).</summary>
    public void SetTopLeft(Vector2 p) => HudLayout.PlaceAt(panel, p);

    /// <summary>Resize for the right column (420 Full, 380 Compact, 340 Tight).</summary>
    public void SetWidth(float w)
    {
        if (Mathf.Approximately(w, width) || panel == null) return;
        width = w;
        float tw = width - 136f;
        routeNameText.rectTransform.sizeDelta = new Vector2(tw, 28f);
        stopsText.rectTransform.sizeDelta = new Vector2(tw, 44f);
        nextText.rectTransform.sizeDelta = new Vector2(tw, 48f);
        listRt.sizeDelta = new Vector2(width, MaxRows * RowHeight + 8f);
        for (int i = 0; i < MaxRows; i++)
        {
            rows[i].rt.anchoredPosition = new Vector2(width * 0.5f, -i * RowHeight);
            rows[i].rt.sizeDelta = new Vector2(width - 24f, RowHeight - 3f);
            rows[i].infoShown = null; // re-split the address / info columns
        }
        lastNameW = -1f;
        lastNextStr = null;
    }

    /// <summary>How many planned rows fit in the right column this frame (0..3).</summary>
    public void SetRowLimit(int n) => rowLimit = Mathf.Clamp(n, 0, MaxRows);

    public void OnSceneChanged()
    {
        target = null;
        planFor = null;
        planned.Clear();
        doors.Clear();
        nextDoorScan = 0f;
        lastBags = lastLate = -1;
    }

    // ------------------------------------------------------------------ live data

    /// <summary>Fill the route part of the HUD data from the live game (player or truck position).</summary>
    public void Gather(ref FeelHud.HudData d, PlayerController pc, float dt)
    {
        var route = TrashRoute.Current;
        // Keep the city meter and its "X bags out" subline in a shop, but not an arrow to a curb through its walls.
        bool indoors = HideGuidanceIndoors(DepotRowWorld.LocalIndoors,
                                          SceneManager.GetActiveScene().name == SceneController.GarageSceneName); // coop:allow(scene) legacy solo shop; Depot Row uses LocalIndoors
        if (route == null || pc == null || indoors)
        {
            d.hasRoute = false;
            d.hasTarget = false;
            target = null;
            chain.Clear();
            return;
        }
        Vector3 from = pc.transform.position;
        if (pc.currentTruck != null && pc.IsDriving) from = pc.currentTruck.transform.position;
        GatherFor(route, from, dt, ref d);
    }

    /// <summary>Route data for a given route + position (no player needed: used by the verification eval too).</summary>
    public void GatherFor(TrashRoute route, Vector3 from, float dt, ref FeelHud.HudData d)
    {
        d.hasRoute = route != null;
        if (!d.hasRoute) return;
        d.routeName = route.routeName;
        d.live = FeelWorld.IsLive;
        Transform aim = null;

        if (!d.live)
        {
            // legacy fixed daily route
            d.cleared = route.StopsCleared;
            d.total = route.StopsTotal;
            d.complete = route.IsComplete;
            if (!d.complete)
            {
                var next = route.NextStop(from);
                if (next != null) { aim = next.transform; d.nextLabel = next.address; }
            }
            else aim = FindDepotDoor(from, d);
            d.rows = rowArrays[0];
        }
        else
        {
            d.curbBags = FeelWorld.TotalCurbBags(route);
            d.lateBags = FeelWorld.LateBags(route);
            d.houses = FeelWorld.HousesWithTrash;
            d.truckFull = TruckHudRules.ShouldReturnToDepot(!d.truckSyncing, d.load, d.cap, false);

            PickupStop stop = null;
            try { stop = route.NextStop(from); }
            catch (System.Exception e) { FeelRunner.Report(e); }
            target = stop;
            d.allClean = stop == null;

            if (d.truckFull || stop == null)
            {
                if (TruckHudRules.ShouldReturnToDepot(!d.truckSyncing, d.load, d.cap, stop == null))
                {
                    aim = FindDepotDoor(from, d);
                    d.targetIsDepot = aim != null;
                }
            }
            else
            {
                aim = stop.transform;
                d.nextLabel = stop.address;
                var st = FeelWorld.At(stop.stopId);
                d.targetBags = st.bags;
                d.targetAge = (float)st.oldestAge;
            }
            d.rows = d.truckFull ? rowArrays[0] : BuildRows(route, stop, from);
        }

        d.hasTarget = aim != null;
        if (d.hasTarget)
        {
            var delta = aim.position - from;
            delta.y = 0f;
            d.targetDist = delta.magnitude;
            d.arrowAngle = CameraRelativeAngle(delta);
        }
    }

    RouteRow[] BuildRows(TrashRoute route, PickupStop first, Vector3 from)
    {
        float now = Time.unscaledTime;
        if (!Application.isPlaying || now >= nextPlan || planFor != first)
        {
            FeelWorld.PlannedRoute(route, from, MaxRows + 1, planned);
            nextPlan = now + PlanInterval;
            planFor = first;
        }
        chain.Clear();
        if (first != null) chain.Add(first);
        for (int i = 0; i < planned.Count && chain.Count < MaxRows; i++)
        {
            var s = planned[i];
            if (s == null || chain.Contains(s) || FeelWorld.At(s.stopId).bags <= 0) continue;
            chain.Add(s);
        }
        var arr = rowArrays[chain.Count];
        for (int i = 0; i < chain.Count; i++)
        {
            var s = chain[i];
            var st = FeelWorld.At(s.stopId);
            var off = s.transform.position - from;
            off.y = 0f;
            arr[i] = new RouteRow { address = s.address, bags = st.bags, ageHours = (float)st.oldestAge, dist = off.magnitude,
                                    district = Districts.ForStopId(s.stopId) };
        }
        return arr;
    }

    Transform FindDepotDoor(Vector3 from, FeelHud.HudData data)
    {
        // Return this truck to its business, not the driver's personal depot.
        if (DepotRowWorld.Active)
        {
            int targetLot = data.hasTruckContext ? data.truckDepotLot : DepotRowWorld.LocalLot;
            var lot = DepotRowScene.Lot(targetLot);
            if (lot != null && lot.streetDoor != null) return lot.streetDoor.transform;
            return null; // Still spawning: don't point at another business or the closed Town Yard.
        }
        doors.RemoveAll(x => x == null);
        if (doors.Count == 0 && (!Application.isPlaying || Time.unscaledTime >= nextDoorScan))
        {
            nextDoorScan = Time.unscaledTime + 2f;
            doors.AddRange(Object.FindObjectsByType<GarageDoorController>(FindObjectsSortMode.None));
        }
        Transform best = null;
        float bestScore = float.MaxValue;
        foreach (var door in doors)
        {
            float score = (door.transform.position - from).sqrMagnitude;
            bool toShop = !string.IsNullOrEmpty(door.targetSceneName) && door.targetSceneName.Contains("Shop");
            if (!toShop) score += 1e8f; // prefer the door that leads back to the garage
            if (score < bestScore) { bestScore = score; best = door.transform; }
        }
        return best;
    }

    public static float CameraRelativeAngle(Vector3 flatDelta)
    {
        var cam = Camera.main;
        Vector3 fwd = Vector3.forward;
        if (cam != null)
        {
            fwd = cam.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) { fwd = cam.transform.up; fwd.y = 0f; }
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
        }
        var right = Vector3.Cross(Vector3.up, fwd);
        return Mathf.Atan2(Vector3.Dot(flatDelta, right), Vector3.Dot(flatDelta, fwd)) * Mathf.Rad2Deg;
    }

    // ------------------------------------------------------------------ view

    public void Tick(FeelHud.HudData d, float dt, float time)
    {
        if (panel == null) return;
        routeAlpha = Mathf.MoveTowards(routeAlpha, d.hasRoute ? 1f : 0f, dt * 4f);
        group.alpha = routeAlpha;
        if (!d.hasRoute)
        {
            lastCleared = -1; lastComplete = false; lastBags = lastLate = -1; lastTargetLabel = null;
            return;
        }

        string dist = !d.hasTarget ? "" : d.targetDist < 8f ? "  <color=#7CFF8A>here!</color>" : $"  <color=#FFD84A>{Mathf.RoundToInt(d.targetDist)} m</color>";
        string stopsStr, nextStr;
        Color bigColor = Color.white, circle = FeelUi.Gold;
        bool showPill = false, showList = false;
        int rowN = d.rows == null ? 0 : Mathf.Min(d.rows.Length, Mathf.Min(MaxRows, rowLimit));

        if (!d.live)
        {
            if (lastCleared >= 0 && d.cleared > lastCleared) { stopsScaleVel += 10f; stopsFlash = 1f; flashColor = FeelUi.GoodGreen; }
            if (d.complete && !lastComplete && lastCleared >= 0) { stopsScaleVel += 14f; arrowVel += 900f; }
            lastCleared = d.cleared;
            lastComplete = d.complete;
            stopsStr = d.complete ? "Route done!" : $"Stops {d.cleared}/{d.total}";
            nextStr = d.complete ? "Back to the depot" + dist : d.hasTarget ? $"Next: {d.nextLabel}{dist}" : "Next: ???";
            if (d.complete) { bigColor = FeelUi.GoodGreen; circle = new Color(0.35f, 0.95f, 0.45f); }
        }
        else
        {
            // bag count juice: collected (down) = green flash + punch, new trash out (up) = small nudge
            if (lastBags >= 0 && d.curbBags < lastBags) { stopsScaleVel += 9f; stopsFlash = 1f; flashColor = FeelUi.GoodGreen; }
            else if (lastBags >= 0 && d.curbBags > lastBags) { stopsScaleVel += 4f; stopsFlash = 0.6f; flashColor = new Color(1f, 0.85f, 0.3f); }
            if (lastLate >= 0 && d.lateBags > lastLate) { pillVel += 10f; }
            if (d.truckFull && !lastFull) { stopsScaleVel += 12f; arrowVel += 700f; }
            if (d.allClean && !lastClean && lastBags > 0) { stopsScaleVel += 14f; arrowVel += 900f; }
            lastBags = d.curbBags; lastLate = d.lateBags; lastFull = d.truckFull; lastClean = d.allClean;

            if (d.truckFull)
            {
                stopsStr = "Truck full!";
                nextStr = d.hasTarget ? "Back to the depot" + dist : "Go dump it at the depot";
                bigColor = Color.Lerp(new Color(1f, 0.45f, 0.25f), new Color(1f, 0.85f, 0.3f), 0.5f + 0.5f * Mathf.Sin(time * 8f));
                circle = new Color(0.35f, 0.95f, 0.45f);
            }
            else if (d.allClean)
            {
                stopsStr = "All clean!";
                nextStr = d.targetIsDepot ? "Dump your load at the depot" + dist : "Relax… more trash soon";
                bigColor = FeelUi.GoodGreen;
                circle = new Color(0.35f, 0.95f, 0.45f);
            }
            else
            {
                stopsStr = $"{d.curbBags} bag{(d.curbBags == 1 ? "" : "s")} out";
                nextStr = d.hasTarget ? $"Next: {d.nextLabel}{dist}" : "Next: ???";
                showPill = d.curbBags > 0;
                showList = rowN > 0;
                var targetDistrict = rowN > 0 ? d.rows[0].district : null;   // rows[0] = the arrow's house
                if (Districts.IsNeglected(targetDistrict, d.targetAge)) circle = Color.Lerp(new Color(1f, 0.25f, 0.22f), new Color(1f, 0.55f, 0.3f), 0.5f + 0.5f * Mathf.Sin(time * 7f));
                else if (Districts.IsLate(targetDistrict, d.targetAge)) circle = new Color(1f, 0.6f, 0.2f);
            }
        }

        if (stopsStr != lastStopsStr) { lastStopsStr = stopsStr; stopsText.text = stopsStr; }
        stopsFlash = Mathf.MoveTowards(stopsFlash, 0f, dt * 1.5f);
        stopsText.color = Color.Lerp(bigColor, flashColor, stopsFlash);
        stopsScale = FeelUi.Spring(stopsScale, ref stopsScaleVel, 1f, dt);
        stopsText.rectTransform.localScale = Vector3.one * Mathf.Clamp(stopsScale, 0.6f, 1.8f);
        if (nextStr != lastNextStr) { lastNextStr = nextStr; nextText.text = FeelUi.FitLines(nextText, nextStr, width - 136f, 2); }

        // late pill
        pillAlpha = Mathf.MoveTowards(pillAlpha, showPill ? 1f : 0f, dt * 5f);
        pillGroup.alpha = pillAlpha;
        if (showPill)
        {
            string ps = d.lateBags > 0 ? $"{d.lateBags} LATE" : "ALL FRESH";
            if (ps != lastPillStr) { lastPillStr = ps; pillText.text = ps; }
            pill.color = d.lateBags > 0
                ? Color.Lerp(new Color(1f, 0.5f, 0.15f), new Color(1f, 0.32f, 0.2f), 0.5f + 0.5f * Mathf.Sin(time * 4f))
                : new Color(0.25f, 0.75f, 0.35f);
        }
        pillScale = FeelUi.Spring(pillScale, ref pillVel, 1f, dt, 260f, 12f);
        pillRt.localScale = Vector3.one * Mathf.Clamp(pillScale, 0.6f, 1.7f);
        var rn = routeNameText.rectTransform.sizeDelta;
        float nameW = pillAlpha > 0.5f ? width - 254f : width - 136f;
        if (!Mathf.Approximately(rn.x, nameW)) routeNameText.rectTransform.sizeDelta = new Vector2(nameW, rn.y);
        if (d.routeName != lastRouteName || !Mathf.Approximately(nameW, lastNameW))
        {
            lastRouteName = d.routeName;
            lastNameW = nameW;
            routeNameText.text = FitRouteName(d.routeName ?? "", nameW);
        }

        // planned stops list
        listAlpha = Mathf.MoveTowards(listAlpha, showList ? 1f : 0f, dt * 5f);
        listGroup.alpha = listAlpha;
        if (showList)
            for (int i = 0; i < MaxRows; i++) TickRow(rows[i], i < rowN ? d.rows[i] : (RouteRow?)null, i, dt, time);
        else if (listAlpha <= 0f)
            for (int i = 0; i < MaxRows; i++) TickRow(rows[i], null, i, dt, time); // keep the old rows while fading out
        float wantH = HeaderHeight + (showList ? rowN * RowHeight + 8f : 0f);
        height = FeelUi.Spring(height, ref heightVel, wantH, dt, 200f, 20f);
        panel.sizeDelta = new Vector2(width, Mathf.Max(HeaderHeight, height));

        // arrow: retarget pop when it swings to a new house
        string label = d.hasTarget ? (d.targetIsDepot || d.complete || d.truckFull ? "#depot" : d.nextLabel) : null;
        if (label != lastTargetLabel)
        {
            if (lastTargetLabel != null && label != null) { arrowScaleVel += 7f; arrowVel += 400f; }
            lastTargetLabel = label;
        }
        arrowBg.color = circle;
        arrowRt.gameObject.SetActive(d.hasTarget);
        if (d.hasTarget)
        {
            float target = -d.arrowAngle;
            float delta = Mathf.DeltaAngle(arrowAngle, target);
            arrowVel += (delta * 160f - arrowVel * 14f) * Mathf.Min(dt, 0.05f);
            arrowAngle += arrowVel * Mathf.Min(dt, 0.05f);
            arrowRt.localRotation = Quaternion.Euler(0f, 0f, arrowAngle);
        }
        arrowScale = FeelUi.Spring(arrowScale, ref arrowScaleVel, 1f, dt, 240f, 12f);
        float bob = d.hasTarget && d.targetDist < 8f ? 1f + 0.12f * Mathf.Sin(time * 10f) : 1f;
        arrowBgRt.localScale = Vector3.one * (bob * Mathf.Clamp(arrowScale, 0.6f, 1.6f));
    }

    /// <summary>"Route 1 — Whole Neighborhood" if it fits, else "Route 1", else trimmed with "…".</summary>
    string FitRouteName(string full, float width)
    {
        if (routeNameText.GetPreferredValues(full, 100000f, 0f).x <= width) return full;
        string shortName = full;
        int i = full.IndexOf(" — ", System.StringComparison.Ordinal);
        if (i <= 0) i = full.IndexOf(" - ", System.StringComparison.Ordinal);
        if (i > 0) shortName = full.Substring(0, i);
        return FeelUi.Fit(routeNameText, shortName, width);
    }

    void TickRow(Row r, RouteRow? data, int index, float dt, float time)
    {
        bool on = data.HasValue;
        if (r.rt.gameObject.activeSelf != on) r.rt.gameObject.SetActive(on);
        if (!on) { r.addrShown = null; return; }
        var d = data.Value;
        bool refit = false;
        string info = FeelWorld.BagsAndFreshness(d.bags, d.ageHours, d.district);
        if (info != r.infoShown)
        {
            r.infoShown = info;
            r.info.text = info;
            // size the two columns to the label so a long freshness text never runs over the address
            float inner = width - 24f - 32f - 8f;
            float iw = Mathf.Min(r.info.GetPreferredValues(info).x + 4f, inner - 96f);
            r.info.rectTransform.sizeDelta = new Vector2(iw, RowHeight);
            float aw = Mathf.Max(90f, inner - iw - 10f);
            if (!Mathf.Approximately(aw, r.addr.rectTransform.sizeDelta.x)) refit = true;
            r.addr.rectTransform.sizeDelta = new Vector2(aw, RowHeight);
        }
        string a = d.address ?? "?";
        if (a != r.addrShown || refit)
        {
            if (r.addrShown != null && a != r.addrShown) r.vel += 6f; // a different house slid into this slot
            r.addrShown = a;
            r.addr.text = FeelUi.Fit(r.addr, a, r.addr.rectTransform.sizeDelta.x);
        }
        var c = FeelWorld.FreshnessColor(d.district, d.ageHours);
        bool neglected = Districts.IsNeglected(d.district, d.ageHours) && d.bags > 0;
        r.dot.color = c;
        r.dot.rectTransform.localScale = Vector3.one * (neglected ? 1f + 0.25f * Mathf.Sin(time * 9f) : 1f);
        if (index == 0) r.hl.color = new Color(1f, 1f, 1f, 0.07f + 0.04f * Mathf.Sin(time * 3f));
        r.scale = FeelUi.Spring(r.scale, ref r.vel, 1f, dt, 260f, 14f);
        r.rt.localScale = Vector3.one * Mathf.Clamp(r.scale, 0.7f, 1.4f);
    }
}
