using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The bar as every player in the room sees it (Docs/Casino.md §11): when a drink is served (<see cref="CasinoClient.BarServed"/>: the buyer's
/// own order and everybody else's alike) the bartender pours, a glass fills behind the counter and slides to the buyer's spot, rests there a
/// few seconds, drains and goes. The glass's colour and shape fit the drink (a pint of flat beer with foam, a short whisky, a Dumpster Fire
/// cocktail with a flame, a plain glass of water).
/// </summary>
public sealed class BarPresenter : CasinoStationPresenter
{
    const float PourSeconds = 1.5f, SlideSeconds = 0.75f, RestSeconds = 6f, DrainSeconds = 2.5f;

    enum Phase { Pouring, Sliding, Resting, Draining }

    sealed class Glass
    {
        public GameObject root;
        public Transform liquid, foam, flame, stream;
        public int drink, spot;
        public Phase phase;
        public double t0;
        public Vector3 from, to;
        public float height, liquidHeight;
        public bool slid;
    }

    readonly CasinoStation st;
    readonly List<Glass> glasses = new List<Glass>(4);
    static Mesh cylinder;

    public BarPresenter(CasinoStation station) { st = station; }

    public CasinoStation Station => st;
    public int GlassCount => glasses.Count;

    public override void Build(CasinoPresenters host)
    {
        this.host = host;
        if (st.spots == null || st.spots.Length == 0 || st.spots[0] == null || st.spots[0].counterSpot == null)
            host.WarnOnce("bar.spots", "the bar has no counter spots: drinks are served at the single counter point");
    }

    static Mesh Cylinder()
    {
        if (cylinder != null) return cylinder;
        cylinder = CasinoPropKit.Lathe("CasinoCylinder", new[] { new Vector2(0.001f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0.001f, 1f) }, 16);
        return cylinder;
    }

    // ------------------------------------------------------------------ serving

    /// <summary>Somebody's drink was paid for: pour it.</summary>
    public void Serve(int key, int drink, int spot)
    {
        if (host == null || !CasinoRules.IsDrink(drink)) return;
        spot = Mathf.Clamp(spot, 0, Mathf.Max(0, CasinoLayout.BarSpots - 1));
        var counter = CounterAt(spot, out Vector3 pour);
        double now = CasinoPresenters.Now;
        // one glass per spot at a time: a new order takes the old one away
        for (int i = glasses.Count - 1; i >= 0; i--) if (glasses[i].spot == spot) Remove(i);
        var g = Make(drink);
        g.drink = drink; g.spot = spot; g.phase = Phase.Pouring; g.t0 = now; g.slid = false;
        g.from = pour; g.to = counter;
        g.root.transform.SetPositionAndRotation(pour, Quaternion.identity);
        SetLevel(g, 0f);
        g.root.SetActive(true);
        glasses.Add(g);
        var bartender = CasinoStaff.Bartender;
        bartender.Play(BartenderGesture.Pour);
        Sfx.Play("pop", pour, 0.35f, 0.9f);
    }

    Vector3 CounterAt(int spot, out Vector3 pour)
    {
        var sp = st.Spot(spot);
        Transform counter = sp != null && sp.counterSpot != null ? sp.counterSpot : st.counterSpot;
        Transform pourT = sp != null && sp.pourSpot != null ? sp.pourSpot : st.pourSpot;
        Vector3 c = counter != null ? counter.position : st.transform.position + Vector3.up * 1.1f;
        pour = pourT != null ? pourT.position : c + Vector3.up * 0.0f;
        return c;
    }

    // ------------------------------------------------------------------ the glass

    Glass Make(int drink)
    {
        var g = new Glass(); // a handful per night: a fresh one per drink is simplest
        var root = new GameObject("Glass " + drink) { hideFlags = HideFlags.DontSave };
        root.transform.SetParent(host.PropRoot, false);
        g.root = root;
        float r, h; Color liquid, glassTint = new Color(0.8f, 0.9f, 0.95f, 0.22f);
        bool foam = false, flame = false;
        switch (drink)
        {
            case CasinoRules.FlatBeer: r = 0.030f; h = 0.135f; liquid = new Color(0.80f, 0.52f, 0.10f); foam = true; break;
            case CasinoRules.HouseWhisky: r = 0.033f; h = 0.075f; liquid = new Color(0.55f, 0.28f, 0.07f); break;
            case CasinoRules.DumpsterFireCocktail: r = 0.034f; h = 0.115f; liquid = new Color(0.9f, 0.22f, 0.08f); flame = true; break;
            default: r = 0.030f; h = 0.105f; liquid = new Color(0.62f, 0.82f, 0.9f); break;
        }
        g.height = h;
        // the glass: a thin see-through shell, a heavy base
        var shell = new GameObject("Shell") { hideFlags = HideFlags.DontSave };
        shell.transform.SetParent(root.transform, false);
        shell.AddComponent<MeshFilter>().sharedMesh = CasinoPropKit.Lathe("CasinoGlassShell" + drink, new[]
        {
            new Vector2(0.001f, 0f), new Vector2(r * 0.95f, 0f), new Vector2(r, 0.01f), new Vector2(r * 1.12f, h), new Vector2(r * 1.05f, h), new Vector2(r * 0.9f, 0.012f), new Vector2(0.001f, 0.012f),
        }, 18);
        var sr = shell.AddComponent<MeshRenderer>();
        sr.sharedMaterial = CasinoPropKit.Glass(glassTint);
        sr.shadowCastingMode = ShadowCastingMode.Off; sr.receiveShadows = false;
        // the drink
        var liq = new GameObject("Liquid") { hideFlags = HideFlags.DontSave };
        liq.transform.SetParent(root.transform, false);
        liq.transform.localPosition = new Vector3(0f, 0.013f, 0f);
        liq.AddComponent<MeshFilter>().sharedMesh = Cylinder();
        var lr = liq.AddComponent<MeshRenderer>();
        lr.sharedMaterial = CasinoPropKit.Solid(liquid, 0.7f);
        lr.shadowCastingMode = ShadowCastingMode.Off; lr.receiveShadows = false;
        g.liquid = liq.transform;
        g.liquidHeight = h * (foam ? 0.78f : 0.82f) - 0.013f;
        if (foam)
        {
            var f = new GameObject("Foam") { hideFlags = HideFlags.DontSave };
            f.transform.SetParent(root.transform, false);
            f.AddComponent<MeshFilter>().sharedMesh = Cylinder();
            var fr = f.AddComponent<MeshRenderer>();
            fr.sharedMaterial = CasinoPropKit.Solid(new Color(0.97f, 0.94f, 0.82f), 0.3f);
            fr.shadowCastingMode = ShadowCastingMode.Off; fr.receiveShadows = false;
            g.foam = f.transform;
        }
        if (flame)
        {
            var f = new GameObject("Flame") { hideFlags = HideFlags.DontSave };
            f.transform.SetParent(root.transform, false);
            f.AddComponent<MeshFilter>().sharedMesh = CasinoPropKit.Lathe("CasinoFlame", new[] { new Vector2(0.001f, 0f), new Vector2(0.012f, 0.004f), new Vector2(0.008f, 0.02f), new Vector2(0.001f, 0.04f) }, 10);
            var fr = f.AddComponent<MeshRenderer>();
            fr.sharedMaterial = CasinoPropKit.Glow(new Color(1f, 0.45f, 0.08f), 3f);
            fr.shadowCastingMode = ShadowCastingMode.Off; fr.receiveShadows = false;
            g.flame = f.transform;
        }
        // the stream from the bottle while it pours
        var s = new GameObject("Stream") { hideFlags = HideFlags.DontSave };
        s.transform.SetParent(root.transform, false);
        s.AddComponent<MeshFilter>().sharedMesh = Cylinder();
        var str = s.AddComponent<MeshRenderer>();
        str.sharedMaterial = CasinoPropKit.Solid(liquid, 0.8f);
        str.shadowCastingMode = ShadowCastingMode.Off; str.receiveShadows = false;
        g.stream = s.transform;
        liq.transform.localScale = new Vector3(r * 0.9f, 0.001f, r * 0.9f);
        return g;
    }

    /// <summary>The fill, 0..1: how much of the glass is liquid (and the foam, the stream and the flame follow it).</summary>
    void SetLevel(Glass g, float level)
    {
        float r = g.liquid.localScale.x;
        float lh = Mathf.Max(0.0005f, g.liquidHeight * Mathf.Clamp01(level));
        g.liquid.localScale = new Vector3(r, lh, r);
        float top = g.liquid.localPosition.y + lh;
        if (g.foam != null)
        {
            bool on = level > 0.05f;
            g.foam.gameObject.SetActive(on);
            g.foam.localPosition = new Vector3(0f, top, 0f);
            g.foam.localScale = new Vector3(r * 1.02f, 0.016f * Mathf.Clamp01(level * 2f), r * 1.02f);
        }
        if (g.flame != null)
        {
            g.flame.gameObject.SetActive(level > 0.9f);
            g.flame.localPosition = new Vector3(0f, g.height + 0.004f, 0f);
        }
    }

    void SetStream(Glass g, bool on, Vector3 from)
    {
        if (!on) { g.stream.gameObject.SetActive(false); return; }
        Vector3 top = g.root.transform.position + Vector3.up * (g.height + 0.01f);
        float len = Mathf.Max(0.02f, from.y - top.y);
        g.stream.gameObject.SetActive(true);
        g.stream.position = top;
        g.stream.rotation = Quaternion.identity;
        g.stream.localScale = new Vector3(0.006f, len, 0.006f);
    }

    void Remove(int index)
    {
        var g = glasses[index];
        glasses.RemoveAt(index);
        if (g.root != null) Object.Destroy(g.root);
    }

    // ------------------------------------------------------------------ per frame

    public override void Tick(double now)
    {
        for (int i = glasses.Count - 1; i >= 0; i--)
        {
            var g = glasses[i];
            if (g.root == null) { glasses.RemoveAt(i); continue; }
            float t = (float)(now - g.t0);
            switch (g.phase)
            {
                case Phase.Pouring:
                {
                    float u = Mathf.Clamp01(t / PourSeconds);
                    SetLevel(g, u);
                    var hand = CasinoStaff.Bartender.Hand;
                    SetStream(g, u < 1f, hand != null ? hand.position : g.from + Vector3.up * 0.3f);
                    if (t >= PourSeconds + 0.1f)
                    {
                        SetStream(g, false, Vector3.zero);
                        g.phase = Phase.Sliding; g.t0 = now;
                        CasinoStaff.Bartender.Play(BartenderGesture.Serve);
                    }
                    break;
                }
                case Phase.Sliding:
                {
                    float u = Mathf.Clamp01(t / SlideSeconds);
                    float e = u * u * (3f - 2f * u);
                    g.root.transform.position = Vector3.Lerp(g.from, g.to, e) + Vector3.up * (Mathf.Sin(u * Mathf.PI) * 0.015f);
                    if (u >= 1f)
                    {
                        g.phase = Phase.Resting; g.t0 = now;
                        Sfx.Play("click", g.to, 0.35f, 1.1f);
                    }
                    break;
                }
                case Phase.Resting:
                    if (t >= RestSeconds) { g.phase = Phase.Draining; g.t0 = now; }
                    break;
                default:
                {
                    float u = Mathf.Clamp01(t / DrainSeconds);
                    SetLevel(g, 1f - u);
                    if (u >= 1f) Remove(i);
                    break;
                }
            }
        }
    }

    public override void Teardown()
    {
        for (int i = glasses.Count - 1; i >= 0; i--) Remove(i);
    }
}
