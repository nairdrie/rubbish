using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>One card that is new in a blackjack snapshot (<see cref="BlackjackDiff.NewCards"/>).</summary>
public struct DealtCard
{
    /// <summary>The seat (0..4) or -1 for the dealer.</summary>
    public int seat;
    public int hand, index;
    public byte card;
    /// <summary>-1: out of the shoe. 0.. : the hand of this seat it was taken from (a split's second card).</summary>
    public int fromHand;
}

/// <summary>How a blackjack hand reads on the felt.</summary>
public enum HandTone { Plain, Active, Twenty1, Blackjack, Bust }

/// <summary>
/// The pure parts of the presenters (Docs/Casino.md §11): everything that can be decided from snapshots and the clock alone, so
/// EditMode tests can pin it down: chip decomposition, which cards are new between two snapshots, the roulette ball's path, the
/// slot drum's travel, the pay plate's words and how a hand's total is printed.
/// </summary>
public static class BlackjackDiff
{
    /// <summary>
    /// The cards in <paramref name="next"/> that <paramref name="prev"/> did not have (appended to <paramref name="into"/>, which is NOT cleared).
    /// A new round, or no previous snapshot, makes every card new. A split's second hand starts with the card that left the first hand
    /// (<see cref="DealtCard.fromHand"/>). Cards that went away are not reported (the presenter reconciles what it shows).
    /// </summary>
    public static int NewCards(in BlackjackTableSnapshot prev, bool hasPrev, in BlackjackTableSnapshot next, List<DealtCard> into)
    {
        int added = 0;
        bool fresh = !hasPrev || prev.round != next.round;
        // the dealer
        int pd = fresh || prev.dealer == null ? 0 : prev.dealer.Length;
        int nd = next.dealer == null ? 0 : next.dealer.Length;
        for (int i = pd; i < nd; i++) { into.Add(new DealtCard { seat = -1, hand = 0, index = i, card = next.dealer[i], fromHand = -1 }); added++; }
        if (next.seats == null) return added;
        for (int s = 0; s < next.seats.Length; s++)
        {
            var hands = next.seats[s].hands;
            if (hands == null) continue;
            var oldHands = !fresh && prev.seats != null && s < prev.seats.Length ? prev.seats[s].hands : null;
            for (int h = 0; h < hands.Length; h++)
            {
                var cards = hands[h].cards;
                if (cards == null) continue;
                int had = oldHands != null && h < oldHands.Length && oldHands[h].cards != null ? oldHands[h].cards.Length : 0;
                bool splitOff = oldHands != null && h >= oldHands.Length && h > 0;
                for (int i = had; i < cards.Length; i++)
                {
                    int from = splitOff && i == 0 ? h - 1 : -1;
                    into.Add(new DealtCard { seat = s, hand = h, index = i, card = cards[i], fromHand = from });
                    added++;
                }
            }
        }
        return added;
    }

    /// <summary>Did the dealer's hole card just turn over (hidden in <paramref name="prev"/>, a real card in <paramref name="next"/>)?</summary>
    public static bool HoleRevealed(in BlackjackTableSnapshot prev, bool hasPrev, in BlackjackTableSnapshot next)
    {
        if (!hasPrev || prev.round != next.round) return false;
        return prev.dealer != null && prev.dealer.Length >= 2 && prev.dealer[1] == BlackjackTable.Hidden &&
               next.dealer != null && next.dealer.Length >= 2 && next.dealer[1] != BlackjackTable.Hidden;
    }

