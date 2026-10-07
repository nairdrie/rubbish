using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

/// <summary>
/// "Hire Bob" card (Docs/Multiplayer.md §13.6, N4): the hourly wage ($1–$200 per in-game hour) and Offer job. Opened face to
/// face (<see cref="HireTarget"/>) or from the laptop's Payroll app. The other player gets an Accept/Decline card
/// (<see cref="PayrollPresenter"/>). Runtime uGUI on the UI kit; never in batchmode.
/// </summary>
public sealed class HireCard : UiScreen
{
    static HireCard current;
    ulong target;
    int wageCents = Payroll.DefaultWageCents;
    static int lastWageCents = Payroll.DefaultWageCents;
    int sharePercent = Payroll.DefaultSharePercent;
    bool earningsShare;
    UiSlider wageSlider, shareSlider;
    UiCycler contract;
    UiButton offerButton, cancelButton;
    TextMeshProUGUI explanation;
    RectTransform card;
    bool destroying;

    public static bool IsShowing => current != null && current.IsOpen;

    public static void Show(ulong target)
    {
        if (Application.isBatchMode || !Application.isPlaying) return;
        Hide();
        string why = Payroll.WhyNotHire(Payroll.LocalClientId, target);
        if (why != null) { GameHUD.Toast(why, ToastStyle.Bad, 2.5f); Sfx.Play("buzz", null, 0.6f); return; }
        var c = UiKit.Canvas("[HireCard]", UiLayers.Dialog - 4);
        current = c.gameObject.AddComponent<HireCard>();
        current.target = target;
        current.wageCents = lastWageCents;
        current.Build();
        current.Open(true);
    }

    public static void Hide()
    {
        if (current == null) return;
        var c = current;
        current = null;
        if (c.IsOpen) c.Close(); // OnHide also handles an external UiStack.Pop / PopAll.
        else if (c != null) Dispose(c.gameObject);
    }

    static void Dispose(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }

