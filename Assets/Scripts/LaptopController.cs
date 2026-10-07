using UnityEngine;

/// <summary>
/// The shop laptop: E opens the laptop screen (<see cref="EbaeUI"/>) with its two apps, ebae (sell) and
/// Scrapazon (buy). The prompt advertises waiting offers (or a Rabies Shot when you're infected) and a little
/// bobbing sign floats above the laptop: "$ 2 offers!" while buyers are waiting, "+ RABIES SHOT $35" when
/// you're foaming at the mouth with no shot in your pocket.
/// </summary>
public class LaptopController : MonoBehaviour, IInteractable
{
    [Tooltip("Show a bobbing offer counter above the laptop while buyers are waiting.")]
    public bool showOfferBadge = true;
    [Tooltip("Extra offset for the badge above the top of the laptop's renderer bounds.")]
    public Vector3 badgeOffset = new Vector3(0f, 0.35f, 0f);

    static readonly Color BadgeColor = new Color(1f, 0.84f, 0.2f);
    static readonly Color RabiesBadgeColor = new Color(1f, 0.35f, 0.35f);

    TextMesh badge;
    Renderer body;
    bool highlighted;
    int shownCount = -1;
    bool shownRabies;
    float punchUntil;

    public bool CanInteract() => !EbaeUI.IsOpen;

    public string GetInteractionPrompt()
    {
        if (!Permissions.LocalCanUseLaptopAt(transform.position)) return "Laptop (not yours)";
        if (NeedsRabiesShot) return $"Use Laptop — buy a Rabies Shot on {Scrapazon.Name}!";
        int n = EbaeMarket.PendingOfferCount;
        string shop = EbaeUI.ScrapazonVisited ? Scrapazon.Name : Scrapazon.Name + " (new!)";
        return n > 0 ? $"Use Laptop — ebae ({n} offer{(n == 1 ? "" : "s")}!) · {shop}" : $"Use Laptop — ebae · {shop}";
    }

    static bool NeedsRabiesShot => GameSession.HasRabies && GameSession.GetConsumable(ShopIds.RabiesShot) == 0;

    public void OnInteract(GameObject interactor) => EnterLaptop(interactor);

    public void Highlight(bool highlight)
    {
        if (highlight && !highlighted) Juice.Punch(transform, 0.05f, 0.2f);
        highlighted = highlight;
    }

    void Awake()
    {
        body = GetComponentInChildren<Renderer>();
    }

    void OnEnable() => EbaeMarket.OfferArrived += OnOfferArrived;

    void OnDisable()
    {
        EbaeMarket.OfferArrived -= OnOfferArrived;
        if (badge != null) badge.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (badge != null) Destroy(badge.gameObject);
    }

    void Update() => UpdateBadge();

    void EnterLaptop(GameObject interactor)
    {
        if (!Permissions.LocalCanUseLaptopAt(transform.position)) { Permissions.Deny(transform.position, "Not your laptop!"); return; }
        if (EbaeUI.Instance == null)
        {
            GameHUD.Toast("The laptop is frozen. Have you tried turning it off and on again?", ToastStyle.Bad);
            Sfx.Play("buzz", transform.position);
            return;
        }
        Sfx.Play("click", transform.position);
        Juice.Squash(transform, 0.1f, 0.2f);
        EbaeUI.Instance.Show();
    }

    void OnOfferArrived(EbaeListing listing, EbaeOffer offer)
    {
        if (EbaeUI.IsOpen || !isActiveAndEnabled) return;
        Vector3 p = BadgePosition();
        GameHUD.FloatingText(p + Vector3.up * 0.2f, "+1 offer! " + Economy.Money(offer.amount), BadgeColor, 0.8f);
        punchUntil = Time.unscaledTime + 0.35f; // EbaeMarket already plays the notify sound + toast
    }

    Vector3 BadgePosition()
    {
        if (body != null)
        {
            var b = body.bounds;
            return new Vector3(b.center.x, b.max.y, b.center.z) + badgeOffset;
        }
        return transform.position + Vector3.up * 0.4f + badgeOffset;
    }

    void UpdateBadge()
    {
        bool rabies = showOfferBadge && !EbaeUI.IsOpen && NeedsRabiesShot;
        int n = showOfferBadge && !EbaeUI.IsOpen ? EbaeMarket.PendingOfferCount : 0;
        if (n == 0 && !rabies)
        {
            if (badge != null && badge.gameObject.activeSelf) badge.gameObject.SetActive(false);
            shownCount = 0;
            shownRabies = false;
            return;
        }
        if (badge == null && !CreateBadge()) return;
        if (!badge.gameObject.activeSelf) badge.gameObject.SetActive(true);
        if (rabies != shownRabies || (!rabies && n != shownCount))
        {
            if (rabies && !shownRabies) punchUntil = Time.unscaledTime + 0.35f;
            shownRabies = rabies;
            shownCount = n;
            badge.text = rabies ? $"+ RABIES SHOT {Economy.Money(ScrapazonCatalog.Get(ShopIds.RabiesShot)?.prices[0] ?? 35f)}" : $"$ {n} offer{(n == 1 ? "" : "s")}!";
            badge.color = rabies ? RabiesBadgeColor : BadgeColor;
        }

        float t = Time.unscaledTime;
        var bt = badge.transform;
        bt.position = BadgePosition() + Vector3.up * (Mathf.Sin(t * 3f) * 0.05f);
        var cam = Camera.main;
        if (cam != null) bt.rotation = Quaternion.LookRotation(bt.position - cam.transform.position);
        float punch = t < punchUntil ? 1f + 0.5f * ((punchUntil - t) / 0.35f) : 1f;
        bt.localScale = Vector3.one * (1f + Mathf.Sin(t * 6f) * 0.06f) * punch;
    }

    bool CreateBadge()
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) { showOfferBadge = false; return false; }
        var go = new GameObject("ebae Offer Badge");
        badge = go.AddComponent<TextMesh>();
        badge.font = font;
        badge.fontSize = 64;
        badge.characterSize = 0.022f;
        badge.anchor = TextAnchor.LowerCenter;
        badge.alignment = TextAlignment.Center;
        badge.fontStyle = FontStyle.Bold;
        badge.color = BadgeColor;
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        return true;
    }
}
