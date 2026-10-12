using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The tutorial's coach marks on the shop laptop (Docs/Campaign.md §14.8): a pulsing gold ring round the tab (or button) the player
/// is asked to click, the rest of the laptop dimmed (looks only: nothing is blocked, the red close dot and Esc always work), and a
/// speech bubble that says what it is. It lives inside the laptop's own UI Toolkit panel, so it scales and sorts with it, and it
/// never touches the laptop's scripts: the tabs are found by the names in <c>Assets/UI/ebae/ebae.uxml</c>.
/// <para>A click is noticed on pointer-down (trickle phase), so it works whether the laptop ends up switching app or not
/// (clicking the tab that is already showing changes nothing, and still counts).</para>
/// </summary>
public sealed class LaptopCoach
{
    const string LayerName = "tutorialCoach";
    static readonly Color Gold = new Color(1f, 0.84f, 0.25f);
    static readonly Color Plum = new Color(0.13f, 0.1f, 0.2f, 0.97f);
    const float Pad = 7f, BubbleW = 360f;

    UIDocument doc;
    VisualElement root, host, layer, ring, bubble, dimT, dimB, dimL, dimR;
    Label titleLabel, bodyLabel, hintLabel;
    VisualElement target;
    int markTab = -1;
    Action<int> clicked;
    EventCallback<PointerDownEvent> onDown;
    EventCallback<NavigationSubmitEvent> onSubmit;
    bool shown;

    public bool IsShown => shown;

    // ------------------------------------------------------------------ finding the laptop's panel

