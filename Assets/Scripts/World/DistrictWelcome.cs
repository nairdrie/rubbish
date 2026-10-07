using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The big "WELCOME TO SNOOTINGTON HILLS: Please Wipe Your Tires" card that slams onto the screen when the truck snaps a
/// district's ribbon for the first time (Docs/Progression.md §5.3). Its own overlay canvas built from the public FeelUi
/// helpers; unscaled time, no Time.timeScale. Also the per-district welcome lines the gates' signs use.
/// </summary>
public class DistrictWelcome : MonoBehaviour
{
    /// <summary>Title line for a district ("WELCOME TO MINIVAN MEADOWS").</summary>
    public static string Title(DistrictDef d) => d == null ? "WELCOME!" : "WELCOME TO " + d.name.ToUpperInvariant();

    /// <summary>The derpy tagline under the title.</summary>
    public static string Tagline(DistrictDef d)
    {
        if (d == null) return "";
        switch (d.id)
        {
            case Districts.MinivanMeadows: return "An HOA Community · Pop. 64 minivans · Please Signal Your Feelings";
            case Districts.SnootingtonHills: return "Please Wipe Your Tires";
            case Districts.BillionaireBluffs: return "Name? …Garbage? …Fine.";
            default: return "Mildly Pleasant. Mostly.";
        }
    }

    /// <summary>One line about what the district pays (shown under the tagline).</summary>
    public static string PayLine(DistrictDef d)
    {
        if (d == null) return "";
        string tip = d.tip > 0f ? $" + {Economy.Money(d.tip)} {(d.butlerDrops ? "butler " : "")}tip" : "";
        string drops = d.butlerDrops ? " · Butler Drops at 7 AM and 5 PM" : "";
        return $"{Economy.Money(d.fee)}/bag{tip} if on time ({d.onTimeHours:0.#} h) · Rep x{d.repMult:0.#}{drops}";
    }

    static readonly Color[] Accent =
    {
        new Color(0.45f, 1f, 0.5f),      // Mildly Pleasant
        new Color(1f, 0.6f, 0.85f),      // Meadows: pastel pink
        new Color(0.75f, 0.9f, 1f),      // Snootington: silver-blue
        new Color(1f, 0.82f, 0.3f),      // Bluffs: gold
    };

    /// <summary>Slam the welcome card onto the screen (local presentation only).</summary>
    public static void Show(DistrictDef d)
    {
        if (d == null) return;
        var go = new GameObject("DistrictWelcomeCard");
        var card = go.AddComponent<DistrictWelcome>();
        card.Build(d);
    }

    RectTransform panel;
    TextMeshProUGUI title, tagline, payLine;
    float t;
    float scale, scaleVel;
    const float Hold = 4.2f;

    void Build(DistrictDef d)
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 640;
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        UiScale.Register(scaler); // Settings → Interface → UI scale

        Color accent = Accent[Mathf.Clamp(d.tier - 1, 0, Accent.Length - 1)];
        // Sits in the clear band between the top HUD rows (toasts at y -172, the route planner on the right down to about
        // y -412) and the truck: 1000 wide so it clears both HUD columns, and every line shrinks to fit (the titles and
        // the Meadows tagline used to overflow the card and spill over the route panel).
        const float W = 1000f, H = 330f;
        panel = FeelUi.Rect("Card", transform, new Vector2(0.5f, 0.6f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, H));
        FeelUi.Panel(panel, FeelUi.PanelColor, 1.4f, new Vector2(8f, -10f));
        var stripe = FeelUi.Rect("Stripe", panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(W - 60f, 14f));
        stripe.gameObject.AddComponent<Image>().color = accent;

        title = FeelUi.Text(panel, "Title", 82f, accent, TextAlignmentOptions.Center);
        title.rectTransform.offsetMin = new Vector2(24f, H - 132f);
        title.rectTransform.offsetMax = new Vector2(-24f, -30f);
        title.text = Title(d);
        FitSize(title, W - 48f, 0f, 82f, 44f);
        tagline = FeelUi.Text(panel, "Tagline", 44f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold | FontStyles.Italic);
        tagline.textWrappingMode = TextWrappingModes.Normal;
        tagline.rectTransform.offsetMin = new Vector2(30f, 64f);
        tagline.rectTransform.offsetMax = new Vector2(-30f, -136f);
        tagline.text = Tagline(d);
        FitSize(tagline, W - 60f, H - 64f - 136f, 44f, 26f);
        payLine = FeelUi.Text(panel, "Pay", 30f, FeelUi.Gold, TextAlignmentOptions.Center);
        payLine.rectTransform.offsetMin = new Vector2(24f, 16f);
        payLine.rectTransform.offsetMax = new Vector2(-24f, -(H - 62f));
        payLine.text = PayLine(d);
        FitSize(payLine, W - 48f, 0f, 30f, 20f);

        scale = 0.2f; scaleVel = 0f;
        panel.localScale = Vector3.one * scale;
        panel.localRotation = Quaternion.Euler(0f, 0f, -6f);
        Sfx.Play("slam", null, 0.8f, 0.9f);
        Sfx.Play("legendary", null, 0.7f, 1f);
        Juice.Shake(0.25f, 0.3f);
    }

    /// <summary>Largest font size in [min, max] (2 pt steps) whose text fits maxWidth (and maxHeight when wrapping, if &gt; 0).</summary>
    static void FitSize(TMP_Text t, float maxWidth, float maxHeight, float max, float min)
    {
        if (t == null || string.IsNullOrEmpty(t.text)) return;
        bool wrap = t.textWrappingMode != TextWrappingModes.NoWrap && maxHeight > 0f;
        for (float size = max; ; size -= 2f)
        {
            t.fontSize = Mathf.Max(min, size);
            Vector2 pref = wrap ? t.GetPreferredValues(t.text, maxWidth, 0f) : t.GetPreferredValues(t.text, 100000f, 0f);
            bool fits = wrap ? pref.y <= maxHeight : pref.x <= maxWidth;
            if (fits || size <= min) break;
        }
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        t += dt;
        float target = t < Hold ? 1f : 0f;
        scale = FeelUi.Spring(scale, ref scaleVel, target, dt, 260f, 15f);
        if (panel != null)
        {
            panel.localScale = Vector3.one * Mathf.Max(0f, scale);
            panel.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(-6f, -1.5f, Mathf.Clamp01(t * 3f)) + Mathf.Sin(t * 2.2f) * 0.6f);
        }
        if (title != null) FeelUi.Wave(title, Time.unscaledTime, 4f, false, 7f);
        if (t > Hold + 0.6f) Destroy(gameObject);
    }
}
