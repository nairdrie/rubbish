using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The wobbly <b>RUBBISH</b> logo (Docs/UIPass.md §3, §4.1): one outlined letter per recycling-bin colour, bobbing on a wave;
/// the I wears a googly eye for its dot that follows the mouse; every few seconds one letter tips over and springs back; a
/// splash sticker underneath ("Now with 40% more raccoons!"); and a fly that buzzes round the logo while Quit is hovered.
/// Everything runs on unscaled time and the resting look is set at build time (edit-mode previews need no Update).
/// </summary>
public sealed class MenuTitle : MonoBehaviour
{
    RectTransform root, eye, pupil, splash, flyRt;
    RectTransform[] letters;
    TextMeshProUGUI[] glyphs;
    Image flyImg;
    float[] tipAngle, tipVel;
    float height, width, nextTipAt = 4f, flyA, pupilVx, pupilVy;
    Vector2 pupilPos;
    int tipping = -1;
    float tipT;
    bool flyOn, live;
    Camera uiCam;

    /// <summary>Logo width in reference px (for layout).</summary>
    public float Width => width;
    /// <summary>Logo height including the splash sticker.</summary>
    public float TotalHeight => height + SplashHeight + 6f;
    public const float SplashHeight = 46f;
    /// <summary>The logo's own rect (letters only; for overlap audits).</summary>
    public RectTransform LettersRect => root;
    public RectTransform SplashRect => splash;

    /// <summary>Build the logo under <paramref name="parent"/> with letters <paramref name="h"/> px tall (cap height ≈ 0.72 h).</summary>
    public static MenuTitle Build(RectTransform parent, float h, string splashText, Camera cam, bool animate)
    {
        var rt = FeelUi.Rect("Title", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(10f, h));
        var t = rt.gameObject.AddComponent<MenuTitle>();
        t.root = rt;
        t.uiCam = cam;
        t.live = animate;
        t.Make(h, splashText);
        return t;
    }

    static readonly Color[] LetterColors =
    {
        UiTheme.BinColors[0], // R hazard yellow
        UiTheme.BinColors[1], // U metal grey
        UiTheme.BinColors[2], // B plastic blue
        UiTheme.BinColors[3], // B glass green
        UiTheme.BinColors[4], // I paper brown (googly eye)
        UiTheme.BinColors[5], // S KEEP gold
        UiTheme.Tangerine,    // H Chuck
    };

    void Make(float h, string splashText)
    {
        height = h;
        string word = UiText.MenuTitle;
        int n = word.Length;
        letters = new RectTransform[n];
        glyphs = new TextMeshProUGUI[n];
        tipAngle = new float[n];
        tipVel = new float[n];
        float size = h * 0.95f;
        float x = 0f;
        for (int i = 0; i < n; i++)
        {
            string ch = word[i].ToString();
            bool isI = word[i] == 'I';
            var holder = FeelUi.Rect("Letter " + ch, root, new Vector2(0f, 0f), new Vector2(0.5f, 0.08f), Vector2.zero, new Vector2(10f, h));
            var g = UiKit.OutlineLabel(holder, "Glyph", ch, isI ? size * 0.86f : size, LetterColors[i % LetterColors.Length],
                                       TextAlignmentOptions.Bottom);
            g.characterSpacing = 0f;
            float w = g.GetPreferredValues(ch).x;
            holder.sizeDelta = new Vector2(w, h * 1.3f); // room for the outline and the line height (bottom-aligned)
            // derpy: letters overlap a little, alternate tilts and heights
            float tilt = (i % 2 == 0 ? -1f : 1f) * (2.5f + (i * 37 % 5));
            holder.anchoredPosition = new Vector2(x + w * 0.5f, (i % 3 == 1 ? 6f : i % 3 == 2 ? -3f : 0f));
            holder.localRotation = Quaternion.Euler(0f, 0f, tilt);
            letters[i] = holder;
            glyphs[i] = g;
            g.rectTransform.pivot = new Vector2(0.88f, 0.06f); // tips over its bottom-right corner
            // Fredoka's round bowls and the fat outline need more room than LiberationSans did (0.9 hid half of each B)
            x += w * (UiTheme.HasFontSet ? 1.02f : 0.9f);
            if (isI) MakeEye(g, size);
        }
        width = x + 12f;
        root.sizeDelta = new Vector2(width, h);

        // splash sticker under the right end of the logo
        splash = FeelUi.Rect("Splash", root, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(10f, -2f), new Vector2(380f, SplashHeight));
        var sh = FeelUi.Img(splash, "Shadow", FeelUi.Rounded, UiTheme.Shadow, 0.4f);
        sh.rectTransform.offsetMin = sh.rectTransform.offsetMax = new Vector2(3f, -4f);
        FeelUi.Img(splash, "Body", FeelUi.Rounded, UiTheme.Gold, 0.4f);
        FeelUi.Img(splash, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.4f), 0.4f);
        var st = UiKit.InkLabel(splash, "Text", splashText, 24f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        float sw = Mathf.Min(st.GetPreferredValues(splashText).x + 40f, 520f);
        splash.sizeDelta = new Vector2(sw, SplashHeight);
        st.enableAutoSizing = true;
        st.fontSizeMax = 24f;
        st.fontSizeMin = UiTheme.MinText;
        UiKit.Inset(st.rectTransform, 16f, 2f, 16f, 2f);
        splash.localRotation = Quaternion.Euler(0f, 0f, -4f);

        // the fly (hidden until Quit is hovered)
        flyRt = FeelUi.Rect("Fly", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(40f, 40f));
        flyImg = FeelUi.Img(flyRt, "Body", MenuArt.Fly, new Color(1f, 1f, 1f, 0f));
    }

