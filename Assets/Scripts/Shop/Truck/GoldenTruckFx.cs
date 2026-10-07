using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Truck looks + toys (Docs/Progression.md §6.5 golden_truck, §6.7 cosmetics), all renderer-only / per-renderer
/// MaterialPropertyBlocks on the truck body's MainBody + Cab materials (the prefab's materials are never touched):
///   golden_truck        -> 24K gold paint, a sparkle trail, a fanfare horn, curbs cheer as you drive past, and a
///                          Scrapazon drone-swarm delivery when you buy it (top speed 18 m/s comes from UpgradeEffects.Engine).
///   truck_paint_*       -> primer / flames / raccoon camo / chrome (the best owned paint shows; gold beats them all).
///   horn_pack_musical   -> H plays a little tune after the honk.
///   trash_fm_radio      -> a DJ chats over your shift while you drive (toasts + floating notes).
/// Added by <see cref="ShopEffectsRunner"/> to every truck.
/// </summary>
[DisallowMultipleComponent]
public class GoldenTruckFx : MonoBehaviour
{
    public const string PaintRootName = "Scrapazon Paint";

    static readonly List<GoldenTruckFx> live = new List<GoldenTruckFx>();
    static bool deliveryPending;

    static readonly Color Gold = new Color(1f, 0.78f, 0.22f);

    enum Paint { Stock, Primer, Flames, Camo, Chrome, Golden }

    TruckControllerV2 truck;
    Rigidbody body;
    Renderer bodyRenderer;
    int mainIdx = -1, cabIdx = -1;
    Paint shown = (Paint)(-1);
    Transform paintRoot, tail;
    TrailRenderer[] trails;
    MaterialPropertyBlock mpb;
    float nextSparkleAt, nextCheerScan, nextHornAt, nextRadioAt, nextNoteAt;
    readonly Dictionary<PickupStop, float> cheeredAt = new Dictionary<PickupStop, float>();
    int radioLine = -1;

    void Awake()
    {
        truck = GetComponent<TruckControllerV2>();
        body = GetComponent<Rigidbody>();
        FindBodyRenderer();
    }

    void OnEnable()
    {
        if (!live.Contains(this)) live.Add(this);
        GameSession.UpgradeChanged += OnUpgradeChanged;
        Apply();
        if (deliveryPending && Application.isPlaying && UpgradeEffects.Level(ShopIds.GoldenTruck) > 0)
        {
            deliveryPending = false;
            StartCoroutine(DroneDelivery());
        }
    }

    void OnDisable()
    {
        live.Remove(this);
        GameSession.UpgradeChanged -= OnUpgradeChanged;
    }

    void OnDestroy()
    {
        if (bodyRenderer != null && truck != null && truck.gameObject.activeInHierarchy) SetBodyPaint(Paint.Stock);
    }

    void OnUpgradeChanged(string id, int level)
    {
        if (id == ShopIds.GoldenTruck || (id != null && id.StartsWith("truck_paint"))) Apply();
    }

