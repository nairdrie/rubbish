using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The SolidWaste 3D floppy disk (Docs/Progression.md "Blueprints"): the one thing that unlocks printing. A business with
/// no disk finds one in its first sorted bags (<see cref="WorkshopState.BagRipped"/>, decided by the host and popped out
/// of the bag on the machine that ripped it). Revealing it puts up <see cref="DiskFoundPopup"/> with a KEEP button; the
/// KEEP crate works too. Kept, it never enters an inventory: it flies to the laptop and the shop's business gets its
/// print tab (<see cref="Workshop.DiskKept"/>). Binned or crushed instead, another one turns up within a few bags.
/// </summary>
public static class DiskItem
{
    public const string DefId = "floppy_disk";
    public static readonly Color Blue = new Color(0.18f, 0.42f, 0.85f);
    static int serial;

    public static bool Is(ItemInstance item) => item != null && item.defId == DefId;

    static ItemInstance NewInstance(int lot)
    {
        var def = ItemCatalog.Get(DefId);
        return new ItemInstance
        {
            // never saved, sold or handed over: unique on this machine for this session is enough
            uid = $"disk-{Permissions.LocalClientId}-{lot}-{Time.frameCount:x}-{++serial}", defId = DefId,
            massKg = def != null ? def.minMass : 0.02f, condition = 1f, value = def != null ? def.minValue : 0f,
            foundAt = "the bottom of a bag", dayFound = GameSession.Day,
        };
    }

    /// <summary>The bag this machine just ripped on <paramref name="table"/> had the disk in it: it pops up out of the mess
    /// and lands on the table top (an ordinary loose item from here on, shared like any other).</summary>
    internal static LooseItem SpawnOn(SortingTable table)
    {
        if (table == null) return null;
        int lot = DepotMachineContext.LotOf(table);
        Vector3 target = table.TopPoint(0f, 0f) + Vector3.up * 0.06f;
        Vector3 start = target + Vector3.up * 0.35f;
        var li = LooseItem.Spawn(NewInstance(lot), start, Quaternion.Euler(0f, 35f * (serial % 10), 0f), Vector3.up * 2.4f, false, Permissions.LocalClientId);
        li.Body.angularVelocity = new Vector3(0f, 9f, 0f);
        li.RevealOnLanding(1.1f);
        if (li.Visual != null) Juice.PopIn(li.Visual.transform, 0.12f);
        Sfx.Play("pop", start, 0.7f, 1.5f);
        return li;
    }

    /// <summary>The disk was just revealed (washed off) on this machine: make it impossible to miss, and offer the finder
    /// a KEEP button before anything can happen to it.</summary>
    internal static void OnRevealed(LooseItem li)
    {
        if (li == null) return;
        Vector3 p = li.transform.position;
        Fx.Sparkle(li.transform, Blue, 3600f);
        Fx.Burst(p, Blue, 26, 3.6f);
        Fx.Burst(p, SortingKit.Gold, 14, 2.6f);
        var local = PlayerController.Local; // coop:allow(local) this viewer's personal discovery popup, never an authority decision
        if (local == null || Application.isBatchMode) return;
        if (!Permissions.LocalCanWorkAt(p) || (local.transform.position - p).sqrMagnitude > 12f * 12f) return;
        bool mine = !NetSession.InSession || li.FinderClientId == local.OwnerClientId || li.FinderClientId == FeedbackAudience.UnknownFinder;
        if (!mine) { GameHUD.Activity("A floppy disk turned up on the sorting table!"); return; }
        Sfx.Play("rare", p);
        Juice.Shake(0.12f, 0.2f);
        li.StartCoroutine(PopupSoon(li));
    }

    /// <summary>A beat later, so a disk that was revealed on its way INTO a bin (a throw at the KEEP crate) is already gone
    /// and never flashes a popup; one a bin spat back is still there and gets it.</summary>
    static IEnumerator PopupSoon(LooseItem li)
    {
        yield return new WaitForSecondsRealtime(0.55f);
        if (li != null && li.State != CarryState.Consumed) DiskFoundPopup.Show(li);
    }

    /// <summary>The shop a point belongs to for the print shop's purposes (−1: the solo garage).</summary>
    static int LotAt(Vector3 world)
    {
        if (!DepotRowWorld.Active) return -1;
        int lot = DepotRowLayout.InteriorAt(world);
        return lot >= 0 ? lot : Workshop.OwnLot;
    }

