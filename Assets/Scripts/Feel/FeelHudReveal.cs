using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The big "LEGENDARY FIND!" card: spinning sunburst, rarity ribbon, name, flavor, note (engraving etc),
/// appraisal that counts up, and UI confetti for Rare+. Pops in, bobs, auto-hides after ~3 s; extra reveals
/// queue up. Plays the rarity sound and a short slow-mo (Rare+) itself. Never blocks input.
/// </summary>
public sealed class FeelHudReveal
{
    const float CardWidth = 720f;
    const float TextWidth = 640f;
    const float OutTime = 0.3f;

    RectTransform root, card, rays, banner, confettiRoot;
    CanvasGroup group;
    Image raysImg, bannerImg, cardBody;
    TextMeshProUGUI bannerText, nameText, flavorText, noteText, valueText, footerText;

    readonly Queue<ItemInstance> queue = new Queue<ItemInstance>();
    ItemInstance current;
    Rarity rarity;
    Color rarityColor;
    float age, hold, baseScale = 1f, targetValue, shownValue, valuePunch = 1f, valuePunchVel;
    bool valueIsMoney, valueDone;
    string valuePrefix;

    struct Conf { public RectTransform rt; public Image img; public Vector2 pos, vel; public float rot, spin, life; public bool alive; }
    readonly List<Conf> confetti = new List<Conf>();

    public bool Showing => current != null;

    public void Build(RectTransform parent)
    {
        root = parent;
        group = root.gameObject.AddComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;

        rays = FeelUi.Rect("Rays", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, 1000f));
        raysImg = rays.gameObject.AddComponent<Image>();
        raysImg.sprite = FeelUi.Rays;
        raysImg.raycastTarget = false;

        confettiRoot = FeelUi.Stretch("Confetti", root);