    void MakeEye(TextMeshProUGUI g, float size)
    {
        float d = Mathf.Max(30f, size * 0.36f);
        // sit the eye on top of the I's stem (measured from the glyph, so it fits any font)
        var r = g.rectTransform.rect;
        float cx = r.width * 0.5f, top = size * 0.7f;
        g.ForceMeshUpdate();
        var ti = g.textInfo;
        if (ti != null && ti.characterCount > 0 && ti.characterInfo[0].isVisible)
        {
            var ci = ti.characterInfo[0];
            top = ci.topRight.y - r.yMin;
            cx = (ci.topLeft.x + ci.topRight.x) * 0.5f - r.xMin;
        }
        eye = FeelUi.Rect("GooglyEye", g.rectTransform, Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(cx, top + d * 0.62f), new Vector2(d, d));
        var back = FeelUi.Img(eye, "Shadow", FeelUi.Circle, new Color(0f, 0f, 0f, 0.35f));
        back.rectTransform.offsetMin = back.rectTransform.offsetMax = new Vector2(2f, -3f);
        FeelUi.Img(eye, "White", FeelUi.Circle, Color.white);
        FeelUi.Img(eye, "Rim", UiKit.Sprites.Ring, UiTheme.Ink);
        pupil = FeelUi.Rect("Pupil", eye, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(d * 0.08f, -d * 0.12f), new Vector2(d * 0.5f, d * 0.5f));
        FeelUi.Img(pupil, "Ink", FeelUi.Circle, UiTheme.Ink);
        var glint = FeelUi.Rect("Glint", pupil, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(d * 0.07f, d * 0.08f), new Vector2(d * 0.14f, d * 0.14f));
        FeelUi.Img(glint, "White", FeelUi.Circle, new Color(1f, 1f, 1f, 0.9f));
        pupilPos = pupil.anchoredPosition;
    }

    /// <summary>The fly buzzes round the logo (Quit hovered).</summary>
    public void SetFly(bool on) => flyOn = on;

    /// <summary>Make letter <paramref name="i"/> tip over now (tests / clicks).</summary>
    public void Tip(int i)
    {
        if (letters == null || i < 0 || i >= letters.Length) return;
        tipping = i;
        tipT = 0f;
    }

    void Update()
    {
        if (!live || letters == null) return;
        float t = Time.unscaledTime;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);

        // wave
        for (int i = 0; i < letters.Length; i++)
        {
            var g = glyphs[i].rectTransform;
            g.anchoredPosition = new Vector2(0f, Mathf.Sin(t * 2.2f + i * 0.7f) * height * 0.035f);
        }

        // one letter tips over every 4–9 s and springs back
        if (tipping < 0 && t >= nextTipAt)
        {
            Tip(Random.Range(0, letters.Length)); // coop:allow(random) cosmetic: which logo letter tips over
            nextTipAt = t + Random.Range(4f, 9f); // coop:allow(random) cosmetic: logo idle timing
        }
        for (int i = 0; i < letters.Length; i++)
        {
            float target = 0f;
            if (i == tipping)
            {
                tipT += dt;
                target = tipT < 0.55f ? 62f : 0f;
                if (tipT > 1.6f) tipping = -1;
            }
            tipAngle[i] = FeelUi.Spring(tipAngle[i], ref tipVel[i], target, dt, i == tipping && tipT < 0.55f ? 60f : 140f, i == tipping && tipT < 0.55f ? 6f : 7f);
            glyphs[i].rectTransform.localRotation = Quaternion.Euler(0f, 0f, -tipAngle[i]);
        }

        // googly eye: the pupil leans toward the mouse and jiggles
        if (eye != null)
        {
            Vector2 local;
            Vector2 want = pupilPos;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(eye, Input.mousePosition, uiCam, out local))
            {
                float r = eye.rect.width * 0.24f;
                want = local.sqrMagnitude > 1f ? local.normalized * Mathf.Min(r, local.magnitude * 0.2f) : Vector2.zero;
                want.y -= eye.rect.width * 0.03f; // gravity: it sits a touch low
            }
            var p = pupil.anchoredPosition;
            p.x = FeelUi.Spring(p.x, ref pupilVx, want.x, dt, 220f, 9f);
            p.y = FeelUi.Spring(p.y, ref pupilVy, want.y, dt, 220f, 9f);
            pupil.anchoredPosition = p;
        }

        // splash sticker pulse (the classic)
        if (splash != null) splash.localScale = Vector3.one * (1f + 0.035f * Mathf.Abs(Mathf.Sin(t * 3.4f)));

        // the fly
        flyA = Mathf.MoveTowards(flyA, flyOn ? 1f : 0f, dt * 3f);
        if (flyImg != null)
        {
            flyImg.color = new Color(1f, 1f, 1f, flyA);
            if (flyA > 0f)
            {
                float a = t * 3.1f;
                flyRt.anchoredPosition = new Vector2(Mathf.Sin(a) * width * 0.45f + Mathf.Sin(a * 4.3f) * 14f,
                                                     Mathf.Sin(a * 1.7f) * height * 0.45f + Mathf.Cos(a * 5.1f) * 10f);
                flyRt.localScale = new Vector3(Mathf.Cos(a) > 0f ? 1f : -1f, 0.8f + 0.2f * Mathf.Abs(Mathf.Sin(t * 60f)), 1f);
            }
        }
    }
}