    void FindBodyRenderer()
    {
        foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r == null || r.name != "TruckBody") continue;
            bodyRenderer = r;
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                string n = mats[i] != null ? mats[i].name : "";
                if (mainIdx < 0 && n.StartsWith("MainBody")) mainIdx = i;
                else if (cabIdx < 0 && n.StartsWith("Cab")) cabIdx = i;
            }
            break;
        }
    }

    static Paint Wanted()
    {
        if (UpgradeEffects.Level(ShopIds.GoldenTruck) > 0) return Paint.Golden;
        if (UpgradeEffects.Level(ShopIds.TruckPaintChrome) > 0) return Paint.Chrome;
        if (UpgradeEffects.Level(ShopIds.TruckPaintRaccoonCamo) > 0) return Paint.Camo;
        if (UpgradeEffects.Level(ShopIds.TruckPaintFlames) > 0) return Paint.Flames;
        if (UpgradeEffects.Level(ShopIds.TruckPaintPrimer) > 0) return Paint.Primer;
        return Paint.Stock;
    }

    /// <summary>Show the best owned paint. Idempotent.</summary>
    public void Apply()
    {
        var want = Wanted();
        if (want == shown) return;
        bool fresh = (int)shown >= 0;
        shown = want;
        SetBodyPaint(want);
        BuildPaintProps(want);
        if (fresh && Application.isPlaying)
        {
            Vector3 p = transform.position + Vector3.up * 2.5f;
            Fx.Burst(p, want == Paint.Golden ? Gold : new Color(0.9f, 0.9f, 0.95f), 30, 6f);
            Sfx.Play(want == Paint.Golden ? "legendary" : "sparkle", p, 0.8f, 1f);
            GameHUD.FloatingText(p + Vector3.up * 2f, want == Paint.Golden ? "24 KARAT!" : "fresh paint!", want == Paint.Golden ? Gold : Color.white, 1.4f);
        }
    }

    void SetBodyPaint(Paint p)
    {
        if (bodyRenderer == null) return;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        Color c; float metal, smooth;
        switch (p)
        {
            case Paint.Golden: c = new Color(1f, 0.82f, 0.3f); metal = 0.85f; smooth = 0.8f; break;
            case Paint.Chrome: c = new Color(0.92f, 0.93f, 0.96f); metal = 1f; smooth = 0.95f; break;
            case Paint.Camo: c = new Color(0.52f, 0.52f, 0.5f); metal = 0f; smooth = 0.25f; break;
            case Paint.Flames: c = new Color(0.85f, 0.15f, 0.08f); metal = 0.2f; smooth = 0.7f; break;
            case Paint.Primer: c = new Color(0.58f, 0.32f, 0.26f); metal = 0f; smooth = 0.08f; break;
            default:
                // Stock: clear our overrides.
                mpb.Clear();
                if (mainIdx >= 0) bodyRenderer.SetPropertyBlock(mpb, mainIdx);
                if (cabIdx >= 0) bodyRenderer.SetPropertyBlock(mpb, cabIdx);
                return;
        }
        mpb.Clear();
        mpb.SetColor("_BaseColor", c);
        mpb.SetFloat("_Metallic", metal);
        mpb.SetFloat("_Smoothness", smooth);
        if (mainIdx >= 0) bodyRenderer.SetPropertyBlock(mpb, mainIdx);
        if (cabIdx >= 0) bodyRenderer.SetPropertyBlock(mpb, cabIdx);
    }

    /// <summary>The hopper flank's half-width (m) at truck-metre z, measured on the body collider at y 1.6-2.0 (tapers to the cab).</summary>
    static float Flank(float z) => Mathf.Lerp(1.07f, 1.22f, Mathf.InverseLerp(-1f, 2f, z));

    void BuildPaintProps(Paint p)
    {
        trails = null;
        tail = null;
        if (p == Paint.Stock || p == Paint.Primer || p == Paint.Chrome)
        {
            var old = transform.Find(PaintRootName);
            if (old != null) Destroy(old.gameObject);
            paintRoot = null;
            return;
        }
        paintRoot = ShopModKit.MetreRoot(transform, PaintRootName);
        if (p == Paint.Flames)
        {
            var orange = ShopModKit.Emissive("mat/shop_flame_orange", new Color(1f, 0.5f, 0.05f), 1.2f);
            var yellow = ShopModKit.Emissive("mat/shop_flame_yellow", new Color(1f, 0.85f, 0.15f), 1.2f);
            var red = ShopModKit.Emissive("mat/shop_flame_red", new Color(0.95f, 0.15f, 0.05f), 1f);
            for (int s = -1; s <= 1; s += 2)
                for (int i = 0; i < 5; i++)
                {
                    // Hot-rod flames licking back along the hopper flank from the cab: each tongue is three blobs that
                    // shrink and rise toward the rear, yellow inside orange (the flank tapers, so each sits on the paint).
                    float z0 = 1.9f - i * 0.12f;
                    float y0 = 1.55f + i * 0.17f;
                    float len = 1.6f + (i % 2 == 0 ? 0.6f : 0f) - Mathf.Abs(i - 2) * 0.25f;
                    for (int k = 0; k < 3; k++)
                    {
                        float z = z0 - len * (0.25f + 0.33f * k);
                        float y = y0 + 0.07f * k * k;
                        float sz = 1f - 0.3f * k;
                        var mat = k == 2 ? red : orange;
                        var blob = ShopModKit.Prim(PrimitiveType.Sphere, "Flame", paintRoot, mat, new Vector3(s * (Flank(z) + 0.012f), y, z),
                            new Vector3(0.015f, 0.2f * sz, len * 0.42f * sz), new Vector3(-8f - 4f * k, s * -2f, 0f));
                        if (k < 2)
                            ShopModKit.Prim(PrimitiveType.Sphere, "Flame Core", blob, yellow, new Vector3(s * 0.7f, -0.05f, 0.05f), new Vector3(1f, 0.5f, 0.62f));
                    }
                }
        }
        else if (p == Paint.Camo)
        {
            var black = ShopMaterials.Get("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
            var grey = ShopMaterials.Get("mat/shop_camo_grey", new Color(0.62f, 0.62f, 0.6f), 0.3f);
            for (int s = -1; s <= 1; s += 2)
                for (int i = 0; i < 4; i++)
                {
                    float z = -1.0f + i * 0.8f;
                    ShopModKit.Prim(PrimitiveType.Cube, "Stripe", paintRoot, black, new Vector3(s * (Flank(z) + 0.012f), 2.0f, z), new Vector3(0.015f, 1.35f, 0.28f), new Vector3(0f, s * -2f, 0f));
                }
            // the bandit mask across the windscreen, and a ringed tail swinging off the back
            ShopModKit.Prim(PrimitiveType.Cube, "Mask", paintRoot, black, new Vector3(0f, 2.08f, 3.71f), new Vector3(2.0f, 0.3f, 0.03f));
            tail = ShopModKit.Pivot("Raccoon Tail", paintRoot, new Vector3(-0.75f, 2.85f, -1.95f));
            for (int i = 0; i < 6; i++)
                ShopModKit.Prim(PrimitiveType.Sphere, "Ring", tail, i % 2 == 0 ? grey : black, new Vector3(0f, -0.18f * i, -0.06f * i), Vector3.one * (0.3f - i * 0.025f));
        }
        else if (p == Paint.Golden)
        {
            var gold = ShopModKit.Emissive("mat/shop_gold_trim", Gold, 0.6f);
            // gold trim rails along the hopper flanks + a little crown on the cab
            for (int s = -1; s <= 1; s += 2)
                ShopModKit.Prim(PrimitiveType.Cube, "Trim", paintRoot, gold, new Vector3(s * (Flank(0.5f) + 0.02f), 1.3f, 0.5f), new Vector3(0.03f, 0.08f, 3.1f), new Vector3(0f, s * -1.9f, 0f));
            var crown = ShopModKit.Pivot("Crown", paintRoot, new Vector3(0f, 2.62f, 3.3f));
            ShopModKit.Prim(PrimitiveType.Cylinder, "Band", crown, gold, Vector3.zero, new Vector3(0.5f, 0.06f, 0.5f));
            for (int i = 0; i < 5; i++)
            {
                float a = i * 72f * Mathf.Deg2Rad;
                ShopModKit.Prim(PrimitiveType.Cube, "Point", crown, gold, new Vector3(Mathf.Cos(a) * 0.22f, 0.14f, Mathf.Sin(a) * 0.22f), new Vector3(0.07f, 0.22f, 0.07f), new Vector3(0f, -i * 72f, 0f));
            }
            // sparkle trails off the rear corners
            var trailMat = ShopModKit.Glow("mat/shop_gold_trail", new Color(1f, 0.85f, 0.3f, 0.8f));
            trails = new TrailRenderer[2];
            for (int s = 0; s < 2; s++)
            {
                var t = ShopModKit.Pivot("Gold Trail", paintRoot, new Vector3(s == 0 ? -1.1f : 1.1f, 2.9f, -2.2f));
                var tr = t.gameObject.AddComponent<TrailRenderer>();
                tr.sharedMaterial = trailMat;
                tr.time = 0.6f;
                tr.minVertexDistance = 0.2f;
                tr.widthMultiplier = 0.22f;
                tr.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
                var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.95f, 0.6f), 0f), new GradientColorKey(Gold, 1f) },
                          new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
                tr.colorGradient = g;
                tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                tr.emitting = false;
                trails[s] = tr;
            }
        }
    }

    // ------------------------------------------------------------------ per frame

    void Update()
    {
        if (truck == null) return;
        float now = Time.time;
        float speed = body != null ? body.linearVelocity.magnitude : 0f;

        if (tail != null)
            tail.localRotation = Quaternion.Euler(-25f + Mathf.Sin(now * 3.1f) * 12f - speed * 2f, Mathf.Sin(now * 2.3f) * 25f, 0f);

        bool golden = shown == Paint.Golden;
        if (golden && trails != null)
            foreach (var tr in trails) if (tr != null) tr.emitting = speed > 1.5f;
        if (golden && speed > 1.5f && now >= nextSparkleAt)
        {
            nextSparkleAt = now + 0.2f;
            Fx.Burst(Tp(new Vector3(ShopModKit.Range(-1f, 1f), ShopModKit.Range(1.2f, 3f), -2.4f)), Gold, 3, 1.2f);
        }

        bool seated = truck.IsLocalPlayerSeated && !PlayerController.IsUIBlocking && !SceneController.IsTransitioning;
        if (!seated) return;

        if (golden && speed > 2f && now >= nextCheerScan)
        {
            nextCheerScan = now + 0.35f;
            CurbCheer();
        }

        if (GameControls.Down(truck.hornKey, GameControls.Action.Horn) && now >= nextHornAt)
        {
            if (golden)
            {
                nextHornAt = now + 1.2f;
                Vector3 at = Tp(new Vector3(0f, 3.2f, 3.6f));
                Sfx.Play("legendary", at, 0.75f, 1f);
                GameHUD.FloatingText(Tp(new Vector3(0f, 6.6f, 2.8f)), "♪ TA-DA-DA-DAAAA ♪", Gold, 1.6f);
                Fx.Burst(at, Gold, 18, 5f);
            }
            else if (UpgradeEffects.Level(ShopIds.HornPackMusical) > 0)
            {
                nextHornAt = now + 2f;
                StartCoroutine(Tune());
            }
        }

        if (UpgradeEffects.Level(ShopIds.TrashFmRadio) > 0) Radio(now);
    }

    Vector3 Tp(Vector3 m) => transform.position + transform.rotation * m;

    static readonly string[] CheerWords = { "WOOO!", "GOLDEN!", "MY HERO!", "IS THAT... GOLD?", "TAKE MY TRASH!", "*swoons*", "BLING TRUCK!" };

    void CurbCheer()
    {
        int cheers = 0;
        float now = Time.time;
        foreach (var stop in PickupStop.All)
        {
            if (stop == null) continue;
            Vector3 d = stop.transform.position - transform.position; d.y = 0f;
            if (d.sqrMagnitude > 12f * 12f) continue;
            if (cheeredAt.TryGetValue(stop, out float t) && now - t < 45f) continue;
            cheeredAt[stop] = now;
            Vector3 at = stop.PileCenter + Vector3.up * 2.2f;
            GameHUD.FloatingText(at, ShopModKit.Pick(CheerWords), Gold, 1f);
            Fx.Burst(at, Gold, 10, 3.5f);
            Fx.Burst(at, new Color(1f, 0.4f, 0.7f), 6, 3f);
            Sfx.Play("ding", at, 0.35f, ShopModKit.Range(1.3f, 1.6f));
            if (++cheers >= 2) break;
        }
    }

    // La Cucaracha-ish, on the horn.
    static readonly float[] TuneNotes = { 0.75f, 0.75f, 0.75f, 1f, 1.26f, 0f, 0.75f, 0.75f, 0.75f, 1f, 1.26f };

    IEnumerator Tune()
    {
        yield return new WaitForSeconds(0.32f);
        GameHUD.FloatingText(Tp(new Vector3(0f, 6.4f, 2.8f)), "♪ LA CUCARAAACHA ♪", new Color(1f, 0.6f, 0.9f), 1.4f);
        foreach (float n in TuneNotes)
        {
            if (n > 0f)
            {
                Vector3 at = Tp(new Vector3(0f, 2.8f, 3.6f));
                Sfx.Play("horn", at, 0.7f, n);
                Fx.Burst(at + Vector3.up * 0.6f, new Color(1f, 0.6f, 0.9f), 3, 2f);
            }
            yield return new WaitForSeconds(n > 0f ? 0.16f : 0.28f);
        }
    }

    static readonly string[] RadioLines =
    {
        "TRASH FM 96.9: all rubbish, all the time.",
        "Up next on TRASH FM: 'Bin There, Done That' by The Rinds.",
        "Traffic update: it's you. You're the traffic.",
        "Weather tonight: 100% chance of raccoons.",
        "This hour is brought to you by Scrapazon. Delivered in 0.3 seconds. Regrets delivered later.",
        "Caller says her husband threw out a wedding ring. If you find it, uh... ebae, we guess.",
        "Now playing: the sound of your hopper. Beautiful. Haunting.",
        "TRASH FM: smooth sounds for smelly jobs.",
        "Reminder from Head Office: the bags go IN the truck.",
    };

    void Radio(float now)
    {
        if (nextRadioAt <= 0f) nextRadioAt = now + 6f;
        if (now >= nextNoteAt)
        {
            nextNoteAt = now + 1.4f;
            GameHUD.FloatingText(Tp(new Vector3(ShopModKit.Range(-0.6f, 0.6f), 2.9f, 3.1f)), ShopModKit.Rand01() < 0.5f ? "♪" : "♫", new Color(0.7f, 0.9f, 1f), 0.8f);
        }
        if (now < nextRadioAt) return;
        nextRadioAt = now + ShopModKit.Range(75f, 120f);
        radioLine = (radioLine + 1) % RadioLines.Length;
        GameHUD.Toast(RadioLines[radioLine], ToastStyle.Info, 3.5f);
        Sfx.Play("notify", null, 0.35f, 0.9f);
    }

    // ------------------------------------------------------------------ delivery

    /// <summary>Called when the Golden Truck is bought: drone-swarm delivery on every truck in the scene (or the next one).</summary>
    public static void PlayDelivery()
    {
        bool any = false;
        foreach (var fx in live)
            if (fx != null && fx.isActiveAndEnabled) { fx.StartCoroutine(fx.DroneDelivery()); any = true; }
        if (!any) deliveryPending = true;
    }

    IEnumerator DroneDelivery()
    {
        var yellow = ShopMaterials.Get("mat/shop_orange", new Color(1f, 0.6f, 0f), 0.4f);
        var black = ShopMaterials.Get("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
        const int n = 8;
        var drones = new Transform[n];
        for (int i = 0; i < n; i++)
        {
            var d = new GameObject("Delivery Drone").transform;
            ShopModKit.Prim(PrimitiveType.Cube, "Body", d, yellow, Vector3.zero, new Vector3(0.6f, 0.2f, 0.6f));
            ShopModKit.Prim(PrimitiveType.Cube, "Rotor", d, black, Vector3.up * 0.15f, new Vector3(0.9f, 0.02f, 0.08f));
            ShopModKit.Prim(PrimitiveType.Cube, "Rotor", d, black, Vector3.up * 0.15f, new Vector3(0.08f, 0.02f, 0.9f));
            drones[i] = d;
        }
        GameHUD.Toast("THE GOLDEN GARBAGE TRUCK HAS ARRIVED. 24K. Street legal-ish.", ToastStyle.Legendary, 4f);
        Sfx.Play("whoosh", transform.position, 1f, 0.6f);
        float t = 0f;
        const float dur = 2.6f;
        bool flashed = false;
        while (t < dur)
        {
            t += Time.deltaTime;
            float k = t / dur;
            Vector3 c = transform.position + Vector3.up * Mathf.Lerp(18f, 4.5f, Mathf.SmoothStep(0f, 1f, Mathf.Min(1f, k * 1.6f)));
            for (int i = 0; i < n; i++)
            {
                if (drones[i] == null) continue;
                float a = (i / (float)n) * Mathf.PI * 2f + t * 2.2f;
                float r = Mathf.Lerp(9f, 3.6f, Mathf.Min(1f, k * 1.5f));
                drones[i].position = c + new Vector3(Mathf.Cos(a) * r, Mathf.Sin(t * 5f + i) * 0.3f, Mathf.Sin(a) * r);
                drones[i].rotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 8f);
                if (k > 0.55f && ShopModKit.Rand01() < 0.15f)
                    Fx.Burst(drones[i].position + Vector3.down * 0.4f, Gold, 2, 2f);
            }
            if (!flashed && k > 0.7f)
            {
                flashed = true;
                Vector3 p = transform.position + Vector3.up * 2f;
                for (int i = 0; i < 4; i++) Fx.Burst(p, i % 2 == 0 ? Gold : Color.white, 40, 8f);
                Sfx.Play("legendary", p, 1f, 1f);
                Juice.Shake(0.3f, 0.35f);
                GameHUD.FloatingText(p + Vector3.up * 3f, "GOLDEN GARBAGE TRUCK!", Gold, 2.2f);
                Apply();
            }
            yield return null;
        }
        foreach (var d in drones) if (d != null) Juice.ShrinkAndDestroy(d.gameObject, 0.3f);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        live.Clear();
        deliveryPending = false;
    }
}
