using System;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using PT = UiText.Pause;

/// <summary>One crew member as the Esc menu shows them.</summary>
public struct CrewRow
{
    public ulong clientId;
    public int slot;
    public string displayName;
    public bool isHost, isMe;
    /// <summary>Round trip in ms, or -1 when this machine can't know it (a guest looking at another guest).</summary>
    public int pingMs;

    public string VariantName
    {
        get
        {
            var v = ChuckPartsData.Variants;
            return v != null && v.Length > 0 ? v[Mathf.Clamp(slot, 0, v.Length - 1)].name : "Chuck";
        }
    }

    public Color Color
    {
        get
        {
            var v = ChuckPartsData.Variants;
            return v != null && v.Length > 0 ? v[Mathf.Clamp(slot, 0, v.Length - 1)].playerColor : UiTheme.Tangerine;
        }
    }
}

/// <summary>
/// The Esc menu's CREW page (Docs/UIPass.md §5.6): one row per roster entry (<see cref="NetRoster"/>): a swatch in the
/// Dumpling's colour with its initial, the variant name (Chuck, Dot, Ozzie, Bert), the player's name, BOSS or CREW, and the
/// round trip (the host sees everyone's; a guest sees their own). The host gets [Kick] ("Kick Dot? They can rejoin." →
/// <see cref="SessionService.Kick"/>). Mute (M3) and Block (M6) stay hidden until they exist. Reads replicated state only.
/// </summary>
public sealed class CrewPanel : MonoBehaviour
{
    RectTransform page;
    readonly List<(ulong id, TextMeshProUGUI ping)> pings = new List<(ulong, TextMeshProUGUI)>();
    readonly List<Selectable> selectables = new List<Selectable>();
    string signature;

    /// <summary>Buttons on the page (keyboard navigation).</summary>
    public IReadOnlyList<Selectable> Selectables => selectables;

    /// <summary>The live roster (empty when there's no roster object yet).</summary>
    public static List<CrewRow> Live()
    {
        var list = new List<CrewRow>();
        var roster = NetRoster.Instance;
        var nm = NetBoot.Manager;
        if (roster == null || nm == null || !nm.IsListening) return list;
        ulong me = nm.LocalClientId;
        bool server = nm.IsServer;
        var tr = nm.NetworkConfig != null ? nm.NetworkConfig.NetworkTransport : null;
        foreach (var e in roster.Entries)
        {
            var r = new CrewRow
            {
                clientId = e.clientId, slot = e.slot, displayName = e.displayName.ToString(), isHost = e.IsHost,
                isMe = e.clientId == me, pingMs = -1,
            };
            try
            {
                if (tr != null)
                {
                    if (server && !r.isMe) r.pingMs = (int)tr.GetCurrentRtt(e.clientId);
                    else if (!server && r.isMe) r.pingMs = (int)tr.GetCurrentRtt(NetworkManager.ServerClientId);
                }
            }
            catch (Exception) { r.pingMs = -1; }
            list.Add(r);
        }
        list.Sort((a, b) => a.isHost != b.isHost ? (a.isHost ? -1 : 1) : a.slot.CompareTo(b.slot));
        return list;
    }

    /// <summary>A full crew for previews.</summary>
    public static List<CrewRow> Sample(bool meIsHost)
    {
        return new List<CrewRow>
        {
            new CrewRow { clientId = 0, slot = 0, displayName = "Nick", isHost = true, isMe = meIsHost, pingMs = -1 },
            new CrewRow { clientId = 1, slot = 1, displayName = "dotty_bins", isMe = !meIsHost, pingMs = 38 },
            new CrewRow { clientId = 2, slot = 2, displayName = "OzzieTheGreat", pingMs = meIsHost ? 71 : -1 },
            new CrewRow { clientId = 3, slot = 3, displayName = "bert", pingMs = meIsHost ? 112 : -1 },
        };
    }

    /// <summary>Build the page for <paramref name="rows"/>. <paramref name="kick"/> is the host's Kick (null hides it).</summary>
    public static CrewPanel Build(RectTransform page, List<CrewRow> rows, Action<CrewRow> kick)
    {
        var p = page.gameObject.AddComponent<CrewPanel>();
        p.page = page;
        p.Rebuild(rows, kick);
        return p;
    }

    /// <summary>Rebuild when the roster changed (cheap check by signature); otherwise only the pings update.</summary>
    public void Refresh(List<CrewRow> rows, Action<CrewRow> kick)
    {
        if (Signature(rows) != signature) { Rebuild(rows, kick); return; }
        foreach (var r in rows)
            foreach (var (id, ping) in pings)
                if (id == r.clientId && ping != null) ping.text = PingText(r);
    }

