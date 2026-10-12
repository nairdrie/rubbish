using System;
using UnityEngine;

/// <summary>
/// The bar (Docs/Casino.md §5, §9, §12): the player stands at a bar spot, looks at the menu card on the counter and orders by clicking a drink
/// (the cursor is free over the card), pressing 1-4, or D-pad + A. Hovering a drink shows its price and effect on the hint strip; the drunk
/// stage and the bartender's remarks sit on the strip's info line. The order goes through <see cref="CasinoClient.Drink"/> (the bar has no host
/// seat); only when the host says yes does <see cref="DrunkStatus.Drink"/> run on this machine, about three seconds later (the pour, the slide,
/// the swallow), and walking off mid-pour still drinks it. From <see cref="CasinoBank.BarCutOffLevel"/> the bartender only sells water.
/// <para>The presenters animate the bartender and the glass from the host's <c>CasinoClient.BarServed</c> broadcast (everybody, the buyer included).</para>
/// </summary>
public sealed class BarController : CasinoController
{
    const float SwallowAfter = 3.0f, BusyFor = 3.6f;

    bool busy;
    float busyUntil, swallowAt = -1f;
    float pendingUnits;
    bool pendingDrink;
    int lastOrderFrame = -1;
    int padSel;
    int lastLevelCents = -1;
    long lastCash = long.MinValue;
    bool lastPad;
    string pendingSwallowText = "";

    public override string TableId => null;
    protected override CasinoSeatCamera.CursorMode WantedMode => CasinoSeatCamera.CursorMode.Free;

    protected override void OnBegin()
    {
        for (int s = 0; s < station.SpotCount; s++)
        {
            var sp = station.Spot(s);
            if (sp == null || sp.drinkSlots == null) continue;
            for (int i = 0; i < sp.drinkSlots.Length && i < CasinoRules.DrinkCount; i++)
            {
                var d = CasinoRules.Drink(i);
                sp.drinkSlots[i]?.Set(d.name, CasinoRules.Dollars(d.priceCents), BarMenu.Effect(i));
            }
        }
        ApplySlots();
        Say("\"What'll it be?\"", HintTone.Normal, 4f);
        RefreshStrip();
    }

    protected override void OnEnd(CasinoSeatCamera.ExitReason reason)
    {
        Swallow(); // paid for, so drunk: walking off mid-pour does not dodge it
        if (spot != null && spot.drinkSlots != null)
            foreach (var s in spot.drinkSlots) if (s != null && s.button != null) { s.button.SetEnabled(true); s.button.SetLit(false); }
    }

    // ------------------------------------------------------------------ slots

    int SlotIndexOf(CasinoPickable p)
    {
        if (spot == null || spot.drinkSlots == null || p == null) return -1;
        for (int i = 0; i < spot.drinkSlots.Length; i++) if (spot.drinkSlots[i] != null && spot.drinkSlots[i].button == p) return i;
        return -1;
    }

    void ApplySlots()
    {
        if (spot == null || spot.drinkSlots == null) return;
        float level = DrunkStatus.Level;
        long cash = Cash;
        for (int i = 0; i < spot.drinkSlots.Length && i < CasinoRules.DrinkCount; i++)
        {
            var b = spot.drinkSlots[i]?.button;
            if (b == null) continue;
            bool refused = BarMenu.Refused(level, i);
            b.SetEnabled(!refused);
            b.SetLit(!refused && !busy && cash >= CasinoRules.Drink(i).priceCents);
        }
    }

    protected override void OnPlate(CasinoButton b)
    {
        int i = SlotIndexOf(b);
        if (i >= 0) Order(i);
    }

    // ------------------------------------------------------------------ ordering

    void Order(int index)
    {
        if (Time.frameCount == lastOrderFrame) return;
        lastOrderFrame = Time.frameCount;
        if (busy || !CasinoRules.TryGetDrink(index, out var d)) return;
        string why = BarMenu.WhyNot(DrunkStatus.Level, Cash, index);
        if (why.Length > 0) { Refuse(why, 3f); return; }
        busy = true;
        busyUntil = Time.unscaledTime + BusyFor + 2f; // a lost answer frees the bar after a while
        ApplySlots();
        CasinoClient.Drink(index, v => OnReply(index, v));
    }

