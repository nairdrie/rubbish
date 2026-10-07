using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// "Rename business" on the shop laptop (Docs/Multiplayer.md §13.2): the laptop's secondary option (F) in a Depot Row
/// shop. Owner only; the host validates (≤ 24 characters, word filter) and everyone's sign updates.
/// Placed next to each pocket interior's laptop by <c>Setup_DepotRow</c> with an on-foot <see cref="InteractionZone"/>.
/// </summary>
public class BusinessRenameDesk : MonoBehaviour, IInteractable
{
    public int lot;

    public bool CanInteract() => DepotRowWorld.Active && !EbaeUI.IsOpen && !BusinessRenameDialog.IsShowing;

    public string GetInteractionPrompt() =>
        Permissions.CanRename(Permissions.LocalClientId, lot) ? "Laptop: rename your business" : "Laptop: rename business (owner only)";

    public void OnInteract(GameObject interactor)
    {
        if (!Permissions.CanRename(Permissions.LocalClientId, lot)) { Permissions.Deny(transform.position); return; }
        var net = DepotRowNet.Instance;
        if (net == null) return;
        string current = net.TryGetLot(lot, out var s) ? s.businessName.ToString() : DepotBusinesses.DefaultName;
        Sfx.Play("click", transform.position);
        BusinessRenameDialog.Show(current, name =>
        {
            if (string.IsNullOrEmpty(name)) return;
            string clean = DepotBusinesses.Clean(name);
            if (clean == null) { GameHUD.Toast("The sign guy won't paint that. Try another name.", ToastStyle.Bad); Sfx.Play("buzz"); return; }
            if (DepotRowNet.Instance != null) DepotRowNet.Instance.RequestRename(clean);
        });
    }

    public void Highlight(bool highlight) { }
}

/// <summary>A paper card with one text field: the business name (see <see cref="BusinessRenameDesk"/>).</summary>
public sealed class BusinessRenameDialog : UiScreen
{
    static BusinessRenameDialog current;
    public static bool IsShowing => current != null && current.IsOpen;

    TMP_InputField field;
    Action<string> done;
    RectTransform card;
    bool answered;

    public static void Show(string currentName, Action<string> onDone)
    {
        if (current != null) current.Answer(null);
        var c = UiKit.Canvas("[RenameBusiness]", UiLayers.Dialog);
        var d = c.gameObject.AddComponent<BusinessRenameDialog>();
        d.Build(currentName, onDone);
        current = d;
        d.Open(true);
    }

    void Build(string currentName, Action<string> cb)
    {
        done = cb;
        var root = (RectTransform)transform;
        var dim = FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.55f));
        dim.raycastTarget = true;
        card = UiKit.Paper(root, new Vector2(860f, 420f));
        card.localRotation = Quaternion.Euler(0f, 0f, 0.6f);

        var title = UiKit.InkLabel(card, "Title", "NAME YOUR BUSINESS", 46f, UiTheme.Ink, TextAlignmentOptions.Center, true);
        UiKit.Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(760f, 64f));
        var body = UiKit.InkLabel(card, "Body", $"Goes on your sign under your name. Up to {DepotBusinesses.MaxNameLength} letters; keep it clean.",
                                  26f, UiTheme.InkSoft, TextAlignmentOptions.Top, false, FontStyles.Normal);
        body.textWrappingMode = TextWrappingModes.Normal;
        UiKit.Place(body.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -112f), new Vector2(740f, 70f));

        field = UiKit.TextField(card, DepotBusinesses.DefaultName, DepotBusinesses.MaxNameLength, TMP_InputField.CharacterValidation.None);
        UiKit.Place((RectTransform)field.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -190f), new Vector2(620f, 72f));
        field.text = currentName ?? "";
        field.onSubmit.AddListener(_ => Answer(field.text));

        var ok = UiKit.Button(card, "Paint the sign", () => Answer(field.text), UiButtonStyle.Primary);
        ok.Size(330f, 76f);
        UiKit.Place(ok.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(185f, 34f), new Vector2(330f, 76f));
        var no = UiKit.Button(card, "Never mind", () => Answer(null), UiButtonStyle.Paper);
        no.Size(330f, 76f);
        UiKit.Place(no.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-185f, 34f), new Vector2(330f, 76f));
        UiPop.On(card.gameObject).Play(true);
    }

    public override void OnShow()
    {
        UiTheme.Play(UiTheme.SfxOpen, 0.5f, 0.9f);
        if (field != null) { UiKit.Focus(field.gameObject); field.ActivateInputField(); }
    }

    public override bool OnBack()
    {
        Answer(null);
        return true;
    }

    void Answer(string name)
    {
        if (answered) return;
        answered = true;
        if (current == this) current = null;
        UiTheme.Play(name != null ? UiTheme.SfxConfirm : UiTheme.SfxBack, 0.45f, 1f);
        Close();
        var cb = done;
        done = null;
        if (card != null) UiPop.On(card.gameObject).Play(false, () => { if (this != null) Destroy(gameObject); });
        else Destroy(gameObject);
        try { cb?.Invoke(name); }
        catch (Exception e) { Debug.LogException(e); }
    }

    protected override void OnDestroy()
    {
        if (current == this) current = null;
        base.OnDestroy();
    }
}
