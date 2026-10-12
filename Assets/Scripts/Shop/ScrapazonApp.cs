using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Random = UnityEngine.Random;

/// <summary>
/// Scrapazon, the buy-side app on the shop laptop (Assets/UI/shop/scrapazon.uxml, instanced inside the ebag
/// laptop document as "scrapazonPage"). Hosted by <see cref="EbaeUI"/>, which owns show/hide and the single
/// PlayerController UI lock — switching apps never touches the lock. Purchases go through
/// <see cref="ScrapazonStore"/>; this class is only the storefront + juice.
/// Progression (Docs/Progression.md §9.5 A.4): departments, per-level titles/blurbs/effects, LOCKED chips (rank,
/// prerequisite, Key to the City), a "NEW FOR YOU" ribbon after a promotion, the "Best next buy" shelf,
/// and the Career page (<see cref="ScrapazonCareer"/>).
/// </summary>
public class ScrapazonApp
{
    const float ConfirmAbovePrice = 100f;   // big-ticket items take a second click ("1-Click™ means 2 clicks")
    const float ConfirmSeconds = 3f;

    static bool greetedThisSession;
    static readonly string[] DeliveryJokes =
    {
        "The driver threw it at you.",
        "Doug on a leaf blower says hi.",
        "It came through the window. Don't ask.",
        "Signed for by a raccoon.",
        "It arrived before you clicked. We're sorry.",
        "The smiling box is just packaging.",
    };
    static readonly string[] SearchLines =
    {
        "Search Scrapazon (we sell {N} things)",
        "Search \"is rabies bad\"",
        "Search \"garbage truck but faster\"",
        "Search \"how to get raccoon smell out of everything\"",
        "Search \"how to get promoted at a garbage company\"",
        "Search \"legal to honk at a raccoon?\"",
        "Search \"can raccoons unionize\"",
        "Search \"golden garbage truck price (serious)\"",
    };
    static readonly string[] AlsoBought =
    {
        "Tetanus Shot (not sold here)", "A Second Raccoon", "Nose Plugs (6-pack)", "Therapy (back-ordered)",
        "Industrial Febreze", "A Normal Job", "Bigger Pockets", "Raccoon Translation Guide",
        "Gloves (you should really wear gloves)", "Emotional Support Trash Can", "One Left Shoe (unclaimed)", "A Promotion (not for sale)",
    };

    readonly EbaeUI host;
    readonly VisualElement root;

    ScrollView scroll;
    VisualElement grid, hero, heroCopy, heroArtHost, logo, logoWord, smileHost, cart, alsoRow, navButtons;
    VisualElement resultsBar, also, careerHost, bestShelf, bestRow, magnusPath;
    Label balance, cartCount, heroKicker, heroTitle, heroSub, results, deliverLabel, helloLabel, footer, searchLabel, alsoTitle, searchDept;
    // Hero "Buy now" button for the product the banner is advertising (the URGENT rabies banner especially: at 720p
    // the product cards' own Buy buttons sit below the fold, so the cure was a scroll away while foaming).
    Label heroCta;
    string heroProductId, heroCtaShown;
    bool heroCtaPulse;
    readonly List<Label> logoLetters = new List<Label>();
    readonly Dictionary<string, Label> nav = new Dictionary<string, Label>();
    readonly List<CardView> cards = new List<CardView>();
    readonly HashSet<string> expandedProducts = new HashSet<string>();
    ScrapazonCareer career;

    string filter = "all";
    string builtFilter;
    string lastBoughtName;
    bool active, dirty, bob, wobble;
    float shownCash = float.NaN;
    float nextHeroAt, nextReviewAt, nextBobAt, nextSearchAt, nextWobbleAt;
    int heroIndex, searchIndex;
    string heroSignature, bestSignature, magnusSignature;
    bool heroSticky;

    class CardView
    {
        public ScrapazonItem item;
        public VisualElement card, art, artImage, review, levelRow, lockChip, details;
        public Label detailsToggle;
        public Label owned, whole, cents, sym, listPrice, effect, levelText, stock, buy, reviewText, title, blurb, lockText, newRibbon;
        public VisualElement reviewStars;
        public readonly List<VisualElement> pips = new List<VisualElement>();
        public VisualElement priceRow;
        public int reviewIndex;
        public float confirmUntil;
        public bool celebrating, notified, wasLocked;
        public int lastOwned = -1;
    }

    public bool IsActive => active;

    public static ScrapazonApp Create(EbaeUI host, VisualElement documentRoot)
    {
        var page = documentRoot?.Q<VisualElement>("scrapazonPage");
        if (page == null || page.Q<VisualElement>("szPage") == null) return null;
        return new ScrapazonApp(host, page);
    }

    ScrapazonApp(EbaeUI host, VisualElement root)
    {
        this.host = host;
        this.root = root;
        Wire();
    }

    // ------------------------------------------------------------------ wiring

    void Wire()
    {
        scroll = root.Q<ScrollView>("szScroll");
        grid = root.Q<VisualElement>("szGrid");
        grid?.RegisterCallback<GeometryChangedEvent>(e =>
        {
            grid.EnableInClassList("sz-grid--wide", e.newRect.width >= 1100f);
            grid.EnableInClassList("sz-grid--narrow", e.newRect.width < 660f);
        });
        hero = root.Q<VisualElement>("szHero");
        heroCopy = hero?.Q<VisualElement>(className: "sz-hero-copy");
        heroArtHost = root.Q<VisualElement>("szHeroArtHost");
        logo = root.Q<VisualElement>("szLogo");
        logoWord = root.Q<VisualElement>("szLogoWord");
        smileHost = root.Q<VisualElement>("szLogoSmileHost");
        cart = root.Q<VisualElement>("szCart");
        alsoRow = root.Q<VisualElement>("szAlsoRow");
        also = root.Q<VisualElement>("szAlso");
        navButtons = root.Q<VisualElement>("szNavButtons");
        balance = root.Q<Label>("szBalanceLabel");
        cartCount = root.Q<Label>("szCartCount");
        heroKicker = root.Q<Label>("szHeroKicker");
        heroTitle = root.Q<Label>("szHeroTitle");
        heroSub = root.Q<Label>("szHeroSub");
        results = root.Q<Label>("szResultsLabel");
        resultsBar = root.Q<VisualElement>(className: "sz-results-bar");
        deliverLabel = root.Q<Label>("szDeliverLabel");
        helloLabel = root.Q<Label>("szHelloLabel");
        footer = root.Q<Label>("szFooterLabel");
        searchLabel = root.Q<Label>("szSearchLabel");
        searchDept = root.Q<Label>("szSearchDept");
        alsoTitle = root.Q<Label>("szAlsoTitle");

        if (scroll != null)
        {
            scroll.mode = ScrollViewMode.Vertical;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.mouseWheelScrollSize = 80f;
            scroll.contentContainer.AddToClassList("sz-scroll-content");
        }

        // logo: letters from the (renameable) store name + the smile arrow
        if (logoWord != null)
        {
            logoWord.Clear();
            logoLetters.Clear();
            foreach (char ch in Scrapazon.Name.ToLowerInvariant())
            {
                var l = L(ch.ToString(), "sz-logo-letter");
                l.pickingMode = PickingMode.Ignore;
                logoWord.Add(l);
                logoLetters.Add(l);
            }
        }
        smileHost?.Add(ScrapazonArt.Smile());
        root.Q<VisualElement>("szSearchButton")?.Add(ScrapazonArt.Magnifier());
        root.Q<VisualElement>("szCartIconHost")?.Add(ScrapazonArt.Cart());
        heroArtHost?.Insert(0, ScrapazonArt.HeroBox());
        if (hero != null)
        {
            heroCta = Btn("", "sz-hero-cta sz-hero-cta--hidden", OnHeroCta);
            int at = heroArtHost != null ? hero.IndexOf(heroArtHost) : -1;
            if (at >= 0) hero.Insert(at, heroCta); else hero.Add(heroCta);
        }

        // "Best next buy" shelf (All department) and the Career page host, both built in code next to the grid
        if (grid != null && grid.parent != null)
        {
            var content = grid.parent;
            bestShelf = V("sz-best-shelf");
            var shelfHead = V("sz-best-head");
            shelfHead.Add(L("Best next buy for you", "sz-best-heading"));
            shelfHead.Add(L("(picked by our algorithm, which is a raccoon)", "sz-best-sub"));
            bestShelf.Add(shelfHead);
            bestRow = V("sz-best-row");
            bestShelf.Add(bestRow);
            content.Insert(content.IndexOf(grid), bestShelf);

            careerHost = V("sz-career");
            careerHost.style.display = DisplayStyle.None;
            content.Insert(content.IndexOf(grid), careerHost);

            // Magnus department: his career path (step N of 9, can now / next), above his products
            magnusPath = V("sz-mc");
            magnusPath.style.display = DisplayStyle.None;
            content.Insert(content.IndexOf(grid), magnusPath);
            career = new ScrapazonCareer(careerHost, (t, k) => host.LaptopToast(t, k), OpenDept, ShowAndBuy);
        }

        if (navButtons != null)
        {
            navButtons.Clear();
            nav.Clear();
            foreach (var (tag, label) in ScrapazonCatalog.Departments)
            {
                string t = tag;
                var b = Btn(label, "sz-nav-btn" + (tag == ScrapazonCatalog.CareerTag ? " sz-nav-btn--career" : ""), () => SetFilter(t, true));
                navButtons.Add(b);
                nav[tag] = b;
            }
        }

        OnClick(logo, () => { LogoHop(); SetFilter("all", true); });
        OnClick(root.Q<VisualElement>("szSearchButton"), () =>
        {
            Sfx.Play("squeak", null, 0.6f, 1.2f);
            host.LaptopToast("search is down. a raccoon ate the index.", "info");
            NextSearchLine();
        });
        OnClick(root.Q<VisualElement>("szDeliver"), () => host.LaptopToast("Upgrades install automatically. Supplies go straight into your inventory. Nothing to unpack.", "info"));
        OnClick(root.Q<VisualElement>("szAccount"), () => SetFilter(ScrapazonCatalog.CareerTag, true));
        OnClick(root.Q<VisualElement>("szReturns"), () => host.LaptopToast("Returns: no. Orders: all of them, instantly.", "info"));
        OnClick(cart, () =>
        {
            Flash(cart, "sz-cart--bump", 180);
            host.LaptopToast($"we don't do carts. everything on {Scrapazon.Name} is 1-Click™. no take-backs.", "info");
        });
        OnClick(root.Q<VisualElement>("szBackToTop"), () =>
        {
            if (scroll != null) scroll.scrollOffset = Vector2.zero;
            Sfx.Play("whoosh", null, 0.6f, 1.3f);
        });

        if (footer != null)
            footer.text = $"{Scrapazon.Name} (c) 1997-2026 · all sales final · Prime delivery by a guy named Doug on a leaf blower · " +
                          $"{Scrapazon.Name} is not responsible for bites, bonks, moths, gnomes or feelings";
        if (deliverLabel != null) deliverLabel.text = "Auto-installed";
        root.style.display = DisplayStyle.None;
        builtFilter = null;
    }