    /// <summary>The popup's KEEP button: take the disk off the table and put it in the laptop.</summary>
    public static void Keep(LooseItem li)
    {
        if (li == null || li.State == CarryState.Consumed || !Is(li.Item)) return;
        Vector3 from = li.transform.position;
        int lot = LotAt(from);
        li.SetInFlight(); // claim a copy this machine does not simulate, like every scripted take
        li.Consume();
        Kept(lot, from);
    }

    /// <summary>The disk is gone from the world (popup, or the KEEP crate swallowed it): tell the host, and show it fly to
    /// the laptop on this machine.</summary>
    internal static void Kept(int lot, Vector3 from)
    {
        Workshop.DiskKept(lot);
        DiskFlight.Play(from);
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    /// <summary>QA (Rubbish > Debug > Shop): pop a disk out on the nearest sorting table, as if a bag had held it.</summary>
    public static bool DebugDrop()
    {
        SortingTable best = null; float bestD = float.MaxValue;
        Vector3 from = SortingKit.TryGetLocalPlayer(out var at) ? at : Vector3.zero;
        foreach (var table in Object.FindObjectsByType<SortingTable>(FindObjectsSortMode.None))
        {
            float d = (table.transform.position - from).sqrMagnitude;
            if (table.isActiveAndEnabled && d < bestD) { bestD = d; best = table; }
        }
        // a disk the host never issued cannot be kept, so issue this one first
        return best != null && Workshop.DebugIssueDisk(out _) && SpawnOn(best) != null;
    }
#endif

    // ------------------------------------------------------------------ the owner's fanfare

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Hook() { if (Application.isPlaying) Workshop.DiskInstalled += Installed; }

    static void Installed()
    {
        GameHUD.Toast($"{SolidWaste.Name} installed! There's a new tab on your laptop: print your own gear.", ToastStyle.Legendary, 7f);
        Sfx.Play("legendary");
        HeadOffice.Send("RE: that floppy disk", SolidWaste.InstallLetter, false);
    }
}

/// <summary>Names and copy of the print software (one place, like <see cref="Scrapazon"/>): rename it here.</summary>
public static class SolidWaste
{
    public const string Name = "SolidWaste 3D";
    public const string Edition = "SolidWaste 3D '97";
    /// <summary>The fake window title / address the laptop shows.</summary>
    public const string Address = "C:\\SOLIDWST\\SW3D.EXE";
    public const string TabLabel = Name + " - print your own gear";
    public const string InstallLetter =
        "IT here. We did not know that disk still existed. It is " + Edition + ", the 3D printing program this company " +
        "stopped paying for decades ago, and it is now on your laptop (third tab). It has BLUEPRINTS for personal gear and " +
        "prints them out of things you find: throw the parts in the KEEP crate, open the laptop, pick a blueprint and press " +
        "PRINT. It also knows how to print a monument out of plain sorted scrap. Please do not print a monument.";
}

/// <summary>The disk leaves the table and flies into the laptop. Visual only, on the machine that kept it.</summary>
sealed class DiskFlight : MonoBehaviour
{
    public static void Play(Vector3 from)
    {
        if (!Application.isPlaying || Application.isBatchMode) return;
        LaptopController laptop = null; float best = 45f * 45f;
        foreach (var candidate in FindObjectsByType<LaptopController>(FindObjectsSortMode.None))
        {
            float d = (candidate.transform.position - from).sqrMagnitude;
            if (candidate.isActiveAndEnabled && d < best) { best = d; laptop = candidate; }
        }
        var def = ItemCatalog.Get(DiskItem.DefId);
        var go = new GameObject("Disk on its way to the laptop");
        go.transform.position = from;
        GameObject visual = null;
        try { visual = ItemVisuals.Build(def, go.transform); } catch (System.Exception e) { Debug.LogException(e); }
        if (visual != null) visual.transform.localScale = Vector3.one * 0.16f;
        go.AddComponent<DiskFlight>().StartCoroutine(Fly(go.transform, laptop));
    }

