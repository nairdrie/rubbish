using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The local player's pocket / gear bar: a small stack of quick-use slots in the bottom-right corner (<see cref="HudZone.Pocket"/>,
/// the minimap's mirror image), so it never sits in the middle of the view. Only things that can be carried or activated appear
/// here (the grabber pole, Rocket Blades, the Compactor Pack, raccoon traps, Garbage Juice, the bag cannon, a stored rabies shot,
/// repellent); permanent passive upgrades and the shop's treasure collection are not quick-use items.
/// The bar is informational (no raycasts), so it cannot steal input from gameplay. <see cref="FeelHud"/> decides when it may show
/// (not while seated at chess or at a station, where none of its keys work) and <see cref="HudLayout"/> reserves its corner.
/// </summary>
public sealed class FeelHudHotbar
{
    public const float SlotW = 138f, SlotH = 55f, Gap = 7f, HeaderH = 21f;
    /// <summary>Two columns at most: the stack stays narrower than the right lane (340 reference px at the tightest).</summary>
    public const int MaxColumns = 2;
    /// <summary>Every kind of gear there is: the bar can show all of it at once.</summary>
    const int MaxSlots = 8;

    struct Item
    {
        public string name, key, detail;
        public bool ready;
        public Color accent;
    }

    sealed class Slot
    {
        public RectTransform root;
        public Image body, keyBody;
        public TextMeshProUGUI key, name, detail;
    }

    readonly List<Item> items = new List<Item>(MaxSlots);
    readonly List<Slot> slots = new List<Slot>(MaxSlots);
    RectTransform root;
    CanvasGroup group;
    TextMeshProUGUI heading;
    int columns, shown;
    float alpha;
    bool hidden = true;
    Vector2 size, lastTopLeft;

    public Vector2 Size => size;
    public float Alpha => alpha;
    public RectTransform Root => root;

    public void Build(RectTransform parent)
    {
        root = FeelUi.Rect("PocketHotbar", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, Vector2.zero);
        group = root.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
        heading = FeelUi.Text(root, "Heading", 16f, new Color(0.9f, 0.9f, 0.96f), TextAlignmentOptions.Left);
        Place(heading.rectTransform, 0f, 0f, SlotW, HeaderH);
        heading.text = "POCKET / GEAR";
    }

    static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    Slot MakeSlot()
    {
        var s = new Slot();
        s.root = FeelUi.Rect("PocketSlot", root, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(SlotW, SlotH));
        s.body = FeelUi.Img(s.root, "Body", FeelUi.Rounded, FeelUi.PanelColor, 0.45f);
        var keyRt = FeelUi.Rect("KeyCap", s.root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(6f, -8f), new Vector2(48f, 38f));
        s.keyBody = FeelUi.Img(keyRt, "Body", FeelUi.Rounded, FeelUi.Gold, 0.28f);
        s.key = FeelUi.Text(keyRt, "Key", 17f, new Color(0.09f, 0.08f, 0.13f), TextAlignmentOptions.Center);
        s.name = FeelUi.Text(s.root, "Item", 19f, Color.white, TextAlignmentOptions.Left);
        Place(s.name.rectTransform, 61f, 6f, SlotW - 65f, 24f);
        s.detail = FeelUi.Text(s.root, "Detail", 14f, new Color(0.88f, 0.87f, 0.91f), TextAlignmentOptions.Left);
        Place(s.detail.rectTransform, 61f, 29f, SlotW - 65f, 19f);
        slots.Add(s);
        return s;
    }

    /// <summary>
    /// Refresh local ownership and counts and return the corner block to reserve in <see cref="HudLayout"/> (zero = nothing to
    /// show). <paramref name="allowed"/> false (chess, a station, a laptop...) reserves nothing and fades the bar out in place.
    /// </summary>
    public Vector2 Sync(HudLayout.PocketLimits limits, HudMode mode, bool preview, bool allowed)
    {
        hidden = !allowed;
        if (hidden) return Vector2.zero;
        items.Clear();
        bool pad = !preview && GameControls.UsingGamepad;
        bool onFoot = mode == HudMode.Street || mode == HudMode.Depot;
        if (preview)
        {
            Add("GRABBY", "V", "READY", true, FeelUi.Gold);
            Add("TRAP", "T", "x2", true, FeelUi.GoodGreen);
            Add("JUICE", "J", "x3", true, FeelUi.GoodGreen);
            Add("SHOT", "AUTO", "x1", true, FeelUi.Gold);
        }
        else
        {
            if (GrabberPole.Owned)
                Add("GRABBY", pad ? "D-L" : "V", onFoot ? "READY" : "ON FOOT", onFoot, FeelUi.Gold);
            if (RocketBlades.Hotbar(out string boostDetail, out bool boostReady))
                Add("BOOST", pad ? "LB" : "Q", onFoot ? boostDetail : "ON FOOT", onFoot && boostReady, FeelUi.Gold);
            if (CompactorPack.Hotbar(out string packDetail, out bool packReady))
                Add("PACK", pad ? "Y" : "C", packDetail, onFoot && packReady, FeelUi.GoodGreen);
            int traps = GameSession.GetConsumable(RaccoonTrap.ConsumableId);
            if (traps > 0)
                Add("TRAP", pad ? "D-R" : "T", "x" + traps, onFoot && !NightPlayers.IsIndoors(PlayerController.Local), FeelUi.GoodGreen); // coop:allow(local) local-only HUD checks the local player's indoor state
            int juice = ShopEffects.GarbageJuiceCans;
            if (juice > 0)
                Add("JUICE", pad ? "D-D" : "J", "x" + juice, true, FeelUi.GoodGreen);
            if (BagCannon.Owned)
            {
                bool ready = onFoot && BagCannon.Local != null && BagCannon.Local.Ready;
                string detail = ready ? "READY" : !onFoot ? "ON FOOT" : TrashCarrier.Local != null && TrashCarrier.Local.Held is TrashBag ? "NEAR TRUCK" : "HOLD BAG";
                Add("CANNON", pad ? "D-U" : "R", detail, ready, FeelUi.Gold);
            }
            int shots = ShopEffects.RabiesShots;
            if (shots > 0) Add("SHOT", "AUTO", "x" + shots, true, FeelUi.Gold);
            int repellent = ShopEffects.RepellentBottles;
            if (repellent > 0) Add("REPEL", "AUTO", "x" + repellent, true, FeelUi.Gold);
        }

        var block = Measure(items.Count, limits, out int cols, out int count);
        if (count == 0)
        {
            hidden = true; // nothing owned (or no room at all): the old slots just fade out where they were
            return Vector2.zero;
        }
        size = block;
        columns = cols;
        shown = count;
        return size;
    }

