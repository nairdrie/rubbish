using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public enum CasinoStationKind { Blackjack, Roulette, Slot, Bar }

/// <summary>
/// One thing you can step up to in The Lucky Raccoon (Docs/Casino.md §3): the blackjack table, the roulette table, a slot
/// machine or the bar. Built and wired by <c>Setup_Casino</c>; found by the player through an <see cref="InteractionZone"/>.
/// <para><b>Contract:</b> when THIS machine's player interacts, <see cref="Used"/> is raised with the station. The world
/// side knows nothing about screens; the screens subscribe. Nothing is sent to anyone.</para>
/// <para>The transforms below are what a screen animates. They are plain children of the station (no physics, no
/// networking): whatever moves them is local and cosmetic.</para>
/// </summary>
public class CasinoStation : MonoBehaviour, IInteractable
{
    /// <summary>Raised on the local machine when its own player uses a station.</summary>
    public static event Action<CasinoStation> Used;

    static readonly List<CasinoStation> none = new List<CasinoStation>();
    /// <summary>Every station of the loaded casino (empty while <c>Casino.unity</c> is not loaded). Listed on every
    /// machine whether or not the local player is inside.</summary>
    public static IReadOnlyList<CasinoStation> All
    {
        get
        {
            var s = CasinoScene.Instance;
            return s != null && s.stations != null ? (IReadOnlyList<CasinoStation>)s.stations : none;
        }
    }

    /// <summary>The station of a kind (<paramref name="index"/>: slot machine 0..4, otherwise 0), or null.</summary>
    public static CasinoStation Find(CasinoStationKind kind, int index = 0)
    {
        var all = All;
        for (int i = 0; i < all.Count; i++)
            if (all[i] != null && all[i].kind == kind && all[i].index == index) return all[i];
        return null;
    }

    [Header("Identity")]
    public CasinoStationKind kind;
    [Tooltip("Slot machine 0..4 (Docs/Casino.md §4.3); 0 for the tables and the bar.")]
    public int index;
    [Tooltip("Shown on the machine and in the prompt (slots).")]
    public string displayName = "";

    [Header("Player")]
    [Tooltip("Where the player stands (feet, facing the station).")]
    public Transform seat;
    [Tooltip("A camera vantage a screen can swoop to: position and rotation of a good view of the play surface.")]
    public Transform view;

    public CasinoStationKind Kind => kind;
    public int Index => index;
    public Transform Seat => seat;
    public Transform View => view;

    [Header("Slot machine")]
    [Tooltip("Reel0..Reel2: drums that spin about their local X (see CasinoLayout.ReelFaces).")]
    public Transform[] reels;
    [Tooltip("Pull lever pivot: rest = identity, pulled = about +70 degrees on local X.")]
    public Transform lever;
    [Tooltip("Where coins land.")]
    public Transform coinTray;
    [Tooltip("Lit glass panels (belly glass, topper): MaterialPropertyBlock-friendly renderers for a win flash.")]
    public Renderer[] glow;

    [Header("Roulette")]
    [Tooltip("The spinning part of the wheel: rotate about local Y. Pocket k of CasinoStation.WheelOrder is centred at local angle k * 360/38 degrees clockwise seen from above, starting at local +Z.")]
    public Transform wheelRotor;
    [Tooltip("The ball: a child of the wheel's fixed bowl. Local Y up; see the radii below.")]
    public Transform ball;
    [Tooltip("Centre of the wheel (fixed), origin of the ball's orbit.")]
    public Transform wheelCentre;
    public float ballTrackRadius = 0.36f, ballPocketRadius = 0.235f, ballTrackHeight = 0.075f, ballPocketHeight = 0.03f;
    [Tooltip("Corners of the printed betting layout on the felt (see LayoutPoint).")]
    public Transform layoutOrigin;
    [Tooltip("Metres per number column (along layoutOrigin +X) and per row (along +Z).")]
    public Vector2 layoutCell = new Vector2(0.115f, 0.16f);

    [Header("Blackjack")]
    [Tooltip("Where the dealer's cards are laid (first card; lay further cards along local +X).")]
    public Transform dealerCards;
    [Tooltip("Up to three player hands (splits): first card of each; lay further cards along local +X, a little up.")]
    public Transform[] playerHands;
    [Tooltip("Where the player's bet sits.")]
    public Transform chipSpot;
    [Tooltip("The mouth of the card shoe: cards slide out along local +Z.")]
    public Transform shoe;
    [Tooltip("The discard holder.")]
    public Transform discard;
    [Tooltip("The dealer's chip tray (payouts come from here).")]
    public Transform chipTray;

