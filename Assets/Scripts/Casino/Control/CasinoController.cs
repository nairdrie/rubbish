using System;
using UnityEngine;

/// <summary>
/// The local-player controller of one seat (Docs/Casino.md §9, §12): one per game, made when the player uses a station and destroyed when
/// they leave. It owns the seat camera session, the world cursor, the hint strip and the HUD cash hold for as long as the player sits, and
/// turns clicks and keys into <see cref="CasinoClient"/> intents. It draws NOTHING of the table (cards, chips, the wheel, name tags are the
/// presenters', driven from the host's snapshots); it only lights the plates, marks the selected chip and shows what is pending locally.
/// <para><b>Leaving, by any path:</b> Esc / pad B / Start (the seat camera's own exit), the host standing the player up (a
/// <see cref="CasinoNotice"/>), a pass-out, a teleport, a carry, the session ending: every path ends in <see cref="End"/>, which tells the
/// host (<c>Leave</c>), releases the HUD cash hold, drops the cursor and the strip, puts the plates back and destroys the controller.</para>
/// </summary>
public abstract class CasinoController : MonoBehaviour
{
    protected CasinoStation station;
    protected int spotIndex = -1;
    protected CasinoSpot spot;
    protected CasinoCursor cursor;
    protected CasinoSeatCamera cam;
    protected CasinoHintStrip strip;

    bool began, ended, holding, leanApplied, watchApplied;
    float beganAt, holdRefreshedAt;
    CasinoSeatCamera.ExitReason endReason;

    // ------------------------------------------------------------------ what a game says

    /// <summary>The host table id this seat is at ("bj0", "rl0", "slot2"), or null (the bar has no host seat).</summary>
    public abstract string TableId { get; }
    /// <summary>Cursor mode of the seat camera for this game.</summary>
    protected virtual CasinoSeatCamera.CursorMode WantedMode => CasinoSeatCamera.CursorMode.Locked;
    /// <summary>The player sat down: subscribe to snapshots, set the plates, write the first hint.</summary>
    protected virtual void OnBegin() { }
    /// <summary>The player stood up (or was stood up): unsubscribe, flush what must be flushed. The base class does the rest.</summary>
    protected virtual void OnEnd(CasinoSeatCamera.ExitReason reason) { }
    /// <summary>Every frame while seated and not blocked by another UI. Unscaled time.</summary>
    protected abstract void Tick(float dt);
    protected virtual void OnClicked(CasinoCursor.Click click) { }
    protected virtual void OnHover(CasinoPickable from, CasinoPickable to) { }
    /// <summary>A 3D plate of THIS station and spot was pressed (by a click or by <see cref="Press"/>).</summary>
    protected virtual void OnPlate(CasinoButton button) { }
    /// <summary>The host did something to this player. Return true when handled.</summary>
    public virtual void OnNotice(string table, CasinoNotice notice) { }
    /// <summary>Hover text for the strip's message line.</summary>
    protected virtual string DescribeHover(CasinoPickable p) => p != null ? p.hint : "";

    public CasinoStation Station => station;
    public int SpotIndex => spotIndex;
    public bool IsEnded => ended;
    public bool IsSeated => began && !ended;
    public CasinoCursor Cursor => cursor;

    /// <summary>Tools and tests: leave the cursor alone (set <c>Cursor.Position</c> and call <c>PressPrimary()</c> / <c>PressSecondary()</c> yourself).</summary>
    public bool ScriptedCursor;

    // ------------------------------------------------------------------ begin / end

    /// <summary>Sit down: the seat camera takes the player. False (nothing changed) when it refuses.</summary>
    public bool Begin(CasinoStation st, int spotIdx)
    {
        if (began || st == null) return false;
        station = st; spotIndex = spotIdx; spot = st.Spot(spotIdx);
        if (spot == null) return false;
        cam = CasinoSeatCamera.Ensure();
        if (!cam.Enter(st, spotIdx)) return false;
        began = true;
        beganAt = Time.unscaledTime;
        cam.Exited += OnCamExited;
        cam.Mode = WantedMode;
        cursor = CasinoCursor.Create(transform);
        cursor.onlyStation = st;
        cursor.Clicked += OnCursor;
        cursor.HoverChanged += OnHoverChanged;
        cursor.SuppressFor(0.4f);
        CasinoButton.AnyPressed += OnAnyPlate;
        strip = CasinoHintStrip.Ensure();
        strip.SetWanted(true);
        try { OnBegin(); }
        catch (Exception e) { Debug.LogException(e); }
        return true;
    }

    void OnCamExited(CasinoStation st, int spotIdx, CasinoSeatCamera.ExitReason reason)
    {
        if (st != station || spotIdx != spotIndex) return;
        End(reason);
    }

    /// <summary>Leave the seat by the front door: the camera glides back, then <see cref="End"/> runs from its Exited event.</summary>
    public void RequestExit()
    {
        if (!began || ended) return;
        if (cam != null && cam.Seated) cam.Exit(CasinoSeatCamera.ExitReason.Requested);
        else End(CasinoSeatCamera.ExitReason.Requested);
    }

