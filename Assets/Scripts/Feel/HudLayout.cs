using UnityEngine;

/// <summary>The HUD's screen zones (Docs/UIPass.md §8.2).</summary>
public enum HudZone { TopLeft, TopRight, LeftColumn, RightColumn, ToastLane, CenterStage, BottomDock, Minimap, BottomLane }

/// <summary>How much fits in the top bar (§8.2): Full ≥ 1608 reference px wide, Compact ≥ 1258, else Tight.</summary>
public enum HudDensity { Full, Compact, Tight }

/// <summary>What the HUD is showing (§8.3), derived every frame (no new public state anywhere else).</summary>
public enum HudMode { Hidden, Map, Paused, Station, Laptop, Driving, Street, Depot }

/// <summary>
/// One layout for the whole HUD (Docs/UIPass.md D3, §8.2). Every frame <see cref="FeelHud"/> computes a <see cref="Frame"/> from
/// the HUD root's size in <b>reference units</b> (which already include the UI-scale setting and the aspect ratio) plus what the
/// widgets want this frame, and every widget asks for its zone instead of hard-coding a position.
/// <para><b>Coordinates.</b> Zone rects are reference units with the origin at the HUD's <b>top-left</b> corner and y growing
/// <b>downward</b> (<c>Rect.y</c> = distance from the top edge). <see cref="Place"/> turns one into a RectTransform position.</para>
/// <para><b>Lanes.</b> Under the top bar the screen is three lanes: the left lane (left column over the minimap), the centre
/// lane (toasts at the top, centre cards, the bottom dock) and the right lane (route panel + Head Office memo). The dock may
/// reach under a side lane only where that lane's content stops above it. Zones never overlap by construction; content that
/// doesn't fit its zone waits or hides by priority (§8.2 collision rules):</para>
/// <list type="number">
/// <item>The toast lane never overlaps the side columns (it's the centre lane; toasts that don't fit its height wait).</item>
/// <item>The minimap steps L → M → S if the left column would reach it; if it still would, it hides while rabies is up.</item>
/// <item>The bottom dock wraps to 2 rows before it shrinks (<see cref="FeelHudHints"/>).</item>
/// <item>A centre card clamps the toast lane's bottom above it (extra toasts queue).</item>
/// </list>
/// </summary>
public static class HudLayout
{
    // ------------------------------------------------------------------ geometry (reference px at 1080)

    /// <summary>M: the screen margin.</summary>
    public const float Margin = 24f;
    /// <summary>The gap between zones.</summary>
    public const float Gap = 16f;
    /// <summary>The top bar spans y 20..128.</summary>
    public const float TopBarTop = 20f, TopBarBottom = 128f;
    /// <summary>Side columns and the toast lane start here (TopBar.bottom + Gap).</summary>
    public const float ColumnTop = TopBarBottom + Gap;
    /// <summary>Gap between the top-bar items.</summary>
    public const float TopGap = 12f;

    public const float CashW = 360f, CashH = 104f;
    public const float ClockW = 300f, ClockTightW = 200f, ClockH = 104f;
    public const float RankW = 432f, RankBadgeW = 82f, RankH = 104f;
    public const float TruckW = 420f, TruckTightW = 300f, TruckH = 96f;

    /// <summary>Station mode: the slim pill (cash + time) at y 16..72.</summary>
    public const float PillTop = 16f, PillH = 56f, PillW = 400f;
    /// <summary>Station mode: the first-person bin signs live in this band (y 90..200); nothing may cover it.</summary>
    public const float BinBandTop = 90f, BinBandBottom = 200f;

    /// <summary>Density cut-offs (top bar = cash + clock + rank + gap + truck + margins).</summary>
    public const float FullMinWidth = 1608f, CompactMinWidth = 1258f;

    /// <summary>The bottom dock: hint chips from y 12 above the bottom edge, the prompt above them; never wider than this.</summary>
    public const float DockBottom = 12f, DockMaxWidth = 1100f, DockMinWidth = 360f;

    /// <summary>The minimap block: the lid, then the street-label pill under it.</summary>
    public const float MinimapPillH = 34f, MinimapPillGap = 6f, MinimapSmallest = 200f, MinimapStep = 50f;

