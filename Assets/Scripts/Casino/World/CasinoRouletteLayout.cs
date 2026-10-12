using UnityEngine;

/// <summary>
/// The roulette betting layout as pure geometry in LAYOUT CELL UNITS (Docs/Casino.md §9.1): the same coordinates
/// <see cref="CasinoStation.LayoutHit"/> / <see cref="CasinoStation.LayoutPoint(float, float)"/> use. <c>u</c> runs along the
/// layout (number 1's cell spans u 0..1, 34-36 spans 11..12, the "2 to 1" boxes 12..13, 0 / 00 sit at u -1..0) and <c>v</c> away
/// from the players (1 / 4 / 7 are v 0..1, 3 / 6 / 9 are v 2..3; the dozens are v -OutsideRow..0 and the even-money boxes
/// -2 OutsideRow..-OutsideRow).
/// <para>This is the hit-zone mapping the QA pass verified for all 161 spots (extracted from the retired uGUI roulette screen,
/// whose cell was 80 x 86 px and outside row 62 px): <see cref="SpotAt"/> turns a point into a bet spot (straight, split,
/// corner, street, six line, top line, dozen, column, even-money) and <see cref="BetCentre"/> is its inverse, where a spot's
/// chips sit. Pure: no scene access.</para>
/// </summary>
public static class CasinoRouletteLayout
{
    /// <summary>Within this many cells of a line the point is the line's bet (split / street / corner / six line), not the number.</summary>
    public const float Edge = 0.2f;
    /// <summary>Height of one outside row (dozens, then the even-money boxes) in cells: 62 / 86.</summary>
    public const float OutsideRow = 62f / 86f;

    /// <summary>The even-money boxes left to right.</summary>
    public static readonly RouletteBetType[] EvenRow =
        { RouletteBetType.Low, RouletteBetType.Even, RouletteBetType.Red, RouletteBetType.Black, RouletteBetType.Odd, RouletteBetType.High };

    /// <summary>The spot at layout point (u, v), or null off the layout. The returned bet has <c>cents = 0</c>.</summary>
    public static RouletteBet? SpotAt(float u, float v)
    {
        // outside boxes
        if (v < -Edge)
        {
            if (u < 0f || u >= 12f) return null;
            if (v >= -OutsideRow) return Make(RouletteBetType.Dozen, Mathf.Clamp((int)(u / 4f), 0, 2));
            if (v >= -2f * OutsideRow) return Make(EvenRow[Mathf.Clamp((int)(u / 2f), 0, 5)], 0);
            return null;
        }
        if (v > 3f || u < -1f || u > 13f) return null;
        // The "2 to 1" boxes start exactly at the grid's right edge: a point inside one is the column, never 34-36.
        if (u >= 12f) { if (v < 0f) return null; return Make(RouletteBetType.Column, Mathf.Clamp((int)v, 0, 2)); }

        // the zeros and the line they share with the first row
        if (u < -Edge)
        {
            if (v < 0f) return null;
            if (Mathf.Abs(v - 1.5f) < Edge) return Found(RouletteBetType.Split, RouletteRules.SplitIndex(0, RouletteRules.DoubleZero));
            return Make(RouletteBetType.Straight, v < 1.5f ? 0 : RouletteRules.DoubleZero);
        }
        if (u < Edge)
        {
            if (v < Edge) return Make(RouletteBetType.TopLine, 0);
            if (Mathf.Abs(v - 1f) < Edge) return Found(RouletteBetType.Street, RouletteRules.StreetIndex(0, 1, 2));
            if (Mathf.Abs(v - 1.5f) < Edge * 0.6f) return Found(RouletteBetType.Street, RouletteRules.StreetIndex(0, RouletteRules.DoubleZero, 2));
            if (Mathf.Abs(v - 2f) < Edge) return Found(RouletteBetType.Street, RouletteRules.StreetIndex(RouletteRules.DoubleZero, 2, 3));
            int a = v < 1.5f ? 0 : RouletteRules.DoubleZero, b = v < 1f ? 1 : v < 2f ? 2 : 3;
            return Found(RouletteBetType.Split, RouletteRules.SplitIndex(a, b));
        }
        if (u > 12f) u = 12f - 0.001f;

        int ku = Mathf.RoundToInt(u), kv = Mathf.RoundToInt(v);
        bool onV = Mathf.Abs(u - ku) < Edge && ku >= 1 && ku <= 11;
        bool onH = Mathf.Abs(v - kv) < Edge && kv >= 1 && kv <= 2;
        bool onBottom = v < Edge;
        int col = Mathf.Clamp((int)u, 0, 11), rowI = Mathf.Clamp((int)Mathf.Max(0f, v), 0, 2);
        if (onBottom)
        {
            if (onV) return Found(RouletteBetType.SixLine, RouletteRules.SixLineIndex(3 * (ku - 1) + 1));
            return Found(RouletteBetType.Street, RouletteRules.StreetIndex(3 * col + 1, 3 * col + 2, 3 * col + 3));
        }
        if (onV && onH) return Found(RouletteBetType.Corner, RouletteRules.CornerIndex(3 * (ku - 1) + kv));
        if (onV) { int n = 3 * (ku - 1) + rowI + 1; return Found(RouletteBetType.Split, RouletteRules.SplitIndex(n, n + 3)); }
        if (onH) { int n = 3 * col + kv; return Found(RouletteBetType.Split, RouletteRules.SplitIndex(n, n + 1)); }
        return Make(RouletteBetType.Straight, 3 * col + rowI + 1);
    }