    /// <summary>How a hand's total is printed: "17", "7/17" (soft), "BUST 24", "BLACKJACK", "21".</summary>
    public static string HandText(byte[] cards, int count, bool blackjackFlag, out HandTone tone)
    {
        tone = HandTone.Plain;
        if (cards == null || count <= 0) return "";
        int best = BlackjackRules.HandValue(cards, count, out bool soft);
        if (best > 21) { tone = HandTone.Bust; return "BUST " + best.ToString(CultureInfo.InvariantCulture); }
        if (blackjackFlag) { tone = HandTone.Blackjack; return "BLACKJACK"; }
        if (best == 21) { tone = HandTone.Twenty1; return "21"; }
        if (soft) return (best - 10).ToString(CultureInfo.InvariantCulture) + "/" + best.ToString(CultureInfo.InvariantCulture);
        return best.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Chips: how an amount is shown as a stack.</summary>
public static class CasinoChipMath
{
    public static readonly int[] BlackjackDenoms = { 10000, 5000, 2500, 1000, 500, 100 };
    public static readonly int[] RouletteDenoms = { 10000, 2500, 500, 100 };

    /// <summary>
    /// Greedy decomposition of <paramref name="cents"/> into chips of the given denominations (descending): the largest first, so the
    /// stack reads big chips at the bottom. Appends to <paramref name="into"/> (not cleared) and returns the number of chips added.
    /// Whatever is smaller than the smallest chip is dropped (bets are whole dollars, so nothing is).
    /// </summary>
    public static int Decompose(long cents, int[] denomsDescending, List<int> into)
    {
        int added = 0;
        long left = Math.Max(0, cents);
        for (int i = 0; i < denomsDescending.Length; i++)
        {
            int d = denomsDescending[i];
            long n = left / d;
            for (long k = 0; k < n; k++) { into.Add(d); added++; }
            left -= n * d;
        }
        return added;
    }

    /// <summary>The value of a decomposition (sum of its chips).</summary>
    public static long Sum(List<int> chips)
    {
        long t = 0;
        for (int i = 0; i < chips.Count; i++) t += chips[i];
        return t;
    }

    /// <summary>"$85": whole dollars as printed on a label.</summary>
    public static string Dollars(long cents) => CasinoRules.Dollars(cents);

    /// <summary>"+$85", "-$20", "PUSH" style signed amount.</summary>
    public static string Signed(long cents) => cents > 0 ? "+" + CasinoRules.Dollars(cents) : cents < 0 ? "-" + CasinoRules.Dollars(-cents) : "$0";
}

/// <summary>
/// The roulette wheel and ball as pure functions of time (Docs/Casino.md §11): every machine computes the same picture from the
/// spin's pocket, its start and the shared table clock. The rotor turns at a constant rate, always; the ball is thrown against it,
/// slows, drops off the track, hops and comes to rest in the true pocket EXACTLY <see cref="SpinSeconds"/> after the start.
/// </summary>
public static class CasinoWheelMath
{
    public const double SpinSeconds = 7.0;
    /// <summary>The rotor's turning rate in degrees per second, clockwise seen from above (constant, whatever the phase).</summary>
    public const float RotorDegPerSec = 26f;
    /// <summary>The ball leaves the track at <see cref="DropStart"/>, is down in the pocket ring by <see cref="DropEnd"/>, then hops until the end.</summary>
    public const float DropStart = 4.9f, DropEnd = 5.7f;
    const float Turns = 7f, Exponent = 2.2f, LaunchSeconds = 0.35f, HopDegrees = 22f;

    /// <summary>The rotor's angle (degrees, 0..360) at a table time. A pure function of the clock, so every machine agrees.</summary>
    public static float RotorAngle(double tableTime)
    {
        double a = (tableTime * RotorDegPerSec) % 360.0;
        return (float)(a < 0 ? a + 360.0 : a);
    }

    /// <summary>The angle of pocket <paramref name="pocket"/> (0..36, 37 = 00) on the rotor, degrees clockwise from the rotor's +Z; -1 for a bad pocket.</summary>
    public static float PocketAngle(int pocket)
    {
        int k = Array.IndexOf(CasinoStation.WheelOrder, pocket);
        return k < 0 ? -1f : k * 360f / CasinoStation.WheelOrder.Length;
    }

    static float Smooth(float x) { x = Math.Max(0f, Math.Min(1f, x)); return x * x * (3f - 2f * x); }

    static float Wrap180(float a)
    {
        a %= 360f;
        if (a > 180f) a -= 360f;
        if (a < -180f) a += 360f;
        return a;
    }

    /// <summary>
    /// The ball at <paramref name="t"/> seconds after the spin started. <paramref name="rotorAtT"/> is <see cref="RotorAngle"/> at that instant.
    /// <paramref name="restPocket"/> is where the ball sat before (-1 = unknown: it appears on the track). Outputs: the ball's angle round the
    /// wheel (degrees, clockwise from +Z, any value), <paramref name="track01"/> (1 = on the ball track, 0 = down in the pocket ring) and
    /// <paramref name="hop"/> (metres above the pocket floor while it bounces). From <see cref="SpinSeconds"/> on it sits exactly in the pocket.
    /// </summary>
    public static void BallAt(int pocket, int restPocket, double t, float rotorAtT, out float angleDeg, out float track01, out float hop)
    {
        float pocketAngle = Math.Max(0f, PocketAngle(pocket));
        float tf = (float)Math.Max(0.0, t);
        if (t >= SpinSeconds)
        {
            angleDeg = rotorAtT + pocketAngle; track01 = 0f; hop = 0f;
            return;
        }
        float rel = Turns * 360f * (float)Math.Pow(1.0 - t / SpinSeconds, Exponent);
        float hopAngle = 0f;
        if (tf > DropStart)
        {
            float u = (tf - DropStart) / (float)(SpinSeconds - DropStart);
            hopAngle = HopDegrees * (1f - u) * (1f - u) * (float)Math.Sin(u * 3.5f * Math.PI);
        }
        float a = rotorAtT + pocketAngle + rel + hopAngle;
        // launched from the old resting pocket: blend the angle over the first moments (the throw)
        if (tf < LaunchSeconds)
        {
            float w = Smooth(tf / LaunchSeconds);
            float rest = restPocket >= 0 ? rotorAtT + Math.Max(0f, PocketAngle(restPocket)) : a;
            a = a - Wrap180(a - rest) * (1f - w);
            track01 = w;
        }
        else if (tf < DropStart) track01 = 1f;
        else track01 = 1f - Smooth((tf - DropStart) / (DropEnd - DropStart));
        hop = 0f;
        if (tf > DropEnd)
        {
            float u2 = (tf - DropEnd) / (float)(SpinSeconds - DropEnd);
            hop = 0.02f * (1f - u2) * (1f - u2) * (float)Math.Abs(Math.Sin(u2 * 3.0 * Math.PI));
        }
        angleDeg = a;
    }

    /// <summary>The ball's angle relative to the rotor at a moment (degrees, folded to 0..360): the pocket's own angle once it has settled.</summary>
    public static float RelativeAngle(float ballAngle, float rotorAtT)
    {
        float r = (ballAngle - rotorAtT) % 360f;
        return r < 0f ? r + 360f : r;
    }
}

/// <summary>The slot drums' travel (Docs/Casino.md §11): how far each reel turns and how it slows into the true stop.</summary>
public static class CasinoReelMath
{
    public const int Faces = CasinoLayout.ReelFaces;
    /// <summary>When reel 0, 1 and 2 stop, seconds after the pull (about 0.4 s apart). The last reel waits longer when the first two hold the top symbol (an honest tease: the stop is still the host's).</summary>
    public static float StopTime(int reel, bool tease) => reel == 2 && tease ? 3.1f : 1.25f + 0.4f * reel;
    public const float SpinUpFraction = 0.08f, CruiseFraction = 0.62f, PeakStopsPerSecond = 26f;

    /// <summary>
    /// Stops of strip the drum turns in <paramref name="duration"/> seconds so it ends on <paramref name="stop"/> having started at
    /// <paramref name="restStop"/>: the nearest count to the cruise speed whose face lands in the window (mod 12 faces).
    /// </summary>
    public static int TravelStops(int restStop, int stop, float duration)
    {
        int want = (int)Math.Round(PeakStopsPerSecond * duration * Area());
        int need = (((restStop - stop) % Faces) + Faces) % Faces;
        int n = want - ((want - need) % Faces + Faces) % Faces; // largest <= want that is congruent to need
        if (n < Faces) n += Faces;
        return n;
    }

    static float Area() => SpinUpFraction * 0.5f + (CruiseFraction - SpinUpFraction) + (1f - CruiseFraction) / 3f;

    /// <summary>0..1 share of the travel done at <paramref name="u"/> (0..1 of the spin): spin up, cruise, then slow into the stop.</summary>
    public static float Progress(float u)
    {
        u = Math.Max(0f, Math.Min(1f, u));
        float a = SpinUpFraction, b = CruiseFraction, total = Area(), done;
        if (u < a) done = u * u / (2f * a);
        else if (u < b) done = a * 0.5f + (u - a);
        else
        {
            float s = (u - b) / (1f - b);
            done = a * 0.5f + (b - a) + (1f - b) * (1f - (1f - s) * (1f - s) * (1f - s)) / 3f;
        }
        return done / total;
    }

    /// <summary>The drum's local X rotation (degrees) <paramref name="t"/> seconds into a spin that started at <paramref name="restStop"/> and stops at <paramref name="stop"/> after <paramref name="duration"/>.</summary>
    public static float AngleDeg(int restStop, int stop, float t, float duration)
    {
        int n = TravelStops(restStop, stop, duration);
        float u = duration <= 0f ? 1f : t / duration;
        float pos = restStop - n * Progress(u); // strip position, decreasing; rotation = -pos * 30 deg
        return -pos * (360f / Faces);
    }

    /// <summary>The rotation (degrees) of a drum resting at <paramref name="stop"/>.</summary>
    public static float RestAngleDeg(int stop) => -stop * (360f / Faces);
}

/// <summary>The words on a slot machine's pay plate, from the real rules (Docs/Casino.md §11).</summary>
public static class SlotPlateText
{
    /// <summary>"3 LUCKY SEVENS", "ANY 3 BARS", "2 CHERRIES": the pay line's name, shortened and shouted.</summary>
    public static string Combo(string payName)
    {
        string s = payName ?? "";
        s = s.Replace("Three ", "3 ").Replace("Two ", "2 ").Replace("One ", "1 ").Replace("Any 3 ", "ANY 3 ").Replace("Any Three ", "ANY 3 ")
             .Replace("Raccoon King's Crown", "CROWN").Replace("The Whole Gang", "WHOLE GANG");
        return s.ToUpperInvariant().Replace("'", "");
    }

    /// <summary>"$500" or "$20,000": what the line pays at this machine's stake.</summary>
    public static string Payout(int machine, int payIndex)
    {
        var m = SlotRules.Machine(machine);
        return CasinoRules.Dollars((long)m.stakeCents * m.pays[payIndex].multiple);
    }

    public static string Returns(int machine) =>
        "RETURNS " + (SlotRules.Return(machine) * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + "% over the long run";

    public static string Stake(int machine) => "STAKE " + CasinoRules.Dollars(SlotRules.StakeCents(machine)) + " A PULL";
}