    [Header("Bar")]
    [Tooltip("On the counter in front of the player: where the drink appears.")]
    public Transform counterSpot;
    [Tooltip("Where the bartender makes it (behind the counter).")]
    public Transform pourSpot;

    // ------------------------------------------------------------------ seat mode (Docs/Casino.md §9 and §9.1)

    [Header("Seat mode (Docs/Casino.md §9.1)")]
    [Tooltip("Where players stand and look from: blackjack 5 seats (0 = dealer's left / first base), roulette 4 spots (left to right as seen from the players' side), slot 1, bar 3 (south to north).")]
    public CasinoSpot[] spots = new CasinoSpot[0];
    [Tooltip("Blackjack and roulette: the countdown lamp (CasinoTimerLamp.SetValue 0..1).")]
    public CasinoTimerLamp timerLamp;
    [Tooltip("Roulette: the last-12 history board.")]
    public CasinoResultBoard resultBoard;
    [Tooltip("Roulette: the dolly. Hidden at rest; PlaceWinMarker(number) puts it on the felt.")]
    public Transform winMarker;
    [Tooltip("Blackjack: the dealer's six card spots, in dealing order (fanned along the dealer's row, tilted toward the players).")]
    public Transform[] dealerSpots = new Transform[0];
    [Tooltip("Blackjack and roulette: world-space label anchor above the dealer.")]
    public Transform dealerFloatText;
    [Tooltip("The printed felt lettering (blackjack pays / soft 17 / insurance): SetActive(false) while cards cover it.")]
    public Transform printRoot;
    [Tooltip("Blackjack: the lit MIN / MAX sign. Roulette: the limits plaque.")]
    public TMP_Text limitsText;

    [Header("Roulette picking")]
    [Tooltip("A thin trigger plane over the printed layout on CasinoLayout.PickLayer: raycast it, then LayoutHit.")]
    public Collider layoutPlane;
    [Tooltip("Height of one outside row (dozens, then the even-money boxes) in layout rows (cell heights). Matches CasinoRouletteLayout.OutsideRow.")]
    public float layoutOutside = 0.7209f;

    [Header("Slot machine (seat mode)")]
    [Tooltip("The lever's pick collider (a CasinoPickable of kind Lever, id \"Lever\"; it rides the lever, which pulls about +70 degrees on local X).")]
    public CasinoPickable leverPick;
    [Tooltip("The three deck buttons (ids Spin, BetUp, BetDown; also spots[0].buttons in that order).")]
    public CasinoButton spinButton, betUpButton, betDownButton;
    [Tooltip("The lit pay table plate on the front glass.")]
    public CasinoPayPlate payPlate;
    [Tooltip("A soft light over the coin tray for payouts.")]
    public Light coinTrayLight;

    public int SpotCount => spots != null ? spots.Length : 0;

    /// <summary>The spot at this index, or null.</summary>
    public CasinoSpot Spot(int i) => spots != null && i >= 0 && i < spots.Length ? spots[i] : null;