        card = FeelUi.Rect("Card", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CardWidth, 400f));
        cardBody = FeelUi.Panel(card, new Color(0.1f, 0.08f, 0.16f, 0.96f), 1.2f, new Vector2(8f, -10f));

        nameText = MakeText("Name", 56f, Color.white, FontStyles.Bold);
        flavorText = MakeText("Flavor", 27f, new Color(0.9f, 0.9f, 0.97f), FontStyles.Italic);
        noteText = MakeText("Note", 26f, new Color(1f, 0.88f, 0.6f), FontStyles.Italic);
        valueText = MakeText("Value", 46f, FeelUi.Gold, FontStyles.Bold);
        footerText = MakeText("Footer", 21f, new Color(0.7f, 0.7f, 0.85f), FontStyles.Normal);

        banner = FeelUi.Rect("Banner", card, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(540f, 72f));
        bannerImg = FeelUi.Panel(banner, Color.white, 0.8f, new Vector2(5f, -6f));
        bannerText = FeelUi.Text(banner, "BannerText", 44f, Color.white, TextAlignmentOptions.Center);

        root.gameObject.SetActive(false);
    }

    TextMeshProUGUI MakeText(string name, float size, Color color, FontStyles style)
    {
        var t = FeelUi.Text(card, name, size, color, TextAlignmentOptions.Top, style);
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(TextWidth, 40f);
        t.textWrappingMode = TextWrappingModes.Normal;
        return t;
    }

    public void Show(ItemInstance item)
    {
        if (item == null || root == null) return;
        if (current != null) { if (queue.Count < 4) queue.Enqueue(item); return; }
        Begin(item);
    }

    void Begin(ItemInstance item)
    {
        current = item;
        age = 0f;
        var def = item.Def;
        rarity = def != null ? def.rarity : Rarity.Common;
        rarityColor = RarityInfo.Color(rarity);

        hold = rarity switch { Rarity.Legendary => 4.2f, Rarity.Epic => 3.6f, Rarity.Rare => 3.2f, _ => 2.8f };
        // minor finds get a smaller card so they interrupt less
        baseScale = rarity switch { Rarity.Legendary => 1f, Rarity.Epic => 0.95f, Rarity.Rare => 0.9f, Rarity.Uncommon => 0.8f, _ => 0.75f };
        bannerText.text = rarity switch
        {
            Rarity.Legendary => "LEGENDARY FIND!",
            Rarity.Epic => "EPIC FIND!",
            Rarity.Rare => "RARE FIND!",
            Rarity.Uncommon => "NICE FIND!",
            _ => "FOUND SOMETHING!",
        };
        bannerImg.color = rarityColor;
        raysImg.color = new Color(rarityColor.r, rarityColor.g, rarityColor.b, 0.55f);

        nameText.text = def != null ? def.name : (item.defId ?? "???");
        nameText.color = rarity == Rarity.Legendary ? FeelUi.Gold : Color.Lerp(rarityColor, Color.white, 0.55f);
        flavorText.text = def != null && !string.IsNullOrEmpty(def.flavor) ? def.flavor : "";
        noteText.text = !string.IsNullOrEmpty(item.note) ? $"\"{item.note}\"" : "";

        // appraisal: rolled value, else the catalog's midpoint; non-valuables show weight instead
        targetValue = item.value;
        if (targetValue <= 0f && def != null && def.maxValue > 0f) targetValue = (def.minValue + def.maxValue) * 0.5f;
        valueIsMoney = targetValue > 0f;
        valuePrefix = valueIsMoney ? "Worth ~" : "";
        shownValue = 0f;
        valueDone = false;
        valueText.text = valueIsMoney ? valuePrefix + Economy.Money(0f)
                       : $"{item.massKg:0.00} kg of {(def != null ? ItemCategoryInfo.DisplayName(def.category) : "stuff")}";

        string footer = !string.IsNullOrEmpty(item.foundAt) ? $"Found at {item.foundAt}" : "Found in the trash";
        if (valueIsMoney) footer += $"  -  condition {Mathf.RoundToInt(Mathf.Clamp01(item.condition) * 100f)}%";
        footerText.text = footer;

        Layout();
        root.gameObject.SetActive(true);
        root.SetAsLastSibling();
        group.alpha = 1f;
        card.localScale = Vector3.zero;

        switch (rarity)
        {
            // sound ids match what Sorting plays for the same find, so the 30 ms throttle de-dupes them
            case Rarity.Legendary:
                Sfx.Play("legendary");
                Juice.HitStop(0.6f, 0.15f);
                Juice.Shake(0.35f, 0.4f);
                SpawnConfetti(70);
                break;
            case Rarity.Epic:
                Sfx.Play("legendary");
                Juice.HitStop(0.45f, 0.2f);
                Juice.Shake(0.2f, 0.25f);
                SpawnConfetti(40);
                break;
            case Rarity.Rare:
                Sfx.Play("rare");
                Juice.HitStop(0.35f, 0.25f);
                SpawnConfetti(20);
                break;
            case Rarity.Uncommon:
                Sfx.Play("rare", null, 0.8f);
                break;
            default:
                Sfx.Play("sparkle");
                break;
        }
    }

    void Layout()
    {
        float y = -64f; // leave room under the ribbon
        y = Stack(nameText, y, 6f);
        y = Stack(flavorText, y, 6f);
        y = Stack(noteText, y, 6f);
        y -= 8f;
        y = Stack(valueText, y, 2f);
        y = Stack(footerText, y, 0f);
        float h = -y + 30f;
        card.sizeDelta = new Vector2(CardWidth, h);
        banner.anchoredPosition = new Vector2(0f, 0f);
        rays.sizeDelta = Vector2.one * Mathf.Max(900f, h * 2.4f);
    }

    static float Stack(TextMeshProUGUI t, float y, float gap)
    {
        bool has = !string.IsNullOrEmpty(t.text);
        t.gameObject.SetActive(has);
        if (!has) return y;
        var pref = t.GetPreferredValues(t.text, TextWidth, 0f);
        float h = Mathf.Ceil(pref.y) + 4f;
        t.rectTransform.sizeDelta = new Vector2(TextWidth, h);
        t.rectTransform.anchoredPosition = new Vector2(0f, y);
        return y - h - gap;
    }

    void SpawnConfetti(int n)
    {
        for (int i = 0; i < n; i++)
        {
            int slot = -1;
            for (int k = 0; k < confetti.Count; k++) if (!confetti[k].alive) { slot = k; break; }
            Conf c;
            if (slot < 0)
            {
                if (confetti.Count >= 100) return;
                c = new Conf();
                c.rt = FeelUi.Rect("Bit", confettiRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(12f, 20f));
                c.img = c.rt.gameObject.AddComponent<Image>();
                c.img.sprite = FeelUi.Rounded;
                c.img.raycastTarget = false;
                confetti.Add(c);
                slot = confetti.Count - 1;
            }
            c = confetti[slot];
            c.alive = true;
            c.pos = new Vector2(Random.Range(-120f, 120f), Random.Range(60f, 160f));
            c.vel = new Vector2(Random.Range(-700f, 700f), Random.Range(350f, 1100f));
            c.rot = Random.Range(0f, 360f);
            c.spin = Random.Range(-720f, 720f);
            c.life = Random.Range(1.6f, 2.6f);
            c.img.color = Color.HSVToRGB(Random.value, Random.Range(0.6f, 0.9f), 1f);
            c.rt.sizeDelta = new Vector2(Random.Range(9f, 14f), Random.Range(14f, 24f));
            c.rt.gameObject.SetActive(true);
            confetti[slot] = c;
        }
    }

    public void Tick(float dt, float time)
    {
        TickConfetti(dt);
        if (current == null) return;
        age += dt;

        if (age >= hold + OutTime)
        {
            current = null;
            if (queue.Count > 0) Begin(queue.Dequeue());
            else root.gameObject.SetActive(false);
            return;
        }

        // card: pop in with overshoot and a derpy tilt, bob while holding, shrink/fade out
        float s, rot, alpha = 1f, rise = 0f;
        if (age < 0.45f)
        {
            float p = age / 0.45f;
            s = Mathf.LerpUnclamped(0.2f, 1f, FeelEase.OutBack(p, 2.6f));
            rot = Mathf.Lerp(-10f, -1.5f, FeelEase.OutCubic(p));
        }
        else
        {
            s = 1f + 0.012f * Mathf.Sin(time * 3f);
            rot = -1.5f + Mathf.Sin(time * 2f);
        }
        if (age > hold)
        {
            float p = (age - hold) / OutTime;
            s *= 1f - FeelEase.InQuad(p) * 0.7f;
            alpha = 1f - p;
            rise = p * 50f;
        }
        s *= baseScale;
        card.localScale = new Vector3(s, s, 1f);
        card.localRotation = Quaternion.Euler(0f, 0f, rot);
        card.anchoredPosition = new Vector2(0f, rise);
        group.alpha = alpha;

        rays.localRotation = Quaternion.Euler(0f, 0f, time * 30f);
        rays.localScale = Vector3.one * (Mathf.Min(1f, age / 0.3f) * (1f + 0.05f * Mathf.Sin(time * 4f)));

        banner.localRotation = Quaternion.Euler(0f, 0f, -3f + 2f * Mathf.Sin(time * 6f));
        if (rarity == Rarity.Legendary)
        {
            FeelUi.Wave(bannerText, time, 5f, true, 9f);
            FeelUi.Wave(nameText, time, 3f, false, 6f);
            bannerImg.color = Color.Lerp(rarityColor, FeelUi.Gold, 0.5f + 0.5f * Mathf.Sin(time * 8f));
        }
        else if (rarity >= Rarity.Rare) FeelUi.Wave(bannerText, time, 3f, false, 7f);

        // appraisal counts up
        if (valueIsMoney && !valueDone)
        {
            float p = Mathf.Clamp01((age - 0.35f) / 0.9f);
            shownValue = targetValue * FeelEase.OutCubic(p);
            valueText.text = valuePrefix + Economy.Money(shownValue);
            if (p >= 1f) { valueDone = true; valuePunchVel += 10f; Sfx.Play("pop", null, 0.6f, 1.2f); }
        }
        valuePunch = FeelUi.Spring(valuePunch, ref valuePunchVel, 1f, dt, 300f, 12f);
        valueText.rectTransform.localScale = Vector3.one * Mathf.Clamp(valuePunch, 0.6f, 1.6f);
    }

    void TickConfetti(float dt)
    {
        for (int i = 0; i < confetti.Count; i++)
        {
            var c = confetti[i];
            if (!c.alive) continue;
            c.life -= dt;
            if (c.life <= 0f)
            {
                c.alive = false;
                c.rt.gameObject.SetActive(false);
                confetti[i] = c;
                continue;
            }
            c.vel.y -= 1400f * dt;
            c.vel.x *= Mathf.Exp(-1.5f * dt);
            if (c.vel.y < -260f) c.vel.y = -260f; // flutter: terminal velocity
            c.pos += c.vel * dt;
            c.rot += c.spin * dt;
            c.rt.anchoredPosition = c.pos;
            c.rt.localRotation = Quaternion.Euler(0f, 0f, c.rot);
            float flip = Mathf.Abs(Mathf.Sin(c.rot * Mathf.Deg2Rad * 2f));
            c.rt.localScale = new Vector3(0.3f + 0.7f * flip, 1f, 1f);
            var col = c.img.color;
            col.a = Mathf.Clamp01(c.life / 0.4f);
            c.img.color = col;
            confetti[i] = c;
        }
    }
}