    void Build()
    {
        var root = (RectTransform)transform;
        var dim = FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.5f));
        dim.raycastTarget = true;
        card = UiKit.Paper(root, new Vector2(1000f, 660f));
        card.localRotation = Quaternion.Euler(0f, 0f, -0.6f);

        string name = Payroll.NameOf(target);
        int myColor = Payroll.ColorOf(Payroll.LocalClientId);
        var title = UiKit.InkLabel(card, "Title", $"Hire {name}?", 50f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        title.enableAutoSizing = true;
        title.fontSizeMin = 28f;
        title.fontSizeMax = 50f;
        title.overflowMode = TextOverflowModes.Ellipsis;
        UiKit.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(900f, 64f));
        var sub = UiKit.InkLabel(card, "Sub",
            $"<color={BusinessColors.Hex(myColor)}>●</color> They'd work for {Payroll.BusinessLabelOf(Payroll.LocalClientId)}: drive your truck, tip, sort and smash in your shop. " +
            "Their work with your truck or in your shop earns your business money. Choose how you pay them below.",
            25f, UiTheme.InkSoft, TextAlignmentOptions.Center, false, FontStyles.Normal);
        sub.textWrappingMode = TextWrappingModes.Normal;
        sub.enableAutoSizing = true;
        sub.fontSizeMin = 20f;
        sub.fontSizeMax = 25f;
        UiKit.Place(sub.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -102f), new Vector2(880f, 90f));

        contract = UiKit.Cycler(card, "Pay contract", new[] { "Fixed hourly wage", "Earnings share" }, 0,
            n => { earningsShare = n == 1; RefreshContract(); });
        UiKit.Place((RectTransform)contract.Row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -210f), new Vector2(900f, UiKit.RowHeight));
        wageSlider = UiKit.Slider(card, "Hourly wage", 1f, 200f, wageCents / 100f, v => Payroll.WageText(Mathf.RoundToInt(v) * 100),
                                  v => { wageCents = Mathf.Clamp(Mathf.RoundToInt(v) * 100, Payroll.MinWageCents, Payroll.MaxWageCents); RefreshHint(); });
        wageSlider.Step = 1f;
        UiKit.Place((RectTransform)wageSlider.Row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -292f), new Vector2(900f, UiKit.RowHeight));
        shareSlider = UiKit.Slider(card, "Employee's share", Payroll.MinSharePercent, Payroll.MaxSharePercent, sharePercent,
            v => Mathf.RoundToInt(v) + "%", v => { sharePercent = Mathf.RoundToInt(v); RefreshHint(); });
        shareSlider.Step = 1f;
        UiKit.Place((RectTransform)shareSlider.Row.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -292f), new Vector2(900f, UiKit.RowHeight));

        explanation = UiKit.InkLabel(card, "Contract explanation", "", 23f, UiTheme.InkSoft, TextAlignmentOptions.Center, false, FontStyles.Normal);
        explanation.textWrappingMode = TextWrappingModes.Normal;
        explanation.enableAutoSizing = true;
        explanation.fontSizeMin = 18f;
        explanation.fontSizeMax = 23f;
        UiKit.Place(explanation.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -374f), new Vector2(880f, 100f));

        hint = UiKit.InkLabel(card, "Hint", "", 22f, UiTheme.InkSoft, TextAlignmentOptions.Center, false, FontStyles.Italic);
        hint.textWrappingMode = TextWrappingModes.Normal;
        UiKit.Place(hint.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -479f), new Vector2(880f, 58f));

        var offer = UiKit.Button(card, "Offer job", OnOffer, UiButtonStyle.Go);
        offerButton = offer;
        offer.Size(360f, 80f);
        UiKit.Place(offer.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-200f, 40f), new Vector2(360f, 80f));
        var cancel = UiKit.Button(card, "Cancel", () => { UiTheme.Play(UiTheme.SfxBack, 0.4f, 1f); Hide(); }, UiButtonStyle.Paper);
        cancelButton = cancel;
        cancel.Size(300f, 72f);
        UiKit.Place(cancel.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(210f, 44f), new Vector2(300f, 72f));
        RefreshContract();
        UiPop.On(card.gameObject).Play(true);
    }

    TextMeshProUGUI hint;

    void RefreshHint()
    {
        int total = Payroll.CrewSharePercent(Payroll.LocalClientId) + (earningsShare ? sharePercent : 0);
        bool valid = !earningsShare || total <= Payroll.MaxCrewSharePercent;
        if (explanation != null) explanation.text = Payroll.PayExplanation(wageCents, earningsShare ? sharePercent : 0);
        if (hint != null)
        {
            hint.text = earningsShare
                ? $"On $100 earned, they get ${sharePercent}; all share contracts together take {total}%. " +
                  (valid ? "Your business keeps the rest before costs." : "Reduce the share: crew total must be 75% or less.")
                : $"A full in-game day costs {Economy.Money(wageCents * 24 / 100f)}. Your cash: {Economy.Money(GameSession.CashCents / 100f)}. Missed wages stay owed; the job stays active.";
            hint.color = valid ? UiTheme.InkSoft : UiTheme.StampRed;
        }
        if (offerButton != null) offerButton.interactable = valid;
    }

    void RefreshContract()
    {
        if (wageSlider == null || shareSlider == null) return;
        wageSlider.Row.gameObject.SetActive(!earningsShare);
        shareSlider.Row.gameObject.SetActive(earningsShare);
        if (offerButton != null && cancelButton != null)
            UiKit.ChainVertical(new List<Selectable> { contract.Control, earningsShare ? shareSlider.Slider : wageSlider.Slider, offerButton, cancelButton });
        RefreshHint();
    }

    void OnOffer()
    {
        UiTheme.Play(UiTheme.SfxConfirm, 0.45f, 1f);
        lastWageCents = wageCents;
        if (earningsShare) Payroll.OfferShare(target, sharePercent);
        else Payroll.Offer(target, wageCents);
        Hide();
    }

    public override void OnShow() => UiTheme.Play(UiTheme.SfxOpen, 0.5f, 0.9f);

    public override bool OnBack()
    {
        Hide();
        return true;
    }

    public override void OnHide()
    {
        if (current == this) current = null;
        if (!destroying && this != null) Dispose(gameObject);
    }

    protected override void OnDestroy()
    {
        destroying = true;
        if (current == this) current = null;
        base.OnDestroy();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { current = null; lastWageCents = Payroll.DefaultWageCents; }
}

/// <summary>
/// The laptop's Payroll app (N4): your crew (wage, paid so far, Fire) and everyone else in the Town (Hire…, or why not).
/// A uGUI sheet over the laptop, opened from the "Payroll" tab <see cref="PayrollPresenter"/> adds to the laptop's app strip.
/// </summary>
public sealed class PayrollApp : UiScreen
{
    static PayrollApp current;
    RectTransform card, list;
    bool dirty = true;
    float nextRefresh;

