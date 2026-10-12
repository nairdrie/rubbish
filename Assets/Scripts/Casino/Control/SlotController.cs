using System;
using UnityEngine;

/// <summary>
/// One slot machine (Docs/Casino.md §9, §12): the camera sits at the cabinet, the cursor is a reticle at the screen centre. Space, a click
/// anywhere (or the lever, or the SPIN plate) or pad A / RT pulls: <c>SlotSpin(machine)</c>. Holding it for half a second keeps pulling
/// (never sooner than 2.6 s after the last pull; the host floors at 1 s). The stake is fixed per machine, so the bet plates stay dark. The
/// drums, the glow, the coins and the floating win are the presenters' (from <see cref="CasinoClient.SlotSpun"/>); this controller keeps the
/// HUD cash quiet until the reveal is over, then reacts to a win with a sound, the hint strip and (big wins) the celebration.
/// </summary>
public sealed class SlotController : CasinoController
{
    readonly AutoSpin auto = new AutoSpin();
    bool inFlight;
    int machine;
    int lastAttemptFrame = -1;
    float revealAt = -1f;
    SlotEventInfo reveal;
    bool revealPending;
    int lastSecond = int.MinValue;
    bool lastPad;
    long lastCash = long.MinValue;
    bool broke;

    public override string TableId => CasinoTableIds.Slot(machine);

    protected override void OnBegin()
    {
        machine = Mathf.Clamp(station.index, 0, CasinoRules.SlotMachineCount - 1);
        CasinoClient.SlotSpun += OnSlotSpun;
        ApplyPlates();
        RefreshStrip();
    }

    protected override void OnEnd(CasinoSeatCamera.ExitReason reason)
    {
        CasinoClient.SlotSpun -= OnSlotSpun;
        // plates back as built
        if (station != null)
            foreach (var b in new[] { station.spinButton, station.betUpButton, station.betDownButton })
                if (b != null) { b.SetEnabled(true); b.SetLit(false); }
    }

    public override void OnNotice(string table, CasinoNotice notice)
    {
        if (table != TableId) return;
        if (notice == CasinoNotice.IdleRelease)
        {
            GameHUD.Toast("The machine timed you out. Somebody else can have a go.", ToastStyle.Info, 2.5f);
            RequestExit();
        }
        else if (notice == CasinoNotice.Released)
        {
            GameHUD.Toast("The house stood you up from the machine.", ToastStyle.Info, 2.2f);
            RequestExit();
        }
    }

    int StakeCents => SlotRules.StakeCents(machine);

    void ApplyPlates()
    {
        if (station.spinButton != null) { station.spinButton.SetEnabled(true); station.spinButton.SetLit(!inFlight); }
        // the stake is fixed per machine: the bet plates never light
        if (station.betUpButton != null) { station.betUpButton.SetEnabled(false); station.betUpButton.SetLit(false); }
        if (station.betDownButton != null) { station.betDownButton.SetEnabled(false); station.betDownButton.SetLit(false); }
    }

    // ------------------------------------------------------------------ the spin

    void OnSlotSpun(SlotEventInfo e)
    {
        if (IsEnded || e.machine != machine || e.key != CasinoClient.LocalKey) return;
        reveal = e;
        revealPending = true;
        revealAt = (float)(CasinoClient.ToLocalTime(e.start) - Time.realtimeSinceStartupAsDouble) + Time.unscaledTime + SlotRevealTiming.Seconds(e);
        HoldCash(true);
    }

    void Reveal()
    {
        revealPending = false;
        HoldCash(false);
        if (reveal.winCents > 0)
        {
            string line = reveal.rule >= 0 && reveal.rule < SlotRules.Machine(machine).pays.Length ? SlotRules.Machine(machine).pays[reveal.rule].name : "Winner";
            Say(line + "! +" + Dollars(reveal.winCents), HintTone.Good, 4f);
            CasinoControl.Celebrate(reveal.winCents, reveal.stakeCents, reveal.rule == 0 ? "JACKPOT on " + SlotRules.Machine(machine).name : line);
        }
        else if (SlotRules.IsNearMiss(machine, reveal.stop0, reveal.stop1, reveal.stop2))
            Say("So close. That's the strip, not a trick.", HintTone.Normal, 2.2f);
        RefreshStrip();
    }

