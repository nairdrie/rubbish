using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One pooled 3D prop on the tables (a card or a chip) and the little motion it can be given: a slide from where it is to where it
/// should be with an arc and a landing, an optional scale pop, then a callback. Local and cosmetic; ticked by
/// <see cref="CasinoPresenters"/> only while something is moving.
/// </summary>
public abstract class CasinoProp
{
    public readonly GameObject go;
    public readonly Transform root;
    protected readonly MeshFilter filter;
    protected readonly MeshRenderer renderer;

    // motion (world space, local clock seconds)
    Vector3 fromPos, toPos;
    Quaternion fromRot, toRot;
    double t0;
    float dur, arc, scaleFrom = 1f;
    bool moving, releaseWhenDone, waiting;
    Action<CasinoProp> arrived;

    public bool Moving => moving;
    public bool Released { get; internal set; }

    protected CasinoProp(string name, Transform parent, Material mat)
    {
        go = new GameObject(name) { hideFlags = HideFlags.DontSave };
        root = go.transform;
        root.SetParent(parent, false);
        var body = new GameObject("Body") { hideFlags = HideFlags.DontSave };
        body.transform.SetParent(root, false);
        filter = body.AddComponent<MeshFilter>();
        renderer = body.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = mat;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false; // props under the eye in the lamp's cone: the shadow map striped flat parts with acne
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        BodyTransform = body.transform;
        go.SetActive(false);
    }

    protected Transform BodyTransform { get; }

    /// <summary>Put it somewhere right now, cancelling any motion.</summary>
    public void Snap(Vector3 pos, Quaternion rot)
    {
        moving = false;
        root.SetPositionAndRotation(pos, rot);
        root.localScale = Vector3.one;
        go.SetActive(true);
    }

    /// <summary>Slide to a place over <paramref name="seconds"/> with an arc (metres of extra height at the middle).</summary>
    public void MoveTo(Vector3 pos, Quaternion rot, double now, float seconds, float arcHeight = 0.05f, float startScale = 1f, bool release = false, Action<CasinoProp> done = null)
    {
        if (!go.activeSelf && !moving) root.SetPositionAndRotation(pos, rot);
        fromPos = root.position; fromRot = root.rotation;
        toPos = pos; toRot = rot;
        t0 = now; dur = Mathf.Max(0.01f, seconds); arc = arcHeight; scaleFrom = startScale;
        releaseWhenDone = release; arrived = done;
        moving = true;
        waiting = false;
        if (t0 > Time.realtimeSinceStartupAsDouble + 0.002) { waiting = true; go.SetActive(false); } // a later start: invisible until then
        else go.SetActive(true);
    }

    /// <summary>Start somewhere and slide to a place (a card from the shoe, a chip from the tray).</summary>
    public void MoveFromTo(Vector3 from, Quaternion fromQ, Vector3 pos, Quaternion rot, double now, float seconds, float arcHeight = 0.05f, float startScale = 1f, Action<CasinoProp> done = null)
    {
        root.SetPositionAndRotation(from, fromQ);
        MoveTo(pos, rot, now, seconds, arcHeight, startScale, false, done);
    }

    /// <summary>Advance. Returns true while still moving or animating.</summary>
    public bool Tick(double now)
    {
        bool busy = false;
        if (moving && waiting)
        {
            if (now < t0) return true;
            waiting = false;
            go.SetActive(true);
        }
        if (moving)
        {
            float u = Mathf.Clamp01((float)((now - t0) / dur));
            float e = u * u * (3f - 2f * u);
            Vector3 p = Vector3.Lerp(fromPos, toPos, e);
            p.y += Mathf.Sin(u * Mathf.PI) * arc;
            root.SetPositionAndRotation(p, Quaternion.Slerp(fromRot, toRot, e));
            float s = Mathf.Lerp(scaleFrom, 1f, Mathf.Clamp01(u * 2.2f));
            root.localScale = new Vector3(s, s, s);
            if (u >= 1f)
            {
                moving = false;
                root.SetPositionAndRotation(toPos, toRot);
                root.localScale = Vector3.one;
                var cb = arrived; arrived = null;
                if (cb != null) { try { cb(this); } catch (Exception e2) { Debug.LogException(e2); } }
                if (releaseWhenDone) go.SetActive(false);
            }
            else busy = true;
        }
        return TickExtra(now) || busy;
    }

    protected virtual bool TickExtra(double now) => false;

    /// <summary>Forget any motion and hide (back to the pool).</summary>
    public virtual void Reset()
    {
        moving = false; waiting = false; arrived = null; releaseWhenDone = false;
        root.localScale = Vector3.one;
        go.SetActive(false);
    }

    public bool Visible => go != null && go.activeSelf;
}