    /// <summary>The spot whose standing place is nearest to a world point, or -1.</summary>
    public int NearestSpot(Vector3 world)
    {
        int best = -1; float bd = float.MaxValue;
        for (int i = 0; spots != null && i < spots.Length; i++)
        {
            var s = spots[i];
            if (s == null || s.stand == null) continue;
            float d = (s.stand.position - world).sqrMagnitude;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Layout coordinates of a world point on the printed roulette layout, in CELL units: <c>u</c> columns along the layout
    /// (number 1's cell spans u 0..1, 34-36 spans 11..12, the "2 to 1" boxes 12..13, 0 / 00 u -1..0), <c>v</c> rows away from
    /// the players (1 / 4 / 7 sit in v 0..1, 3 / 6 / 9 in v 2..3), so the dozens are v -layoutOutside..0 and the even-money
    /// boxes -2*layoutOutside..-layoutOutside. Feed them to <c>CasinoRouletteLayout.SpotAt(u, v)</c> (the QA'd hit-zone mapping).
    /// Returns false off the printed layout.
    /// </summary>
    public bool LayoutHitLocal(Vector3 worldPoint, out float u, out float v)
    {
        u = v = 0f;
        if (layoutOrigin == null || layoutCell.x <= 0f || layoutCell.y <= 0f) return false;
        Vector3 l = layoutOrigin.InverseTransformPoint(worldPoint);
        u = l.x / layoutCell.x; v = l.z / layoutCell.y;
        return u >= -1f && u <= 13f && v >= -2f * layoutOutside && v <= 3f;
    }

    /// <summary>The roulette station's <see cref="LayoutHitLocal"/> (false when there is no roulette table).</summary>
    public static bool LayoutHit(Vector3 worldPoint, out float u, out float v)
    {
        var s = Find(CasinoStationKind.Roulette);
        if (s == null) { u = v = 0f; return false; }
        return s.LayoutHitLocal(worldPoint, out u, out v);
    }

    /// <summary>Inverse of <see cref="LayoutHitLocal"/>: the world point (on the felt) of layout coordinates (cell units).</summary>
    public Vector3 LayoutPoint(float u, float v)
    {
        if (layoutOrigin == null) return transform.position;
        return layoutOrigin.TransformPoint(new Vector3(u * layoutCell.x, 0.002f, v * layoutCell.y));
    }

    /// <summary>Put the dolly on a pocket's cell of the layout (0..36, 37 = 00).</summary>
    public void PlaceWinMarker(int number)
    {
        if (winMarker == null) return;
        winMarker.position = LayoutPoint(number);
        winMarker.gameObject.SetActive(true);
    }

    public void HideWinMarker()
    {
        if (winMarker != null) winMarker.gameObject.SetActive(false);
    }

    void Update()
    {
        if (kind != CasinoStationKind.Blackjack || spots == null) return;
        float dt = Time.unscaledDeltaTime;
        for (int i = 0; i < spots.Length; i++) spots[i]?.TickStool(dt);
    }

    /// <summary>American wheel order, clockwise from 0 (37 stands for 00).</summary>
    public static readonly int[] WheelOrder =
    {
        0, 28, 9, 26, 30, 11, 7, 20, 32, 17, 5, 22, 34, 15, 3, 24, 36, 13, 1,
        37, 27, 10, 25, 29, 12, 8, 19, 31, 18, 6, 21, 33, 16, 4, 23, 35, 14, 2,
    };

    static readonly HashSet<int> Reds = new HashSet<int> { 1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36 };
    public static bool IsRed(int number) => Reds.Contains(number);

    /// <summary>Local angle (degrees, clockwise from the rotor's +Z seen from above) of a number's pocket on the rotor
    /// (37 = 00), or -1.</summary>
    public static float PocketAngle(int number)
    {
        int k = Array.IndexOf(WheelOrder, number);
        return k < 0 ? -1f : k * 360f / WheelOrder.Length;
    }

    /// <summary>
    /// World centre of a number's cell on the printed layout (roulette station): 1..36 in the usual 12 x 3 grid (1 at the
    /// wheel end, the 3-6-9 row furthest from the player), 0 and 37 (00) in the two head cells.
    /// </summary>
    public Vector3 LayoutPoint(int number)
    {
        if (layoutOrigin == null) return transform.position;
        float x, z;
        if (number <= 0) { x = -0.5f; z = 0.75f; }
        else if (number >= 37) { x = -0.5f; z = 2.25f; }
        else { x = (number - 1) / 3 + 0.5f; z = (number - 1) % 3 + 0.5f; }
        return layoutOrigin.TransformPoint(new Vector3(x * layoutCell.x, 0.002f, z * layoutCell.y));
    }

    public string Prompt
    {
        get
        {
            switch (kind)
            {
                case CasinoStationKind.Blackjack: return "Play Blackjack";
                case CasinoStationKind.Roulette: return "Play Roulette";
                case CasinoStationKind.Bar: return "Order a drink";
                default: return "Play " + (string.IsNullOrEmpty(displayName) ? CasinoLayout.SlotName(index) : displayName);
            }
        }
    }

    /// <summary>Set by a screen while this machine's player is at a station, so the prompt and a second press don't
    /// stack. Cleared by the screen when it closes.</summary>
    public static CasinoStation LocalBusy;

    float usedAt = -10f;

    public bool CanInteract() => LocalBusy == null && !CasinoWorld.Transitioning && !DrunkStatus.PassingOut && Time.unscaledTime - usedAt > 0.4f;

    public string GetInteractionPrompt() => Prompt;

    public void OnInteract(GameObject interactor)
    {
        var pc = interactor != null ? interactor.GetComponent<PlayerController>() : null;
        if (pc == null || !pc.IsOwner || pc.IsDriving || !CanInteract()) return; // the local player only
        usedAt = Time.unscaledTime;
        var handlers = Used;
        if (handlers == null)
        {
            // No screen is listening yet (the world side is testable on its own).
            GameHUD.Toast("The table's closed for a minute.", ToastStyle.Info, 1.6f);
            Sfx.Play("click", transform.position, 0.6f);
            return;
        }
        try { handlers(this); }
        catch (Exception e) { Debug.LogException(e); }
    }

    public void Highlight(bool highlight) { }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Used = null;
        LocalBusy = null;
    }
}