    /// <summary>The laptop's document root, or null while the laptop is not wired up (no UIDocument in this scene).</summary>
    VisualElement Root()
    {
        if (doc == null || doc.rootVisualElement == null)
        {
            doc = null;
            foreach (var d in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (d != null && d.visualTreeAsset != null && d.visualTreeAsset.name == "ebae") { doc = d; break; }
        }
        return doc != null ? doc.rootVisualElement : null;
    }

    /// <summary>The tab strip's elements by tab id (<see cref="TutorialRules"/>.Tab*); null when that one is not on screen.</summary>
    public VisualElement TabElement(int tab)
    {
        var r = Root();
        if (r == null) return null;
        switch (tab)
        {
            case TutorialRules.TabEbae: return r.Q("appTabEbae");
            case TutorialRules.TabScrapazon: return r.Q("appTabShop");
            case TutorialRules.TabPrinter:
            {
                var t = r.Q("appTabPrint");
                return t != null && t.resolvedStyle.display != DisplayStyle.None ? t : null;
            }
            case TutorialRules.TabCareer:
            {
                // a department button inside Scrapazon (no name): class sz-nav-btn, text "Career"
                VisualElement found = null;
                r.Query<VisualElement>(className: "sz-nav-btn").ForEach(e =>
                {
                    if (found == null && e is TextElement te && te.text != null && te.text.IndexOf("Career", StringComparison.OrdinalIgnoreCase) >= 0) found = e;
                });
                return found != null && found.resolvedStyle.display != DisplayStyle.None ? found : null;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ show / hide

    /// <param name="element">what to ring and wait for a click on.</param>
    /// <param name="mark">the tab id to report when it is clicked (-1 = report nothing, just move on).</param>
    public bool Show(VisualElement element, int mark, string title, string body, string hint, Action<int> onClicked)
    {
        var r = Root();
        if (r == null || element == null) { Hide(); return false; }
        if (!Ensure(r)) return false;
        if (target != element)
        {
            Unhook();
            target = element;
            markTab = mark;
            clicked = onClicked;
            onDown = e => { if (e.button <= 0) Fire(); };     // the primary button (a pad or touch press reports 0 or -1)
            onSubmit = e => Fire();
            target.RegisterCallback(onDown, TrickleDown.TrickleDown);
            target.RegisterCallback(onSubmit, TrickleDown.TrickleDown);
        }
        else { markTab = mark; clicked = onClicked; }
        if (titleLabel.text != title) titleLabel.text = title;
        if (bodyLabel.text != body) bodyLabel.text = body;
        if (hintLabel.text != hint) hintLabel.text = hint;
        shown = true;
        layer.style.display = DisplayStyle.Flex;
        layer.style.visibility = Visibility.Visible;
        Tick();
        return true;
    }

    public void Hide()
    {
        Unhook();
        shown = false;
        if (layer != null) layer.style.display = DisplayStyle.None;
    }

    void Fire()
    {
        var cb = clicked;
        int mark = markTab;
        try { cb?.Invoke(mark); } catch (Exception e) { Debug.LogException(e); }
    }

    void Unhook()
    {
        if (target != null)
        {
            if (onDown != null) target.UnregisterCallback(onDown, TrickleDown.TrickleDown);
            if (onSubmit != null) target.UnregisterCallback(onSubmit, TrickleDown.TrickleDown);
        }
        target = null;
        onDown = null;
        onSubmit = null;
        clicked = null;
    }

    // ------------------------------------------------------------------ the layer

    bool Ensure(VisualElement r)
    {
        if (root != r) { root = r; host = r.Q("ebaeScreen") ?? r; layer = null; }
        if (layer != null && layer.parent == host) return true;
        if (layer != null) layer.RemoveFromHierarchy();
        layer = new VisualElement { name = LayerName, pickingMode = PickingMode.Ignore };
        Fill(layer);
        dimT = Dim(); dimB = Dim(); dimL = Dim(); dimR = Dim();
        ring = new VisualElement { pickingMode = PickingMode.Ignore };
        ring.style.position = Position.Absolute;
        Border(ring, 4f, Gold, 12f);
        layer.Add(ring);

        bubble = new VisualElement { pickingMode = PickingMode.Ignore };
        bubble.style.position = Position.Absolute;
        bubble.style.width = BubbleW;
        bubble.style.backgroundColor = Plum;
        Border(bubble, 3f, Gold, 14f);
        bubble.style.paddingLeft = 16; bubble.style.paddingRight = 16; bubble.style.paddingTop = 12; bubble.style.paddingBottom = 12;
        titleLabel = Text(20, Gold, true);
        bodyLabel = Text(16, Color.white, false);
        bodyLabel.style.marginTop = 6;
        hintLabel = Text(14, new Color(1f, 1f, 1f, 0.6f), false);
        hintLabel.style.marginTop = 8;
        bubble.Add(titleLabel); bubble.Add(bodyLabel); bubble.Add(hintLabel);
        layer.Add(bubble);
        host.Add(layer);
        return true;
    }

    static void Fill(VisualElement e)
    {
        e.style.position = Position.Absolute;
        e.style.left = 0; e.style.top = 0; e.style.right = 0; e.style.bottom = 0;
    }

    VisualElement Dim()
    {
        var e = new VisualElement { pickingMode = PickingMode.Ignore };
        e.style.position = Position.Absolute;
        e.style.backgroundColor = new Color(0f, 0f, 0f, 0.68f);     // blended in linear space it reads about half as strong
        layer.Add(e);
        return e;
    }

    static void Border(VisualElement e, float w, Color c, float radius)
    {
        e.style.borderTopWidth = w; e.style.borderBottomWidth = w; e.style.borderLeftWidth = w; e.style.borderRightWidth = w;
        e.style.borderTopColor = c; e.style.borderBottomColor = c; e.style.borderLeftColor = c; e.style.borderRightColor = c;
        e.style.borderTopLeftRadius = radius; e.style.borderTopRightRadius = radius;
        e.style.borderBottomLeftRadius = radius; e.style.borderBottomRightRadius = radius;
    }

    static Label Text(float size, Color color, bool bold)
    {
        var l = new Label { pickingMode = PickingMode.Ignore };
        l.style.fontSize = size;
        l.style.color = color;
        l.style.whiteSpace = WhiteSpace.Normal;
        if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
        return l;
    }

    // ------------------------------------------------------------------ every frame

    /// <summary>Follow the target (the window pops open over a quarter of a second, and the strip can reflow).</summary>
    public void Tick()
    {
        if (!shown || layer == null) return;
        if (layer.parent != host) { Ensure(root); layer.style.display = DisplayStyle.Flex; }
        // Hidden with visibility, never display: a display:none layer has no layout, so its size reads 0 and it could never come back.
        // The host (the laptop's screen element) is always laid out and the layer fills it, so it gives the origin and the size.
        var lb = host.worldBound;
        var tb = target != null && target.panel != null ? target.worldBound : default;
        if (target == null || target.panel == null || float.IsNaN(tb.x) || tb.width < 1f || tb.height < 1f || float.IsNaN(lb.x) || lb.width < 1f)
        {
            layer.style.visibility = Visibility.Hidden;
            return;
        }
        layer.style.visibility = Visibility.Visible;

        // local to the layer
        float x = tb.x - lb.x - Pad, y = tb.y - lb.y - Pad, w = tb.width + 2f * Pad, h = tb.height + 2f * Pad;
        float W = lb.width, H = lb.height;
        Place(dimT, 0f, 0f, W, Mathf.Max(0f, y));
        Place(dimB, 0f, y + h, W, Mathf.Max(0f, H - (y + h)));
        Place(dimL, 0f, y, Mathf.Max(0f, x), h);
        Place(dimR, x + w, y, Mathf.Max(0f, W - (x + w)), h);
        Place(ring, x, y, w, h);
        ring.style.opacity = 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 6f);

        float bh = bubble.resolvedStyle.height;
        if (float.IsNaN(bh) || bh < 10f) bh = 150f;
        float bx = Mathf.Clamp(x, 10f, Mathf.Max(10f, W - BubbleW - 10f));
        float by = y + h + 14f;
        if (by + bh > H - 8f) by = Mathf.Max(8f, y - bh - 14f);
        bubble.style.left = bx;
        bubble.style.top = by;
    }

    static void Place(VisualElement e, float x, float y, float w, float h)
    {
        e.style.left = x; e.style.top = y; e.style.width = w; e.style.height = h;
        e.style.display = w > 0.5f && h > 0.5f ? DisplayStyle.Flex : DisplayStyle.None;
    }

    /// <summary>Drop the coach for good (the panel went away with the scene).</summary>
    public void Dispose()
    {
        Hide();
        if (layer != null) layer.RemoveFromHierarchy();
        layer = null; root = null; host = null; doc = null;
    }
}