    void TryPull(bool explicitTap)
    {
        if (Time.frameCount == lastAttemptFrame) return; // a key and a click in the same frame are one pull
        lastAttemptFrame = Time.frameCount;
        double now = Time.realtimeSinceStartupAsDouble;
        if (inFlight) return;
        if (!auto.CanPullNow(now))
        {
            if (explicitTap) Say("Easy. Next pull in " + CasinoSeatText.Countdown(auto.WaitSeconds(now)) + ".", HintTone.Normal, 1.2f);
            return;
        }
        if (Cash < StakeCents)
        {
            auto.StopAuto();
            if (explicitTap || !broke) Refuse("You need " + Dollars(StakeCents) + " for a pull.", 3f);
            broke = true;
            return;
        }
        broke = false;
        inFlight = true;
        auto.MarkPull(now);
        HoldCash(true); // before the request that moves the money
        Sfx.Play("click", station.transform.position, 0.5f, 0.8f);
        Sfx.Play("spring", station.transform.position, 0.35f, 1.1f);
        if (station.spinButton != null) station.spinButton.SetLit(false);
        CasinoClient.SlotSpin(machine, r =>
        {
            inFlight = false;
            if (IsEnded) return;
            if (station.spinButton != null) station.spinButton.SetLit(true);
            if (r.Ok) return;
            // nothing was charged
            auto.StopAuto();
            if (!revealPending) HoldCash(false);
            Refuse(r.gate != CasinoError.Ok ? CasinoBank.Describe(r.gate) : r.error == CasinoTableError.CantAfford ? "You need " + Dollars(StakeCents) + " for a pull." : CasinoTables.Describe(r), 3f);
        });
    }

    // ------------------------------------------------------------------ input

    protected override void Tick(float dt)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        bool held = CasinoInput.Held(KeyCode.Space) || CasinoInput.MouseHeld(0) || CasinoInput.PadHeld(p => p.buttonSouth) || CasinoInput.PadHeld(p => p.rightTrigger);
        bool wasAuto = auto.Auto;
        auto.Tick(held, dt);
        if (auto.Auto != wasAuto) RefreshStrip();
        if (CasinoInput.Down(KeyCode.Space)) TryPull(true);
        else if (auto.Auto) TryPull(false);

        if (revealPending && Time.unscaledTime >= revealAt) Reveal();

        long cash = Cash;
        bool pad = CasinoInput.UsingPad;
        int sec = (int)Math.Ceiling(auto.WaitSeconds(now));
        if (cash != lastCash || pad != lastPad || (auto.Auto && sec != lastSecond))
        {
            lastCash = cash; lastPad = pad; lastSecond = sec;
            RefreshStrip();
        }
    }

    protected override void OnClicked(CasinoCursor.Click c)
    {
        if (c.secondary) return;
        var t = c.hit ? c.target : null;
        if (t is CasinoButton b && (b.id == "BetUp" || b.id == "BetDown"))
        {
            Say(SlotRules.Machine(machine).name + " takes a fixed " + Dollars(StakeCents) + " a pull.", HintTone.Normal, 2.5f);
            return;
        }
        TryPull(true);
    }

    protected override void OnPlate(CasinoButton b)
    {
        // the cursor's click on the SPIN plate: OnClicked pulls as well (one pull per frame)
        if (b.id == "Spin") TryPull(true);
    }

    protected override string DescribeHover(CasinoPickable p)
    {
        if (p == null) return "";
        if (p is CasinoButton b)
        {
            if (b.id == "Spin") return "Spin: " + Dollars(StakeCents) + " a pull";
            if (b.id == "BetUp" || b.id == "BetDown") return "The stake is fixed at " + Dollars(StakeCents) + " on this machine";
        }
        if (p.kind == CasinoPickKind.Lever) return "Pull the lever: " + Dollars(StakeCents);
        return p.hint;
    }

    // ------------------------------------------------------------------ the hint strip

    void RefreshStrip()
    {
        if (strip == null || IsEnded) return;
        bool pad = CasinoInput.UsingPad;
        string K(string kb, string pd, string label) => CasinoHintStrip.Key(pad ? pd : kb, label);
        string stake = Dollars(StakeCents);
        strip.SetHint(K("Space", "A", "pull " + stake) + "  " + K("LMB", "RT", "pull") + "  " + K("Hold Space", "Hold A", "auto-spin") + "  " + K("V", "D-pad up", "pay table") + "  " + K("Esc", "B", "leave"));
        string info = SlotRules.Machine(machine).name + "   Stake " + stake;
        if (auto.Auto) info = "AUTO-SPIN: let go to stop   " + stake + " a pull";
        else if (Cash < StakeCents) info = "You need " + stake + " for a pull";
        strip.SetInfo(info);
    }
}
