using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// "Which business are you bringing?" (Docs/Multiplayer.md §13.10 #2, package N1): shown to a guest once they're in a friend's
/// Town. One sticker per saved business ("RUBBISH CO. · $1,240") and "Start a new business". Picking calls
/// <see cref="BusinessSlots.Choose"/> / <see cref="BusinessSlots.ChooseNew"/>; the host's refusal (that business is already
/// open here) brings it back with the reason. Runtime uGUI on the UI kit, built on demand; never in batchmode (bots pick
/// by flag). Esc doesn't close it: everyone in a Town has a business.
/// </summary>
public sealed class BusinessPicker : UiScreen
{
    static BusinessPicker current;

    Canvas canvas;
    RectTransform card;
    bool menuMode;
    System.Action menuClosed;
    int page;
    string message;
    List<BusinessSlots.Slot> slots;

    /// <summary>The picker is up.</summary>
    public static bool IsShowing => current != null && current.IsOpen;

    /// <summary>Show the picker (again), with an optional line on top (why the last pick didn't work).</summary>
    public static void Show(string message)
    {
        if (Application.isBatchMode || !Application.isPlaying || NetBoot.Quitting) return;
        Hide();
        NetBoot.Run(ShowWhenReady(message));
    }

    /// <summary>The same saves for solo/hosting/visiting friends; opening this never loads a save by itself.</summary>
    public static void ShowForPlay(string message, System.Action closed = null)
    {
        if (Application.isBatchMode || !Application.isPlaying || NetBoot.Quitting) return;
        Hide();
        var c = UiKit.Canvas("[BusinessPicker]", UiLayers.Dialog - 5);
        current = c.gameObject.AddComponent<BusinessPicker>();
        current.canvas = c;
        current.menuMode = true;
        current.menuClosed = closed;
        current.Build(message, BusinessSlots.List());
        current.Open(true);
    }

    /// <summary>Remove the picker (the Town ended, or a pick went through).</summary>
    public static void Hide()
    {
        if (current == null) return;
        var c = current;
        current = null;
        c.Close();
        c.menuClosed?.Invoke();
        if (c != null) Destroy(c.gameObject);
    }

    static IEnumerator ShowWhenReady(string message)
    {
        // Wait for our player (so the gameplay lock applies to them), but never more than a few seconds.
        float until = Time.realtimeSinceStartup + 6f;
        while (PlayerController.Local == null && Time.realtimeSinceStartup < until) yield return null; // coop:allow(local) the picker locks the local player's input only
        if (!GameSession.IsConnectedGuest || BusinessSlots.Declared) yield break;
        var c = UiKit.Canvas("[BusinessPicker]", UiLayers.Dialog - 5);
        current = c.gameObject.AddComponent<BusinessPicker>();
        current.canvas = c;
        current.Build(message, BusinessSlots.List());
        current.Open(true);
    }

