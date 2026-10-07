using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>A deadpan memo from Rubbish Co. Head Office.</summary>
public sealed class HeadOfficeLetter
{
    public string subject;
    public string body;
    public int day;
    public float hour;
    /// <summary>False = inbox only (e.g. promotions, whose line is already on the promotion stamp card).</summary>
    public bool popup = true;
}

/// <summary>
/// Head Office letters (Docs/Progression.md §4.4, §4.6, §2.2): promotions, dirty-district complaints, the Kevin nudge,
/// the Key to the City. The HUD slides each one in as a memo card; the Career page keeps the latest few as an inbox.
/// Presentation only (no money, no Rep), so it never needs to be networked: every machine can show its own.
/// </summary>
public static class HeadOffice
{
    public const int InboxSize = 8;

    static readonly List<HeadOfficeLetter> inbox = new List<HeadOfficeLetter>();

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<HeadOfficeLetter> Inbox => inbox;

    /// <summary>A letter arrived (the HUD shows it).</summary>
    public static event Action<HeadOfficeLetter> LetterSent;

    public static void Send(string subject, string body, bool popup = true)
    {
        if (string.IsNullOrEmpty(body)) return;
        var l = new HeadOfficeLetter { subject = subject ?? "Memo", body = body, day = Progression.Today, hour = GameClock.Hours, popup = popup };
        inbox.Insert(0, l);
        if (inbox.Count > InboxSize) inbox.RemoveRange(InboxSize, inbox.Count - InboxSize);
        Debug.Log($"[HeadOffice] {l.subject}: {l.body}");
        var h = LetterSent;
        if (h == null) return;
        try { h(l); } catch (Exception e) { Debug.LogException(e); }
    }

    public static void ResetStatics()
    {
        inbox.Clear();
        LetterSent = null;
    }
}
