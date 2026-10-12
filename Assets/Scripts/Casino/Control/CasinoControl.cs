using System;
using UnityEngine;

/// <summary>
/// The front desk of seat mode (Docs/Casino.md §12): listens for <see cref="CasinoStation.Used"/>, asks the host for a seat through
/// <see cref="CasinoClient"/> (blackjack: the nearest free seat, roulette: join the wheel, slots: take the machine, the bar: the nearest
/// free bar spot), and on success makes the controller of that game, which swoops the camera in. Also routes the host's notices
/// (<see cref="CasinoClient.Notice"/>) to the seated controller and turns the room's big wins into toasts. The world side knows nothing
/// about any of this.
/// </summary>
public static class CasinoControl
{
    /// <summary>The controller this machine's player sits at, or null.</summary>
    public static CasinoController Active { get; private set; }

    static bool pending;
    static GameObject host;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return; // a headless bot: no seats
        CasinoStation.Used -= OnUsed;
        CasinoStation.Used += OnUsed;
        CasinoClient.Notice -= OnNotice;
        CasinoClient.Notice += OnNotice;
        CasinoClient.BigWin -= OnBigWin;
        CasinoClient.BigWin += OnBigWin;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Active = null;
        pending = false;
        host = null;
    }

    static void OnUsed(CasinoStation station)
    {
        if (station != null) TryEnter(station);
    }

    /// <summary>
    /// Use a station: sit down through the host and, when it says yes, glide in. Returns false at once when nothing was started (already
    /// seated, no player, no casino in this session). <paramref name="done"/> gets the final answer (true = seated).
    /// </summary>
    public static bool TryEnter(CasinoStation station, Action<bool> done = null)
    {
        if (station == null || !Application.isPlaying) return false;
        if (Active != null || pending || CasinoSeatCamera.IsSeated) return false;
        var me = CasinoWorld.Me;
        if (me == null || !me.IsOwner) return false;
        if (!CasinoClient.Reachable)
        {
            GameHUD.Toast("The house isn't taking bets in this session.", ToastStyle.Info, 2.5f);
            Sfx.Play("buzz", null, 0.5f);
            return false;
        }
        Vector3 at = me.transform.position;
        switch (station.kind)
        {
            case CasinoStationKind.Bar:
                return Finish(station, null, PickBarSpot(station, at), done);
            case CasinoStationKind.Slot:
            {
                int occ = CasinoClient.OccupantOfSlot(station.index);
                if (occ >= 0 && occ != CasinoClient.LocalKey)
                {
                    GameHUD.Toast("In use · " + NameOrSomebody(occ), ToastStyle.Info, 1.8f);
                    Sfx.Play("buzz", station.transform.position, 0.4f);
                    done?.Invoke(false);
                    return false;
                }
                return Request(station, CasinoTableIds.Slot(station.index), -1, done, _ => 0);
            }
            case CasinoStationKind.Roulette:
                return Request(station, CasinoTableIds.Roulette, -1, done, r => r.seat); // the chip colour is the spot: nobody shares one
            default:
                return Request(station, CasinoTableIds.Blackjack, station.NearestSpot(at), done, r => r.seat);
        }
    }

    static bool Request(CasinoStation station, string table, int preferred, Action<bool> done, Func<CasinoResult, int> spotOf)
    {
        pending = true;
        CasinoClient.Sit(table, preferred, r =>
        {
            pending = false;
            if (!r.Seated)
            {
                SayNo(station, r);
                done?.Invoke(false);
                return;
            }
            // the answer can come late on a guest: is the player still here, and still free to sit?
            var me = CasinoWorld.Me;
            bool ok = station != null && me != null && me.IsOwner && Active == null && !CasinoSeatCamera.IsSeated && CasinoWorld.LocalInside;
            if (!ok) { CasinoClient.Leave(table); done?.Invoke(false); return; }
            if (!Finish(station, table, Mathf.Clamp(spotOf(r), 0, Mathf.Max(0, station.SpotCount - 1)), done)) CasinoClient.Leave(table); // the seat camera said no: give the seat back
        });
        return true;
    }

    /// <summary>Make the controller and glide in. False (and the host seat to be given back by the caller) when the seat camera refuses.</summary>
    static bool Finish(CasinoStation station, string table, int spotIndex, Action<bool> done)
    {
        var go = Host();
        CasinoController c;
        switch (station.kind)
        {
            case CasinoStationKind.Blackjack: c = go.AddComponent<BlackjackController>(); break;
            case CasinoStationKind.Roulette: c = go.AddComponent<RouletteController>(); break;
            case CasinoStationKind.Slot: c = go.AddComponent<SlotController>(); break;
            default: c = go.AddComponent<BarController>(); break;
        }
        if (!c.Begin(station, spotIndex))
        {
            UnityEngine.Object.Destroy(c);
            done?.Invoke(false);
            return false;
        }
        Active = c;
        done?.Invoke(true);
        return true;
    }

    internal static void Cleared(CasinoController c)
    {
        if (Active == c) Active = null;
    }

    static GameObject Host()
    {
        if (host != null) return host;
        host = new GameObject("[CasinoControl]");
        UnityEngine.Object.DontDestroyOnLoad(host);
        return host;
    }

    static void SayNo(CasinoStation station, CasinoResult r)
    {
        string why;
        if (r.gate == CasinoError.Ok && r.error == CasinoTableError.Occupied && station != null && station.kind == CasinoStationKind.Slot)
            why = "In use · " + NameOrSomebody(CasinoClient.OccupantOfSlot(station.index));
        else if (r.gate == CasinoError.Ok && r.error == CasinoTableError.TableFull) why = "Table full";
        else why = CasinoTables.Describe(r);
        if (string.IsNullOrEmpty(why)) why = "Can't sit there right now.";
        GameHUD.Toast(why, ToastStyle.Info, 2f);
        Sfx.Play("buzz", station != null ? station.transform.position : (Vector3?)null, 0.4f);
    }

    static string NameOrSomebody(int key)
    {
        string n = CasinoClient.NameOf(key);
        return string.IsNullOrEmpty(n) ? "somebody" : n;
    }

    /// <summary>The bar has no host seat: take the spot nearest the player, preferring one nobody else is standing at.</summary>
    static int PickBarSpot(CasinoStation station, Vector3 from)
    {
        int best = -1; float bd = float.MaxValue;
        var others = UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
        for (int i = 0; i < station.SpotCount; i++)
        {
            var s = station.Spot(i);
            if (s == null || s.stand == null) continue;
            float d = (s.stand.position - from).sqrMagnitude;
            for (int k = 0; k < others.Length; k++)
            {
                var o = others[k];
                if (o == null || o.IsOwner) continue;
                if ((o.transform.position - s.stand.position).sqrMagnitude < 0.7f * 0.7f) { d += 100f; break; }
            }
            if (d < bd) { bd = d; best = i; }
        }
        return Mathf.Max(0, best);
    }

    // ------------------------------------------------------------------ the host's notices, the room's news

    static void OnNotice(string table, CasinoNotice notice)
    {
        var c = Active;
        if (c != null && !c.IsEnded) c.OnNotice(table, notice);
    }

    static void OnBigWin(int key, string who, string what, long cents)
    {
        if (key == CasinoClient.LocalKey) return;   // the winner's own seat celebrates
        if (!CasinoWorld.LocalInside) return;       // only the room hears it
        GameHUD.Toast((string.IsNullOrEmpty(who) ? "Somebody" : who) + " hit " + what + ": " + CasinoRules.Dollars(cents) + "!", ToastStyle.Rare, 4f);
        Sfx.Play("sparkle", null, 0.5f);
    }

    /// <summary>A win worth a noise. Big ones (50x the stake, or $1,000) get the works; the host tells the room itself.</summary>
    public static void Celebrate(long wonCents, long stakeCents, string what)
    {
        if (wonCents <= 0) return;
        bool big = CasinoBank.IsBig(wonCents, stakeCents);
        if (!big)
        {
            Sfx.Play("cash", null, 0.55f, wonCents >= stakeCents * 5 ? 1.15f : 1f);
            return;
        }
        Sfx.Play("legendary", null, 0.8f);
        Sfx.Play("cash", null, 0.7f, 0.9f);
        GameHUD.Toast((string.IsNullOrEmpty(what) ? "BIG WIN" : what) + ": " + CasinoRules.Dollars(wonCents) + "!", ToastStyle.Legendary, 4f);
        Juice.Shake(0.22f, 0.35f);
    }
}
