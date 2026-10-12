using System;
using TMPro;
using UnityEngine;

/// <summary>
/// One place a player can occupy at a <see cref="CasinoStation"/> in seat mode (Docs/Casino.md §9 and §9.1): a blackjack
/// seat (5), a roulette spot (4), a slot machine's one spot, a bar spot (3). Built by <c>Setup_Casino</c>; every Transform
/// is a plain empty child with a stable name, there for the presenters / controllers to place props on.
/// <para><b>Frames.</b> <see cref="stand"/> is on the floor, forward = facing the table. <see cref="eye"/> / <see cref="lean"/>
/// / <see cref="watch"/> are camera poses (forward = the camera's forward, no roll). For the table spots the felt-side
/// transforms (<see cref="betCircle"/>, <see cref="handSpots"/>, ...) are laid out in the PLAYER'S frame: local +X is the
/// player's right, +Z points away from the player toward the dealer, +Y is up from the felt.</para>
/// </summary>
[Serializable]
public class CasinoSpot
{
    public string label = "";

    [Header("Where the player is")]
    [Tooltip("Feet on the floor, clear of colliders, forward = facing the table.")]
    public Transform stand;
    [Tooltip("First-person eye pose (about 1.3 m up).")]
    public Transform eye;
    [Tooltip("Optional closer / lower pose for looking at your own cards or the layout. Null = none.")]
    public Transform lean;
    [Tooltip("Optional third pose: the other thing worth watching from here (roulette: the wheel, blackjack: the dealer, bar: the menu). Null = none.")]
    public Transform watch;
    public float eyeFov = 60f, leanFov = 48f, watchFov = 52f;
    [Tooltip("Look limits. Yaw is +/- degrees from the pose's own facing; pitch is ABSOLUTE degrees above the horizon (negative = down).")]
    public float lookYaw = 55f, lookPitchMin = -70f, lookPitchMax = 30f;
    [Tooltip("World-space label anchor above the player's head.")]
    public Transform floatText;

    [Header("Stool (blackjack)")]
    public Transform stool;
    [Tooltip("Offset in the stool's PARENT space that slides it clear of an occupied spot.")]
    public Vector3 stoolAway;

    [Header("Table spots (player frame: +X right, +Z toward the dealer)")]
    [Tooltip("Blackjack: the bet circle. Roulette: the spot's chip rest.")]
    public Transform betCircle;
    [Tooltip("Blackjack: where an insurance bet sits (the printed band).")]
    public Transform insuranceSpot;
    [Tooltip("The numbered brass plate (blackjack).")]
    public Transform plate;
    [Tooltip("Blackjack: three hands (splits); each a row of up to five card spots, fanned a little toward the player.")]
    public CasinoHandSpot[] handSpots = new CasinoHandSpot[0];
    [Tooltip("Where the stacks of each denomination stand (CasinoLayout.BlackjackChipCents / RouletteChipCents), left to right.")]
    public Transform[] chipRack = new Transform[0];
    [Tooltip("3D buttons: blackjack Hit, Stand, Double, Split, Insurance; roulette Ready, Undo, Clear, Rebet (ids as CasinoLayout.*ButtonIds).")]
    public CasinoButton[] buttons = new CasinoButton[0];

    [Header("Bar")]
    [Tooltip("On the counter in front of this spot: where the glass appears.")]
    public Transform counterSpot;
    [Tooltip("Behind the counter, where the bartender makes this spot's drink.")]
    public Transform pourSpot;
    [Tooltip("The menu card's four drink slots (hover / click).")]
    public CasinoDrinkSlot[] drinkSlots = new CasinoDrinkSlot[0];

    // ---------------------------------------------------------------- helpers

    public bool HasLean => lean != null;
    public bool HasWatch => watch != null;

    /// <summary>The button with this id (case-insensitive), or null.</summary>
    public CasinoButton Button(string id)
    {
        if (buttons == null) return null;
        for (int i = 0; i < buttons.Length; i++)
            if (buttons[i] != null && string.Equals(buttons[i].id, id, StringComparison.OrdinalIgnoreCase)) return buttons[i];
        return null;
    }

    /// <summary>Where the player is placed: floor position and facing.</summary>
    public Pose StandPose => stand != null ? new Pose(stand.position, stand.rotation) : default;

    // ---------------------------------------------------------------- stool slide (cosmetic, local)

    [NonSerialized] Vector3 stoolRest;
    [NonSerialized] bool restKnown, stoolWantAway;
    [NonSerialized] float stoolT;

    public bool StoolIsAway => stoolWantAway;

    /// <summary>Slide this spot's stool out of the way of its occupant (or back). Local and cosmetic; the controller calls it
    /// from the replicated seat state so every machine shows it.</summary>
    public void SetStoolAway(bool away, bool instant = false)
    {
        if (stool == null) return;
        if (!restKnown) { stoolRest = stool.localPosition; restKnown = true; }
        stoolWantAway = away;
        if (instant) { stoolT = away ? 1f : 0f; stool.localPosition = stoolRest + stoolAway * stoolT; }
    }

    /// <summary>Advance the slide. Returns true while still moving.</summary>
    public bool TickStool(float dt)
    {
        if (stool == null || !restKnown) return false;
        float target = stoolWantAway ? 1f : 0f;
        if (Mathf.Approximately(stoolT, target)) return false;
        stoolT = Mathf.MoveTowards(stoolT, target, dt / 0.4f);
        float s = stoolT * stoolT * (3f - 2f * stoolT);
        stool.localPosition = stoolRest + stoolAway * s;
        return !Mathf.Approximately(stoolT, target);
    }
}

/// <summary>A hand's row of card spots on the felt (blackjack). Lay card k at <c>cards[k]</c> (position and rotation: they are
/// fanned a little and tilted toward the player so faces read from the eye).</summary>
[Serializable]
public class CasinoHandSpot
{
    public Transform root;
    [Tooltip("Up to five spots, in dealing order.")]
    public Transform[] cards = new Transform[0];
    [Tooltip("World-space label anchor above the hand (total, bet).")]
    public Transform floatText;
}

/// <summary>One drink on the bar's menu card: a hover / click button plus the three text areas to fill.</summary>
[Serializable]
public class CasinoDrinkSlot
{
    public CasinoButton button;
    public TMP_Text nameText, priceText, effectText;

    public void Set(string name, string price, string effect)
    {
        if (nameText != null) nameText.text = name ?? "";
        if (priceText != null) priceText.text = price ?? "";
        if (effectText != null) effectText.text = effect ?? "";
        if (button != null) button.label = name ?? "";
    }
}