    public static bool IsShowing => current != null && current.IsOpen;

    public static void Show()
    {
        if (Application.isBatchMode || !Application.isPlaying) return;
        if (current != null) return;
        var c = UiKit.Canvas("[PayrollApp]", UiLayers.Laptop + 10);
        current = c.gameObject.AddComponent<PayrollApp>();
        current.Build();
        current.Open(false);
        Payroll.Changed += current.MarkDirty;
        Businesses.Changed += current.MarkDirty;
    }

    public static void Hide()
    {
        if (current == null) return;
        var c = current;
        current = null;
        c.Close();
        if (c != null) Destroy(c.gameObject);
    }

    void MarkDirty() => dirty = true;

    void Build()
    {
        var root = (RectTransform)transform;
        var dim = FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.35f));
        dim.raycastTarget = true;
        card = UiKit.Paper(root, new Vector2(1180f, 720f));
        card.localRotation = Quaternion.Euler(0f, 0f, 0.4f);
        var title = UiKit.InkLabel(card, "Title", "PAYROLL", 54f, UiTheme.Ink, TextAlignmentOptions.Left, true);
        UiKit.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(48f, -26f), new Vector2(600f, 66f));
        var sub = UiKit.InkLabel(card, "Sub", "Fixed wages: every 50 s. Earnings shares: paid as your business earns. Joining this Town does not automatically hire anybody.",
                                 23f, UiTheme.InkSoft, TextAlignmentOptions.Left, false, FontStyles.Normal);
        sub.textWrappingMode = TextWrappingModes.Normal;
        UiKit.Place(sub.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(50f, -94f), new Vector2(1080f, 56f));
        var scroll = UiKit.ScrollList(card, new Vector2(1122f, 446f));
        UiKit.Place((RectTransform)scroll.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -160f), new Vector2(1122f, 446f));
        list = scroll.content;
        list.GetComponent<VerticalLayoutGroup>().enabled = false;
        list.GetComponent<ContentSizeFitter>().enabled = false;
        var close = UiKit.Button(card, "Close", () => { UiTheme.Play(UiTheme.SfxBack, 0.4f, 1f); Hide(); }, UiButtonStyle.Primary);
        close.Size(260f, 72f);
        UiKit.Place(close.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-40f, 30f), new Vector2(260f, 72f));
        UiPop.On(card.gameObject).Play(true);
    }

    void Update()
    {
        if (!EbaeUI.IsOpen) { Hide(); return; }
        if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + 1f; dirty = true; } // "paid so far" ticks
        if (!dirty) return;
        dirty = false;
        Rebuild();
    }

    void Rebuild()
    {
        for (int i = list.childCount - 1; i >= 0; i--) Destroy(list.GetChild(i).gameObject);
        ulong me = Payroll.LocalClientId;
        float y = 0f;
        if (Payroll.TryGetLocalJob(out var mine))
        {
            Heading("YOUR JOB", ref y);
            Row($"Employer: <b>{Payroll.BusinessLabelOf(mine.employerClientId)}</b>\n<size=80%>{Payroll.PayText(mine)} · Paid to YOUR cash: {Economy.Money(mine.paidCents / 100f)}" +
                (mine.unpaidCents > 0 ? $" · OWED {Economy.Money(mine.unpaidCents / 100f)}" : "") + "</size>",
                "Quit Job", UiButtonStyle.Danger,
                () => UiKit.Confirm("Quit your job?", "End this job and keep your own business. You will stay in this Town.", "Quit Job", "Keep working", yes => { if (yes) Payroll.Quit(); }), ref y);
        }
        Heading("YOUR CREW", ref y);
        var crew = Payroll.EmployeesOf(me);
        if (crew.Count == 0) Note("Nobody yet. Hire someone below, or walk up to them and press E.", ref y);
        foreach (var j in crew)
        {
            ulong who = j.employeeClientId;
            string line = $"{Dot(who)} <b>{Payroll.NameOf(who)}</b>   {Payroll.PayText(j)}\n<size=80%>Paid {Economy.Money(j.paidCents / 100f)} to their own cash</size>" +
                          (j.unpaidCents > 0 ? $"   <color=#DB1F29>OWED {Economy.Money(j.unpaidCents / 100f)}</color>" : "");
            Row(line, "Fire", UiButtonStyle.Danger, () => AskFire(who), ref y);
        }
        y -= 16f;
        Heading("IN TOWN", ref y);
        int others = 0;
        foreach (var b in Businesses.All)
        {
            if (!b.IsValid || b.ownerClientId == me) continue;
            others++;
            ulong who = b.ownerClientId;
            if (Payroll.TryGetJobOf(who, out var j) && j.employerClientId == me) continue; // listed above
            string why = Payroll.WhyNotHire(me, who);
            string line = $"{Dot(who)} <b>{Payroll.NameOf(who)}</b>   <size=80%>{Payroll.BusinessLabelOf(who)}</size>" +
                          (why != null ? $"\n<size=75%><color=#5A5266>{why}</color></size>" : "");
            Row(line, why == null ? "Hire…" : null, UiButtonStyle.Go, () => HireCard.Show(who), ref y);
        }
        if (others == 0) Note("Nobody else is in your Town. Invite friends from the Esc menu!", ref y);
        list.sizeDelta = new Vector2(0f, Mathf.Max(446f, -y + 12f));
    }

    static string Dot(ulong clientId) => $"<color={BusinessColors.Hex(Payroll.ColorOf(clientId))}>●</color>";

    void Heading(string text, ref float y)
    {
        var t = UiKit.InkLabel(list, "H " + text, text, 26f, UiTheme.Ink, TextAlignmentOptions.BottomLeft, true);
        t.characterSpacing = 6f;
        UiKit.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(10f, y), new Vector2(1080f, 40f));
        y -= 46f;
    }

    void Note(string text, ref float y)
    {
        var t = UiKit.InkLabel(list, "Note", text, 23f, UiTheme.InkSoft, TextAlignmentOptions.Left, false, FontStyles.Italic);
        UiKit.Place(t.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, y), new Vector2(1060f, 40f));
        y -= 50f;
    }

    void Row(string text, string button, UiButtonStyle style, System.Action click, ref float y)
    {
        var bg = FeelUi.Img(list, "Row", FeelUi.Rounded, UiTheme.WithAlpha(UiTheme.PaperDark, 0.8f), 0.4f);
        UiKit.Place(bg.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, y), new Vector2(1100f, 84f));
        var t = UiKit.InkLabel(bg.rectTransform, "Text", text, 27f, UiTheme.Ink, TextAlignmentOptions.Left, false);
        t.richText = true;
        UiKit.Inset(t.rectTransform, 24f, 4f, 300f, 4f);
        if (!string.IsNullOrEmpty(button))
        {
            var b = UiKit.Button(bg.rectTransform, button, click, style);
            b.Size(240f, 64f);
            UiKit.Place(b.Rect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-14f, 0f), new Vector2(240f, 64f));
        }
        y -= 94f;
    }

    static void AskFire(ulong who)
    {
        string name = Payroll.NameOf(who);
        UiKit.Confirm($"Fire {name}?", "Ends their job, not their visit to this Town. Fixed wages settle the part hour; earnings shares already paid stay theirs.", "Let them go", "Keep them",
                      yes => { if (yes) Payroll.Fire(who); });
    }

    public override void OnShow() => UiTheme.Play(UiTheme.SfxOpen, 0.5f, 1f);

    protected override void OnDestroy()
    {
        Payroll.Changed -= MarkDirty;
        Businesses.Changed -= MarkDirty;
        if (current == this) current = null;
        base.OnDestroy();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => current = null;
}