    void Add(string name, string key, string detail, bool ready, Color accent) =>
        items.Add(new Item { name = name, key = key, detail = detail, ready = ready, accent = accent });

    static int RowsFor(int count, int columns) => (count + columns - 1) / columns;

    static float HeightFor(int rows) => HeaderH + rows * SlotH + (rows - 1) * Gap;

    /// <summary>
    /// The grid for <paramref name="count"/> slots in the corner: one column while the stack stays under
    /// <see cref="HudLayout.PocketLimits.preferredHeight"/>, a second one past that (and never wider than
    /// <see cref="HudLayout.PocketLimits.maxWidth"/>). Rows still taller than <see cref="HudLayout.PocketLimits.maxHeight"/>
    /// are dropped, last slots first (the AUTO counters sit last), so the route panel above never loses its header. Returns the
    /// block's size (zero = nothing fits) and how many columns and slots it takes.
    /// </summary>
    public static Vector2 Measure(int count, HudLayout.PocketLimits limits, out int columns, out int shown)
    {
        columns = shown = 0;
        if (count <= 0) return Vector2.zero;
        int fit = Mathf.FloorToInt((limits.maxHeight - HeaderH + Gap) / (SlotH + Gap)); // whole rows under the hard cap
        if (fit < 1) return Vector2.zero;
        int most = Mathf.Clamp(Mathf.FloorToInt((limits.maxWidth + Gap) / (SlotW + Gap)), 1, MaxColumns);
        columns = 1;
        while (columns < most && columns < count && HeightFor(RowsFor(count, columns)) > limits.preferredHeight) columns++;
        int rows = Mathf.Min(RowsFor(count, columns), fit);
        shown = Mathf.Min(count, rows * columns);
        return new Vector2(columns * SlotW + (columns - 1) * Gap, HeightFor(rows));
    }

    /// <summary>Place the bar in <paramref name="zone"/>, the corner <see cref="HudLayout"/> reserved for it (top-down reference
    /// units; an empty rect = none this frame), and animate it. While it fades out it stays where it last was.</summary>
    public void Tick(Rect zone, float dt)
    {
        if (root == null) return;
        bool show = !hidden && zone.width > 0.5f;
        alpha = Mathf.MoveTowards(alpha, show ? 1f : 0f, dt * 8f);
        group.alpha = alpha;
        if (show) lastTopLeft = zone.position;
        if (alpha <= 0f || items.Count == 0) return;
        Place(root, lastTopLeft.x, lastTopLeft.y, size.x, size.y);
        heading.rectTransform.sizeDelta = new Vector2(size.x, HeaderH);
        int n = Mathf.Min(shown, items.Count); // a fade-out can outlive the list that drew it
        for (int i = 0; i < n; i++)
        {
            var s = i < slots.Count ? slots[i] : MakeSlot();
            var item = items[i];
            s.root.gameObject.SetActive(true);
            Place(s.root, (i % columns) * (SlotW + Gap), HeaderH + (i / columns) * (SlotH + Gap), SlotW, SlotH);
            s.body.color = item.ready ? new Color(0.13f, 0.12f, 0.20f, 0.93f) : new Color(0.12f, 0.12f, 0.15f, 0.80f);
            s.keyBody.color = item.ready ? item.accent : new Color(0.45f, 0.45f, 0.48f, 0.85f);
            s.key.text = item.key;
            s.name.text = item.name;
            s.name.color = item.ready ? Color.white : new Color(0.69f, 0.69f, 0.72f);
            s.detail.text = item.detail;
        }
        for (int i = n; i < slots.Count; i++) slots[i].root.gameObject.SetActive(false);
    }
}