    void Build(string message, List<BusinessSlots.Slot> slots)
    {
        this.message = message;
        this.slots = slots;
        var root = (RectTransform)transform;
        for (int i = root.childCount - 1; i >= 0; i--)
        { root.GetChild(i).gameObject.SetActive(false); Destroy(root.GetChild(i).gameObject); }
        var dim = FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.55f));
        dim.raycastTarget = true;

        float available = UiScale.ReferenceSize(Screen.width, Screen.height).y - 40f;
        int pageSize = Mathf.Clamp(Mathf.FloorToInt((available - 340f - (string.IsNullOrEmpty(message) ? 0f : 60f)) / 84f), 1, 5);
        int pages = Mathf.Max(1, Mathf.CeilToInt(slots.Count / (float)pageSize));
        page = Mathf.Clamp(page, 0, pages - 1);
        int firstIndex = page * pageSize;
        int rows = Mathf.Min(slots.Count - firstIndex, pageSize) + 1;
        float h = 230f + rows * 84f + (string.IsNullOrEmpty(message) ? 0f : 60f);
        card = UiKit.Paper(root, new Vector2(860f, h));
        card.localRotation = Quaternion.Euler(0f, 0f, 0.6f);

        var title = UiKit.InkLabel(card, "Title", menuMode ? "Choose your business" : "Which business are you bringing?", 44f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        title.enableAutoSizing = true;
        title.fontSizeMax = 44f;
        title.fontSizeMin = 26f;
        UiKit.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -34f), new Vector2(780f, 60f));
        var sub = UiKit.InkLabel(card, "Sub", "Every business works solo, hosting, or with friends. Progress stays yours.", 24f,
                                 UiTheme.InkSoft, TextAlignmentOptions.Center, false, FontStyles.Normal);
        UiKit.Place(sub.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -100f), new Vector2(780f, 40f));

        float y = -150f;
        if (!string.IsNullOrEmpty(message))
        {
            var why = UiKit.InkLabel(card, "Why", message, 26f, UiTheme.StampRed, TextAlignmentOptions.Center, false);
            why.textWrappingMode = TextWrappingModes.Normal;
            UiKit.Place(why.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(780f, 56f));
            y -= 60f;
        }

        var buttons = new List<Selectable>();
        for (int i = firstIndex; i < slots.Count && i < firstIndex + pageSize; i++)
        {
            var s = slots[i];
            bool selected = menuMode && (s.isMain ? BusinessSlots.SelectedId == null : s.id == BusinessSlots.SelectedId);
            var b = UiKit.Button(card, s.Label + (selected ? "  ·  SELECTED" : ""), () => Pick(s), selected || i == 0 ? UiButtonStyle.Primary : UiButtonStyle.Sticker);
            b.Size(700f, 72f);
            UiKit.Place(b.Rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(700f, 72f));
            buttons.Add(b);
            y -= 84f;
        }
        var fresh = UiKit.Button(card, "Start a new business", PickNew, UiButtonStyle.Go);
        fresh.Size(700f, 72f);
        UiKit.Place(fresh.Rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(700f, 72f));
        buttons.Add(fresh);
        y -= 84f;
        if (pages > 1)
        {
            var prev = UiKit.Button(card, "◀ Previous", () => { page = (page + pages - 1) % pages; Build(this.message, this.slots); }, UiButtonStyle.Sticker);
            var next = UiKit.Button(card, "Next ▶", () => { page = (page + 1) % pages; Build(this.message, this.slots); }, UiButtonStyle.Sticker);
            UiKit.Place(prev.Rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-240f, y), new Vector2(220f, 54f));
            UiKit.Place(next.Rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(240f, y), new Vector2(220f, 54f));
            buttons.Add(prev); buttons.Add(next);
        }
        if (menuMode)
        {
            var back = UiKit.Button(card, "Back", Hide, UiButtonStyle.Paper);
            UiKit.Place(back.Rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(220f, 54f));
            buttons.Add(back);
        }
        UiKit.ChainVertical(buttons);
        UiPop.On(card.gameObject).Play(true);
    }

    public override void OnShow()
    {
        UiTheme.Play(UiTheme.SfxOpen, 0.5f, 0.9f);
        var first = card != null ? card.GetComponentInChildren<UiButton>() : null;
        if (first != null) UiKit.Focus(first.gameObject);
    }

    // Esc closes the menu selector, but a connected guest must bring a business.
    public override bool OnBack() { if (menuMode) Hide(); return true; }

    void Pick(BusinessSlots.Slot s)
    {
        UiTheme.Play(UiTheme.SfxConfirm, 0.45f, 1f);
        if (menuMode)
        {
            if (BusinessSlots.SelectForPlay(s, out string error)) Hide();
            else Build(error, BusinessSlots.List());
            return;
        }
        Hide();
        BusinessSlots.Choose(s);
    }

    void PickNew()
    {
        UiTheme.Play(UiTheme.SfxConfirm, 0.45f, 1f);
        if (menuMode)
        {
            if (BusinessSlots.CreateForPlay(null, out string error)) Hide();
            else Build(error, BusinessSlots.List());
            return;
        }
        Hide();
        BusinessSlots.ChooseNew(null);
    }

    protected override void OnDestroy()
    {
        if (current == this) current = null;
        base.OnDestroy();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => current = null;
}
