using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// treasure_wand (Docs/Progression.md §6.4) on the local player: picking up a bag beeps if it holds a valuable, with
/// <see cref="UpgradeEffects.WandDetect"/> chance (45% / 80%), deterministic by bagId (UpgradeEffects.DetectRoll with
/// salt "wand", so the 45% set is inside the 80% set) and never a false positive. L2 also beeps when you walk within
/// 2 m of such a curb bag. The wand pops out of your pocket to beep, then goes away again.
/// </summary>
[DisallowMultipleComponent]
public class TreasureWand : MonoBehaviour
{
    public const string Salt = "wand";
    public const float ProximityRadius = 2f;

    PlayerController pc;
    TrashCarrier carrier;
    readonly HashSet<string> nearBeeped = new HashSet<string>();
    float nextScanAt;
    Transform wand;

    /// <summary>Beeps this session (tests / stats).</summary>
    public int Beeps { get; private set; }

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        carrier = GetComponent<TrashCarrier>();
    }

    void OnEnable() => TrashBag.AnyPickedUp += OnAnyPickedUp;
    void OnDisable() => TrashBag.AnyPickedUp -= OnAnyPickedUp;

    /// <summary>Would the owned wand beep for this bag? (valuable inside AND the deterministic roll hits)</summary>
    public static bool WouldBeep(BagContents c)
    {
        float chance = UpgradeEffects.WandDetect;
        if (chance <= 0f || c == null || string.IsNullOrEmpty(c.bagId)) return false;
        return ShopModKit.HasValuable(c) && UpgradeEffects.DetectRoll(c.bagId, Salt, chance);
    }

    void OnAnyPickedUp(TrashBag bag, TrashCarrier by)
    {
        if (bag == null || by == null || by != carrier || pc == null || !pc.IsOwner) return;
        if (!WouldBeep(bag.Contents)) return;
        nearBeeped.Add(bag.Contents.bagId);
        Beep(bag, true);
    }

    void Update()
    {
        if (pc == null || !pc.IsOwner || pc.IsDriving) return;
        if (UpgradeEffects.Level(ShopIds.TreasureWand) < 2 || Time.time < nextScanAt) return;
        nextScanAt = Time.time + 0.25f;
        Vector3 me = transform.position;
        foreach (var stop in PickupStop.All)
        {
            if (stop == null || (stop.transform.position - me).sqrMagnitude > 9f * 9f) continue;
            var bags = stop.SpawnedBags;
            for (int i = 0; i < bags.Count; i++)
            {
                var b = bags[i];
                if (b == null || b.State != CarryState.Resting || b.Contents == null) continue;
                Vector3 d = b.WorldCenter - me; d.y = 0f;
                if (d.sqrMagnitude > ProximityRadius * ProximityRadius) continue;
                if (nearBeeped.Contains(b.Contents.bagId) || !WouldBeep(b.Contents)) continue;
                nearBeeped.Add(b.Contents.bagId);
                Beep(b, false);
                return; // one at a time
            }
        }
        if (nearBeeped.Count > 400) nearBeeped.Clear();
    }

    void Beep(TrashBag bag, bool pickedUp)
    {
        Beeps++;
        StartCoroutine(BeepFx(bag, pickedUp));
    }

    IEnumerator BeepFx(TrashBag bag, bool pickedUp)
    {
        var gold = new Color(1f, 0.84f, 0.2f);
        ShowWand(bag);
        for (int i = 0; i < 3; i++)
        {
            Vector3 at = bag != null ? bag.WorldCenter : transform.position + Vector3.up;
            Sfx.Play("beep", at, 0.55f, 1.2f + 0.25f * i);
            if (wand != null) Juice.Punch(wand, 0.25f, 0.12f);
            if (i == 0)
            {
                GameHUD.FloatingText(at + Vector3.up * 0.8f, pickedUp ? "BEEP BEEP! * BLING INSIDE *" : "beep? *", gold, pickedUp ? 1.1f : 0.9f);
                if (bag != null) Fx.Sparkle(bag.JuiceTarget, gold, 1.6f);
            }
            yield return new WaitForSeconds(0.13f);
        }
        if (bag != null) Sfx.Play("sparkle", bag.WorldCenter, 0.4f, 1.3f);
        yield return new WaitForSeconds(0.5f);
        HideWand();
    }

    void ShowWand(TrashBag bag)
    {
        if (wand == null)
        {
            var chrome = ShopMaterials.Get("mat/shop_chrome", new Color(0.86f, 0.88f, 0.92f), 0.85f, 0.9f);
            var black = ShopMaterials.Get("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
            var led = ShopModKit.Emissive("mat/shop_wand_led", new Color(1f, 0.2f, 0.15f), 3f);
            wand = new GameObject("Treasure Wand").transform;
            wand.SetParent(transform, false);
            ShopModKit.Prim(PrimitiveType.Cylinder, "Shaft", wand, chrome, new Vector3(0f, 0f, 0.3f), new Vector3(0.03f, 0.3f, 0.03f), new Vector3(90f, 0f, 0f));
            ShopModKit.Prim(PrimitiveType.Cylinder, "Grip", wand, black, new Vector3(0f, 0f, 0.02f), new Vector3(0.05f, 0.08f, 0.05f), new Vector3(90f, 0f, 0f));
            ShopModKit.Prim(PrimitiveType.Cylinder, "Coil", wand, black, new Vector3(0f, -0.02f, 0.62f), new Vector3(0.2f, 0.015f, 0.2f));
            ShopModKit.Prim(PrimitiveType.Sphere, "LED", wand, led, new Vector3(0f, 0.04f, 0.1f), Vector3.one * 0.035f);
        }
        wand.localPosition = new Vector3(0.32f, 0.95f, 0.25f);
        wand.localRotation = Quaternion.Euler(28f, -12f, 0f);
        wand.gameObject.SetActive(true);
        Juice.PopIn(wand, 0.15f);
    }

    void HideWand()
    {
        if (wand != null) wand.gameObject.SetActive(false);
    }
}