    // ------------------------------------------------------------------ activation

    /// <summary>Show (true) or hide (false) the Scrapazon page. The host calls this on every open / app switch / close.</summary>
    public void SetActive(bool on)
    {
        if (!on)
        {
            active = false;
            root.style.display = DisplayStyle.None;
            OnLaptopClosed();
            return;
        }
        bool fresh = !active;
        active = true;
        root.style.display = DisplayStyle.Flex;
        OnShown(fresh);
    }

    /// <summary>The laptop closed (or switched away).</summary>
    public void OnLaptopClosed()
    {
        foreach (var v in cards) v.confirmUntil = 0f;
        // "NEW FOR YOU" ribbons last for the visit after a promotion
        try { ScrapazonAdvisor.MarkSeen(); } catch (Exception e) { Debug.LogException(e); }
    }

    void OnShown(bool fresh)
    {
        host.SetAddress(AddressFor(filter));
        shownCash = float.NaN;
        RefreshCash(true);
        if (builtFilter != filter || fresh) BuildCards(false);
        RefreshStates();
        RefreshNav();
        BuildAlso();
        RefreshHeader();
        nextHeroAt = 0f;
        nextReviewAt = Time.unscaledTime + 7f;
        heroSignature = null;
        TickHero(true);
        NextSearchLine();
        if (!fresh) return;
        LogoHop();
        Sfx.Play("pop", null, 0.7f, 1.4f);

        bool needShot = GameSession.HasRabies && ShopEffects.RabiesShots == 0;
        int fresh4u = ScrapazonAdvisor.NewForYouCount();
        if (needShot)
        {
            var shot = ScrapazonCatalog.Get(ShopIds.RabiesShot);
            if (filter != "all" && shot != null && !shot.HasTag(filter)) SetFilter("all", false);
            Spotlight(ShopIds.RabiesShot);
            host.LaptopToast("you look... foamy. the Rabies Shot is right here. (Pharmacy aisle)", "bad");
        }
        else if (fresh4u > 0)
        {
            if (filter == ScrapazonCatalog.CareerTag) SetFilter("all", false);
            host.LaptopToast($"congrats on the promotion! {fresh4u} new product{(fresh4u == 1 ? "" : "s")} unlocked for you. look for the NEW FOR YOU ribbons.", "sz");
        }
        else if (!greetedThisSession)
            host.LaptopToast($"welcome to {Scrapazon.Name}! everything ships with Prime: delivered in 0.3 seconds.", "sz");
        greetedThisSession = true;
    }

    public void MarkDirty() => dirty = true;

    // ------------------------------------------------------------------ per-frame

    public void Tick()
    {
        if (!active) return;
        float now = Time.unscaledTime;
        if (dirty)
        {
            dirty = false;
            if (filter == ScrapazonCatalog.CareerTag) career?.RefreshThrottled();
            else { RefreshStates(); RefreshBestShelf(false); RefreshMagnusPath(false); }
            RefreshHeader();
        }
        RefreshCash(false);

        foreach (var v in cards)
            if (v.confirmUntil > 0f && now > v.confirmUntil) { v.confirmUntil = 0f; UpdateCard(v); }

        TickHero(false);
        RefreshHeroCta();

        if (now >= nextReviewAt)
        {
            nextReviewAt = now + 7f;
            RotateReviews();
        }
        if (now >= nextBobAt && heroArtHost != null)
        {
            nextBobAt = now + 0.65f;
            bob = !bob;
            heroArtHost.EnableInClassList("sz-hero-art-host--bob", bob);
        }
        if (now >= nextWobbleAt)
        {
            nextWobbleAt = now + 0.5f;
            wobble = !wobble;
            if (heroCta != null)
            {
                bool pulse = wobble && hero != null && hero.ClassListContains("sz-hero--alert");
                if (pulse != heroCtaPulse) { heroCtaPulse = pulse; heroCta.EnableInClassList("sz-hero-cta--pulse", pulse); }
            }
            foreach (var v in cards)
            {
                if (v.card.ClassListContains("sz-card--spotlight") && v.artImage != null)
                    v.artImage.EnableInClassList("sz-art--wobble", wobble);
                v.newRibbon?.EnableInClassList("sz-new-ribbon--wiggle", wobble);
            }
            if (nav.TryGetValue(ScrapazonCatalog.CareerTag, out var careerBtn))
                careerBtn.EnableInClassList("sz-nav-btn--glow", wobble && HasCareerNews());
        }
        if (now >= nextSearchAt) NextSearchLine();
    }

    static bool HasCareerNews()
    {
        foreach (var w in WorkOrders.Today) if (w.done) return true;
        return Progression.WaitingOnCleanDays;
    }

    // ------------------------------------------------------------------ building

    void SetFilter(string tag, bool fromClick)
    {
        if (tag == filter && builtFilter == filter)
        {
            if (fromClick && nav.TryGetValue(tag, out var same)) Flash(same, "sz-nav-btn--pulse", 150);
            return;
        }
        filter = tag;
        if (fromClick) Sfx.Play("whoosh", null, 0.5f, 1.4f);
        RefreshNav();
        BuildCards(true);
        RefreshStates();
        host.SetAddress(AddressFor(filter));
        if (scroll != null) scroll.scrollOffset = Vector2.zero;
    }

    void OpenDept(string dept)
    {
        if (string.IsNullOrEmpty(dept)) return;
        SetFilter(dept, true);
    }

    /// <summary>Jump to a product from the Career page / best-buy shelf and press its buy button (same flow as the card).</summary>
    void ShowAndBuy(string id)
    {
        var v = FindCard(id);
        if (v == null)
        {
            SetFilter("all", false);
            v = FindCard(id);
        }
        if (v == null) return;
        Spotlight(id);
        if (scroll != null && v.card != null) v.card.schedule.Execute(() => scroll.ScrollTo(v.card)).ExecuteLater(60);
        OnBuy(v);
    }

