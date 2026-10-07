using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using Random = UnityEngine.Random;

/// <summary>The two apps on the shop laptop's tab strip.</summary>
public enum LaptopApp { Ebae, Scrapazon }

/// <summary>
/// The shop laptop screen (UI Toolkit, Assets/UI/ebae/ebae.uxml): a tiny browser with an app tab strip —
/// ebae (sell-side, this class) | Scrapazon (buy-side, <see cref="ScrapazonApp"/>, Assets/UI/shop/).
/// ebae, the parody auction site. Tabs: Your Junk (GameSession.Inventory: list at a preset/custom price or
/// pawn now), Listings (live offers with accept/decline + countdowns) and Sold (history). All economy goes
/// through <see cref="EbaeMarket"/>. Show()/Hide() push/pop the player's UI lock exactly once each; switching
/// apps while open never touches the lock.
/// Scene wiring: CommonWorldElements/UIManager, "ebae" = the GameObject holding the UIDocument.
/// </summary>
public partial class EbaeUI : MonoBehaviour
{
    public static EbaeUI Instance;

    [SerializeField] private GameObject ebae;

    /// <summary>True while the laptop screen is up (EbaeMarket skips HUD toasts then).</summary>
    public static bool IsOpen => Instance != null && Instance.isOpen;

    /// <summary>The app showing on the laptop right now (Ebae when closed).</summary>
    public static LaptopApp CurrentApp => IsOpen ? Instance.app : LaptopApp.Ebae;

    /// <summary>True once Scrapazon has been opened this session (the "NEW" tab badge goes away).</summary>
    public static bool ScrapazonVisited { get; private set; }

    enum Tab { Junk, Listings, Sold, Neighbours }

    class OfferView
    {
        public EbaeOffer offer;
        public Label time;
        public VisualElement fill;
        public int lastSecond = -1;
        public bool urgent;
    }

    class Confetto
    {
        public VisualElement el;
        public Vector2 pos, vel;
        public float rot, spin, age, life;
    }

    UIDocument doc;
    VisualElement wiredRoot, screen, window, content, toastLayer, fxLayer, currentStamp;
    ScrollView scroll;
    Label balanceLabel, sellerLabel, tickerLabel, addressLabel, listingsBadge;
    VisualElement tabJunk, tabListings, tabSold;
    Label tabJunkLabel, tabListingsLabel, tabSoldLabel;
    readonly List<VisualElement> logoLetters = new List<VisualElement>();

    // laptop app strip
    VisualElement ebaePage, shopPage, appTabEbae, appTabShop;
    Label appTabEbaeLabel, appTabEbaeBadge, appTabShopLabel, appTabShopBadge, osClockLabel;
    ScrapazonApp shop;
    LaptopApp app = LaptopApp.Ebae;
    static LaptopApp lastApp = LaptopApp.Ebae;
    float nextClockAt;

    bool isOpen, dirty;
    Tab tab = Tab.Junk;
    Tab builtTab;
    string builtSignature;
    float shownCash = float.NaN;
    float nextTickerAt, nextWobbleAt;
    int tickerIndex;
    bool wobble;

    readonly Dictionary<string, string> customPrice = new Dictionary<string, string>();
    readonly Dictionary<string, string> repriceText = new Dictionary<string, string>();
    readonly HashSet<string> seenOffers = new HashSet<string>();
    readonly HashSet<string> seenCards = new HashSet<string>();
    readonly List<OfferView> offerViews = new List<OfferView>();
    readonly List<VisualElement> shinyIcons = new List<VisualElement>();
    readonly List<Confetto> confetti = new List<Confetto>();