/// <summary>
/// E on another player (N4): "Hire Bob" (opens <see cref="HireCard"/>) or, if Bob works for you, "Fire Bob". Lives on a child of
/// every other player's object (added by <see cref="PayrollPresenter"/>) with an <see cref="InteractionZone"/>, so it shows up
/// when you stand next to them.
/// </summary>
public sealed class HireTarget : MonoBehaviour, IInteractable
{
    public ulong clientId;

    bool MineToFire => Payroll.TryGetJobOf(clientId, out var j) && j.employerClientId == Payroll.LocalClientId;

    public bool CanInteract() =>
        Payroll.Available && clientId != Payroll.LocalClientId && !SortingKit.LocalPlayerDriving &&
        (MineToFire || Payroll.WhyNotHire(Payroll.LocalClientId, clientId) == null);

    public string GetInteractionPrompt() => MineToFire ? $"Fire {Payroll.NameOf(clientId)}" : $"Hire {Payroll.NameOf(clientId)}";

    public void OnInteract(GameObject interactor)
    {
        if (MineToFire)
        {
            string name = Payroll.NameOf(clientId);
            ulong who = clientId;
            UiKit.Confirm($"Fire {name}?", "Ends their job, not their visit to this Town. Fixed wages settle the part hour; earnings shares already paid stay theirs.", "Let them go", "Keep them",
                          yes => { if (yes) Payroll.Fire(who); });
            return;
        }
        HireCard.Show(clientId);
    }