    void RefreshNav()
    {
        foreach (var kv in nav) kv.Value.EnableInClassList("sz-nav-btn--active", kv.Key == filter);
        if (searchDept != null)
        {
            string label = "All";
            foreach (var (tag, l) in ScrapazonCatalog.Departments) if (tag == filter) label = l;
            searchDept.text = label;
        }
    }

    static string AddressFor(string tag) =>
        Scrapazon.Domain + (tag == "all" ? "/s?k=stuff&ref=garbage"
                          : tag == ScrapazonCatalog.CareerTag ? "/your-account/career?employee=you&raise=denied"
                          : "/b?dept=" + tag + "&sort=featured_by_money");

    void BuildCards(bool animate)
    {
        if (grid == null) return;
        grid.Clear();
        cards.Clear();
        builtFilter = filter;

        bool careerPage = filter == ScrapazonCatalog.CareerTag;
        grid.style.display = careerPage ? DisplayStyle.None : DisplayStyle.Flex;
        if (resultsBar != null) resultsBar.style.display = careerPage ? DisplayStyle.None : DisplayStyle.Flex;
        if (also != null) also.style.display = careerPage ? DisplayStyle.None : DisplayStyle.Flex;
        if (careerHost != null) careerHost.style.display = careerPage ? DisplayStyle.Flex : DisplayStyle.None;
        if (careerPage)
        {
            if (bestShelf != null) bestShelf.style.display = DisplayStyle.None;
            if (magnusPath != null) magnusPath.style.display = DisplayStyle.None;
            career?.Build(true);
            return;
        }
        RefreshBestShelf(true);
        RefreshMagnusPath(true);

        var items = SortedItems(filter);
        // Personal gear left the shop for SolidWaste 3D's blueprint tree: the Gear aisle says where it went.
        if (filter == "gear") grid.Add(GearMovedNotice());

        int index = 0;
        foreach (var it in items)
        {
            var v = BuildCard(it, index);
            cards.Add(v);
            grid.Add(v.card);
            if (animate || !greetedThisSession)
            {
                var c = v.card;
                c.AddToClassList("sz-card--pop");
                c.schedule.Execute(() => c.RemoveFromClassList("sz-card--pop")).ExecuteLater(30 + Mathf.Min(index, 18) * 40);
            }
            index++;
        }
        string dept = "stuff";
        foreach (var (tag, label) in ScrapazonCatalog.Departments) if (tag == filter && tag != "all") dept = label;
        if (results != null) results.text = index == 0 ? "no results. try being less specific." : $"1-{index} of {index} results for \"{dept}\"";
    }

    /// <summary>
    /// The department's cards in shop order: buyable first, then locked (by rank), then installed, teasers last, so the
    /// shop reads as "what can I get now". The Magnus department follows his career path instead (next step first).
    /// </summary>
    static List<ScrapazonItem> SortedItems(string tag)
    {
        var items = new List<ScrapazonItem>();
        foreach (var it in ScrapazonCatalog.All)
            if (tag == "all" || it.HasTag(tag)) items.Add(it);
        var order = new Dictionary<ScrapazonItem, int>();
        for (int i = 0; i < items.Count; i++) order[items[i]] = i;
        bool magnusDept = tag == "magnus";
        items.Sort((a, b) =>
        {
            // Magnus department: his career path order (next step first, installed parts last)
            if (magnusDept)
            {
                int ma = MagnusOrder(a), mb = MagnusOrder(b);
                if (ma != mb) return ma.CompareTo(mb);
            }
            int ga = Group(a), gb = Group(b);
            if (ga != gb) return ga.CompareTo(gb);
            if (ga == 1)
            {
                int ra = a.RankFor(ScrapazonStore.NextLevel(a)), rb = b.RankFor(ScrapazonStore.NextLevel(b));
                if (ra != rb) return ra.CompareTo(rb);
            }
            return order[a].CompareTo(order[b]);
        });
        return items;
    }

    /// <summary>After a Magnus purchase the next step's card moves to the front: rebuild the grid when the shown order is
    /// stale (other departments keep their cards in place, so a buy never yanks the card out from under the cursor).</summary>
    void ResortMagnusIfStale()
    {
        if (!active || filter != "magnus" || builtFilter != "magnus") return;
        var want = SortedItems(filter);
        bool stale = want.Count != cards.Count;
        for (int i = 0; !stale && i < want.Count; i++) stale = cards[i].item != want[i];
        if (!stale) return;
        float keep = scroll != null ? scroll.scrollOffset.y : 0f;
        BuildCards(false);
        RefreshStates();
        if (scroll != null) scroll.scrollOffset = new Vector2(0f, keep);   // stay where you were looking
    }

    /// <summary>Career order for the Magnus department: the step a card would buy next; installed parts, then others last.</summary>
    static int MagnusOrder(ScrapazonItem it)
    {
        if (it.IsTeaser) return 1000;
        if (!MagnusCareer.IsCareerItem(it.id)) return 500;
        int n = MagnusCareer.NextStepNumberFor(it);
        return n > 0 ? n : 100;
    }