    static IEnumerator Fly(Transform disk, LaptopController laptop)
    {
        Vector3 start = disk.position;
        Sfx.Play("whoosh", start, 0.7f, 1.3f);
        if (laptop == null)
        {
            // no laptop in sight (a keep made far from the office): it just zips up and away
            for (float e = 0f; e < 0.45f; e += Time.deltaTime) { disk.position = start + Vector3.up * (e * e * 14f); disk.Rotate(0f, 900f * Time.deltaTime, 0f); yield return null; }
            Destroy(disk.gameObject);
            yield break;
        }
        Vector3 to = laptop.transform.position + Vector3.up * 0.12f;
        float dist = Vector3.Distance(start, to), dur = Mathf.Clamp(0.45f + dist * 0.07f, 0.55f, 1.25f), height = Mathf.Clamp(0.9f + dist * 0.16f, 1f, 2.4f);
        for (float e = 0f; e < dur; e += Time.deltaTime)
        {
            if (disk == null || laptop == null) yield break;
            float k = e / dur, ease = k * k * (3f - 2f * k);
            disk.position = Vector3.Lerp(start, to, ease) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * height);
            disk.Rotate(0f, 1080f * Time.deltaTime, 0f, Space.World);
            disk.localScale = Vector3.one * Mathf.Lerp(1f, 0.55f, ease);
            yield return null;
        }
        if (disk != null) Destroy(disk.gameObject);
        if (laptop == null) yield break;
        Sfx.Play("click", to, 0.9f, 0.7f);
        Sfx.Play("ding", to, 0.8f, 1.25f);
        Juice.Squash(laptop.transform, 0.25f, 0.3f);
        Fx.Burst(to, DiskItem.Blue, 22, 3f);
        GameHUD.FloatingText(to + Vector3.up * 0.45f, "DISK INSERTED", DiskItem.Blue, 1.25f);
    }
}

/// <summary>
/// "You found a floppy disk": the one find that must not be binned by accident, so it stops the action and offers KEEP
/// right there. Leaving it on the table is allowed (the KEEP crate takes it later; any other bin spits it back; the
/// compactor crushes it, and then another disk turns up within a few bags).
/// </summary>
public sealed class DiskFoundPopup : UiScreen
{
    const float CardW = 980f, CardH = 520f;
    static DiskFoundPopup current;
    public static bool IsShowing => current != null && current.IsOpen;

    LooseItem item;
    RectTransform card, photoTilt;
    UiButton keep, leave;
    EbaeItemThumbnails thumbs;
    bool preview, destroying;

    public static void Show(LooseItem li)
    {
        if (!Application.isPlaying || Application.isBatchMode || li == null) return;
        if (current != null) { if (current.item == li) return; current.Close(); }
        var canvas = UiKit.Canvas("[DiskFound]", UiLayers.Dialog - 1);
        current = canvas.gameObject.AddComponent<DiskFoundPopup>();
        current.item = li;
        current.Build(!DepotRowWorld.Active || Workshop.CanManage(DepotRowLayout.InteriorAt(li.transform.position)));
        current.Open(true);
    }

    /// <summary>Edit-mode preview for screenshots (no play mode, no stack, no lock).</summary>
    public static DiskFoundPopup CreatePreview(Transform parent, Camera cam, int w, int h, bool owner = true, float uiScale = -1f)
    {
        var canvas = UiKit.Canvas("[DiskFound Preview]", UiLayers.Dialog - 1, parent);
        UiKit.PreviewCanvas(canvas, cam, w, h, uiScale);
        var popup = canvas.gameObject.AddComponent<DiskFoundPopup>();
        popup.preview = true;
        popup.Build(owner);
        Vector2 size = UiScale.ReferenceSize(w, h, uiScale);
        popup.card.localScale = Vector3.one * Mathf.Max(0.1f, Mathf.Min(1f, (size.x - 36f) / CardW, (size.y - 36f) / CardH));
        return popup;
    }