    static string Signature(List<CrewRow> rows)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in rows) sb.Append(r.clientId).Append(':').Append(r.slot).Append(':').Append(r.displayName).Append(r.isHost ? "H" : "").Append(';');
        return sb.ToString();
    }

    static string PingText(CrewRow r) => r.isHost && !r.isMe ? "host" : r.pingMs >= 0 ? string.Format(PT.PingMs, r.pingMs) : r.isMe && r.isHost ? "you" : "–";

    void Rebuild(List<CrewRow> rows, Action<CrewRow> kick)
    {
        for (int i = page.childCount - 1; i >= 0; i--) PauseUi.DestroyNow(page.GetChild(i).gameObject);
        pings.Clear();
        selectables.Clear();
        signature = Signature(rows);
        float w = Mathf.Max(400f, page.rect.width);
        const float H = 78f, Gap = 8f;
        float y = 0f;
        foreach (var r in rows)
        {
            var row = PauseUi.TopLeft(FeelUi.Rect("Crew " + r.slot, page, Vector2.up, Vector2.up, Vector2.zero, Vector2.zero), 0f, y, w, H);
            var body = FeelUi.Img(row, "Body", FeelUi.Rounded, r.isMe ? UiTheme.Hex(0xFFF0C4) : UiTheme.Hex(0xFFFDF6), 0.3f);
            FeelUi.Img(row, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, r.isMe ? 0.5f : 0.22f), 0.3f);
            body.raycastTarget = false;

            // the Dumpling swatch: a disc in their colour with the variant's initial
            var sw = FeelUi.Rect("Swatch", row, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(44f, 0f), new Vector2(54f, 54f));
            var ssh = FeelUi.Img(sw, "Shadow", FeelUi.Circle, UiTheme.WithAlpha(Color.black, 0.25f));
            ssh.rectTransform.offsetMin = ssh.rectTransform.offsetMax = new Vector2(2f, -3f);
            FeelUi.Img(sw, "Disc", FeelUi.Circle, r.Color);
            FeelUi.Img(sw, "Rim", UiKit.Sprites.Ring, UiTheme.Ink);
            UiKit.OutlineLabel(sw, "Initial", r.VariantName.Substring(0, 1), 28f, Color.white, TextAlignmentOptions.Center, true);

            float right = 20f;
            if (kick != null && !r.isHost && !r.isMe)
            {
                var kb = UiKit.Button(row, PT.Kick, () => kick(r), UiButtonStyle.Danger);
                kb.Size(124f, 52f);
                UiKit.Place(kb.Rect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-14f, 0f), new Vector2(124f, 52f));
                kb.Text.fontSizeMax = 24f;
                selectables.Add(kb);
                right = 150f;
            }
            var ping = PauseUi.Text(row, "Ping", PingText(r), 20f, TextAlignmentOptions.MidlineRight, false, UiTheme.InkSoft, FontStyles.Normal);
            UiKit.Place(ping.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-right, 0f), new Vector2(84f, H));
            pings.Add((r.clientId, ping));
            right += 92f;

            // the role pill
            var pill = FeelUi.Rect("Role", row, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-right, 0f), new Vector2(96f, 34f));
            FeelUi.Img(pill, "Body", FeelUi.Rounded, r.isHost ? UiTheme.Gold : UiTheme.PaperDark, 0.5f);
            FeelUi.Img(pill, "Edge", UiKit.Sprites.RoundedOutline, UiTheme.WithAlpha(UiTheme.Ink, 0.5f), 0.5f);
            var role = UiKit.InkLabel(pill, "Text", r.isHost ? PT.Boss : PT.Crew, 18f, UiTheme.Ink, TextAlignmentOptions.Center, true);
            role.characterSpacing = 3f;
            right += 108f;

            var name = PauseUi.Text(row, "Variant", r.VariantName + (r.isMe ? " (you)" : ""), 26f, TextAlignmentOptions.BottomLeft, true, UiTheme.Ink);
            UiKit.Inset(name.rectTransform, 86f, H * 0.5f - 2f, right, 6f);
            var who = PauseUi.Text(row, "Name", "", 20f, TextAlignmentOptions.TopLeft, false, UiTheme.InkSoft, FontStyles.Normal);
            UiKit.Inset(who.rectTransform, 86f, 6f, right, H * 0.5f + 2f);
            who.text = FeelUi.Fit(who, string.IsNullOrEmpty(r.displayName) ? "?" : r.displayName, w - 86f - right);
            name.text = FeelUi.Fit(name, name.text, w - 86f - right);
            y += H + Gap;
        }
        if (rows.Count <= 1)
        {
            var t = PauseUi.Text(page, "Nobody", PT.NobodyYet, 22f, TextAlignmentOptions.TopLeft, false, UiTheme.InkSoft, FontStyles.Italic);
            t.textWrappingMode = TextWrappingModes.Normal;
            PauseUi.TopLeft(t.rectTransform, 8f, y + 10f, w - 16f, 64f);
        }
    }
}