/// <summary>A playing card prop: the mesh of one card (or the back), face up or face down, with a flip.</summary>
public sealed class CardProp : CasinoProp
{
    public int Card { get; private set; } = -1;
    public bool FaceDown { get; private set; }

    bool flipping;
    double flipT0;
    float flipDur, flipFrom, flipTo;

    public CardProp(Transform parent) : base("Card", parent, CasinoPropKit.CardMaterial)
    {
        BodyTransform.localPosition = new Vector3(0f, CasinoLayout.CardThickness * 0.5f, 0f);
    }

    /// <summary>Show a card byte (0..51) or the back (<see cref="BlackjackTable.Hidden"/>), face up or down, with no animation.</summary>
    public void Show(byte card, bool faceDown)
    {
        int cell = card == BlackjackTable.Hidden || card > 51 ? CasinoPaint.CardBackCell : card;
        if (cell != Card) { filter.sharedMesh = CasinoPropKit.Card(cell); Card = cell; }
        FaceDown = faceDown;
        flipping = false;
        BodyTransform.localRotation = Quaternion.Euler(0f, 0f, faceDown ? 180f : 0f);
        BodyTransform.localPosition = new Vector3(0f, CasinoLayout.CardThickness * 0.5f, 0f);
    }

    /// <summary>The card turns over about its long axis, lifting a little: from face down (hidden) to face up showing <paramref name="card"/>.</summary>
    public void FlipUp(byte card, double now, float seconds = 0.45f)
    {
        int cell = card == BlackjackTable.Hidden || card > 51 ? CasinoPaint.CardBackCell : card;
        if (cell != Card) { filter.sharedMesh = CasinoPropKit.Card(cell); Card = cell; }
        FaceDown = false;
        flipping = true; flipT0 = now; flipDur = Mathf.Max(0.05f, seconds); flipFrom = 180f; flipTo = 360f;
    }

    protected override bool TickExtra(double now)
    {
        if (!flipping) return false;
        float u = Mathf.Clamp01((float)((now - flipT0) / flipDur));
        float e = u * u * (3f - 2f * u);
        BodyTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(flipFrom, flipTo, e));
        BodyTransform.localPosition = new Vector3(0f, CasinoLayout.CardThickness * 0.5f + Mathf.Sin(u * Mathf.PI) * 0.045f, 0f);
        if (u >= 1f)
        {
            flipping = false;
            BodyTransform.localRotation = Quaternion.identity;
            BodyTransform.localPosition = new Vector3(0f, CasinoLayout.CardThickness * 0.5f, 0f);
            return false;
        }
        return true;
    }

    public override void Reset()
    {
        base.Reset();
        flipping = false;
        Card = -1;
    }
}

/// <summary>One chip prop (an atlas style).</summary>
public sealed class ChipProp : CasinoProp
{
    public int Style { get; private set; } = -1;

    public ChipProp(Transform parent) : base("Chip", parent, CasinoPropKit.ChipMaterial) { }

    public void SetStyle(int style)
    {
        if (style == Style) return;
        Style = style;
        filter.sharedMesh = CasinoPropKit.Chip(style);
    }

    public override void Reset()
    {
        base.Reset();
        Style = -1;
    }
}

/// <summary>The pools of cards and chips the presenters share (Docs/Casino.md §11): nothing is allocated while a table plays.</summary>
public sealed class CasinoPropPool
{
    readonly Transform parent;
    readonly Stack<CardProp> cards = new Stack<CardProp>();
    readonly Stack<ChipProp> chips = new Stack<ChipProp>();
    readonly List<CasinoProp> all = new List<CasinoProp>();

    public CasinoPropPool(Transform parent) { this.parent = parent; }

    public int InUse { get; private set; }

    public CardProp Card()
    {
        CardProp c = null;
        while (cards.Count > 0 && c == null) { c = cards.Pop(); if (c != null && c.go == null) c = null; }
        if (c == null) { c = new CardProp(parent); all.Add(c); }
        c.Released = false;
        InUse++;
        return c;
    }

    public ChipProp Chip()
    {
        ChipProp c = null;
        while (chips.Count > 0 && c == null) { c = chips.Pop(); if (c != null && c.go == null) c = null; }
        if (c == null) { c = new ChipProp(parent); all.Add(c); }
        c.Released = false;
        InUse++;
        return c;
    }

    public void Release(CasinoProp p)
    {
        if (p == null || p.Released) return;
        p.Released = true;
        p.Reset();
        InUse--;
        if (p is CardProp cp) cards.Push(cp);
        else if (p is ChipProp ch) chips.Push(ch);
    }

