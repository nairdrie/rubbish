using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The roulette result board (Docs/Casino.md §9.1): the last 12 numbers as coloured discs with numbers, on a lit panel.
/// <see cref="SetHistory"/> takes the newest number FIRST (slot 0 is the big, newest one).
/// </summary>
public class CasinoResultBoard : MonoBehaviour
{
    public Renderer[] discs = new Renderer[CasinoLayout.ResultHistory];
    public TMP_Text[] numbers = new TMP_Text[CasinoLayout.ResultHistory];
    public TMP_Text title;
    public Color red = new Color(0.78f, 0.08f, 0.07f), black = new Color(0.07f, 0.07f, 0.08f), green = new Color(0.06f, 0.50f, 0.22f), empty = new Color(0.10f, 0.09f, 0.08f);
    public Color redGlow = new Color(1.0f, 0.10f, 0.08f), blackGlow = new Color(0.12f, 0.12f, 0.14f), greenGlow = new Color(0.08f, 0.9f, 0.3f);

    MaterialPropertyBlock mpb;
    static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor"), BaseColor = Shader.PropertyToID("_BaseColor");

    /// <summary>Show up to 12 results, newest first: pockets 0..36, 37 = 00; a negative number (or a missing entry) leaves the
    /// slot empty.</summary>
    public void SetHistory(IList<int> newestFirst)
    {
        if (mpb == null) mpb = new MaterialPropertyBlock();
        for (int i = 0; i < CasinoLayout.ResultHistory; i++)
        {
            int n = newestFirst != null && i < newestFirst.Count ? newestFirst[i] : -1;
            bool has = n >= 0 && n <= 37;
            Color fill = !has ? empty : (n == 0 || n == 37) ? green : CasinoStation.IsRed(n) ? red : black;
            Color glow = !has ? Color.black : (n == 0 || n == 37) ? greenGlow : CasinoStation.IsRed(n) ? redGlow : blackGlow;
            if (discs != null && i < discs.Length && discs[i] != null)
            {
                discs[i].GetPropertyBlock(mpb);
                mpb.SetColor(BaseColor, fill);
                mpb.SetColor(EmissionColor, glow);
                discs[i].SetPropertyBlock(mpb);
            }
            if (numbers != null && i < numbers.Length && numbers[i] != null)
                numbers[i].text = !has ? "" : n == 37 ? "00" : n.ToString();
        }
    }

    public void Clear() => SetHistory(null);
}
