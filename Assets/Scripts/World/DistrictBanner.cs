using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>When to announce a district (pure; DistrictBannerTests). The player has to have been inside the new district
/// for a moment, so driving along a border does not flicker, and the same district is not announced twice running.</summary>
public struct DistrictBannerState
{
    public const float SettleSeconds = 1.2f, QuietSeconds = 6f;

    /// <summary>The district last announced (or the one the player started in), the one they seem to be entering, how
    /// long they have been in it, and how long since the last banner.</summary>
    public string current, candidate;
    public float candidateFor, sinceShown;

    /// <summary>Advance by <paramref name="dt"/> with the player standing in <paramref name="districtId"/> (null = nowhere
    /// that counts: indoors, no player). Returns the id to announce now, or null.</summary>
    public string Step(string districtId, float dt)
    {
        sinceShown += dt;
        if (string.IsNullOrEmpty(districtId)) { candidate = null; candidateFor = 0f; return null; }
        if (current == null) { current = districtId; return null; } // where you start is not news
        if (districtId == current) { candidate = null; candidateFor = 0f; return null; }
        if (districtId != candidate) { candidate = districtId; candidateFor = 0f; return null; }
        candidateFor += dt;
        if (candidateFor < SettleSeconds || sinceShown < QuietSeconds) return null;
        current = districtId;
        candidate = null;
        candidateFor = 0f;
        sinceShown = 0f;
        return districtId;
    }
}

/// <summary>
/// A small "now entering" banner at the top of the screen whenever this machine's player crosses into another district
/// on foot, in a truck or in the Hoverhog (Nick: "a popup somewhere on screen when you move between neighborhoods").
/// The big <see cref="DistrictWelcome"/> card still slams on the first time a district's ribbon is cut; this is the
/// quiet everyday one. Local presentation only; built from the same FeelUi helpers.
/// </summary>
public class DistrictBanner : MonoBehaviour
{
    static DistrictBanner instance;
    DistrictBannerState state;
    float nextLook;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("[District banner]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<DistrictBanner>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    /// <summary>The district the local player is standing in, or null while they are indoors or not in the town.</summary>
    static string LocalDistrict()
    {
        var me = PlayerController.Local; // coop:allow(local) the banner is this machine's own screen
        if (me == null || TownMapAnchor.Active == null) return null;
        Vector3 p = me.transform.position;
        if (!WorldCameraOcclusion.Outdoors(p)) return null;
        var d = Districts.AtPosition(p);
        // AtPosition falls back to the starter district for anywhere outside every district (Depot Row, the bridges,
        // the gaps between districts): that is nowhere in particular, not a crossing into the Acres.
        if (d == null || (d == Districts.Starter && !d.boundsXZ.Contains(new Vector2(p.x, p.z)))) return null;
        return d.id;
    }

    void Update()
    {
        if (Time.unscaledTime < nextLook) return;
        float dt = 0.25f;
        nextLook = Time.unscaledTime + dt;
        string entered = state.Step(LocalDistrict(), dt);
        if (entered != null) Show(Districts.Get(entered));
    }

    // ------------------------------------------------------------------ the card

    static Color Accent(DistrictDef d)
    {
        switch (d.id)
        {
            case Districts.MinivanMeadows: return new Color(1f, 0.6f, 0.85f);
            case Districts.CouponCommons: return new Color(1f, 0.62f, 0.2f);
            case Districts.SnootingtonHills: return new Color(0.75f, 0.9f, 1f);
            case Districts.StackingtonHeights: return new Color(0.7f, 0.75f, 1f);
            case Districts.BillionaireBluffs: return new Color(1f, 0.82f, 0.3f);
            default: return new Color(0.45f, 1f, 0.5f);
        }
    }

    GameObject card;
    RectTransform panel;
    CanvasGroup fade;
    float shownAt = -100f;
    const float Hold = 3.2f, In = 0.25f, Out = 0.5f, W = 760f, H = 112f, RestY = -196f;

    /// <summary>Slide the banner in for <paramref name="d"/> (replacing one that is still up).</summary>
    public void Show(DistrictDef d)
    {
        if (d == null) return;
        if (GameObject.Find("DistrictWelcomeCard") != null) return; // the first-visit card is already saying it, louder
        if (card != null) Destroy(card);
        card = new GameObject("DistrictBannerCard");
        card.transform.SetParent(transform, false);
        var canvas = card.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 630;
        var scaler = card.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        UiScale.Register(scaler);
        fade = card.AddComponent<CanvasGroup>();
        fade.interactable = false;
        fade.blocksRaycasts = false;

        Color accent = Accent(d);
        bool locked = !DistrictAccess.LocalCanEnter(d);
        panel = FeelUi.Rect("Banner", card.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, RestY + 80f), new Vector2(W, H));
        FeelUi.Panel(panel, FeelUi.PanelColor, 1f, new Vector2(5f, -6f));
        var stripe = FeelUi.Rect("Stripe", panel, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(10f, H - 28f));
        stripe.gameObject.AddComponent<Image>().color = accent;
        var small = FeelUi.Text(panel, "Entering", 22f, new Color(1f, 1f, 1f, 0.7f), TextAlignmentOptions.Left);
        small.rectTransform.offsetMin = new Vector2(42f, H - 40f);
        small.rectTransform.offsetMax = new Vector2(-20f, -10f);
        small.text = locked ? "NOW ENTERING · NO PERMIT YET" : "NOW ENTERING";
        var name = FeelUi.Text(panel, "Name", 46f, accent, TextAlignmentOptions.Left);
        name.rectTransform.offsetMin = new Vector2(40f, 30f);
        name.rectTransform.offsetMax = new Vector2(-20f, -32f);
        name.text = d.name.ToUpperInvariant();
        name.enableAutoSizing = true;
        name.fontSizeMin = 26f;
        name.fontSizeMax = 46f;
        var line = FeelUi.Text(panel, "Line", 20f, FeelUi.Gold, TextAlignmentOptions.Left, FontStyles.Italic);
        line.rectTransform.offsetMin = new Vector2(42f, 8f);
        line.rectTransform.offsetMax = new Vector2(-20f, -(H - 34f));
        line.text = locked ? DistrictWelcome.Tagline(d) : $"{DistrictWelcome.Tagline(d)}  ·  {Economy.Money(d.fee)}/bag";
        line.enableAutoSizing = true;
        line.fontSizeMin = 13f;
        line.fontSizeMax = 20f;
        fade.alpha = 0f;
        shownAt = Time.unscaledTime;
        Sfx.Play("whoosh", null, 0.35f, 1.2f);
    }

    void LateUpdate()
    {
        if (card == null || panel == null) return;
        float t = Time.unscaledTime - shownAt;
        float inK = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / In));
        float outK = Mathf.Clamp01((t - In - Hold) / Out);
        panel.anchoredPosition = new Vector2(0f, Mathf.Lerp(RestY + 80f, RestY, inK) + outK * 30f);
        fade.alpha = inK * (1f - outK);
        if (t > In + Hold + Out) { Destroy(card); card = null; panel = null; }
    }
}