    void OnReply(int index, BarView v)
    {
        if (IsEnded && !pendingDrink && !v.Ok) { busy = false; return; }
        if (!v.Ok)
        {
            busy = false;
            if (!IsEnded) { Refuse(CasinoBank.Describe(v.error), 3f); ApplySlots(); }
            return;
        }
        // Paid. Whatever happens to this seat now, the drink gets drunk.
        pendingUnits = v.units;
        pendingDrink = true;
        swallowAt = Time.unscaledTime + SwallowAfter;
        busyUntil = Time.unscaledTime + BusyFor;
        bool water = v.units < 0f;
        var d = CasinoRules.TryGetDrink(v.drink, out var drink) ? drink : default;
        pendingSwallowText = water ? "Cold. Wet. A dollar." : "Down the hatch: " + (string.IsNullOrEmpty(d.name) ? "that" : d.name) + ".";
        if (!IsEnded)
        {
            Say("\"" + (water ? "Smart." : "Coming up.") + "\"  " + Dollars(v.priceCents) + " on the tab.", HintTone.Normal, 3f);
            Sfx.Play("cash", null, 0.35f, 1.2f);
            ApplySlots();
        }
    }

    /// <summary>The paid-for drink takes effect on this machine, exactly once.</summary>
    void Swallow()
    {
        if (!pendingDrink) return;
        pendingDrink = false;
        float units = pendingUnits;
        pendingUnits = 0f;
        swallowAt = -1f;
        try { DrunkStatus.Drink(units); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ input

    protected override void Tick(float dt)
    {
        if (swallowAt > 0f && Time.unscaledTime >= swallowAt)
        {
            Sfx.Play("pop", null, 0.5f, 0.7f);
            Swallow();
            if (pendingSwallowText.Length > 0) Say(pendingSwallowText, HintTone.Normal, 3f);
            pendingSwallowText = "";
            ApplySlots();
            RefreshStrip();
        }
        if (busy && Time.unscaledTime >= busyUntil) { busy = false; ApplySlots(); RefreshStrip(); }

        int n = CasinoRules.DrinkCount;
        int digit = CasinoInput.DigitDown();
        if (digit >= 1 && digit <= n) { padSel = digit - 1; Order(digit - 1); }
        int step = 0;
        if (CasinoInput.PadDpadLeft || CasinoInput.PadLb) step--;
        if (CasinoInput.PadDpadRight || CasinoInput.PadRb) step++;
        if (step != 0)
        {
            padSel = ((padSel + step) % n + n) % n;
            Say(DescribeDrink(padSel), HintTone.Normal, 4f);
            Sfx.Play("click", null, 0.25f, 1.3f);
        }
        if (CasinoInput.PadA && SlotIndexOf(cursor != null ? cursor.Hovered : null) < 0) Order(padSel);

        // how you feel, and what the menu allows, follow the drunk level and the cash
        int lv = (int)(DrunkStatus.Level * 100f);
        long cash = Cash;
        bool pad = CasinoInput.UsingPad;
        if (lv != lastLevelCents || cash != lastCash || pad != lastPad)
        {
            lastLevelCents = lv; lastCash = cash; lastPad = pad;
            ApplySlots();
            RefreshStrip();
        }
    }

    protected override void OnClicked(CasinoCursor.Click c)
    {
        if (c.secondary) return;
        int i = c.hit ? SlotIndexOf(c.target) : -1;
        if (i >= 0 && !c.target.Clickable)
        {
            string why = BarMenu.WhyNot(DrunkStatus.Level, Cash, i);
            if (why.Length > 0) Refuse(why, 3f);
        }
    }

    protected override string DescribeHover(CasinoPickable p)
    {
        int i = SlotIndexOf(p);
        return i >= 0 ? DescribeDrink(i) : p != null ? p.hint : "";
    }

    string DescribeDrink(int i)
    {
        if (!CasinoRules.TryGetDrink(i, out var d)) return "";
        string why = BarMenu.WhyNot(DrunkStatus.Level, Cash, i);
        string basic = d.name + " - " + CasinoRules.Dollars(d.priceCents) + " - " + BarMenu.Effect(i);
        return why.Length > 0 ? basic + "   " + why : basic + "   " + BarMenu.Blurb(i);
    }

    // ------------------------------------------------------------------ the hint strip

    void RefreshStrip()
    {
        if (strip == null || IsEnded) return;
        bool pad = CasinoInput.UsingPad;
        string K(string kb, string pd, string label) => CasinoHintStrip.Key(pad ? pd : kb, label);
        strip.SetHint(K("LMB", "A", "order") + "  " + K("1-4", "D-pad", "pick a drink") + "  " + K("V", "D-pad up", "the bartender") + "  " + K("Esc", "B", "leave"));
        strip.SetInfo(BarMenu.FeelLine(DrunkStatus.Level));
    }
}