    /// <summary>Everything that stands the player up, whatever the reason. Safe to call twice.</summary>
    protected void End(CasinoSeatCamera.ExitReason reason)
    {
        if (ended) return;
        ended = true;
        endReason = reason;
        if (cam != null) cam.Exited -= OnCamExited;
        CasinoButton.AnyPressed -= OnAnyPlate;
        try { OnEnd(reason); }
        catch (Exception e) { Debug.LogException(e); }
        if (cursor != null)
        {
            cursor.Clicked -= OnCursor;
            cursor.HoverChanged -= OnHoverChanged;
            Destroy(cursor.gameObject);
            cursor = null;
        }
        if (strip != null) { strip.SetReticle(false, Vector2.zero, false); strip.SetWanted(false); }
        if (holding) { CasinoClient.HoldHudCash(false); holding = false; }
        string table = TableId;
        if (!string.IsNullOrEmpty(table))
        {
            try { CasinoClient.Leave(table); }
            catch (Exception e) { Debug.LogException(e); }
        }
        if (CasinoStation.LocalBusy == station && (cam == null || !cam.Seated)) CasinoStation.LocalBusy = null;
        ToastFor(reason);
        CasinoControl.Cleared(this);
        Destroy(this);
    }

    void ToastFor(CasinoSeatCamera.ExitReason reason)
    {
        switch (reason)
        {
            case CasinoSeatCamera.ExitReason.Teleported:
            case CasinoSeatCamera.ExitReason.Body:
            case CasinoSeatCamera.ExitReason.Vehicle:
            case CasinoSeatCamera.ExitReason.LeftCasino:
                GameHUD.Toast("You left your seat.", ToastStyle.Info, 1.8f);
                break;
        }
    }

    void OnDestroy()
    {
        if (began && !ended) End(CasinoSeatCamera.ExitReason.Session);
    }

    // ------------------------------------------------------------------ frame

    void Update()
    {
        if (!began || ended) return;
        if (cam == null || !cam.Seated || cam.Station != station)
        {
            End(CasinoSeatCamera.ExitReason.Requested); // the camera left without telling us (should not happen)
            return;
        }
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        // the HUD lets go of a hold nobody renews after 15 s: a long hand keeps renewing it while we mean it
        if (holding && Time.unscaledTime - holdRefreshedAt > 6f) { holdRefreshedAt = Time.unscaledTime; CasinoClient.HoldHudCash(true); }
        bool blocked = PlayerController.IsUIBlocking;
        if (cursor != null) cursor.readInput = !ScriptedCursor && !blocked && !cam.Gliding;
        if (blocked) return;

        // lean and watch: hold keys (the camera blends; a disabled pose does nothing)
        bool wantWatch = CasinoInput.WatchHeld && spot != null && spot.HasWatch;
        bool wantLean = !wantWatch && CasinoInput.LeanHeld && spot != null && spot.HasLean;
        if (wantWatch != watchApplied) { watchApplied = wantWatch; cam.Watch(wantWatch); }
        if (wantLean != leanApplied) { leanApplied = wantLean; cam.Lean(wantLean); }

        UpdateReticle();
        if (cam.Gliding) return; // a click or key while the camera is still swooping in is the press that sat us down
        try { Tick(dt); }
        catch (Exception e) { Debug.LogException(e); }
    }

    void UpdateReticle()
    {
        if (strip == null || cursor == null) return;
        bool locked = UnityEngine.Cursor.lockState == CursorLockMode.Locked;
        bool pad = cursor.CurrentSource == CasinoCursor.Source.Pad;
        bool show = locked || pad;
        bool hot = cursor.Hovered != null && cursor.Hovered.Clickable;
        strip.SetReticle(show, cursor.Position, hot);
    }

    void OnCursor(CasinoCursor.Click c)
    {
        if (!began || ended || cam == null || cam.Gliding || PlayerController.IsUIBlocking) return;
        try { OnClicked(c); }
        catch (Exception e) { Debug.LogException(e); }
    }

    void OnHoverChanged(CasinoPickable from, CasinoPickable to)
    {
        try
        {
            OnHover(from, to);
            if (strip != null) strip.SetHover(to != null ? DescribeHover(to) : "");
        }
        catch (Exception e) { Debug.LogException(e); }
    }

    void OnAnyPlate(CasinoButton b)
    {
        if (!began || ended || b == null || b.station != station || b.spot != spotIndex) return;
        try { OnPlate(b); }
        catch (Exception e) { Debug.LogException(e); }
    }

    // ------------------------------------------------------------------ helpers for the games

    /// <summary>Press a plate of this spot by id (the same path as a click): false when it is missing or disabled.</summary>
    protected bool Press(string id)
    {
        var b = spot != null ? spot.Button(id) : null;
        return b != null && b.Press();
    }

    protected void Say(string text, HintTone tone = HintTone.Normal, float seconds = 3.2f)
    {
        if (strip != null) strip.Say(text, tone, seconds);
    }

    /// <summary>A refusal: red line and a buzz.</summary>
    protected void Refuse(string text, float seconds = 3f)
    {
        Say(text, HintTone.Bad, seconds);
        Sfx.Play("buzz", spot != null && spot.eye != null ? spot.eye.position : (Vector3?)null, 0.35f, 1.2f);
    }

    protected void HoldCash(bool on)
    {
        if (on == holding) return;
        holding = on;
        holdRefreshedAt = Time.unscaledTime;
        CasinoClient.HoldHudCash(on);
    }

    protected bool HoldingCash => holding;
    protected long Cash => CasinoClient.CashCents;
    protected static string Dollars(long cents) => CasinoRules.Dollars(cents);
    protected Vector3 EyePosition => spot != null && spot.eye != null ? spot.eye.position : transform.position;
    protected double SecondsSinceBegan => Time.unscaledTime - beganAt;
}