    /// <summary>Where a spot's chips sit, in layout cell units (the inverse of <see cref="SpotAt"/>). False for an illegal spot.</summary>
    public static bool BetCentre(RouletteBet bet, out float u, out float v)
    {
        u = v = 0f;
        if (!RouletteRules.IsLegalSpot(bet.type, bet.index)) return false;
        int index = bet.index;
        switch (bet.type)
        {
            case RouletteBetType.Straight: PocketCentre(index, out u, out v); return true;
            case RouletteBetType.Street:
                if (index < 12) { u = index + 0.5f; v = 0f; }
                else { u = 0f; v = index == 12 ? 1f : index == 13 ? 1.5f : 2f; }
                return true;
            case RouletteBetType.Corner: u = index / 2 + 1; v = index % 2 + 1; return true;
            case RouletteBetType.SixLine: u = index + 1; v = 0f; return true;
            case RouletteBetType.TopLine: u = 0f; v = 0f; return true;
            case RouletteBetType.Dozen: u = index * 4f + 2f; v = -OutsideRow * 0.5f; return true;
            case RouletteBetType.Column: u = 12.5f; v = index + 0.5f; return true;
            case RouletteBetType.Split:
            {
                var p = RouletteRules.Pockets(bet.type, index);
                if (p.Length != 2) return false;
                bool z0 = p[0] == 0 || p[0] == RouletteRules.DoubleZero, z1 = p[1] == 0 || p[1] == RouletteRules.DoubleZero;
                if (z0 && z1) { u = -0.5f; v = 1.5f; return true; }
                if (z0 || z1)
                {
                    int zero = z0 ? p[0] : p[1], n = z0 ? p[1] : p[0];
                    u = 0f;
                    v = n == 1 ? 0.5f : n == 3 ? 2.5f : zero == 0 ? 1.25f : 1.75f;
                    return true;
                }
                PocketCentre(p[0], out float u0, out float v0);
                PocketCentre(p[1], out float u1, out float v1);
                u = (u0 + u1) * 0.5f; v = (v0 + v1) * 0.5f;
                return true;
            }
            default:
                for (int i = 0; i < EvenRow.Length; i++)
                    if (EvenRow[i] == bet.type) { u = i * 2f + 1f; v = -OutsideRow * 1.5f; return true; }
                return false;
        }
    }

    /// <summary>The centre of a pocket's own cell (0 and 00 in the two head cells).</summary>
    public static void PocketCentre(int pocket, out float u, out float v)
    {
        if (pocket == 0) { u = -0.5f; v = 0.75f; }
        else if (pocket == RouletteRules.DoubleZero) { u = -0.5f; v = 2.25f; }
        else { u = (pocket - 1) / 3 + 0.5f; v = (pocket - 1) % 3 + 0.5f; }
    }

    /// <summary>Does the printed layout hold this point?</summary>
    public static bool OnLayout(float u, float v) => SpotAt(u, v).HasValue;

    static RouletteBet? Make(RouletteBetType type, int index) => new RouletteBet(type, index, 0);

    static RouletteBet? Found(RouletteBetType type, int index) => index >= 0 ? new RouletteBet(type, index, 0) : (RouletteBet?)null;
}
