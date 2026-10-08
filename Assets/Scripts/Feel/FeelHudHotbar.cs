using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The local player's pocket/action bar. Only things that can be carried or activated appear here;
/// permanent passive upgrades and the shop's treasure collection are not quick-use items.
/// The bar is informational (no raycasts), so it cannot steal input from gameplay.
/// </summary>
public sealed class FeelHudHotbar
{
    const float SlotW = 138f, SlotH = 55f, Gap = 7f, HeaderH = 21f;
    const int MaxSlots = 6;

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
    int columns;
    float alpha;
    Vector2 size;

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
        Place(heading.rectTransform, 0f, 0f, 220f, HeaderH);
        heading.text = "POCKET  /  GEAR";
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

    /// <summary>Refresh local ownership and counts, then return the height to reserve in the HUD dock.</summary>
    public float Sync(float maxWidth, HudMode mode, bool preview)
    {
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

        if (items.Count == 0)
        {
            size = Vector2.zero;
            return 0f;
        }
        columns = Mathf.Clamp(Mathf.FloorToInt((maxWidth + Gap) / (SlotW + Gap)), 1, MaxSlots);
        columns = Mathf.Min(columns, items.Count);
        int rows = Mathf.CeilToInt((float)items.Count / columns);
        size = new Vector2(columns * SlotW + (columns - 1) * Gap, HeaderH + rows * SlotH + (rows - 1) * Gap);
        return size.y;
    }

    void Add(string name, string key, string detail, bool ready, Color accent) =>
        items.Add(new Item { name = name, key = key, detail = detail, ready = ready, accent = accent });

    public void Tick(Vector2 topLeft, bool visible, float dt)
    {
        if (root == null) return;
        alpha = Mathf.MoveTowards(alpha, visible && items.Count > 0 ? 1f : 0f, dt * 8f);
        group.alpha = alpha;
        if (items.Count == 0) return;
        Place(root, topLeft.x, topLeft.y, size.x, size.y);
        for (int i = 0; i < items.Count; i++)
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
        for (int i = items.Count; i < slots.Count; i++) slots[i].root.gameObject.SetActive(false);
    }
}