    /// <summary>The Magnus career strip: only in the Magnus department; rebuilt when its signature changes.</summary>
    void RefreshMagnusPath(bool force)
    {
        if (magnusPath == null) return;
        bool show = filter == "magnus";
        magnusPath.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        if (!show) return;
        string sig = ScrapazonMagnusPath.Signature();
        if (!force && sig == magnusSignature) return;
        magnusSignature = sig;
        try { ScrapazonMagnusPath.Build(magnusPath, ShowAndBuy, (t, k) => host.LaptopToast(t, k)); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>0 = buyable now, 1 = locked, 2 = maxed, 3 = teaser.</summary>
    static int Group(ScrapazonItem it)
    {
        if (it.IsTeaser) return 3;
        if (it.IsUpgrade && ScrapazonStore.IsMaxed(it)) return 2;
        return ScrapazonStore.IsLocked(it) ? 1 : 0;
    }

    void RefreshBestShelf(bool force)
    {
        if (bestShelf == null || bestRow == null) return;
        bool show = filter == "all";
        bestShelf.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        if (!show) return;
        var list = ScrapazonAdvisor.BestNextBuys(3);
        var sb = new System.Text.StringBuilder();
        foreach (var s in list) sb.Append(s.item.id).Append(s.level).Append(s.affordable).Append(Mathf.RoundToInt(s.shortfall)).Append('|');
        string sig = sb.ToString();
        if (!force && sig == bestSignature) return;
        bestSignature = sig;
        bestRow.Clear();
        if (list.Count == 0)
        {
            bestRow.Add(L("Nothing to buy right now. Earn Rep at the curb to unlock more (Career page).", "sz-best-empty"));
            return;
        }
        foreach (var s in list) bestRow.Add(ScrapazonCareer.BestBuyTile(s, ShowAndBuy));
    }

    /// <summary>A full-width shelf note at the top of the Gear department (not a product: no card, no buy button).</summary>
    static VisualElement GearMovedNotice()
    {
        var note = Ignore(new VisualElement { name = "szGearMoved" });
        note.style.width = Length.Percent(99f);
        note.style.flexDirection = FlexDirection.Row;
        note.style.alignItems = Align.Center;
        note.style.marginBottom = 10f;
        note.style.paddingLeft = note.style.paddingRight = 12f;
        note.style.paddingTop = note.style.paddingBottom = 8f;
        note.style.backgroundColor = (Color)new Color32(0xFF, 0xF8, 0xE1, 0xFF);
        note.style.borderLeftWidth = 6f;
        note.style.borderLeftColor = ScrapazonArt.Orange;
        note.style.borderTopLeftRadius = note.style.borderBottomLeftRadius = 4f;
        note.style.borderTopRightRadius = note.style.borderBottomRightRadius = 8f;
        foreach (string id in new[] { ShopIds.WorkGloves, ShopIds.WorkBoots, ShopIds.Headlamp })
        {
            var art = Ignore(ScrapazonArt.Product(id));
            // .sz-art is an absolute fill inside a product tile; here it is a small inline thumbnail
            art.style.position = Position.Relative;
            art.style.left = art.style.top = art.style.right = art.style.bottom = StyleKeyword.Auto;
            art.style.width = 60f; art.style.height = 42f; art.style.flexShrink = 0f; art.style.marginRight = 4f;
            note.Add(art);
        }
        var text = Ignore(new VisualElement());
        text.style.flexGrow = 1f; text.style.flexShrink = 1f; text.style.marginLeft = 8f;
        var title = Ignore(new Label(UiText.ScrapazonGearMovedTitle));
        title.style.unityFontStyleAndWeight = FontStyle.Bold; title.style.fontSize = 15f; title.style.whiteSpace = WhiteSpace.Normal;
        title.style.color = ScrapazonArt.Ink;
        var body = Ignore(new Label(UiText.ScrapazonGearMovedBody));
        body.style.fontSize = 13f; body.style.whiteSpace = WhiteSpace.Normal; body.style.color = ScrapazonArt.Ink;
        text.Add(title); text.Add(body);
        note.Add(text);
        return note;
    }

    CardView BuildCard(ScrapazonItem it, int index)
    {
        var v = new CardView { item = it, reviewIndex = 0 };
        var card = V("sz-card " + (index % 2 == 0 ? "sz-card--tilt-l" : "sz-card--tilt-r"));
        if (it.IsTeaser) card.AddToClassList("sz-card--teaser");
        if (it.id == ShopIds.GoldenTruck) card.AddToClassList("sz-card--gold");
        if (it.dept == "permits") card.AddToClassList("sz-card--permit");
        v.card = card;
        var head = V("sz-product-head");
        card.Add(head);

        // art tile
        var art = V("sz-card-art");
        v.artImage = ScrapazonArt.Product(it.id);
        art.Add(v.artImage);
        string sticker = StickerFor(it.id);
        if (sticker != null) art.Add(Ignore(L(sticker, "sz-sticker")));
        if (it.IsTeaser)
        {
            art.Add(Ignore(L("COMING SOON™", "sz-ribbon sz-ribbon--soon")));
            art.Add(Ignore(L("NOT YET", "sz-teaser-stamp")));
        }
        else if (!string.IsNullOrEmpty(it.badge))
        {
            string cls = it.badge.Contains("deal") ? "sz-ribbon sz-ribbon--deal" : it.badge.Contains("Choice") ? "sz-ribbon sz-ribbon--choice" : "sz-ribbon";
            art.Add(Ignore(L(it.badge, cls)));
        }
        v.newRibbon = Ignore(L("NEW FOR YOU", "sz-new-ribbon sz-new-ribbon--hidden"));
        art.Add(v.newRibbon);
        v.owned = Ignore(L("", "sz-owned-tag sz-owned-tag--hidden"));
        art.Add(v.owned);
        // Requirements stay beside the price/buy action, never inside the optional details.
        v.lockChip = Ignore(V("sz-requirements"));
        v.lockText = Ignore(L("", "sz-requirements-text"));
        v.lockChip.Add(v.lockText);
        v.art = art;
        head.Add(art);

        // body
        var body = V("sz-card-body");
        v.title = L(it.title, "sz-title");
        head.Add(v.title);
        v.details = V("sz-product-details");
        v.details.Add(L($"Visit the {it.brand} Store", "sz-brand"));
        if (!string.IsNullOrEmpty(it.badge)) v.details.Add(L(it.badge, "sz-brand"));
        v.blurb = L("", "sz-blurb");
        v.details.Add(v.blurb);

        var rating = V("sz-rating-row");
        rating.Add(ScrapazonArt.Stars(it.rating));
        rating.Add(L(it.rating.ToString("0.0"), "sz-rating"));
        rating.Add(L($"({it.ratingCount:N0})", "sz-rating-count"));
        v.details.Add(rating);

        if (it.reviews != null && it.reviews.Length > 0)
        {
            v.review = V("sz-review");
            v.reviewIndex = Random.Range(0, it.reviews.Length);
            var r = it.reviews[v.reviewIndex];
            v.reviewStars = ScrapazonArt.Stars(r.stars, "sz-stars--small");
            v.review.Add(v.reviewStars);
            v.reviewText = L(ReviewLine(r), "sz-review-text");
            v.review.Add(v.reviewText);
            v.details.Add(v.review);
        }

        var price = V("sz-price-row");
        v.sym = L("$", "sz-price-sym");
        v.whole = L("", "sz-price-whole");
        v.cents = L("", "sz-price-cents");
        v.listPrice = L("", "sz-list-price");
        price.Add(v.sym); price.Add(v.whole); price.Add(v.cents); price.Add(v.listPrice);
        v.priceRow = price;
        body.Add(price);

        v.effect = L("", "sz-effect");
        body.Insert(0, v.effect);
        body.Add(v.lockChip);
        body.Add(L(DeliveryDescription(it), "sz-delivery-summary"));

        if (it.IsUpgrade && it.maxLevel > 1)
        {
            v.levelRow = V("sz-level-row");
            for (int i = 0; i < it.maxLevel; i++)
            {
                var pip = V("sz-pip");
                v.pips.Add(pip);
                v.levelRow.Add(pip);
            }
            v.levelText = L("", "sz-level-text");
            v.levelRow.Add(v.levelText);
            body.Add(v.levelRow);
        }

        v.stock = L("", "sz-stock");
        body.Add(v.stock);
        card.Add(body);

        v.buy = Btn("", "sz-buy", () => OnBuy(v));
        card.Add(v.buy);
        v.detailsToggle = Btn("", "sz-details-toggle", () =>
        {
            if (!expandedProducts.Remove(it.id)) expandedProducts.Add(it.id);
            SetDetails(v, expandedProducts.Contains(it.id));
        });
        card.Add(v.detailsToggle);
        card.Add(v.details);
        SetDetails(v, expandedProducts.Contains(it.id));
        return v;
    }

    static void SetDetails(CardView v, bool expanded)
    {
        v.details.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
        v.detailsToggle.text = expanded ? "− Hide details & reviews" : "+ Details & reviews";
        v.card.EnableInClassList("sz-card--expanded", expanded);
    }

    public static string DeliveryDescription(ScrapazonItem item) => item == null || item.IsTeaser
        ? "Not available to purchase."
        : item.id == ShopIds.TruckModel ? "Trade-in at your depot: stop and clear all riders first. Cargo and upgrades transfer automatically."
        : item.IsUpgrade ? "Installs automatically"
        : "Added to your supplies";

    /// <summary>All gates for the shown level remain readable on a collapsed card.</summary>
    public static string RequirementDescription(ScrapazonItem item, int level)
    {
        if (item == null || item.IsTeaser) return "Coming soon — not for sale";
        var needs = new List<string> { "Rank " + item.RankFor(level) };
        if (item.prereqs != null && item.prereqs.Length > 0)
        {
            var prerequisites = item.prereqs[Mathf.Clamp(level - 1, 0, item.prereqs.Length - 1)];
            if (prerequisites != null) foreach (var prerequisite in prerequisites)
            {
                if (string.IsNullOrEmpty(prerequisite)) continue;
                if (prerequisite == ShopIds.KeyToTheCity) { needs.Add("Key to the City"); continue; }
                var parts = prerequisite.Split(':');
                var required = ScrapazonCatalog.Get(parts[0]);
                string title = required != null ? ScrapazonStore.ShortName(required) : parts[0].Replace('_', ' ');
                needs.Add(title + (parts.Length > 1 ? " Lv " + parts[1] : ""));
            }
        }
        return "Requires: " + string.Join(" · ", needs);
    }

    void BuildAlso()
    {
        if (alsoRow == null) return;
        alsoRow.Clear();
        if (alsoTitle != null) alsoTitle.text = $"Customers who bought {lastBoughtName ?? "stuff"} also bought";
        var pool = new List<string>(AlsoBought);
        for (int i = 0; i < 5 && pool.Count > 0; i++)
        {
            int k = Random.Range(0, pool.Count);
            string name = pool[k];
            pool.RemoveAt(k);
            var chip = Btn(name, "sz-also-chip", () =>
            {
                Sfx.Play("squeak", null, 0.6f, 1.3f);
                host.LaptopToast($"\"{name}\" is not a real product, sweetie.", "info");
            });
            alsoRow.Add(chip);
        }
    }

    // ------------------------------------------------------------------ state refresh

    void RefreshStates()
    {
        foreach (var v in cards) UpdateCard(v);
        if (!(GameSession.HasRabies && ShopEffects.RabiesShots == 0)) Spotlight(null);
    }

    void RefreshHeader()
    {
        if (cartCount != null) cartCount.text = ScrapazonStore.TotalStuffOwned().ToString();
        if (helloLabel != null)
            helloLabel.text = GameSession.HasRabies ? "Hello, foamy person" : $"Hello, Rank {Progression.Rank} trash person";
    }

    void UpdateCard(CardView v)
    {
        var it = v.item;
        int owned = ScrapazonStore.Owned(it);
        bool maxed = ScrapazonStore.IsMaxed(it);
        float price = ScrapazonStore.NextPrice(it);
        bool afford = ScrapazonStore.CanAfford(it);
        bool locked = ScrapazonStore.IsLocked(it, out string why);
        bool bumped = v.lastOwned >= 0 && owned > v.lastOwned;
        v.lastOwned = owned;
        int next = ScrapazonStore.NextLevel(it);
        int shownLevel = it.IsUpgrade ? (maxed ? Mathf.Max(1, owned) : next) : 1;

        // title + blurb of the level you'd buy next (or the top level once maxed)
        v.title.text = it.TitleFor(shownLevel);
        string blurb = it.BlurbFor(shownLevel);
        v.blurb.text = string.IsNullOrEmpty(blurb) ? "" : blurb;
        v.blurb.EnableInClassList("sz-blurb--hidden", string.IsNullOrEmpty(blurb));

        // owned tag
        string ownedText = null;
        if (it.IsUpgrade && owned > 0) ownedText = it.maxLevel > 1 ? $"Lv {owned}/{it.maxLevel}" : "OWNED";
        else if (it.IsConsumable && owned > 0) ownedText = $"x{owned} in pocket";
        v.owned.text = ownedText ?? "";
        v.owned.EnableInClassList("sz-owned-tag--hidden", ownedText == null);
        if (bumped) Flash(v.owned, "sz-owned-tag--bump", 260);
        v.card.EnableInClassList("sz-card--maxed", it.IsUpgrade && maxed);

        // locked chip (rank / prereq / Key to the City)
        v.card.EnableInClassList("sz-card--locked", locked);
        v.lockChip.EnableInClassList("sz-requirements--locked", locked);
        v.lockText.text = it.IsUpgrade && maxed ? "Installed · maximum level"
            : RequirementDescription(it, shownLevel);
        if (v.wasLocked && !locked && !maxed) { Flash(v.card, "sz-card--unlocked", 400); Sfx.Play("sparkle", null, 0.6f, 1.3f); }
        v.wasLocked = locked;
        v.newRibbon.EnableInClassList("sz-new-ribbon--hidden", !ScrapazonAdvisor.IsNewForYou(it));

        // price
        bool na = it.IsTeaser || (it.IsUpgrade && maxed);
        v.priceRow.EnableInClassList("sz-price-row--na", na);
        if (it.IsTeaser) SetPrice(v, "", "Currently unavailable", "");
        else if (it.IsUpgrade && maxed) SetPrice(v, "", "Maxed out. You win.", "");
        else
        {
            float p = price >= 0f ? price : (it.prices.Length > 0 ? it.prices[0] : 0f);
            int whole = Mathf.FloorToInt(p + 0.0001f);
            int cents = Mathf.Clamp(Mathf.RoundToInt((p - whole) * 100f), 0, 99);
            SetPrice(v, "$", whole.ToString("N0"), cents.ToString("00"));
        }
        v.listPrice.text = it.listPrice > 0f ? $"List: <s>{Economy.Money(it.listPrice)}</s>" : "";

        // what it actually does (the important bit)
        v.effect.text = EffectText(it, owned, out string effectMood);
        v.effect.EnableInClassList("sz-effect--alert", effectMood == "alert");
        v.effect.EnableInClassList("sz-effect--active", effectMood == "active");

        // level pips
        for (int i = 0; i < v.pips.Count; i++)
        {
            bool on = i < owned;
            var pip = v.pips[i];
            if (on && !pip.ClassListContains("sz-pip--on") && bumped) Flash(pip, "sz-pip--pop", 250);
            pip.EnableInClassList("sz-pip--on", on);
            pip.EnableInClassList("sz-pip--locked", !on && i == owned && locked);
        }
        int careerStep = MagnusCareer.NextStepNumberFor(it);
        if (v.levelText != null)
            v.levelText.text = maxed ? "fully extended"
                             : careerStep > 0 ? $"Lv {owned}/{it.maxLevel} · career step {careerStep} of {MagnusCareer.Count} · Rank {it.RankFor(next)}"
                             : $"Lv {owned}/{it.maxLevel} · next needs Rank {it.RankFor(next)}";

        // stock line
        v.stock.RemoveFromClassList("sz-stock--low");
        v.stock.RemoveFromClassList("sz-stock--out");
        if (it.IsTeaser) { v.stock.text = it.teaserLine; v.stock.AddToClassList("sz-stock--out"); }
        else if (it.id == ShopIds.RabiesShot && GameSession.HasRabies) { v.stock.text = "Only 1 left in stock - order soon. (you're foaming)"; v.stock.AddToClassList("sz-stock--low"); }
        else if (it.IsConsumable && maxed) { v.stock.text = "Your pockets are full. Use some first."; v.stock.AddToClassList("sz-stock--low"); }
        else if (it.IsUpgrade && maxed) v.stock.text = "Ready to use.";
        else if (locked) { v.stock.text = why; v.stock.AddToClassList("sz-stock--out"); }
        else v.stock.text = !it.IsUpgrade && (StableHash(it.id) + GameSession.Day) % 3 == 0 ? "Only 3 left in stock (we have 9 million)" : "In Stock.";

        // buy button (unless it's mid-celebration)
        if (!v.celebrating) SetBuy(v, it, owned, maxed, price, afford, locked, why);
    }

    static string LockedStockLine(ScrapazonItem it, string why)
    {
        if (why != null && why.StartsWith("Requires Rank")) return "Employees only. Earn Rep at the curb to get promoted.";
        if (MagnusCareer.IsCareerItem(it.id)) return "Magnus isn't ready for this yet. Earlier career steps first (see his career path up top).";
        if (why != null && why.Contains("Key to the City")) return "Reserved for whoever holds the Key to the City.";
        return "Install the prerequisite first. Then we'll talk.";
    }

    void SetBuy(CardView v, ScrapazonItem it, int owned, bool maxed, float price, bool afford, bool locked, string why)
    {
        var b = v.buy;
        foreach (var c in new[] { "sz-buy--confirm", "sz-buy--poor", "sz-buy--maxed", "sz-buy--teaser", "sz-buy--done", "sz-buy--locked" }) b.RemoveFromClassList(c);
        if (it.IsTeaser)
        {
            b.text = v.notified ? "Notified! (not really)" : "Notify me (we won't)";
            b.AddToClassList("sz-buy--teaser");
        }
        else if (maxed)
        {
            b.text = it.IsUpgrade ? "Owned (max level)" : "Pockets full";
            b.AddToClassList("sz-buy--maxed");
        }
        else if (locked)
        {
            b.text = why != null && why.StartsWith("Requires Rank") ? $"Locked · Rank {it.RankFor(ScrapazonStore.NextLevel(it))}" : "Locked";
            b.AddToClassList("sz-buy--locked");
        }
        else if (v.confirmUntil > Time.unscaledTime)
        {
            b.text = $"Click again to pay {Economy.Money(price)}";
            b.AddToClassList("sz-buy--confirm");
        }
        else if (!afford)
        {
            b.text = $"Can't afford ({Economy.Money(price - GameSession.Cash)} short)";
            b.AddToClassList("sz-buy--poor");
        }
        else
        {
            string verb = it.dept == "permits" ? "Get Permit" : it.IsUpgrade && owned > 0 ? "Upgrade" : "Buy Now";
            b.text = $"{verb} · {Economy.Money(price)}";
        }
    }

    static void SetPrice(CardView v, string sym, string whole, string cents)
    {
        v.sym.text = sym;
        v.whole.text = whole;
        v.cents.text = cents;
    }

    static string EffectText(ScrapazonItem it, int owned, out string mood)
    {
        mood = null;
        switch (it.id)
        {
            case ShopIds.RabiesShot:
                if (GameSession.HasRabies) { mood = "alert"; return "YOU HAVE RABIES. This fixes that. Right now."; }
                return "Cures rabies. Kept in your pocket and auto-injected the moment something rabid bites you.";
            case ShopIds.Repellent:
                if (ShopEffects.RepellentActive) { mood = "active"; return "ACTIVE: critters won't bite or bonk you until morning."; }
                return "The first time a critter tries to bite or bonk you, you spritz a bottle: nothing touches you until morning.";
            case ShopIds.TruckModel:
            {
                int capacityLevel = ShopEffects.TruckCapacityLevel;
                float next = TruckProgression.CapacityKg(TruckProgression.RearLoader, capacityLevel);
                if (owned >= it.maxLevel) return $"Your Rear-loader holds {next:0} kg with your current hopper upgrades.";
                float current = TruckProgression.CapacityKg(TruckProgression.Mini, capacityLevel);
                return $"Curb Pup → Rear-loader: {current:0} → {next:0} kg. All equipment, hopper upgrades and cargo transfer free. Park at your own depot with everyone off the truck. Trade-in included.";
            }
            case ShopIds.TruckCapacity:
            {
                float cur = ShopEffects.TruckCapacityFor(owned);
                string model = TruckProgression.ModelName(TruckProgression.CurrentModel);
                if (owned >= it.maxLevel) return $"Your {model} holds {cur:0} kg. The ram goes THUNK. Transfers free when you trade up.";
                float nxt = ShopEffects.TruckCapacityFor(owned + 1);
                return $"Your {model}: {cur:0} → {nxt:0} kg (+{nxt - cur:0} kg). Transfers free when you trade up." + (owned + 1 >= 4 ? " Includes a compaction ram." : "");
            }
            case ShopIds.AirHorn:
            {
                float now = UpgradeEffects.HornScareRadius;
                if (owned >= it.maxLevel) return $"Honking (H) scares critters within {now:0} m. Trees flinch.";
                return owned == 0 ? $"Honking (H) scares critters within {ShopEffects.AirHornScareRadius:0} m instead of {ShopEffects.BaseHornScareRadius:0} m. Louder. Derpier."
                                  : $"Scare radius {now:0} → 40 m. Nearby hedges and trees shake.";
            }
            case ShopIds.LuckyGnome:
            case ShopIds.GoldenGnome:
            {
                float left = Progression.GnomeHoursLeft(it.id);
                if (left > 0f) { mood = "active"; return $"ON DUTY: {left:0.#} h of treasure luck left." + (owned > 0 ? $" {owned} more waiting in line." : ""); }
                return it.EffectFor(1);
            }
        }
        if (it.IsTeaser) return it.blurb ?? "";
        if (it.IsConsumable) return it.EffectFor(1) ?? "";
        if (MagnusCareer.IsCareerItem(it.id))
        {
            // Magnus's career: name the step, say exactly what it adds
            int top = Mathf.Min(owned, it.maxLevel);
            if (owned >= it.maxLevel) return $"Installed (career step {MagnusCareer.StepNumber(it.id, top)} of {MagnusCareer.Count}): {it.EffectFor(top)}";
            int step = MagnusCareer.StepNumber(it.id, owned + 1);
            return $"Career step {step} of {MagnusCareer.Count}: {it.EffectFor(owned + 1)}";
        }

        // upgrades: what the next level does (and what you have now)
        if (it.maxLevel <= 1) return it.EffectFor(1) ?? "";
        if (owned >= it.maxLevel) return $"Installed (Lv {owned}): {it.EffectFor(owned)}";
        string nextFx = it.EffectFor(owned + 1);
        return owned > 0 ? $"Now (Lv {owned}): {it.EffectFor(owned)}\nNext (Lv {owned + 1}): {nextFx}" : nextFx ?? "";
    }

    static string StickerFor(string id)
    {
        switch (id)
        {
            case ShopIds.RabiesShot: return "OWWW";
            case ShopIds.Repellent: return "1 NIGHT";
            case ShopIds.TruckModel: return "+80 kg";
            case ShopIds.TruckCapacity: return "+40 kg";
            case ShopIds.TruckTurbo: return "VROOM";
            case ShopIds.AirHorn: return "HONK";
            case ShopIds.TruckArm: return "PRESS X";
            case ShopIds.TruckHandling: return "UNFLIP";
            case ShopIds.TruckHeadlights: return "BRIGHT";
            case ShopIds.AutoTipper: return "FWOOMP";
            case ShopIds.ConveyorBelt: return "IN STOCK!";
            case ShopIds.CompactorMk2: return "ACHOO";
            case ShopIds.RecyclingContracts: return "×1.5";
            case ShopIds.HazmatContract: return "$6.00";
            case ShopIds.TrophyShelf: return "+REP";
            case ShopIds.DisplayPlankShelf: return "WOBBLY";
            case ShopIds.DisplayGlassCase: return "LIT";
            case ShopIds.DisplayPedestal: return "SPOTLIGHT";
            case ShopIds.DisplayVault: return "EPIC+";
            case ShopIds.MagnusBrain: return "I AM HELPING";
            case ShopIds.MagnusRailMotor: return "STRIPES";
            case ShopIds.MagnusGripper: return "PINCH";
            case ShopIds.MagnusVision: return "!";
            case ShopIds.BagCannon: return "FWOOMP";
            case ShopIds.PermitMinivanMeadows: return "$45/BAG";
            case ShopIds.PermitCouponCommons: return "$75/BAG";
            case ShopIds.PermitSnootingtonHills: return "$120/BAG";
            case ShopIds.PermitStackingtonHeights: return "$165/BAG";
            case ShopIds.PermitBillionaireBluffs: return "$240/BAG";
            case ShopIds.EbaePowerSeller: return "5% FEE";
            case ShopIds.GoldenTruck: return "24K";
            case ShopIds.GarbageJuice: return "*HIC*";
            case ShopIds.LuckyGnome: return "24 h";
            case ShopIds.GoldenGnome: return "×1.4";
            default: return null;
        }
    }

    static int StableHash(string str)
    {
        unchecked
        {
            int h = 17;
            foreach (char ch in str) h = h * 31 + ch;
            return h & 0x7fffffff;
        }
    }

    static string ReviewLine(ScrapazonReview r) => $"\"{r.text}\" — {r.author}";

    // ------------------------------------------------------------------ buying

    void OnBuy(CardView v)
    {
        if (v.celebrating) return;
        var it = v.item;
        float now = Time.unscaledTime;

        if (it.IsTeaser)
        {
            Sfx.Play("squeak", null, 0.7f, 0.9f);
            Flash(v.card, "sz-card--shake", 140);
            v.notified = true;
            host.LaptopToast($"{it.teaserLine}. we'll notify you. (we won't)", "info");
            UpdateCard(v);
            return;
        }
        if (ScrapazonStore.IsMaxed(it))
        {
            Sfx.Play("buzz", null, 0.5f, 1.2f);
            Flash(v.card, "sz-card--shake", 140);
            host.LaptopToast(it.IsUpgrade ? "you already own the best one. stop." : "your pockets are full. use some first.", "info");
            return;
        }
        if (ScrapazonStore.IsLocked(it, out string why))
        {
            Sfx.Play("buzz", null, 0.7f, 0.8f);
            Flash(v.card, "sz-card--shake", 140);
            Flash(v.lockChip, "sz-lock--bump", 220);
            string hint = why != null && why.StartsWith("Requires Rank")
                ? $" ({Mathf.CeilToInt(Progression.RepToNext):N0} Rep to your next promotion: on-time bags, clean curbs, sorting. See Career.)"
                : "";
            host.LaptopToast($"{why}.{hint}", "bad");
            return;
        }
        float price = ScrapazonStore.NextPrice(it);
        if (!ScrapazonStore.CanAfford(it))
        {
            Sfx.Play("buzz");
            Flash(v.card, "sz-card--shake", 140);
            if (balance != null) Flash(balance, "sz-balance--spend", 200);
            host.LaptopToast($"payment declined. your card is also garbage. ({Economy.Money(price - GameSession.Cash)} short)", "bad");
            return;
        }
        if (price >= ConfirmAbovePrice && v.confirmUntil <= now)
        {
            v.confirmUntil = now + ConfirmSeconds;
            Sfx.Play("click", null, 0.8f, 1.35f);
            Flash(v.buy, "sz-buy--done", 90);
            UpdateCard(v);
            return;
        }
        v.confirmUntil = 0f;

        var result = ScrapazonStore.TryBuy(it.id, out string message);
        if (result != ScrapazonPurchase.Ok)
        {
            Sfx.Play("buzz");
            Flash(v.card, "sz-card--shake", 140);
            host.LaptopToast(message, "bad");
            UpdateCard(v);
            return;
        }
        Celebrate(v);
    }

    void Celebrate(CardView v)
    {
        var it = v.item;
        string name = ScrapazonStore.ShortName(it);
        lastBoughtName = name;
        v.celebrating = true;
        bool big = it.dept == "permits" || it.id == ShopIds.GoldenTruck || it.id == ShopIds.TruckModel;

        Sfx.Play("cash");
        Sfx.Play(big ? "legendary" : "ding", null, 0.8f, big ? 1f : 1.25f);
        Juice.Shake(big ? 0.18f : 0.08f, big ? 0.25f : 0.15f);
        LogoHop();
        if (balance != null) Flash(balance, "sz-balance--spend", 200);
        if (cart != null) Flash(cart, "sz-cart--bump", 220);

        v.buy.RemoveFromClassList("sz-buy--confirm");
        v.buy.AddToClassList("sz-buy--done");
        v.buy.text = it.IsUpgrade ? "Installed!" : "Added to supplies!";

        // No delivery box: the purchase just takes effect, with a stamp and confetti on the card.
        host.LaptopAfter(330, () =>
        {
            Flash(v.card, "sz-card--bought", 230);
            if (it.dept == "permits") host.LaptopStamp("PERMIT APPROVED!", name, "sz");
            else if (it.id == ShopIds.GoldenTruck) host.LaptopStamp("24 KARATS!", "The Golden Garbage Truck", "sz");
            else if (it.id == ShopIds.TruckModel) host.LaptopStamp("NEW TRUCK!", "Your upgrades came along", "sz");
            else host.LaptopStamp(it.IsUpgrade ? "INSTALLED!" : "SUPPLIES ADDED!", "+1 " + name, "sz");
            host.LaptopConfetti(big ? 80 : 36, 6, 7, 8, 2);
        });
        // Magnus department: re-sort into career order once the purchase celebration is done.
        if (MagnusCareer.IsCareerItem(it.id)) host.LaptopAfter(1700, ResortMagnusIfStale);
        host.LaptopAfter(900, () =>
        {
            v.celebrating = false;
            v.buy.RemoveFromClassList("sz-buy--done");
            UpdateCard(v);
        });

        string joke = DeliveryJokes[Random.Range(0, DeliveryJokes.Length)];
        int lvl = ScrapazonStore.Level(it.id);
        switch (it.id)
        {
            case ShopIds.RabiesShot when GameSession.HasRabies:
                host.LaptopToast("Rabies Shot incoming. It's coming out of the screen. BRACE.", "bad");
                host.LaptopAfter(1100, host.CloseLaptop);
                break;
            case ShopIds.RabiesShot:
                host.LaptopToast($"Rabies Shot delivered in 0.3 seconds. It auto-injects if something rabid bites you. {joke}", "sz");
                break;
            case ShopIds.Repellent:
                host.LaptopToast($"Raccoon-Be-Gone delivered! It spritzes itself the moment a critter tries something. {joke}", "sz");
                break;
            case ShopIds.TruckCapacity:
                host.LaptopToast($"Hopper upgraded! Your {TruckProgression.ModelName(TruckProgression.CurrentModel)} now holds {ShopEffects.TruckCapacityFor(lvl):0} kg. Transfers free when you trade up. {joke}", "sz");
                break;
            case ShopIds.TruckModel:
                host.LaptopToast($"Your Rear-loader is ready: {GameSession.TruckCapacityKg:0} kg! Cargo and upgrades transferred. Same business, bigger truck.", "sz");
                break;
            case ShopIds.TruckTurbo:
                host.LaptopToast(lvl <= 1 ? "TURBO installed. Press W and try not to scream." : $"{it.TitleFor(lvl)} installed. Top speed {UpgradeEffects.Engine.maxSpeed:0.#} m/s. Scream responsibly.", "sz");
                break;
            case ShopIds.AirHorn:
                host.LaptopToast("Air Horn installed on your truck. Press H to ruin the neighborhood's day.", "sz");
                break;
            case ShopIds.TruckArm:
                host.LaptopToast(lvl <= 1 ? "Grabby Arm installed! Stop next to a curb and HOLD X. Never leave the cab." : $"{it.TitleFor(lvl)} installed! Press X while driving for AUTO.", "sz");
                break;
            case ShopIds.PermitMinivanMeadows:
            case ShopIds.PermitCouponCommons:
            case ShopIds.PermitSnootingtonHills:
            case ShopIds.PermitStackingtonHeights:
            case ShopIds.PermitBillionaireBluffs:
            {
                var d = DistrictFor(it.id);
                host.LaptopToast(d != null ? $"Permit approved! Drive through the {d.name} gate to cut the ribbon. {Progression.Subscribers(d)} houses already signed up." : "Permit approved!", "sz");
                break;
            }
            case ShopIds.LuckyGnome:
            case ShopIds.GoldenGnome:
            {
                float left = Progression.GnomeHoursLeft(it.id);
                host.LaptopToast(left > 23.5f && ScrapazonStore.Count(it.id) == 0 ? $"{name} is on duty for 24 h. Rub his belly." : $"{name} delivered. He'll wait in line until the current one clocks out.", "sz");
                break;
            }
            case ShopIds.GoldenTruck:
                host.LaptopToast("THE GOLDEN GARBAGE TRUCK. A swarm of drones is delivering it. The curbs will cheer for you.", "sz");
                break;
            case ShopIds.MagnusBrain when lvl <= 1:
                host.LaptopToast("Magnus got the job! \"I AM HELPING.\" He drives over to your sort table and sorts the recyclables + junk you rip onto it. " + MagnusCareer.NextLine(), "sz");
                break;
            case ShopIds.MagnusBrain:
            case ShopIds.MagnusRailMotor:
            case ShopIds.MagnusGripper:
            case ShopIds.MagnusVision:
            {
                int n = MagnusCareer.StepNumber(it.id, lvl);
                string adds = n > 0 ? MagnusCareer.Steps[n - 1].adds : "got an upgrade";
                // The step's short name, not the catalog title (step 9's ends in "...Better.)", which read "Better.)!").
                string stepName = n > 0 ? MagnusCareer.Steps[n - 1].name : it.TitleFor(lvl);
                host.LaptopToast($"Career step {n} of {MagnusCareer.Count}: {stepName}! Magnus now {adds}. {MagnusCareer.NextLine()}", "sz");
                break;
            }
            default:
                host.LaptopToast($"{name}{(it.maxLevel > 1 && it.IsUpgrade ? $" Lv {lvl}" : "")} {(it.IsUpgrade ? "installed automatically" : "added to your supplies")}.", "sz");
                break;
        }

        RefreshHeader();
        if (alsoTitle != null) alsoTitle.text = $"Customers who bought {lastBoughtName} also bought";
        bestSignature = null;
        dirty = true;
    }

    static DistrictDef DistrictFor(string permitId)
    {
        foreach (var d in Districts.All) if (d.permitId == permitId) return d;
        return null;
    }

    /// <summary>Highlight one product card (wobbling art, red border); null clears.</summary>
    public void Spotlight(string id)
    {
        foreach (var v in cards)
        {
            bool on = id != null && v.item.id == id;
            v.card.EnableInClassList("sz-card--spotlight", on);
            if (!on) v.artImage?.RemoveFromClassList("sz-art--wobble");
        }
    }

    // ------------------------------------------------------------------ ambient juice

    void TickHero(bool force)
    {
        if (hero == null) return;
        float now = Time.unscaledTime;
        var (kicker, title, sub, mood, sticky, product) = HeroMessage();
        string sig = kicker + "|" + title;
        bool contextChanged = sticky != heroSticky || (sticky && sig != heroSignature);
        heroSticky = sticky;
        if (!force && !contextChanged && now < nextHeroAt) return;
        nextHeroAt = now + 6.5f;
        if (!sticky) heroIndex++;

        hero.EnableInClassList("sz-hero--alert", mood == "alert");
        hero.EnableInClassList("sz-hero--night", mood == "night");
        hero.EnableInClassList("sz-hero--promo", mood == "promo");
        if (force || heroCopy == null)
        {
            ApplyHero(kicker, title, sub, product);
            heroSignature = sig;
            return;
        }
        if (sig == heroSignature) return;
        heroSignature = sig;
        heroCopy.AddToClassList("sz-hero-copy--swap");
        host.LaptopAfter(260, () =>
        {
            ApplyHero(kicker, title, sub, product);
            heroCopy.RemoveFromClassList("sz-hero-copy--swap");
        });
    }

    void ApplyHero(string kicker, string title, string sub, string product)
    {
        if (heroKicker != null) heroKicker.text = kicker;
        if (heroTitle != null) heroTitle.text = title;
        if (heroSub != null) heroSub.text = sub;
        heroProductId = product;
        heroCtaShown = null;
        RefreshHeroCta();
    }

    static string PriceOf(string id)
    {
        var it = ScrapazonCatalog.Get(id);
        return it != null && it.prices != null && it.prices.Length > 0 ? Economy.Money(ScrapazonStore.NextPrice(it) >= 0 ? ScrapazonStore.NextPrice(it) : it.prices[0]) : "";
    }

    (string kicker, string title, string sub, string mood, bool sticky, string product) HeroMessage()
    {
        if (GameSession.HasRabies && ShopEffects.RabiesShots == 0)
            return ("URGENT", "You have rabies. (probably)",
                $"Rabies Shot™ {PriceOf(ShopIds.RabiesShot)} · Prime: in your arm in 0.3 seconds · the hospital charges {Economy.Money(Economy.HospitalBill)}", "alert", true, ShopIds.RabiesShot);

        // a permit you can buy right now beats everything else
        foreach (var d in Districts.ProgressionOrder)
        {
            var permit = ScrapazonCatalog.Get(d.permitId);
            if (permit == null || Districts.IsUnlocked(d) || ScrapazonStore.IsLocked(permit)) continue;
            if (heroIndex % 3 == 0)
                return ("NOW HIRING (YOU)", $"{d.name} wants you",
                    $"Collection permit {Economy.Money(d.permitPrice)} · {Economy.Money(d.fee)} a bag{(d.tip > 0f ? $" + {Economy.Money(d.tip)} tip" : "")} · Rep ×{d.repMult:0.#}", "promo", false, permit.id);
            break;
        }
        int fresh = ScrapazonAdvisor.NewForYouCount();
        if (fresh > 0 && heroIndex % 2 == 1)
            return ("NEW FOR YOU", $"Rank {Progression.Rank} unlocked {fresh} product{(fresh == 1 ? "" : "s")}",
                $"Congratulations, {Progression.RankTitle(Progression.Rank)}. Your salary is unchanged. Your shopping options are not.", "promo", false, null);
        if (GameClock.IsNight && heroIndex % 2 == 0)
            return ("NIGHT OWL DEALS", "The raccoons are out. Are you ready?",
                $"Raccoon-Be-Gone {PriceOf(ShopIds.Repellent)} · Raccoon Trap {PriceOf(RaccoonTrap.ConsumableId)} (fish included) · Air Horn {PriceOf(ShopIds.AirHorn)}", "night", false, ShopIds.Repellent);
        switch (heroIndex % 6)
        {
            case 0: return ("DEAL OF THE DAY", "Rabies Shot™: cheaper than the hospital",
                $"{PriceOf(ShopIds.RabiesShot)} vs a {Economy.Money(Economy.HospitalBill)} hospital bill. Math!", null, false, ShopIds.RabiesShot);
            case 1: return ("PRIME DAY (EVERY DAY)", "Delivered in 0.3 seconds.", "Our drivers are terrified of us.", null, false, null);
            case 2: return ("NEW!", "Grabby Arm™: never leave the cab", "Press X to grab. 95% success rate. That's an A.", null, false, ShopIds.TruckArm);
            case 3: return ("FREE RETURNS*", "*we do not accept returns", "All sales final. Like your life choices.", null, false, null);
            case 4: return ("FINALLY BACK IN STOCK", "The Conveyor Belt (1997-2026)", "Walt waited 29 years. You don't have to.", null, false, ShopIds.ConveyorBelt);
            default: return ("TOP RATED", "Industrial Air Horn: 4.8 stars", "\"scared a raccoon so bad it paid my rent\" — Ashley", null, false, ShopIds.AirHorn);
        }
    }

    CardView FindCard(string id)
    {
        foreach (var v in cards) if (v.item != null && v.item.id == id) return v;
        return null;
    }

    /// <summary>Mirror the advertised product's buy state on the hero button (hidden for maxed / teaser / locked / no product).</summary>
    void RefreshHeroCta()
    {
        if (heroCta == null) return;
        var it = heroProductId != null ? ScrapazonCatalog.Get(heroProductId) : null;
        bool show = it != null && !it.IsTeaser && !ScrapazonStore.IsMaxed(it) && !ScrapazonStore.IsLocked(it);
        string text = null;
        bool confirm = false, poor = false, done = false;
        if (show)
        {
            var v = FindCard(it.id);
            if (v != null && (v.celebrating || v.confirmUntil > Time.unscaledTime))
            {
                text = v.buy.text;
                confirm = v.confirmUntil > Time.unscaledTime;
                done = v.celebrating;
            }
            else if (!ScrapazonStore.CanAfford(it))
            {
                text = $"{ScrapazonStore.ShortName(it)}: {Economy.Money(ScrapazonStore.NextPrice(it) - GameSession.Cash)} short";
                poor = true;
            }
            else text = $"Buy {ScrapazonStore.ShortName(it)} · {Economy.Money(ScrapazonStore.NextPrice(it))}";
        }
        string sig = show ? $"{text}|{confirm}|{poor}|{done}" : "hidden";
        if (sig == heroCtaShown) return;
        heroCtaShown = sig;
        heroCta.EnableInClassList("sz-hero-cta--hidden", !show);
        heroCta.EnableInClassList("sz-hero-cta--confirm", confirm);
        heroCta.EnableInClassList("sz-hero-cta--poor", poor);
        heroCta.EnableInClassList("sz-hero-cta--done", done);
        if (show) heroCta.text = text;
    }

    void OnHeroCta()
    {
        if (heroProductId == null) return;
        var v = FindCard(heroProductId);
        if (v == null)
        {
            SetFilter("all", false); // the advertised product isn't in this department: show everything
            v = FindCard(heroProductId);
        }
        if (v == null) return;
        OnBuy(v); // same flow as the card's own button (confirm for big tickets, "can't afford" shake, celebration)
        heroCtaShown = null;
        RefreshHeroCta();
    }

    void RotateReviews()
    {
        foreach (var v in cards)
        {
            var reviews = v.item.reviews;
            if (v.review == null || reviews == null || reviews.Length < 2) continue;
            var cv = v;
            int next = (cv.reviewIndex + 1) % reviews.Length;
            cv.reviewIndex = next;
            cv.review.AddToClassList("sz-review--swap");
            host.LaptopAfter(250 + Random.Range(0, 200), () =>
            {
                var r = reviews[next];
                cv.reviewText.text = ReviewLine(r);
                var stars = ScrapazonArt.Stars(r.stars, "sz-stars--small");
                int at = cv.review.IndexOf(cv.reviewStars);
                cv.reviewStars.RemoveFromHierarchy();
                cv.review.Insert(Mathf.Max(0, at), stars);
                cv.reviewStars = stars;
                cv.review.RemoveFromClassList("sz-review--swap");
            });
        }
    }

    void NextSearchLine()
    {
        nextSearchAt = Time.unscaledTime + 5f;
        if (searchLabel == null) return;
        string next = SearchLines[searchIndex++ % SearchLines.Length].Replace("Scrapazon", Scrapazon.Name)
                                                                    .Replace("{N}", ScrapazonCatalog.All.Count.ToString()); // = what "All" lists (incl. the "currently unavailable" teaser)
        searchLabel.AddToClassList("sz-search-text--swap");
        host.LaptopAfter(250, () =>
        {
            searchLabel.text = next;
            searchLabel.RemoveFromClassList("sz-search-text--swap");
        });
    }

    void RefreshCash(bool snap)
    {
        if (balance == null) return;
        float cash = GameSession.Cash;
        if (!snap && Mathf.Approximately(cash, shownCash)) return;
        bool up = !snap && !float.IsNaN(shownCash) && cash > shownCash;
        shownCash = cash;
        balance.text = Economy.Money(cash);
        if (up) Flash(balance, "sz-balance--bump", 170);
    }

    public void LogoHop()
    {
        for (int i = 0; i < logoLetters.Count; i++)
        {
            var letter = logoLetters[i];
            letter.schedule.Execute(() => letter.AddToClassList("sz-logo-letter--hop")).ExecuteLater(i * 45);
            letter.schedule.Execute(() => letter.RemoveFromClassList("sz-logo-letter--hop")).ExecuteLater(i * 45 + 140);
        }
        if (smileHost != null)
        {
            int end = logoLetters.Count * 45;
            smileHost.schedule.Execute(() => smileHost.AddToClassList("sz-logo-smile-host--wiggle")).ExecuteLater(end);
            smileHost.schedule.Execute(() => smileHost.RemoveFromClassList("sz-logo-smile-host--wiggle")).ExecuteLater(end + 200);
        }
    }

    // ------------------------------------------------------------------ element helpers

    static void Flash(VisualElement e, string cls, long ms)
    {
        if (e == null) return;
        e.AddToClassList(cls);
        e.schedule.Execute(() => e.RemoveFromClassList(cls)).ExecuteLater(ms);
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
        var b = L(text, classes);
        OnClick(b, action);
        return b;
    }

    static T Ignore<T>(T e) where T : VisualElement
    {
        e.pickingMode = PickingMode.Ignore;
        return e;
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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => greetedThisSession = false;
}