    /// <summary>Side-lane widths by density (Full, Compact, Tight).</summary>
    public static float LeftWidth(HudDensity d) => d == HudDensity.Full ? 390f : d == HudDensity.Compact ? 360f : 330f;
    public static float RightWidth(HudDensity d) => d == HudDensity.Full ? 420f : d == HudDensity.Compact ? 380f : 340f;

    // ------------------------------------------------------------------ inputs and result

    /// <summary>What the widgets want this frame (FeelHud fills it from last frame's content).</summary>
    public struct Inputs
    {
        /// <summary>The HUD root's size in reference units.</summary>
        public Vector2 size;
        public HudMode mode;
        /// <summary>Reserve a thin co-op row for YOUR BUSINESS / WORKING FOR badges below the top bar.</summary>
        public bool businessContext;
        /// <summary>The minimap could show (a town map anchor is active and the mode allows it).</summary>
        public bool minimapWanted;
        /// <summary>Settings → Interface → Minimap size (200 / 250 / 300).</summary>
        public float minimapDiameter;
        /// <summary>Show the street-label pill under the lid.</summary>
        public bool minimapLabel;
        public bool rabiesUp;
        /// <summary>Height the left column's live widgets want (stacked, with gaps).</summary>
        public float leftWanted;
        /// <summary>Height the bottom dock wants (prompt + hint chips).</summary>
        public float dockWanted;
        /// <summary>A centre card is up (reveal, PROMOTED, Work Order) at <see cref="centerCard"/> (top-down rect).</summary>
        public bool hasCenterCard;
        public Rect centerCard;
    }

    /// <summary>One frame's layout. Rects are top-down reference units (see the class summary).</summary>
    public struct Frame
    {
        public Vector2 size;
        public HudDensity density;
        public HudMode mode;
        public bool valid;

        // top bar (TopLeft = cash + clock + rank; TopRight = truck)
        public Rect cash, clock, rank, truck, topLeft, topRight;
        public bool rankBadge, clockCompact, truckCompact;
        /// <summary>Station mode's slim pill (cash + time).</summary>
        public Rect pill;

        public Rect leftColumn, rightColumn, toastLane, centerStage, bottomDock, bottomLane;
        /// <summary>The whole minimap block (lid + label pill); <see cref="minimapLid"/> is the lid itself.</summary>
        public Rect minimap, minimapLid;
        public float minimapDiameter;
        public bool minimapVisible;
        /// <summary>The minimap stepped down from the setting (L → M → S) to clear the left column.</summary>
        public bool minimapStepped;
        public float leftWidth, rightWidth;
    }

    static Frame frame;

    /// <summary>The layout computed this frame (default/invalid until FeelHud has ticked once).</summary>
    public static Frame Layout => frame;

    /// <summary>The rect of a zone this frame (top-down reference units).</summary>
    public static Rect Zone(HudZone z)
    {
        switch (z)
        {
            case HudZone.TopLeft: return frame.topLeft;
            case HudZone.TopRight: return frame.topRight;
            case HudZone.LeftColumn: return frame.leftColumn;
            case HudZone.RightColumn: return frame.rightColumn;
            case HudZone.ToastLane: return frame.toastLane;
            case HudZone.CenterStage: return frame.centerStage;
            case HudZone.BottomDock: return frame.bottomDock;
            case HudZone.Minimap: return frame.minimap;
            case HudZone.BottomLane: return frame.bottomLane;
        }
        return default;
    }

    public static HudDensity Density => frame.density;
    public static HudMode Mode => frame.mode;

    /// <summary>FeelHud publishes the frame it laid out.</summary>
    public static void Publish(Frame f) => frame = f;

    // ------------------------------------------------------------------ right column bookkeeping (ProgressionHud's memo)

    static float rightUsedBottom = ColumnTop;
    static int rightUsedFrame = -1;

    /// <summary>FeelHud reports where the route panel ends (top-down y) after laying out the right column.</summary>
    public static void ReportRightUsed(float bottomY)
    {
        rightUsedBottom = bottomY;
        rightUsedFrame = Time.frameCount;
    }

