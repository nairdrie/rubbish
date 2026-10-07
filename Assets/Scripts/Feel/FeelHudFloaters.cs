using TMPro;
using UnityEngine;

/// <summary>
/// World-anchored floating labels ("+$0.42") drawn on the HUD canvas: each frame the anchor is projected to the
/// screen, so they stay crisp and always face the camera. They pop in, rise, and fade; hidden when behind the
/// camera. Pooled (oldest is recycled when full). Near-identical labels spawned at the same spot in the same
/// instant are merged (the HUD and a caller may both react to one payout).
/// </summary>
public sealed class FeelHudFloaters
{
    public const int Max = 48;
    const float Life = 1.25f;
    const float Rise = 1.3f;
    // Several different words popping at one spot at once ("OWIE!" "CHOMP!" "heh heh" "*drops bag*") used to print
    // on top of each other: each extra one now waits a beat and sits a line LOWER (the earlier words are already
    // rising away, so the gap only grows), zig-zagging a little sideways.
    const float CrowdRadius = 1.6f;   // world m
    const float CrowdWindow = 0.45f;  // s: floaters younger than this count as "the same moment"
    const float StaggerDelay = 0.08f; // s per crowd slot
    const float StackStep = 52f;      // canvas px per crowd slot ≈ one line of floater text at a typical scale
    const int MaxStack = 4;

    sealed class F
    {
        public RectTransform rt;
        public TextMeshProUGUI text;
        public Vector3 world;
        public Vector2 drift;
        public float age, scale;
        public int slot;
        public bool active;
        public string str;
    }

    RectTransform root;
    readonly F[] items = new F[Max];
    int created, cursor;

    public void Build(RectTransform parent) => root = parent;

    public void Show(Vector3 world, string text, Color color, float scale)
    {
        if (root == null || string.IsNullOrEmpty(text)) return;
        int slot = 0;
        bool[] taken = null;
        for (int i = 0; i < created; i++)
        {
            var f = items[i];
            if (!f.active) continue;
            float d2 = (f.world - world).sqrMagnitude;
            if (f.age < 0.15f && f.str == text && d2 < 1f) return;
            if (f.age < CrowdWindow && d2 < CrowdRadius * CrowdRadius && f.slot < MaxStack)
            {
                taken ??= new bool[MaxStack];
                taken[f.slot] = true;
            }
        }
        if (taken != null)
        {
            slot = MaxStack - 1;
            for (int s = 0; s < MaxStack; s++) if (!taken[s]) { slot = s; break; }
        }

        F it = null;
        for (int i = 0; i < created; i++) if (!items[i].active) { it = items[i]; break; }
        if (it == null && created < Max) it = items[created++] = Create();
        if (it == null) { it = items[cursor]; cursor = (cursor + 1) % Max; }

        it.active = true;
        it.world = world;
        it.slot = slot;
        it.age = -StaggerDelay * slot; // negative age = waiting its turn (hidden)
        it.str = text;
        it.scale = Mathf.Clamp(scale, 0.3f, 4f);
        it.drift = new Vector2(Random.Range(-0.25f, 0.25f), 0f);
        it.text.text = text;
        it.text.color = color;
        it.text.alpha = 1f;
        it.rt.gameObject.SetActive(true);
        it.rt.SetAsLastSibling();
        it.rt.localScale = Vector3.zero;
    }

    F Create()
    {
        var f = new F();
        f.rt = FeelUi.Rect("Floater", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(400f, 60f));
        f.text = FeelUi.Text(f.rt, "Text", 42f, Color.white, TextAlignmentOptions.Center);
        f.rt.gameObject.SetActive(false);
        return f;
    }

    public void Clear()
    {
        for (int i = 0; i < created; i++)
        {
            items[i].active = false;
            if (items[i].rt != null) items[i].rt.gameObject.SetActive(false);
        }
    }

    public void Tick(float dt, Camera cam, Canvas canvas)
    {
        for (int i = 0; i < created; i++)
        {
            var f = items[i];
            if (!f.active) continue;
            f.age += dt;
            if (f.age >= Life || cam == null)
            {
                f.active = false;
                f.rt.gameObject.SetActive(false);
                continue;
            }
            if (f.age < 0f)
            {
                // staggered: not its turn yet
                if (f.text.enabled) f.text.enabled = false;
                continue;
            }
            float p = f.age / Life;
            var right = cam.transform.right;
            var w = f.world + Vector3.up * (Rise * FeelEase.OutQuad(p)) + right * (f.drift.x * p);
            var sp = cam.WorldToScreenPoint(w);
            bool visible = sp.z > 0.1f;
            float dist = Mathf.Clamp(9f / Mathf.Max(0.5f, sp.z), 0.5f, 1.25f);
            if (visible)
            {
                var uiCam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                visible = RectTransformUtility.ScreenPointToLocalPointInRectangle(root, sp, uiCam, out var lp);
                if (visible)
                {
                    if (f.slot > 0)
                    {
                        // same step for every slot (not the word's own scale), so small and big words can't collide
                        float k = dist * 1.25f;
                        lp += new Vector2((f.slot % 2 == 1 ? -1f : 1f) * 26f * k, -f.slot * StackStep * k);
                    }
                    f.rt.anchoredPosition = lp;
                }
            }
            if (f.text.enabled != visible) f.text.enabled = visible;
            if (!visible) continue;

            float pop = f.age < 0.18f ? FeelEase.OutBack(f.age / 0.18f, 3f) : 1f;
            f.rt.localScale = Vector3.one * (pop * dist * f.scale);
            f.text.alpha = p < 0.6f ? 1f : 1f - (p - 0.6f) / 0.4f;
        }
    }
}