    // Neighbours tab (co-op ebae, Docs/Multiplayer.md §13.8, N5)
    class NeighbourTimer { public int offerId; public Label time; public VisualElement fill; public int lastSecond = -1; }
    VisualElement tabNeighbours;
    Label tabNeighboursLabel, tabNeighboursBadge;
    string openOfferForm;                                    // "seller/listing" whose offer form is open
    readonly Dictionary<string, string> offerCash = new Dictionary<string, string>();
    readonly Dictionary<string, string> offerTrade = new Dictionary<string, string>(); // listing key -> inventory uid
    readonly Dictionary<int, string> counterText = new Dictionary<int, string>();
    readonly List<NeighbourTimer> neighbourTimers = new List<NeighbourTimer>();

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        Instance = this;
        if (ebae == null)
        {
            var d = GetComponentInChildren<UIDocument>(true);
            if (d != null) ebae = d.gameObject;
        }
        if (ebae == null)
        {
            Debug.LogWarning("[EbaeUI] No 'ebae' UIDocument GameObject assigned; the laptop can't open ebae.");
            return;
        }
        ebae.SetActive(true); // the document lives on an inactive object in the scene
        if (Wire()) SetVisible(false);
        tickerIndex = Random.Range(0, EbaeContent.Ticker.Length);
    }

    void Start()
    {
        // In case the document built its tree after our Awake: make sure it starts hidden.
        if (!isOpen && wiredRoot == null && ebae != null && Wire()) SetVisible(false);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        lastApp = LaptopApp.Ebae;
        ScrapazonVisited = false;
    }

    void OnEnable()
    {
        EbaeMarket.Changed += MarkDirty;
        EbaeMarket.OfferArrived += OnOfferArrived;
        EbaeMarket.OfferExpired += OnOfferExpired;
        GameSession.Changed += MarkDirty;
        EbaeNeighbours.Changed += MarkDirty;
        Businesses.Changed += MarkDirty;
    }

    void OnDisable()
    {
        EbaeMarket.Changed -= MarkDirty;
        EbaeMarket.OfferArrived -= OnOfferArrived;
        EbaeMarket.OfferExpired -= OnOfferExpired;
        GameSession.Changed -= MarkDirty;
        EbaeNeighbours.Changed -= MarkDirty;
        Businesses.Changed -= MarkDirty;
        ReleaseLock();
    }

    void OnDestroy()
    {
        ReleaseLock();
        itemThumbnails.Dispose();
        if (Instance == this) Instance = null;
    }

    /// <summary>Never leak the player's UI lock (scene unload / disable while open).</summary>
    void ReleaseLock()
    {
        if (!isOpen) return;
        isOpen = false;
        SetVisible(false);
        ClearFx();
        shop?.SetActive(false);
        PlayerController.PopUILock();
    }

    void Update()
    {
        if (!isOpen) return;
        itemThumbnails.Tick();

        // UI Builder live-reload / document re-enable rebuilds the tree: re-wire.
        if (doc == null || doc.rootVisualElement != wiredRoot)
        {
            if (!Wire()) { Hide(); return; }
            SetVisible(true);
            builtSignature = null;
            dirty = true;
            SwitchApp(app, false);
        }

        if (GameControls.Down(KeyCode.Escape, GameControls.Action.Back) || GameControls.PadDown(GameControls.Action.Pause)) { Hide(); return; }

        if (dirty) { dirty = false; Refresh(false); }
        RefreshCash(false);
        TickOfferTimers();
        TickNeighbourTimers();
        TickTicker();
        TickWobble();
        TickConfetti();
        TickClock();
        if (app == LaptopApp.Scrapazon) shop?.Tick();
    }

    // ------------------------------------------------------------------ show / hide

    /// <summary>Open the laptop screen on the most useful app. Takes the player's UI lock (once).</summary>
    public void Show() => Show(DefaultApp());

    /// <summary>
    /// Open the laptop on a specific app (or switch to it if already open). Takes the player's UI lock only
    /// when it actually opens; switching apps never pushes/pops the lock.
    /// </summary>
    public void Show(LaptopApp which)
    {
        if (isOpen) { SwitchApp(which, true); return; }
        bool stale = doc == null || wiredRoot == null || doc.rootVisualElement != wiredRoot;
        if (stale && !Wire())
        {
            GameHUD.Toast("ebae is down for maintenance (a raccoon ate the wifi)", ToastStyle.Bad);
            Sfx.Play("buzz");
            return;
        }

        isOpen = true;
        PlayerController.PushUILock();

        int offers = EbaeMarket.PendingOfferCount;
        int waiting = NeighbourWaiting;
        tab = waiting > 0 ? Tab.Neighbours : offers > 0 ? Tab.Listings : Tab.Junk;
        offers += waiting;
        builtSignature = null;
        SetVisible(true);
        RefreshCash(true);
        Refresh(true);
        nextTickerAt = 0f;
        nextClockAt = 0f;
        SwitchApp(which, false);
        if (GameControls.HasGamepad) (app == LaptopApp.Ebae ? appTabEbae : appTabShop)?.Focus();

        Sfx.Play("click");
        Sfx.Play("pop", null, 0.7f, 1.3f);
        if (app != LaptopApp.Ebae) return; // Scrapazon greets you itself
        LogoHop();
        if (offers > 0)
            UiToast($"welcome back! {offers} offer{(offers == 1 ? " is" : "s are")} waiting", "offer");
        else if (GameSession.Inventory.Count == 0 && EbaeMarket.Listings.Count == 0 && EbaeMarket.TotalSales == 0)
            UiToast("welcome to ebae! sell your garbage to strangers on the internet", "good");
    }

    /// <summary>Infected with no shot in your pocket → Scrapazon; buyers waiting → ebae; else the last app used.</summary>
    static LaptopApp DefaultApp()
    {
        if (GameSession.HasRabies && GameSession.GetConsumable(ShopIds.RabiesShot) == 0) return LaptopApp.Scrapazon;
        if (EbaeMarket.PendingOfferCount > 0 || NeighbourWaiting > 0) return LaptopApp.Ebae;
        return lastApp;
    }

    /// <summary>Swap the visible app page. Pure UI: the player's lock is untouched.</summary>
    void SwitchApp(LaptopApp which, bool animate)
    {
        if (which == LaptopApp.Scrapazon && shop == null)
        {
            which = LaptopApp.Ebae;
            if (animate) UiToast($"{Scrapazon.Name} is down. a raccoon unplugged the router.", "bad");
        }
        bool changed = which != app;
        app = which;
        lastApp = which;

        if (ebaePage != null) ebaePage.style.display = which == LaptopApp.Ebae ? DisplayStyle.Flex : DisplayStyle.None;
        shop?.SetActive(which == LaptopApp.Scrapazon);
        if (which == LaptopApp.Scrapazon) ScrapazonVisited = true;
        else { SetEbaeAddress(); Refresh(false); }
        RefreshAppTabs();

        if (!animate || !changed) return;
        var page = which == LaptopApp.Ebae ? ebaePage : shopPage;
        if (page != null) PopIn(page, "app-page--enter");
        Flash(which == LaptopApp.Ebae ? appTabEbae : appTabShop, "app-tab--pulse", 160);
        Sfx.Play("whoosh", null, 0.45f, 1.5f);
        if (which == LaptopApp.Ebae) LogoHop();
    }

    void RefreshAppTabs()
    {
        appTabEbae?.EnableInClassList("app-tab--active", app == LaptopApp.Ebae);
        appTabShop?.EnableInClassList("app-tab--active", app == LaptopApp.Scrapazon);
        if (appTabEbaeLabel != null) appTabEbaeLabel.text = "ebae - sell your junk";
        if (appTabEbaeBadge != null)
        {
            int offers = EbaeMarket.PendingOfferCount + NeighbourWaiting;
            appTabEbaeBadge.text = offers.ToString();
            appTabEbaeBadge.EnableInClassList("app-tab-badge--hidden", offers == 0);
        }
        if (appTabShopLabel != null) appTabShopLabel.text = $"{Scrapazon.Name}.com: Spend more. Smell more.";
        if (appTabShopBadge != null)
        {
            bool needShot = GameSession.HasRabies && GameSession.GetConsumable(ShopIds.RabiesShot) == 0;
            appTabShopBadge.text = needShot ? "!" : "NEW";
            appTabShopBadge.EnableInClassList("app-tab-badge--alert", needShot);
            appTabShopBadge.EnableInClassList("app-tab-badge--hidden", !needShot && ScrapazonVisited);
        }
    }

    void TickClock()
    {
        if (osClockLabel == null || Time.unscaledTime < nextClockAt) return;
        nextClockAt = Time.unscaledTime + 1f;
        osClockLabel.text = $"{GameClock.ClockText} \u00b7 Day {GameClock.Day}";
        osClockLabel.EnableInClassList("os-clock--night", GameClock.IsNight);
    }

    void SetEbaeAddress()
    {
        if (addressLabel == null || app != LaptopApp.Ebae) return;
        addressLabel.text = tab switch
        {
            Tab.Junk => "www.ebae.com/my-junk",
            Tab.Listings => "www.ebae.com/selling?sort=most_desperate",
            Tab.Neighbours => "www.ebae.com/neighbours?distance=next_door",
            _ => "www.ebae.com/sold?feelings=proud",
        };
    }

    // ------------------------------------------------------------------ laptop host API (used by ScrapazonApp)

    /// <summary>In-window toast. kind: good, bad, offer, info, sz.</summary>
    public void LaptopToast(string text, string kind) => UiToast(text, kind);

    /// <summary>Big rubber-stamp slam over the window. variant: null (ebae red), "pawn", "sz".</summary>
    public void LaptopStamp(string text, string sub, string variant) => Stamp(text, sub, variant);

    /// <summary>Confetti burst; palette = confetto color indices (0-8).</summary>
    public void LaptopConfetti(int count, params int[] palette) => Confetti(count, palette);

    /// <summary>Run action after ms (UI scheduler; unscaled, stops when the document goes away).</summary>
    public void LaptopAfter(long ms, Action action) => After(ms, action);

    public void SetAddress(string url)
    {
        if (addressLabel != null) addressLabel.text = url;
    }

    public void CloseLaptop() => Hide();

    /// <summary>Close the laptop screen. Releases the UI lock (only if Show took it).</summary>
    public void Hide()
    {
        if (!isOpen) { SetVisible(false); return; }
        isOpen = false;
        SetVisible(false);
        ClearFx();
        shop?.SetActive(false);
        PlayerController.PopUILock();
        Sfx.Play("pop", null, 0.6f, 0.8f);
    }

    void SetVisible(bool visible)
    {
        if (wiredRoot == null) return;
        wiredRoot.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        if (window == null) return;
        window.RemoveFromClassList("window--open");
        if (visible) window.schedule.Execute(() => window.AddToClassList("window--open")).ExecuteLater(30);
    }

    void MarkDirty()
    {
        dirty = true;
        shop?.MarkDirty();
    }

    // ------------------------------------------------------------------ wiring

    bool Wire()
    {
        if (ebae == null) return false;
        doc = ebae.GetComponent<UIDocument>();
        var root = doc != null ? doc.rootVisualElement : null;
        if (root == null) return false;
        screen = root.Q<VisualElement>("ebaeScreen");
        if (screen == null)
        {
            Debug.LogWarning("[EbaeUI] ebae.uxml has no 'ebaeScreen' element (is the UIDocument using Assets/UI/ebae/ebae.uxml?).");
            return false;
        }

        wiredRoot = root;
        window = root.Q<VisualElement>("window");
        scroll = root.Q<ScrollView>("contentScroll");
        toastLayer = root.Q<VisualElement>("toastLayer");
        fxLayer = root.Q<VisualElement>("fxLayer");
        balanceLabel = root.Q<Label>("balanceLabel");
        sellerLabel = root.Q<Label>("sellerLabel");
        tickerLabel = root.Q<Label>("tickerLabel");
        addressLabel = root.Q<Label>("addressLabel");
        tabJunk = root.Q<VisualElement>("tabJunk");
        tabListings = root.Q<VisualElement>("tabListings");
        tabSold = root.Q<VisualElement>("tabSold");
        tabJunkLabel = root.Q<Label>("tabJunkLabel");
        tabListingsLabel = root.Q<Label>("tabListingsLabel");
        tabSoldLabel = root.Q<Label>("tabSoldLabel");
        listingsBadge = root.Q<Label>("tabListingsBadge");

        logoLetters.Clear();
        root.Query<VisualElement>(className: "logo-letter").ForEach(logoLetters.Add);

        if (scroll != null)
        {
            scroll.mode = ScrollViewMode.Vertical;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.mouseWheelScrollSize = 80f;
            content = new VisualElement { name = "contentInner" };
            content.AddToClassList("content-inner");
            scroll.Add(content);
        }

        OnClick(root.Q<VisualElement>("closeButton"), Hide);
        OnClick(root.Q<VisualElement>("logoffButton"), Hide);
        OnClick(root.Q<VisualElement>("logo"), LogoHop);
        OnClick(tabJunk, () => SelectTab(Tab.Junk));
        OnClick(tabListings, () => SelectTab(Tab.Listings));
        OnClick(tabSold, () => SelectTab(Tab.Sold));
        WireNeighboursTab(root);

        // laptop app strip: ebae | Scrapazon
        ebaePage = root.Q<VisualElement>("ebaePage");
        shopPage = root.Q<VisualElement>("scrapazonPage");
        appTabEbae = root.Q<VisualElement>("appTabEbae");
        appTabShop = root.Q<VisualElement>("appTabShop");
        appTabEbaeLabel = root.Q<Label>("appTabEbaeLabel");
        appTabEbaeBadge = root.Q<Label>("appTabEbaeBadge");
        appTabShopLabel = root.Q<Label>("appTabShopLabel");
        appTabShopBadge = root.Q<Label>("appTabShopBadge");
        osClockLabel = root.Q<Label>("osClockLabel");
        var shopIcon = root.Q<Label>("appTabShopIcon");
        if (shopIcon != null) shopIcon.text = Scrapazon.Name.Substring(0, 1).ToLowerInvariant();
        OnClick(appTabEbae, () => SwitchApp(LaptopApp.Ebae, true));
        OnClick(appTabShop, () => SwitchApp(LaptopApp.Scrapazon, true));
        OnClick(root.Q<VisualElement>("appTabPlus"), () =>
        {
            Sfx.Play("squeak", null, 0.6f, 1.2f);
            UiToast("new tab? on THIS laptop? it can barely run two.", "info");
        });
        try { shop = ScrapazonApp.Create(this, root); }
        catch (Exception e) { Debug.LogException(e); shop = null; }
        if (shop == null) Debug.LogWarning("[EbaeUI] No Scrapazon page in ebae.uxml (scrapazonPage); the laptop runs ebae only.");
        if (ebaePage != null) ebaePage.style.display = DisplayStyle.Flex;

        builtSignature = null;
        offerViews.Clear();
        shinyIcons.Clear();
        confetti.Clear();
        currentStamp = null;
        return true;
    }

    void SelectTab(Tab t)
    {
        if (tab == t) return;
        tab = t;
        Refresh(true);
    }

    // ------------------------------------------------------------------ refresh

    void Refresh(bool force)
    {
        RefreshTabs();
        if (sellerLabel != null) sellerLabel.text = SellerLine();
        if (content == null) return;

        string sig = Signature(tab);
        if (!force && builtTab == tab && sig == builtSignature) return;

        bool sameTab = builtTab == tab && builtSignature != null;
        Vector2 offset = scroll != null ? scroll.scrollOffset : Vector2.zero;
        builtTab = tab;
        builtSignature = sig;

        content.Clear();
        offerViews.Clear();
        neighbourTimers.Clear();
        shinyIcons.Clear();
        switch (tab)
        {
            case Tab.Junk: BuildJunk(GameSession.Inventory); break;
            case Tab.Listings: BuildListings(EbaeMarket.Listings); break;
            case Tab.Neighbours: BuildNeighbours(); break;
            default: BuildSold(); break;
        }

        SetEbaeAddress();

        if (scroll != null)
        {
            if (sameTab) scroll.schedule.Execute(() => scroll.scrollOffset = offset).ExecuteLater(1);
            else scroll.scrollOffset = Vector2.zero;
        }
    }

    void RefreshTabs()
    {
        RefreshAppTabs();
        SetTab(tabJunk, tabJunkLabel, Tab.Junk, $"Your Junk ({GameSession.Inventory.Count})");
        SetTab(tabListings, tabListingsLabel, Tab.Listings, $"Listings ({EbaeMarket.Listings.Count})");
        SetTab(tabSold, tabSoldLabel, Tab.Sold, $"Sold ({EbaeMarket.TotalSales})");
        RefreshNeighboursTab();
        if (listingsBadge != null)
        {
            int offers = EbaeMarket.PendingOfferCount;
            listingsBadge.text = offers.ToString();
            listingsBadge.EnableInClassList("tab-badge--hidden", offers == 0);
        }
    }

    void SetTab(VisualElement tabElement, Label label, Tab t, string text)
    {
        if (label != null) label.text = text;
        tabElement?.EnableInClassList("tab--active", tab == t);
    }

    void RefreshCash(bool snap)
    {
        if (balanceLabel == null) return;
        float cash = GameSession.Cash;
        if (!snap && Mathf.Approximately(cash, shownCash)) return;
        bool up = !snap && !float.IsNaN(shownCash) && cash > shownCash;
        shownCash = cash;
        balanceLabel.text = Economy.Money(cash);
        if (up) Flash(balanceLabel, "wallet-balance--bump", 170);
    }

    static string SellerLine()
    {
        int n = EbaeMarket.TotalSales;
        if (n == 0) return "new seller (no feedback)";
        return $"{n} sale{(n == 1 ? "" : "s")} · 99.{(n * 7) % 10}% positive (probably)";
    }

    string Signature(Tab t)
    {
        var sb = new StringBuilder();
        switch (t)
        {
            case Tab.Junk:
                foreach (var it in GameSession.Inventory)
                    sb.Append(it != null ? it.uid : "-").Append(it != null && EbaeMarket.IsReservedUid(it.uid) ? "r" : "")
                        .Append(it != null && EbaeNeighbours.UsesHostNpc && EbaeNeighbours.Instance.IsNpcPending(it.uid) ? "p" : "").Append(',');
                sb.Append("slots:").Append(EbaeMarket.Listings.Count);
                break;
            case Tab.Listings:
                foreach (var l in EbaeMarket.Listings)
                {
                    if (l == null) continue;
                    sb.Append(l.id).Append(':').Append(l.askingPrice).Append(l.neighbourHold ? "h" : "")
                        .Append(NeighbourOffersOn(l.id));
                    if (EbaeNeighbours.Active)
                    {
                        var publication = EbaeNeighbours.Instance.PublicationStatus(l.id);
                        sb.Append(':').Append((int)publication.State).Append(':').Append(publication.Reason);
                    }
                    else sb.Append(":local");
                    if (EbaeNeighbours.UsesHostNpc) sb.Append(EbaeNeighbours.Instance.IsNpcPending(l.id) ? ":npc-pending" : ":npc-ready");
                    sb.Append(':');
                    foreach (var o in l.offers) sb.Append(o.id).Append(',');
                    sb.Append(';');
                }
                break;
            case Tab.Neighbours:
                NeighbourSignature(sb);
                break;
            default:
                sb.Append(EbaeMarket.TotalSales).Append('/').Append(EbaeMarket.History.Count);
                break;
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ tab: your junk

    void BuildJunk(IReadOnlyList<ItemInstance> items)
    {
        int active = EbaeMarket.Listings.Count;
        content.Add(SectionHead("Your Junk", items.Count == 0 ? null
            : $"treasures you fished out of the trash. {active}/{EbaeTradingRules.MaxActiveListings} listing slots used."));
        if (active >= EbaeTradingRules.MaxActiveListings)
            content.Add(L(EbaeTradingRules.CapacityError(active), "item-note"));
        if (items.Count == 0)
        {
            content.Add(Empty("(o_o)", "Nothing to sell.", "Go dig through some garbage. (toss treasures in the KEEP crate while sorting)"));
            return;
        }
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i] != null) content.Add(BuildJunkCard(items[i], i));
    }

    VisualElement BuildJunkCard(ItemInstance it, int index)
    {
        var def = it.Def;
        string name = EbaeMarket.NameOf(it);
        var card = V("card junk-card " + (index % 2 == 0 ? "card--tilt-l" : "card--tilt-r"));

        card.Add(Icon(it, false));

        var body = V("card-body");
        var title = V("title-row");
        title.Add(RarityChip(it));
        title.Add(L(name, "item-name"));
        body.Add(title);
        body.Add(L("condition: " + ConditionText(it.condition), "item-meta"));
        body.Add(L(FoundText(it), "item-meta"));
        if (def != null && !string.IsNullOrEmpty(def.flavor)) body.Add(L(def.flavor, "item-flavor"));
        if (!string.IsNullOrEmpty(it.note)) body.Add(L($"note: \"{it.note}\"", "item-note"));
        if (EbaeMarket.IsReservedUid(it.uid)) body.Add(L("offered to a neighbour in a trade (locked until they answer)", "item-note"));
        card.Add(body);

        if (EbaeNeighbours.UsesHostNpc && EbaeNeighbours.Instance.IsNpcPending(it.uid))
        {
            card.Add(L("Waiting for Town host…", "item-note"));
            PopInOnce(card, "junk:" + it.uid, "card--pop");
            return card;
        }

        var actions = V("card-actions");
        var appraisal = V("appraisal-row");
        appraisal.Add(L("ebae appraisal", "appraisal-caption"));
        appraisal.Add(L(Economy.Money(EbaeMarket.Appraise(it)), "appraisal-value"));
        actions.Add(appraisal);

        var presets = V("preset-row");
        AddPreset(presets, card, it, EbaePricePreset.Lowball, "Lowball", "btn-lowball");
        AddPreset(presets, card, it, EbaePricePreset.Fair, "Fair", "btn-fair");
        AddPreset(presets, card, it, EbaePricePreset.Greedy, "Greedy", "btn-greedy");
        actions.Add(presets);

        var custom = V("custom-row");
        var field = new TextField { maxLength = 10 };
        field.AddToClassList("price-field");
        field.textEdition.placeholder = "custom price $";
        if (customPrice.TryGetValue(it.uid, out var typed)) field.SetValueWithoutNotify(typed);
        var hint = L("", "greed-hint");
        UpdateGreedHint(hint, it, field.value);
        field.RegisterValueChangedCallback(e =>
        {
            customPrice[it.uid] = e.newValue;
            UpdateGreedHint(hint, it, e.newValue);
        });
        field.RegisterCallback<KeyDownEvent>(e =>
        {
            if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
            TryListCustom(card, it, field, hint);
            e.StopPropagation();
        }, TrickleDown.TrickleDown);
        custom.Add(field);
        custom.Add(Btn("List it!", "btn-list", () => TryListCustom(card, it, field, hint)));
        actions.Add(custom);
        actions.Add(hint);

        var pawnRow = V("pawn-row");
        pawnRow.Add(Btn($"Pawn now {Economy.Money(EbaeMarket.PawnPrice(it))}  ({(EbaeNeighbours.UsesHostNpc ? "host-confirmed" : "instant")}, Pawn Pete)",
            "btn-pawn", () => DoPawn(card, it)));
        actions.Add(pawnRow);
        card.Add(actions);

        PopInOnce(card, "junk:" + it.uid, "card--pop");
        return card;
    }

    void AddPreset(VisualElement row, VisualElement card, ItemInstance it, EbaePricePreset preset, string label, string cls)
    {
        float price = EbaeMarket.PresetPrice(it, preset);
        bool allowed = EbaeTradingRules.TryPriceCents(price, out _, out _);
        var button = Btn($"<size=70%>{label}</size>\n{(allowed ? Economy.Money(price) : "over $50k cap")}",
            "btn-preset " + cls, () => DoList(card, it, price));
        if (!allowed) button.style.opacity = 0.5f;
        row.Add(button);
    }

    void UpdateGreedHint(Label hint, ItemInstance it, string text)
    {
        foreach (var c in new[] { "greed-hint--steal", "greed-hint--fair", "greed-hint--greedy", "greed-hint--delusional", "greed-hint--bad" })
            hint.RemoveFromClassList(c);
        if (string.IsNullOrWhiteSpace(text)) { hint.text = "price range: $0.50–$50,000"; return; }
        if (!EbaeTradingRules.TryParsePrice(text, out long cents, out string error))
        {
            hint.text = error;
            hint.AddToClassList("greed-hint--bad");
            return;
        }
        float price = cents / 100f;
        float greed = EbaeMarket.GreedOf(it, price);
        var tier = EbaeMarket.GreedTier(greed);
        hint.text = tier switch
        {
            EbaeGreed.Steal => $"STEAL ({greed:0.0}x) - buyers will swarm",
            EbaeGreed.Fair => $"FAIR ({greed:0.0}x) - steady offers",
            EbaeGreed.Greedy => $"GREEDY ({greed:0.0}x) - few, rude offers",
            _ => $"DELUSIONAL ({greed:0.0}x) - good luck lol",
        };
        hint.AddToClassList("greed-hint--" + tier.ToString().ToLowerInvariant());
    }

    void TryListCustom(VisualElement card, ItemInstance it, TextField field, Label hint)
    {
        if (!EbaeTradingRules.TryParsePrice(field.value, out long cents, out string error))
        {
            Sfx.Play("buzz", null, 0.6f);
            Flash(field, "price-field--shake", 120);
            hint.text = error;
            hint.AddToClassList("greed-hint--bad");
            return;
        }
        DoList(card, it, cents / 100f);
    }

    void DoList(VisualElement card, ItemInstance it, float price)
    {
        if (!EbaeTradingRules.TryPriceCents(price, out _, out string priceError))
        {
            Sfx.Play("buzz");
            UiToast(priceError, "bad");
            return;
        }
        if (!EbaeTradingRules.CanCreateListing(EbaeMarket.Listings.Count))
        {
            Sfx.Play("buzz");
            UiToast(EbaeTradingRules.CapacityError(EbaeMarket.Listings.Count), "bad");
            return;
        }
        if (!BeginLeaving(card, "card--leaving")) return;
        Sfx.Play("whoosh");
        string name = EbaeMarket.NameOf(it);
        string greed = EbaeMarket.GreedLabel(EbaeMarket.GreedOf(it, price));
        After(200, () =>
        {
            if (!EbaeTradingRules.CanCreateListing(EbaeMarket.Listings.Count))
            {
                Sfx.Play("buzz");
                UiToast(EbaeTradingRules.CapacityError(EbaeMarket.Listings.Count), "bad");
                MarkDirty();
                return;
            }
            if (!EbaeMarket.List(it.uid, price))
            {
                Sfx.Play("buzz");
                UiToast("couldn't list that. is it... gone?", "bad");
                MarkDirty();
                return;
            }
            customPrice.Remove(it.uid);
            Sfx.Play("ding", null, 1f, 1.1f);
            UiToast($"Listed {name} for {Economy.Money(price)} ({greed}). buyers incoming...", "good");
            Flash(tabListings, "tab--pulse", 350);
            LogoHop();
        });
    }

    void DoPawn(VisualElement card, ItemInstance it)
    {
        if (EbaeNeighbours.UsesHostNpc)
        {
            if (EbaeNeighbours.Instance.RequestNpcPawn(it.uid))
                UiToast("Waiting for Town host…", "info");
            else { Sfx.Play("buzz"); UiToast("Couldn't submit that pawn sale. Check the item and try again.", "bad"); }
            MarkDirty();
            return;
        }
        if (!BeginLeaving(card, "card--leaving")) return;
        Sfx.Play("whoosh");
        After(180, () =>
        {
            if (!EbaeMarket.PawnNow(it.uid, out var sale))
            {
                Sfx.Play("buzz");
                UiToast("Pawn Pete squints at you. that item isn't here.", "bad");
                MarkDirty();
                return;
            }
            CelebrateSale(sale, true);
            UiToast($"Pawn Pete: \"{sale.review}\"", "good");
        });
    }

    // ------------------------------------------------------------------ tab: listings

    void BuildListings(IReadOnlyList<EbaeListing> listings)
    {
        int offers = EbaeMarket.PendingOfferCount;
        string sub = listings.Count == 0 ? null
            : offers > 0 ? $"{offers} offer{(offers == 1 ? "" : "s")} waiting! buyers are flaky, don't leave them hanging."
            : "waiting for buyers to smell your deals...";
        if (listings.Count > EbaeTradingRules.MaxActiveListings)
            sub = $"{listings.Count} saved listings; only {EbaeTradingRules.MaxActiveListings} can be live in Town. Extras stay saved until you delist them.";
        content.Add(SectionHead("Your Listings", sub));
        if (listings.Count == 0)
        {
            content.Add(Empty("zzz", "You're not selling anything.", "List something from Your Junk and the vultures will come."));
            return;
        }
        for (int i = listings.Count - 1; i >= 0; i--) content.Add(BuildListingCard(listings[i], i));
    }

    VisualElement BuildListingCard(EbaeListing l, int index)
    {
        var card = V("card listing-card " + (index % 2 == 0 ? "card--tilt-r" : "card--tilt-l"));
        bool town = EbaeNeighbours.Active;
        var publication = town ? EbaeNeighbours.Instance.PublicationStatus(l.id) : default;
        bool npcPending = EbaeNeighbours.UsesHostNpc && EbaeNeighbours.Instance.IsNpcPending(l.id);

        var head = V("listing-head");
        head.Add(Icon(l.item, true));
        var info = V("listing-info");
        var title = V("title-row");
        title.Add(RarityChip(l.item));
        title.Add(L(l.ItemName, "item-name"));
        info.Add(title);
        var sub = V("listing-sub");
        var tier = EbaeMarket.GreedTier(l.Greed);
        sub.Add(L("asking " + Economy.Money(l.askingPrice), "listing-asking"));
        sub.Add(L(tier.ToString().ToUpperInvariant(), "greed-tag greed-tag--" + tier.ToString().ToLowerInvariant()));
        sub.Add(L($"appraised {Economy.Money(l.Appraisal)} · listed day {l.dayListed} · {l.offersReceived} offer{(l.offersReceived == 1 ? "" : "s")} so far", "item-meta"));
        sub.Add(L(!town ? "LOCAL ONLY · join a Town to show neighbours"
            : publication.State == EbaePublicationState.Live ? "● LIVE TO NEIGHBOURS"
            : publication.State == EbaePublicationState.Rejected ? "● NOT SHOWN TO NEIGHBOURS"
            : "● PUBLISHING TO TOWN... " + publication.Reason, "item-meta"));
        info.Add(sub);
        head.Add(info);

        var buttons = V("listing-buttons");
        if (npcPending) buttons.Add(L("Waiting for Town host…", "item-meta"));
        else
        {
            if (!EbaeNeighbours.UsesHostNpc || publication.State == EbaePublicationState.Live)
                buttons.Add(Btn($"Pawn {Economy.Money(EbaeMarket.PawnPrice(l.item))}", "btn-pawn btn-small", () => DoPawnListing(card, l)));
            else buttons.Add(L("Pawn after Live", "item-meta"));
            buttons.Add(Btn("Delist", "btn-delist btn-small", () => DoDelist(card, l)));
        }
        head.Add(buttons);
        card.Add(head);

        if (EbaeNeighbours.UsesHostNpc && publication.State != EbaePublicationState.Live && !npcPending)
            card.Add(L("Want to pawn it now? Delist first, then pawn it from Your Junk.", "item-note"));

        if (town && publication.State == EbaePublicationState.Rejected)
        {
            card.Add(L("Town listing rejected: " + (string.IsNullOrEmpty(publication.Reason) ? "the host did not accept it" : publication.Reason)
                + ". This item remains in your saved listings.", "item-note"));
            if (!npcPending)
            {
                bool badPrice = !EbaeTradingRules.TryPriceCents(l.askingPrice, out _, out _);
                if (badPrice && l.item != null)
                {
                    card.Add(BuildRepriceRow(l));
                    if (l.offers.Count > 0) card.Add(L("Repricing clears existing buyer offers.", "item-meta"));
                }
                else if (l.item != null) card.Add(Btn("Retry publishing", "btn-list btn-small", () => DoRetryPublication(l)));
            }
        }

        var offers = V("offers");
        if (l.neighbourHold) offers.Add(L("a neighbour is buying this right now...", "offers-empty"));
        int fromNeighbours = NeighbourOffersOn(l.id);
        if (fromNeighbours > 0)
            offers.Add(L($"{fromNeighbours} offer{(fromNeighbours == 1 ? "" : "s")} from neighbours - see the Neighbours tab", "offers-empty"));
        if (l.offers.Count == 0) offers.Add(L(WaitingLine(tier, l.counterAt > 0f), "offers-empty"));
        else for (int i = l.offers.Count - 1; i >= 0; i--) offers.Add(BuildOfferRow(l, l.offers[i]));
        card.Add(offers);

        PopInOnce(card, "listing:" + l.id, "card--pop");
        return card;
    }

    VisualElement BuildRepriceRow(EbaeListing l)
    {
        var row = V("custom-row");
        var field = new TextField { maxLength = 10 };
        field.AddToClassList("price-field");
        field.textEdition.placeholder = "new price $0.50–$50,000";
        field.SetValueWithoutNotify(repriceText.TryGetValue(l.id, out var typed) ? typed : "");
        field.RegisterValueChangedCallback(e => repriceText[l.id] = e.newValue);
        row.Add(field);
        row.Add(Btn("Reprice", "btn-list btn-small", () => DoReprice(l, field)));
        return row;
    }

    void DoReprice(EbaeListing l, TextField field)
    {
        if (!EbaeTradingRules.TryParsePrice(field.value, out long cents, out string error))
        {
            Sfx.Play("buzz");
            Flash(field, "price-field--shake", 120);
            UiToast(error, "bad");
            return;
        }
        if (l.neighbourHold || (EbaeNeighbours.UsesHostNpc && EbaeNeighbours.Instance.IsNpcPending(l.id)))
        {
            Sfx.Play("buzz");
            UiToast("A sale is in progress. Wait for it to finish before repricing.", "bad");
            return;
        }
        if (!EbaeMarket.Reprice(l.id, cents / 100f))
        {
            Sfx.Play("buzz");
            UiToast("Couldn't reprice that listing. It may be in a sale.", "bad");
            MarkDirty();
            return;
        }
        repriceText.Remove(l.id);
        Sfx.Play("ding");
        UiToast($"Repriced at {Economy.Money(cents / 100f)}. Publishing the update...", "good");
        MarkDirty();
    }

    void DoRetryPublication(EbaeListing l)
    {
        if (!EbaeNeighbours.Active) return;
        EbaeNeighbours.Instance.RetryPublication(l.id);
        UiToast("Retrying publication. Check this card for the host's result.", "info");
        MarkDirty();
    }

    static string WaitingLine(EbaeGreed tier, bool counterPending)
    {
        if (counterPending) return "someone is typing...";
        return tier switch
        {
            EbaeGreed.Steal => "buyers are sprinting over. hold on to your hat...",
            EbaeGreed.Fair => "no offers yet. someone is typing...",
            EbaeGreed.Greedy => "crickets. maybe that price is a bit much?",
            _ => "buyers are laughing at this listing in the group chat.",
        };
    }

    VisualElement BuildOfferRow(EbaeListing l, EbaeOffer o)
    {
        bool full = o.amount >= l.askingPrice - 0.001f;
        var row = V("offer-row");
        if (full) row.AddToClassList("offer-row--asking");
        if (o.isJoke) row.AddToClassList("offer-row--joke");
        if (o.isSpecial) row.AddToClassList("offer-row--special");

        string buyer = string.IsNullOrEmpty(o.buyer) ? "anonymous" : o.buyer;
        row.Add(L(buyer.Substring(0, 1).ToUpperInvariant(), "avatar avatar--" + (StableHash(buyer) % 6)));

        var body = V("offer-body");
        var nameRow = V("buyer-row");
        nameRow.Add(L(buyer, "buyer-name"));
        if (full) nameRow.Add(L("FULL PRICE", "buyer-flair buyer-flair--asking"));
        else if (o.isCounter) nameRow.Add(L("came back!", "buyer-flair"));
        else if (o.isJoke) nameRow.Add(L("lol", "buyer-flair"));
        if (o.isSpecial) nameRow.Add(L("interested party", "buyer-flair buyer-flair--special"));
        body.Add(nameRow);
        body.Add(L($"\"{o.message}\"", "buyer-message"));
        row.Add(body);

        var amount = V("offer-amount");
        amount.Add(L(Economy.Money(o.amount), "offer-gross"));
        amount.Add(L($"you get {Economy.Money(EbaeMarket.NetAfterFee(o.amount))}", "offer-net"));
        row.Add(amount);

        var timer = V("offer-timer");
        var time = L("", "offer-time");
        var bar = V("timer-bar");
        var fill = V("timer-fill");
        bar.Add(fill);
        timer.Add(time);
        timer.Add(bar);
        row.Add(timer);

        if (EbaeNeighbours.UsesHostNpc && EbaeNeighbours.Instance.IsNpcPending(l.id))
            row.Add(L("Waiting for Town host…", "item-meta"));
        else
        {
            row.Add(Btn("Accept", "btn-accept", () => DoAccept(row, l, o)));
            row.Add(Btn("Decline", "btn-decline", () => DoDecline(row, o)));
        }

        offerViews.Add(new OfferView { offer = o, time = time, fill = fill });
        if (seenOffers.Add(o.id)) PopIn(row, "offer-row--pop");
        return row;
    }

    void DoAccept(VisualElement row, EbaeListing l, EbaeOffer o)
    {
        if (row.ClassListContains("offer-row--leaving")) return;
        if (EbaeNeighbours.UsesHostNpc)
        {
            if (EbaeNeighbours.Instance.RequestNpcAccept(o.id))
                UiToast("Waiting for Town host…", "info");
            else { Sfx.Play("buzz"); UiToast("Couldn't submit that offer. It may have expired.", "bad"); }
            MarkDirty();
            return;
        }
        if (!EbaeMarket.Accept(o.id, out var sale))
        {
            Sfx.Play("buzz");
            UiToast("too slow! that offer is gone", "bad");
            MarkDirty();
            return;
        }
        CelebrateSale(sale, false);
        bool robbed = sale.price < Mathf.Max(0.5f, EbaeMarket.Appraise(sale.item)) * 0.25f;
        UiToast(robbed
            ? $"you sold the {sale.itemName} for {Economy.Money(sale.price)}. {sale.buyer} can't believe it."
            : $"{sale.buyer} bought your {sale.itemName}! feedback: \"{sale.review}\"", "good");
    }

    void DoDecline(VisualElement row, EbaeOffer o)
    {
        if (EbaeNeighbours.UsesHostNpc)
        {
            if (EbaeNeighbours.Instance.RequestNpcDecline(o.id))
                UiToast("Waiting for Town host…", "info");
            else { Sfx.Play("buzz"); UiToast("Couldn't decline that offer. It may have expired.", "bad"); }
            MarkDirty();
            return;
        }
        if (!BeginLeaving(row, "offer-row--leaving")) return;
        Sfx.Play("squeak", null, 0.7f);
        After(170, () =>
        {
            if (EbaeMarket.Decline(o.id, out var reply) && !string.IsNullOrEmpty(reply))
                UiToast($"{o.buyer}: \"{reply}\"", "bad");
        });
    }

    void DoDelist(VisualElement card, EbaeListing l)
    {
        if (EbaeNeighbours.UsesHostNpc && EbaeNeighbours.Instance.IsNpcPending(l.id))
        {
            Sfx.Play("buzz");
            UiToast("A sale is being confirmed. Wait before delisting.", "bad");
            return;
        }
        if (!BeginLeaving(card, "card--leaving")) return;
        Sfx.Play("whoosh", null, 1f, 0.9f);
        string name = l.ItemName;
        After(180, () =>
        {
            if (EbaeMarket.Delist(l.id)) UiToast($"{name} is back in Your Junk. the buyers are devastated.", "info");
            else MarkDirty();
        });
    }

    void DoPawnListing(VisualElement card, EbaeListing l)
    {
        if (EbaeNeighbours.UsesHostNpc)
        {
            if (EbaeNeighbours.Instance.PublicationStatus(l.id).State != EbaePublicationState.Live)
            {
                Sfx.Play("buzz");
                UiToast("This listing isn't live to neighbours. Wait or delist it to pawn from Your Junk.", "bad");
                return;
            }
            string uid = l.item != null ? l.item.uid : null;
            if (!string.IsNullOrEmpty(uid) && EbaeNeighbours.Instance.RequestNpcPawn(uid))
                UiToast("Waiting for Town host…", "info");
            else { Sfx.Play("buzz"); UiToast("Couldn't submit that pawn sale. Check the listing and try again.", "bad"); }
            MarkDirty();
            return;
        }
        if (!BeginLeaving(card, "card--leaving")) return;
        Sfx.Play("whoosh");
        After(180, () =>
        {
            if (!EbaeMarket.PawnNow(l.item != null ? l.item.uid : l.id, out var sale)) { MarkDirty(); return; }
            CelebrateSale(sale, true);
            UiToast($"Pawn Pete: \"{sale.review}\"", "good");
        });
    }

    // ------------------------------------------------------------------ tab: sold

    void BuildSold()
    {
        var history = EbaeMarket.History;
        content.Add(SectionHead("Sold", history.Count == 0 ? null : "look at you. a real businessperson."));

        var stats = V("stats-row");
        stats.Add(Stat("total earned", Economy.Money(EbaeMarket.TotalEarned), true));
        stats.Add(Stat("things sold", EbaeMarket.TotalSales.ToString(), false));
        stats.Add(Stat("best sale", EbaeMarket.TotalSales == 0 ? "-" : $"{Economy.Money(EbaeMarket.BestSalePrice)} ({EbaeMarket.BestSaleName})", false));
        content.Add(stats);

        if (history.Count == 0)
        {
            content.Add(Empty("$ ?", "Nothing sold yet.", "Capitalism awaits."));
            return;
        }

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var s = history[i];
            if (s == null) continue;
            var row = V("sold-row");
            if (s.item != null) row.Add(Icon(s.item, true));
            row.Add(L(s.pawned ? "PAWNED" : "SOLD", "sold-stamp" + (s.pawned ? " sold-stamp--pawn" : "")));
            var body = V("sold-body");
            var title = V("title-row");
            title.Add(L(RarityInfo.Label(s.rarity), "rarity-chip rar--" + s.rarity.ToString().ToLowerInvariant()));
            title.Add(L(s.itemName, "item-name"));
            body.Add(title);
            body.Add(L($"to {s.buyer} · day {s.day}", "item-meta"));
            if (!string.IsNullOrEmpty(s.review)) body.Add(L($"\"{s.review}\"", "sold-review"));
            row.Add(body);
            var price = V("sold-price");
            price.Add(L("+" + Economy.Money(s.net), "offer-gross"));
            price.Add(L(s.pawned ? "pawned, no fee" : $"sold for {Economy.Money(s.price)}, ebae took {Economy.Money(s.price - s.net)}", "offer-net"));
            row.Add(price);
            content.Add(row);
        }
    }

    static VisualElement Stat(string caption, string value, bool money)
    {
        var s = V("stat" + (money ? " stat--money" : ""));
        s.Add(L(caption, "stat-caption"));
        s.Add(L(value, "stat-value" + (money ? " stat-value--money" : "")));
        return s;
    }

    // ------------------------------------------------------------------ market events

    void OnOfferArrived(EbaeListing l, EbaeOffer o)
    {
        dirty = true;
        if (!isOpen) return;
        bool full = o.amount >= l.askingPrice - 0.001f;
        Sfx.Play("notify");
        if (o.isSpecial || full) Sfx.Play("sparkle", null, 0.6f);
        UiToast(o.isJoke ? $"{o.buyer} offered {Economy.Money(o.amount)} for your {l.ItemName}. seriously."
                         : $"{o.buyer} offered {Economy.Money(o.amount)} for your {l.ItemName}!", "offer");
        Flash(tabListings, "tab--pulse", 300);
        RefreshAppTabs();
        if (app != LaptopApp.Ebae) Flash(appTabEbae, "app-tab--pulse", 300);
        LogoHop();
    }

    void OnOfferExpired(EbaeListing l, EbaeOffer o)
    {
        dirty = true;
        if (!isOpen || tab != Tab.Listings) return;
        Sfx.Play("pop", null, 0.4f, 0.7f);
        UiToast($"{o.buyer} got bored and left", "info");
    }

    // ------------------------------------------------------------------ juice

    void CelebrateSale(EbaeSale sale, bool pawned)
    {
        bool big = sale.price >= 500f || sale.rarity >= Rarity.Epic;
        bool robbed = !pawned && sale.price < Mathf.Max(0.5f, EbaeMarket.Appraise(sale.item)) * 0.25f;
        Sfx.Play("cash");
        Sfx.Play(big ? "legendary" : "ding", null, 0.8f, 1.1f);
        Stamp(pawned ? "PAWNED" : robbed ? "SOLD...?" : "SOLD!", "+" + Economy.Money(sale.net), pawned);
        Confetti(big ? 80 : pawned ? 24 : 40, pawned);
        Juice.Shake(big ? 0.25f : 0.1f, 0.2f);
        LogoHop();
    }

    void LogoHop()
    {
        for (int i = 0; i < logoLetters.Count; i++)
        {
            var letter = logoLetters[i];
            letter.schedule.Execute(() => letter.AddToClassList("logo-letter--hop")).ExecuteLater(i * 70);
            letter.schedule.Execute(() => letter.RemoveFromClassList("logo-letter--hop")).ExecuteLater(i * 70 + 150);
        }
    }

    void Stamp(string text, string sub, bool pawn) => Stamp(text, sub, pawn ? "pawn" : null);

    void Stamp(string text, string sub, string variant)
    {
        if (fxLayer == null) return;
        currentStamp?.RemoveFromHierarchy();
        var box = V("stamp-box");
        box.pickingMode = PickingMode.Ignore;
        var t = L(text, "stamp-text" + (string.IsNullOrEmpty(variant) ? "" : " stamp-text--" + variant));
        t.pickingMode = PickingMode.Ignore;
        box.Add(t);
        if (!string.IsNullOrEmpty(sub))
        {
            var s = L(sub, "stamp-sub");
            s.pickingMode = PickingMode.Ignore;
            box.Add(s);
        }
        fxLayer.Add(box);
        currentStamp = box;
        fxLayer.schedule.Execute(() => box.AddToClassList("stamp-box--slam")).ExecuteLater(30);
        fxLayer.schedule.Execute(() => { if (isOpen) Sfx.Play("slam", null, 0.5f, 1.2f); }).ExecuteLater(200);
        fxLayer.schedule.Execute(() => box.AddToClassList("stamp-box--fade")).ExecuteLater(1300);
        fxLayer.schedule.Execute(() =>
        {
            box.RemoveFromHierarchy();
            if (currentStamp == box) currentStamp = null;
        }).ExecuteLater(1900);
    }

    static readonly int[] PaletteParty = { 0, 1, 2, 3, 4 };
    static readonly int[] PaletteGold = { 2, 5 };

    void Confetti(int count, bool gold) => Confetti(count, gold ? PaletteGold : PaletteParty);

    void Confetti(int count, int[] palette)
    {
        if (fxLayer == null) return;
        if (palette == null || palette.Length == 0) palette = PaletteParty;
        float w = fxLayer.layout.width, h = fxLayer.layout.height;
        if (float.IsNaN(w) || w < 10f) { w = 1200f; h = 800f; }
        var origin = new Vector2(w * 0.5f, h * 0.42f);
        for (int i = 0; i < count; i++)
        {
            int color = palette[Random.Range(0, palette.Length)];
            var el = V("confetto confetto--" + color);
            el.pickingMode = PickingMode.Ignore;
            el.style.left = origin.x;
            el.style.top = origin.y;
            fxLayer.Add(el);
            float ang = Random.Range(0f, Mathf.PI * 2f);
            float speed = Random.Range(300f, 1000f);
            confetti.Add(new Confetto
            {
                el = el,
                pos = Vector2.zero,
                vel = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang) - 0.9f) * speed,
                rot = Random.Range(0f, 360f),
                spin = Random.Range(-900f, 900f),
                life = Random.Range(1.1f, 1.9f),
            });
        }
    }

    void TickConfetti()
    {
        if (confetti.Count == 0) return;
        float dt = Time.unscaledDeltaTime;
        for (int i = confetti.Count - 1; i >= 0; i--)
        {
            var c = confetti[i];
            c.age += dt;
            if (c.age >= c.life)
            {
                c.el.RemoveFromHierarchy();
                confetti.RemoveAt(i);
                continue;
            }
            c.vel.y += 1500f * dt;
            c.vel *= 1f - Mathf.Min(0.9f, 1.3f * dt);
            c.pos += c.vel * dt;
            c.rot += c.spin * dt;
            c.el.style.translate = new Translate(c.pos.x, c.pos.y);
            c.el.style.rotate = new Rotate(new Angle(c.rot, AngleUnit.Degree));
            c.el.style.opacity = Mathf.Clamp01((c.life - c.age) / 0.35f);
        }
    }

    void ClearFx()
    {
        foreach (var c in confetti) c.el.RemoveFromHierarchy();
        confetti.Clear();
        currentStamp?.RemoveFromHierarchy();
        currentStamp = null;
        toastLayer?.Clear();
    }

    void UiToast(string text, string kind)
    {
        if (toastLayer == null || !isOpen) return;
        while (toastLayer.childCount >= 4) toastLayer.RemoveAt(0);
        var t = L(text, "ui-toast ui-toast--" + kind + " ui-toast--enter");
        t.pickingMode = PickingMode.Ignore;
        toastLayer.Add(t);
        toastLayer.schedule.Execute(() => t.RemoveFromClassList("ui-toast--enter")).ExecuteLater(30);
        toastLayer.schedule.Execute(() => t.AddToClassList("ui-toast--leave")).ExecuteLater(3400);
        toastLayer.schedule.Execute(() => t.RemoveFromHierarchy()).ExecuteLater(3800);
    }

    void TickOfferTimers()
    {
        for (int i = 0; i < offerViews.Count; i++)
        {
            var v = offerViews[i];
            float left = v.offer.TimeLeft;
            int sec = Mathf.CeilToInt(left);
            if (sec != v.lastSecond)
            {
                v.lastSecond = sec;
                v.time.text = sec + "s";
            }
            v.fill.style.width = Length.Percent(v.offer.TimeLeft01 * 100f);
            bool urgent = left < 10f;
            if (urgent != v.urgent)
            {
                v.urgent = urgent;
                v.time.EnableInClassList("offer-time--urgent", urgent);
                v.fill.EnableInClassList("timer-fill--urgent", urgent);
            }
        }
    }

    void TickTicker()
    {
        if (tickerLabel == null || Time.unscaledTime < nextTickerAt) return;
        nextTickerAt = Time.unscaledTime + 6f;
        string next = EbaeContent.Ticker[tickerIndex++ % EbaeContent.Ticker.Length];
        tickerLabel.AddToClassList("ticker-text--swap");
        After(260, () =>
        {
            tickerLabel.text = next;
            tickerLabel.RemoveFromClassList("ticker-text--swap");
        });
    }

    void TickWobble()
    {
        if (shinyIcons.Count == 0 || Time.unscaledTime < nextWobbleAt) return;
        nextWobbleAt = Time.unscaledTime + 0.5f;
        wobble = !wobble;
        foreach (var icon in shinyIcons) icon.EnableInClassList("item-icon--wobble", wobble);
    }

    // ------------------------------------------------------------------ element helpers

    void After(long ms, Action action)
    {
        var host = screen ?? wiredRoot;
        if (host == null) { action(); return; }
        host.schedule.Execute(action).ExecuteLater(ms);
    }

    static void Flash(VisualElement e, string cls, long ms)
    {
        if (e == null) return;
        e.AddToClassList(cls);
        e.schedule.Execute(() => e.RemoveFromClassList(cls)).ExecuteLater(ms);
    }

    /// <summary>Mark an element as on its way out (animates via USS). False if it already is.</summary>
    static bool BeginLeaving(VisualElement e, string cls)
    {
        if (e.ClassListContains(cls)) return false;
        e.AddToClassList(cls);
        e.pickingMode = PickingMode.Ignore;
        return true;
    }

    static void PopIn(VisualElement e, string startClass)
    {
        e.AddToClassList(startClass);
        e.schedule.Execute(() => e.RemoveFromClassList(startClass)).ExecuteLater(30);
    }

    void PopInOnce(VisualElement e, string key, string startClass)
    {
        if (seenCards.Add(key)) PopIn(e, startClass);
    }

    static void OnClick(VisualElement e, Action action)
    {
        if (e == null) return;
        e.focusable = true;
        e.RegisterCallback<FocusInEvent>(_ => e.AddToClassList("controller-focus"));
        e.RegisterCallback<FocusOutEvent>(_ => e.RemoveFromClassList("controller-focus"));
        e.RegisterCallback<NavigationSubmitEvent>(ev =>
        {
            ev.StopPropagation();
            Sfx.Play("click", null, 0.5f);
            action();
        });
        e.AddManipulator(new Clickable(() =>
        {
            Sfx.Play("click", null, 0.5f);
            action();
        }));
    }

    static Label Btn(string text, string classes, Action action)
    {
        var b = L(text, "btn " + classes);
        OnClick(b, action);
        return b;
    }

    static Label L(string text, string classes)
    {
        var l = new Label(text);
        AddClasses(l, classes);
        return l;
    }

    static VisualElement V(string classes)
    {
        var v = new VisualElement();
        AddClasses(v, classes);
        return v;
    }

    static void AddClasses(VisualElement e, string classes)
    {
        if (string.IsNullOrEmpty(classes)) return;
        foreach (var c in classes.Split(' '))
            if (c.Length > 0) e.AddToClassList(c);
    }

    static VisualElement SectionHead(string title, string sub)
    {
        var h = V("section-head");
        h.Add(L(title, "section-title"));
        if (!string.IsNullOrEmpty(sub)) h.Add(L(sub, "section-sub"));
        return h;
    }

    static VisualElement Empty(string art, string title, string sub)
    {
        var e = V("empty");
        e.Add(L(art, "empty-art"));
        e.Add(L(title, "empty-title"));
        e.Add(L(sub, "empty-sub"));
        return e;
    }

    readonly EbaeItemThumbnails itemThumbnails = new EbaeItemThumbnails();

    VisualElement Icon(ItemInstance it, bool small) =>
        Icon(it?.defId, it?.Def != null ? it.Def.rarity : Rarity.Common, EbaeMarket.NameOf(it), small);

    VisualElement Icon(string defId, Rarity rarity, string name, bool small)
    {
        var icon = V("item-icon rar--" + rarity.ToString().ToLowerInvariant() + (small ? " item-icon--small" : ""));
        icon.tooltip = name;
        icon.Add(itemThumbnails.Request(defId, name));
        if (string.IsNullOrEmpty(defId) || ItemCatalog.Get(defId) == null)
            icon.Add(L("NO PHOTO", "item-photo-missing"));
        if (rarity >= Rarity.Epic) shinyIcons.Add(icon);
        return icon;
    }

    static Label RarityChip(ItemInstance it)
    {
        var rarity = it?.Def != null ? it.Def.rarity : Rarity.Common;
        return L(RarityInfo.Label(rarity), "rarity-chip rar--" + rarity.ToString().ToLowerInvariant());
    }

    static string ConditionText(float c)
    {
        string word = c >= 0.95f ? "mint (smells fine)"
                    : c >= 0.8f ? "like new-ish"
                    : c >= 0.6f ? "gently yucky"
                    : c >= 0.4f ? "crusty"
                    : "absolutely cooked";
        return $"{Mathf.RoundToInt(c * 100f)}% · {word}";
    }

    static string FoundText(ItemInstance it)
    {
        string where = string.IsNullOrEmpty(it.foundAt) ? "a mystery bin" : it.foundAt;
        return $"found at {where} · day {Mathf.Max(1, it.dayFound)}";
    }

    static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char ch in s) h = h * 31 + ch;
            return h & 0x7fffffff;
        }
    }
}