    static TextMeshProUGUI Text(Transform parent, string name, string text, float font, float x, float y, float w, float h,
        Color color, bool display = false, FontStyles style = FontStyles.Normal)
    {
        var label = UiKit.InkLabel(parent, name, text, font, color, TextAlignmentOptions.TopLeft, display, style);
        label.textWrappingMode = TextWrappingModes.Normal;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.enableAutoSizing = true;
        label.fontSizeMin = Mathf.Min(UiTheme.MinText, font);
        label.fontSizeMax = font;
        UiKit.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(w, h));
        return label;
    }

    void Build(bool owner)
    {
        var root = (RectTransform)transform;
        FeelUi.Img(root, "Dim", null, UiTheme.WithAlpha(UiTheme.Ink, 0.62f)).raycastTarget = true;
        card = UiKit.Paper(root, new Vector2(CardW, CardH));
        card.localRotation = Quaternion.Euler(0f, 0f, 0.6f);

        // the find, big, on a tilted photo card
        photoTilt = FeelUi.Rect("Photo", card, Vector2.up, new Vector2(0.5f, 0.5f), new Vector2(190f, -230f), new Vector2(290f, 290f));
        photoTilt.localRotation = Quaternion.Euler(0f, 0f, -5f);
        FeelUi.Img(photoTilt, "Shadow", FeelUi.Rounded, UiTheme.WithAlpha(UiTheme.Ink, 0.25f), 0.35f).rectTransform.anchoredPosition = new Vector2(6f, -8f);
        FeelUi.Img(photoTilt, "Card", FeelUi.Rounded, UiTheme.Hex(0xF0EDE0), 0.35f);
        var photo = new GameObject("Disk photo", typeof(RectTransform)).AddComponent<RawImage>();
        photo.gameObject.layer = photoTilt.gameObject.layer;
        photo.rectTransform.SetParent(photoTilt, false);
        photo.rectTransform.anchorMin = Vector2.zero; photo.rectTransform.anchorMax = Vector2.one;
        photo.rectTransform.offsetMin = new Vector2(14f, 14f); photo.rectTransform.offsetMax = new Vector2(-14f, -14f);
        photo.raycastTarget = false;
        thumbs = new EbaeItemThumbnails();
        photo.texture = thumbs.PhotoNow(DiskItem.DefId);
        photo.enabled = photo.texture != null;
        var tag = UiKit.OutlineLabel(photoTilt, "Rarity", "ONE OF A KIND", 22f, Color.white, TextAlignmentOptions.Center, true);
        UiKit.Place(tag.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 6f), new Vector2(250f, 34f));

        const float X = 380f, W = CardW - X - 44f;
        Text(card, "Kicker", "WAIT. WHAT IS THAT?", 24f, X, 40f, W, 32f, UiTheme.InkSoft, false, FontStyles.Bold).textWrappingMode = TextWrappingModes.NoWrap;
        Text(card, "Title", "YOU FOUND A FLOPPY DISK!", 50f, X, 70f, W, 66f, UiTheme.Ink, true, FontStyles.Bold).textWrappingMode = TextWrappingModes.NoWrap;
        Text(card, "Label", "\"" + SolidWaste.Edition + ". DO NOT THROW AWAY.\"", 26f, X, 142f, W, 36f, DiskItem.Blue, false, FontStyles.Bold | FontStyles.Italic)
            .textWrappingMode = TextWrappingModes.NoWrap;
        Text(card, "Body", owner
            ? "Somebody threw it away. KEEP it and it goes straight into your laptop: a new tab that prints your own gear out of the junk you keep."
            : "Somebody threw it away. KEEP it and it goes straight into the boss's laptop: it prints gear out of kept junk.",
            27f, X, 190f, W, 128f, UiTheme.Ink);

        keep = UiKit.Button(card, "KEEP IT", () => Answer(true), UiButtonStyle.Go);
        UiKit.Place(keep.Rect, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(X, 96f), new Vector2(W, 86f));
        keep.Text.fontSizeMax = 38f;
        leave = UiKit.Button(card, "Leave it on the table", () => Answer(false), UiButtonStyle.Paper);
        UiKit.Place(leave.Rect, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(X, 40f), new Vector2(W, 48f));
        leave.Text.fontSizeMax = 23f;
        var note = Text(card, "Note", "Binned it by accident? Another one turns up within a few bags.", UiTheme.MinText, 44f, CardH - 54f, 320f, 44f, UiTheme.InkSoft, false, FontStyles.Italic);
        note.alignment = TextAlignmentOptions.Top;
        UiKit.ChainVertical(new Selectable[] { keep, leave });
        if (!preview) UiPop.On(card.gameObject).Play(true);
    }

    void Answer(bool yes)
    {
        if (preview) return;
        var li = item; item = null;
        Close();
        if (yes) DiskItem.Keep(li);
    }

    void Update()
    {
        if (photoTilt != null) photoTilt.localRotation = Quaternion.Euler(0f, 0f, -5f + Mathf.Sin(Time.unscaledTime * 2.2f) * 2.5f);
        if (preview || !IsOpen) return;
        // somebody else kept it, it was crushed, or we left the shop: nothing to offer any more
        if (item == null || item.State == CarryState.Consumed || SceneController.IsTransitioning || DepotRowWorld.Transitioning) { item = null; Close(); return; }
        var root = (RectTransform)transform;
        card.localScale = Vector3.one * Mathf.Max(0.1f, Mathf.Min(1f, (root.rect.width - 36f) / CardW, (root.rect.height - 36f) / CardH));
    }

    public override void OnShow() { UiTheme.Play(UiTheme.SfxOpen, 0.6f, 1.2f); if (keep != null) UiKit.Focus(keep.gameObject); }
    public override bool OnBack() { Answer(false); return true; }
    public override void OnHide() { if (current == this) current = null; if (!destroying && this != null) Destroy(gameObject); }
    protected override void OnDestroy()
    {
        destroying = true; if (current == this) current = null;
        thumbs?.Dispose(); thumbs = null;
        base.OnDestroy();
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() => current = null;
}
