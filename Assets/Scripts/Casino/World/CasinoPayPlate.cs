using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// The pay table plate on a slot machine's front glass (Docs/Casino.md §9.1): a lit panel about 0.36 x 0.45 m with a title,
/// 14 lines of {combination, payout}, the "RETURNS xx.x%" line and the stake. The controller fills it from the real SlotRules.
/// </summary>
public class CasinoPayPlate : MonoBehaviour
{
    public const int Lines = 14;
    public TMP_Text title;
    public TMP_Text[] left = new TMP_Text[Lines], right = new TMP_Text[Lines];
    public TMP_Text returnsLine, stakeLine;

    public void SetTitle(string text) { if (title != null) title.text = text ?? ""; }

    /// <summary>Line <paramref name="i"/> (0..13): the combination on the left, the payout on the right.</summary>
    public void SetLine(int i, string combo, string payout)
    {
        if (left != null && i >= 0 && i < left.Length && left[i] != null) left[i].text = combo ?? "";
        if (right != null && i >= 0 && i < right.Length && right[i] != null) right[i].text = payout ?? "";
    }

    public void SetReturns(string text) { if (returnsLine != null) returnsLine.text = text ?? ""; }
    public void SetStake(string text) { if (stakeLine != null) stakeLine.text = text ?? ""; }

    public void Clear()
    {
        SetTitle("");
        for (int i = 0; i < Lines; i++) SetLine(i, "", "");
        SetReturns(""); SetStake("");
    }
}