    /// <summary>Tick every prop that is moving; returns true while any is.</summary>
    public bool Tick(double now)
    {
        bool busy = false;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p.Released || (!p.go.activeSelf && !p.Moving)) continue;
            if (p.Tick(now)) busy = true;
        }
        return busy;
    }

    public void DestroyAll()
    {
        for (int i = 0; i < all.Count; i++) if (all[i].go != null) UnityEngine.Object.Destroy(all[i].go);
        all.Clear(); cards.Clear(); chips.Clear();
        InUse = 0;
    }
}

/// <summary>
/// A stack of chips standing for an amount at a place on a table: the decomposition into denominations (big chips at the bottom), a
/// drop-in animation for chips that are new, and a slide away for chips that leave (swept to the dealer, paid out to a player).
/// </summary>
public sealed class ChipStackView
{
    /// <summary>Most chips drawn in one stack (a spot cannot hold more than this many at the limits).</summary>
    public const int MaxChips = 18;

    readonly CasinoPropPool pool;
    readonly List<ChipProp> chips = new List<ChipProp>(MaxChips);
    readonly List<int> want = new List<int>(MaxChips);
    readonly List<int> styles = new List<int>(MaxChips);
    Vector3 origin;
    Quaternion rot = Quaternion.identity;
    int owner = -1; // -1 = blackjack (colour by denomination), 0..3 = roulette owner colour
    long cents;

    public ChipStackView(CasinoPropPool pool) { this.pool = pool; }

    public long Cents => cents;
    public int Count => chips.Count;
    public bool Empty => chips.Count == 0;
    public Vector3 Origin => origin;
    /// <summary>World position of the top of the stack (for a label).</summary>
    public Vector3 TopPoint => origin + Vector3.up * (CasinoLayout.ChipThickness * chips.Count + 0.012f);

    /// <summary>Move the stack's base (no animation of the chips already on it beyond snapping them).</summary>
    public void SetOrigin(Vector3 world, Quaternion rotation)
    {
        origin = world; rot = rotation;
        for (int i = 0; i < chips.Count; i++) chips[i].Snap(Slot(i), rot);
    }

    Vector3 Slot(int i) => origin + Vector3.up * (CasinoLayout.ChipThickness * i);

    /// <summary>Show <paramref name="amountCents"/> (0 = none). New chips drop onto the stack when <paramref name="animate"/>.</summary>
    public void Set(long amountCents, int ownerColour, double now, bool animate, float delay = 0f, Vector3? slideFrom = null)
    {
        cents = amountCents;
        owner = ownerColour;
        want.Clear();
        CasinoChipMath.Decompose(amountCents, ownerColour >= 0 ? CasinoChipMath.RouletteDenoms : CasinoChipMath.BlackjackDenoms, want);
        if (want.Count > MaxChips) want.RemoveRange(MaxChips, want.Count - MaxChips);
        styles.Clear();
        for (int i = 0; i < want.Count; i++) styles.Add(ownerColour >= 0 ? CasinoPaint.RouletteStyle(ownerColour, want[i]) : CasinoPaint.BlackjackStyle(want[i]));
        // keep the chips that already match from the bottom
        int keep = 0;
        while (keep < chips.Count && keep < styles.Count && chips[keep].Style == styles[keep]) keep++;
        for (int i = chips.Count - 1; i >= keep; i--) { pool.Release(chips[i]); chips.RemoveAt(i); }
        for (int i = keep; i < styles.Count; i++)
        {
            var c = pool.Chip();
            c.SetStyle(styles[i]);
            chips.Add(c);
            Vector3 at = Slot(i);
            if (animate && slideFrom.HasValue)
            {
                // counted out from a tray: each chip slides across in turn
                float stagger = (i - keep) * 0.05f + delay;
                c.MoveFromTo(slideFrom.Value + Vector3.up * 0.01f, rot, at, rot, now + stagger, 0.42f, 0.05f, 1f);
            }
            else if (animate)
            {
                // a short drop from above with a little pop, one after the other
                float stagger = (i - keep) * 0.045f + delay;
                c.MoveFromTo(at + Vector3.up * 0.07f, rot, at, rot, now + stagger, 0.16f, 0f, 0.7f);
            }
            else c.Snap(at, rot);
        }
    }

    public void Clear()
    {
        for (int i = 0; i < chips.Count; i++) pool.Release(chips[i]);
        chips.Clear();
        cents = 0;
    }

    /// <summary>All the chips slide to <paramref name="dest"/> (a tray, a player) and go back to the pool when they get there.</summary>
    public void SlideAway(Vector3 dest, double now, float seconds = 0.5f, float stagger = 0.03f, float delay = 0f)
    {
        var p = pool;
        for (int i = 0; i < chips.Count; i++)
        {
            var c = chips[i];
            c.MoveTo(dest + Vector3.up * (CasinoLayout.ChipThickness * 0.4f * (i % 3)), rot, now + delay + i * stagger, seconds, 0.04f, 1f, false, x => p.Release(x));
        }
        chips.Clear();
        cents = 0;
    }
}