    /// <summary>
    /// Room for a Head Office memo of <paramref name="height"/> under the route panel (H9)? Gives its top-left corner
    /// (top-down) when it fits inside the right column; else false (the memo waits).
    /// </summary>
    public static bool MemoSlot(float width, float height, out Vector2 topLeft)
    {
        topLeft = default;
        if (!frame.valid || frame.mode == HudMode.Station || frame.rightColumn.width <= 0f) return false;
        float y = Mathf.Max(frame.rightColumn.yMin, rightUsedBottom) + 12f;
        if (y + height > frame.rightColumn.yMax) return false;
        topLeft = new Vector2(frame.rightColumn.xMax - width, y);
        return true;
    }

    // ------------------------------------------------------------------ compute

    public static HudDensity DensityFor(float refWidth) =>
        refWidth >= FullMinWidth ? HudDensity.Full : refWidth >= CompactMinWidth ? HudDensity.Compact : HudDensity.Tight;

    /// <summary>Lay out every zone for this frame (pure: same inputs, same frame).</summary>
    public static Frame Compute(Inputs i)
    {
        var f = new Frame { size = i.size, mode = i.mode, valid = true };
        float W = Mathf.Max(1f, i.size.x), H = Mathf.Max(1f, i.size.y);
        var d = DensityFor(W);
        f.density = d;
        float L = LeftWidth(d), R = RightWidth(d);
        float columnTop = ColumnTop + (i.businessContext ? 30f : 0f);
        f.leftWidth = L;
        f.rightWidth = R;

        // ---- top bar
        f.rankBadge = d != HudDensity.Full;
        f.clockCompact = d == HudDensity.Tight;
        f.truckCompact = d == HudDensity.Tight;
        f.cash = new Rect(Margin, TopBarTop, CashW, CashH);
        float clockW = f.clockCompact ? ClockTightW : ClockW;
        f.clock = new Rect(f.cash.xMax + TopGap, TopBarTop, clockW, ClockH);
        f.rank = new Rect(f.clock.xMax + TopGap, TopBarTop, f.rankBadge ? RankBadgeW : RankW, RankH);
        float truckW = f.truckCompact ? TruckTightW : TruckW;
        f.truck = new Rect(W - Margin - truckW, TopBarTop, truckW, TruckH);
        f.topLeft = Rect.MinMaxRect(f.cash.xMin, TopBarTop, f.rank.xMax, TopBarBottom);
        f.topRight = Rect.MinMaxRect(f.truck.xMin, TopBarTop, f.truck.xMax, TopBarBottom);
        f.pill = new Rect(Margin, PillTop, PillW, PillH);

        // ---- bottom dock (centred; as wide as the minimap block on both sides allows)
        float dCfg = Mathf.Max(MinimapSmallest, i.minimapDiameter);
        float dockW = Mathf.Clamp(Mathf.Min(DockMaxWidth, W - 2f * (Margin + dCfg + Gap)), Mathf.Min(DockMinWidth, W - 2f * Margin), DockMaxWidth);
        float dockH = Mathf.Clamp(i.dockWanted, 0f, H * 0.45f);
        f.bottomDock = new Rect((W - dockW) * 0.5f, H - DockBottom - dockH, dockW, dockH);
        float dockTop = dockH > 0.5f ? f.bottomDock.yMin : H - DockBottom;

        bool station = i.mode == HudMode.Station;

        // ---- minimap (bottom-left: lid, then the label pill)
        float pillTotal = i.minimapLabel ? MinimapPillH + MinimapPillGap : 0f;
        f.minimapVisible = false;
        if (i.minimapWanted && !station)
        {
            float want = Mathf.Max(MinimapSmallest, i.minimapDiameter);
            float chosen = -1f;
            for (float dd = want; dd >= MinimapSmallest - 0.5f; dd -= MinimapStep)
            {
                float blockTop = H - Margin - pillTotal - dd;
                float budget = blockTop - Gap - columnTop;
                if (i.leftWanted <= budget + 0.5f) { chosen = dd; break; }
            }
            if (chosen < 0f) chosen = i.rabiesUp ? -1f : MinimapSmallest;   // rule 2: hide while rabies is up
            if (chosen > 0f)
            {
                f.minimapVisible = true;
                f.minimapStepped = chosen < want - 0.5f;
                f.minimapDiameter = chosen;
                float blockH = chosen + pillTotal;
                f.minimap = new Rect(Margin, H - Margin - blockH, chosen, blockH);
                f.minimapLid = new Rect(Margin, f.minimap.yMin, chosen, chosen);
            }
        }

        // ---- side columns
        if (!station)
        {
            float leftBottom = H - Margin;
            if (f.minimapVisible) leftBottom = Mathf.Min(leftBottom, f.minimap.yMin - Gap);
            if (dockH > 0.5f && f.bottomDock.xMin < Margin + L) leftBottom = Mathf.Min(leftBottom, dockTop - Gap);
            f.leftColumn = new Rect(Margin, columnTop, L, Mathf.Max(0f, leftBottom - columnTop));

            float rightBottom = H - Margin;
            if (dockH > 0.5f && f.bottomDock.xMax > W - Margin - R) rightBottom = Mathf.Min(rightBottom, dockTop - Gap);
            f.rightColumn = new Rect(W - Margin - R, columnTop, R, Mathf.Max(0f, rightBottom - columnTop));
        }
        else
        {
            // Station mode: no columns. Rabies (the only left-column widget left) sits bottom-left, clear of the dock.
            float bottom = H - Margin;
            if (dockH > 0.5f && f.bottomDock.xMin < Margin + L) bottom = Mathf.Min(bottom, dockTop - Gap);
            float top = Mathf.Max(BinBandBottom + Gap, bottom - FeelHudRabies.Height);
            f.leftColumn = new Rect(Margin, top, L, Mathf.Max(0f, bottom - top));
            f.rightColumn = new Rect(W - Margin - R, columnTop, R, 0f);
        }

        // ---- centre lane: toasts at the top, centre cards in the middle, the dock at the bottom
        float laneX0 = Margin + L + Gap, laneX1 = W - Margin - R - Gap;
        float laneBottom = dockTop - Gap;
        if (i.hasCenterCard) laneBottom = Mathf.Min(laneBottom, i.centerCard.yMin - Gap);
        f.toastLane = Rect.MinMaxRect(laneX0, columnTop, Mathf.Max(laneX0, laneX1), Mathf.Max(columnTop, laneBottom));
        f.centerStage = Rect.MinMaxRect(laneX0, columnTop, Mathf.Max(laneX0, laneX1), Mathf.Max(columnTop, dockTop - Gap));

        // ---- station toasts: a bottom lane above the dock, never up into the bin-sign band
        float blBottom = dockTop - Gap;
        float blTop = Mathf.Min(blBottom, BinBandBottom + Gap);
        f.bottomLane = Rect.MinMaxRect(f.bottomDock.xMin, blTop, f.bottomDock.xMax, Mathf.Max(blTop, blBottom));
        return f;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Put <paramref name="rt"/> at a top-down rect (anchor and pivot top-left of the HUD root).</summary>
    public static void Place(RectTransform rt, Rect r)
    {
        if (rt == null) return;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(r.x, -r.y);
        rt.sizeDelta = r.size;
    }

    /// <summary>Top-left anchoring with a given position (top-down) and the rect's current size.</summary>
    public static void PlaceAt(RectTransform rt, Vector2 topLeft)
    {
        if (rt == null) return;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
    }

    /// <summary>A top-left-anchored child rect (pivot top-left) of <paramref name="rt"/> as a top-down rect in its parent.</summary>
    public static Rect TopDownRect(RectTransform rt)
    {
        if (rt == null) return default;
        var s = rt.rect.size;
        var p = rt.anchoredPosition;
        return new Rect(p.x - rt.pivot.x * s.x, -p.y - (1f - rt.pivot.y) * s.y, s.x, s.y);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        frame = default;
        rightUsedBottom = ColumnTop;
        rightUsedFrame = -1;
    }
}