    public void Highlight(bool highlight) { }
}

/// <summary>
/// Payroll's presentation on every machine with a screen (N4): hire zones on the other players, the employer's colour on
/// employees (<see cref="ChuckPuppet.SetVestColor"/>), the HUD line ("Working for Nick's RUBBISH CO., $12/hr"), the job-offer
/// card, and the Payroll tab on the laptop's app strip. Built at boot; does nothing outside a Town with the one world on.
/// </summary>
public sealed class PayrollPresenter : MonoBehaviour
{
    static PayrollPresenter instance;

    CanvasGroup group, announcementGroup;
    RectTransform hudTag;
    RectTransform announcementCard;
    TextMeshProUGUI label, termsLabel, announcementTitle, announcementDetail;
    UnityEngine.UI.Image announcementBody;
    float announcementUntil, announcementPunch;
    float nextScan, nextTab;
    readonly List<PlayerController> players = new List<PlayerController>(8);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode || instance != null) return;
        var c = UiKit.Canvas("[PayrollHud]", FeelHud.SortingOrder + 1);
        instance = c.gameObject.AddComponent<PayrollPresenter>();
        instance.BuildHud();
        Payroll.ContractOfferReceived += instance.OnOffer;
        Payroll.Announcement += instance.OnAnnouncement;
    }

    void OnDestroy()
    {
        Payroll.ContractOfferReceived -= OnOffer;
        Payroll.Announcement -= OnAnnouncement;
        if (instance == this) instance = null;
    }

    // ------------------------------------------------------------------ offer card

    void OnOffer(ulong employer, int wageCents, byte sharePercent, string label)
    {
        Sfx.Play("notify", null, 0.8f);
        string name = Payroll.NameOf(employer);
        if (name.Length > 28) name = name.Substring(0, 27) + "…";
        string terms = sharePercent > 0
            ? $"{sharePercent}% of collection, recycling, sale and bonus income. Paid as earned; no hourly wage."
            : $"{Payroll.WageText(wageCents)}, every 50 seconds from their cash.";
        UiKit.Confirm("Job offer!",
            $"{name} offers: {terms}\nTheir shop/truck earns their business money; your pay goes to YOUR cash. Quit Job in Esc stays in Town.\n(Declines in {{0}})",
            "Accept", "Decline", ok => Payroll.Answer(ok), 30f);
    }

    // ------------------------------------------------------------------ HUD line

    void BuildHud()
    {
        var root = (RectTransform)transform;
        hudTag = FeelUi.Rect("PayrollTag", root, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(420f, 29f));
        group = hudTag.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;
        group.interactable = false;
        hudTag.localRotation = Quaternion.Euler(0f, 0f, 1f);
        FeelUi.Img(hudTag, "Body", FeelUi.Rounded, new Color(0.11f, 0.09f, 0.17f, 0.9f), 0.5f);
        label = FeelUi.Text(hudTag, "Text", 18f, Color.white, TextAlignmentOptions.Left);
        label.enableAutoSizing = true;
        label.fontSizeMin = 14f;
        label.fontSizeMax = 18f;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Truncate;
        label.richText = true;
        termsLabel = FeelUi.Text(hudTag, "WageAndArrears", 17f, Color.white, TextAlignmentOptions.Right);
        termsLabel.enableAutoSizing = true;
        termsLabel.fontSizeMin = 13f;
        termsLabel.fontSizeMax = 17f;
        termsLabel.textWrappingMode = TextWrappingModes.NoWrap;
        termsLabel.overflowMode = TextOverflowModes.Truncate;

        announcementCard = FeelUi.Rect("JobAnnouncement", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                                       new Vector2(0f, 90f), new Vector2(760f, 174f));
        announcementGroup = announcementCard.gameObject.AddComponent<CanvasGroup>();
        announcementGroup.alpha = 0f;
        announcementGroup.interactable = false;
        announcementGroup.blocksRaycasts = false;
        announcementBody = FeelUi.Panel(announcementCard, new Color(0.12f, 0.29f, 0.18f, 0.97f), 0.7f);
        announcementTitle = FeelUi.Text(announcementCard, "Title", 69f, FeelUi.Gold, TextAlignmentOptions.Center);
        UiKit.Place(announcementTitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(720f, 87f));
        announcementDetail = FeelUi.Text(announcementCard, "Detail", 28f, Color.white, TextAlignmentOptions.Center);
        UiKit.Place(announcementDetail.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 17f), new Vector2(710f, 61f));
        announcementDetail.textWrappingMode = TextWrappingModes.Normal;
    }

    void OnAnnouncement(string title, string detail, bool good)
    {
        announcementTitle.text = title;
        announcementDetail.text = detail;
        announcementBody.color = good ? new Color(0.12f, 0.34f, 0.20f, 0.97f) : new Color(0.40f, 0.12f, 0.14f, 0.97f);
        announcementTitle.color = good ? FeelUi.Gold : new Color(1f, 0.82f, 0.45f);
        announcementUntil = Time.unscaledTime + 3.6f;
        announcementPunch = 1f;
        Sfx.Play(good ? "legendary" : "buzz", null, 0.75f);
    }

    string HudText(bool compact, out string terms, out bool arrears)
    {
        terms = null;
        arrears = false;
        if (!Payroll.Available) return null;
        ulong me = Payroll.LocalClientId;
        if (Payroll.TryGetJobOf(me, out var j))
        {
            arrears = j.unpaidCents > 0;
            terms = (j.IsEarningsShare ? $"{j.sharePercent}% share" : Payroll.WageText(j.wageCents)) +
                (arrears ? $" · OWED {Economy.Money(j.unpaidCents / 100f)}" : " · Esc: job");
            string employer = compact ? Payroll.NameOf(j.employerClientId) : Payroll.BusinessLabelOf(j.employerClientId);
            return $"<color={BusinessColors.Hex(Payroll.ColorOf(j.employerClientId))}>●</color> WORKING FOR {employer}";
        }
        var crew = Payroll.EmployeesOf(me);
        if (crew.Count == 0) return null;
        var sb = new System.Text.StringBuilder("YOUR CREW: ");
        for (int i = 0; i < crew.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Payroll.NameOf(crew[i].employeeClientId)).Append(' ').Append(Payroll.PayText(crew[i]));
        }
        return sb.ToString();
    }

    void LateUpdate()
    {
        var frame = HudLayout.Layout;
        var mode = HudLayout.Mode;
        bool cashShown = mode == HudMode.Driving || mode == HudMode.Street || mode == HudMode.Depot || mode == HudMode.Laptop || mode == HudMode.Paused;
        float x = frame.clock.xMin + 12f;
        float maxW = Mathf.Max(1f, frame.truck.xMin - x - 10f);
        string terms = null;
        bool arrears = false;
        string text = frame.valid && cashShown ? HudText(maxW < 450f, out terms, out arrears) : null;
        bool show = text != null;
        group.alpha = Mathf.MoveTowards(group.alpha, show ? 1f : 0f, Time.unscaledDeltaTime * 6f);
        if (show)
        {
            if (label.text != text) label.text = text;
            if (termsLabel.text != terms) termsLabel.text = terms ?? "";
            termsLabel.color = arrears ? new Color(1f, 0.42f, 0.42f) : Color.white;
            // The business badge is bounded to the cash panel. Keep this separate work/wage badge under
            // the clock, ending before the truck panel; the layout reserves a separate row below the top bar.
            float w = Mathf.Min(maxW, Mathf.Max(200f, label.preferredWidth + termsLabel.preferredWidth + 38f));
            HudLayout.PlaceAt(hudTag, new Vector2(x, HudLayout.TopBarBottom + 4f));
            hudTag.sizeDelta = new Vector2(w, 29f);
            float termsW = terms == null ? 0f : Mathf.Min(Mathf.Max(95f, w * 0.58f), termsLabel.preferredWidth + 8f);
            var left = label.rectTransform;
            left.anchorMin = new Vector2(0f, 0f);
            left.anchorMax = new Vector2(0f, 1f);
            left.offsetMin = new Vector2(12f, 0f);
            left.offsetMax = new Vector2(Mathf.Max(12f, w - termsW - 8f), 0f);
            var right = termsLabel.rectTransform;
            right.anchorMin = new Vector2(1f, 0f);
            right.anchorMax = new Vector2(1f, 1f);
            right.offsetMin = new Vector2(-termsW - 8f, 0f);
            right.offsetMax = new Vector2(-8f, 0f);
        }
        float remaining = announcementUntil - Time.unscaledTime;
        float want = remaining > 0f ? Mathf.Clamp01(remaining / 0.45f) : 0f;
        announcementGroup.alpha = Mathf.MoveTowards(announcementGroup.alpha, want, Time.unscaledDeltaTime * 8f);
        announcementPunch = Mathf.MoveTowards(announcementPunch, 0f, Time.unscaledDeltaTime * 4f);
        announcementCard.localScale = Vector3.one * (1f + 0.11f * announcementPunch);
    }

    // ------------------------------------------------------------------ zones, vests, laptop tab

    void Update()
    {
        bool on = Payroll.Available;
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 0.5f;
            if (on) ScanPlayers();
        }
        if (Time.unscaledTime >= nextTab)
        {
            nextTab = Time.unscaledTime + 0.25f;
            if (on && EbaeUI.IsOpen) EnsureLaptopTab();
            else if (PayrollApp.IsShowing && !EbaeUI.IsOpen) PayrollApp.Hide();
        }
    }

    void ScanPlayers()
    {
        var net = DepotRowNet.Instance;
        players.Clear();
        players.AddRange(FindObjectsByType<PlayerController>(FindObjectsSortMode.None));
        foreach (var p in players)
        {
            if (p == null || !p.IsSpawned) continue;
            ulong id = p.OwnerClientId;
            if (!p.IsOwner) EnsureZone(p, id);
            var puppet = p.GetComponent<ChuckPuppet>();
            if (puppet == null) continue;
            Color? vest = null;
            if (Payroll.TryGetJobOf(id, out var j) && net != null)
            {
                int lot = net.LotOwnedBy(j.employerClientId);
                if (lot >= 0 && net.TryGetLot(lot, out var s)) vest = DepotColors.Get(s.color);
            }
            puppet.SetVestColor(vest);
        }
    }

    static void EnsureZone(PlayerController p, ulong id)
    {
        var t = p.transform.Find("PayrollHireZone");
        if (t == null)
        {
            var go = new GameObject("PayrollHireZone");
            go.transform.SetParent(p.transform, false);
            var ht = go.AddComponent<HireTarget>();
            var z = go.AddComponent<InteractionZone>();
            z.center = new Vector3(0f, 1f, 0f);
            z.size = new Vector3(2.8f, 2.6f, 2.8f);
            z.mode = ZoneMode.OnFoot;
            z.score = 0.6f;
            z.targetOverride = ht;
            t = go.transform;
        }
        var h = t.GetComponent<HireTarget>();
        if (h != null) h.clientId = id;
    }

    void EnsureLaptopTab()
    {
        var ui = EbaeUI.Instance;
        if (ui == null) return;
        // The payroll is the owner's: the laptop only opens for them (Permissions.CanUseLaptop).
        foreach (var doc in ui.GetComponentsInChildren<UIDocument>(true))
        {
            var root = doc != null ? doc.rootVisualElement : null;
            var shop = root?.Q<VisualElement>("appTabShop");
            if (shop == null || shop.parent == null) continue;
            if (shop.parent.Q<VisualElement>("appTabPayroll") != null) return;
            var tab = new VisualElement { name = "appTabPayroll" };
            tab.AddToClassList("app-tab");
            var icon = new UnityEngine.UIElements.Label("$") { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("app-tab-icon");
            icon.style.backgroundColor = new Color(0.18f, 0.66f, 0.4f);
            var text = new UnityEngine.UIElements.Label("Payroll - hire your friends") { pickingMode = PickingMode.Ignore };
            text.AddToClassList("app-tab-label");
            tab.Add(icon);
            tab.Add(text);
            tab.RegisterCallback<ClickEvent>(_ => { Sfx.Play("click", null, 0.6f); PayrollApp.Show(); });
            int at = shop.parent.IndexOf(shop) + 1;
            shop.parent.Insert(at, tab);
            return;
        }
    }
}
