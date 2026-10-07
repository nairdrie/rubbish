using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "Any shop's Rabies Shot" (F4, Docs/Multiplayer.md §14.4). Every shop's laptop desk gets an emergency pharmacy shelf that
/// anyone can use, visitors included (the laptop itself stays owner-only, §13.5): rabid? walk up and buy a shot ($35, your own
/// business pays). Carrying a passed-out (or flopped) rabid friend, or standing next to one? Jab them instead, you pay.
/// The prompt only shows up when somebody there is rabid.
/// </summary>
public class RabiesShotKiosk : MonoBehaviour, IInteractable
{
    float lastUse = -9f;

    /// <summary>
    /// Host validation for a player at any claimed shop's first-aid box. Depot Row hides remote shop interiors on this
    /// machine, so an active-object scan alone rejects a guest standing at their own perfectly valid kiosk.
    /// </summary>
    public static bool ContainsAny(Vector3 worldPos)
    {
        foreach (var kiosk in FindObjectsByType<RabiesShotKiosk>(FindObjectsSortMode.None))
        {
            var zone = kiosk != null ? kiosk.GetComponent<InteractionZone>() : null;
            if (zone != null && zone.isActiveAndEnabled && zone.Contains(worldPos)) return true;
        }
        if (!DepotRowWorld.Active) return false;
        int lotId = DepotRowLayout.InteriorAt(worldPos);
        var net = DepotRowNet.Instance;
        if (lotId < 0 || net == null || !net.TryGetLot(lotId, out var state) || !state.Claimed) return false;
        var laptop = DepotRowScene.Lot(lotId)?.laptop;
        if (laptop == null) return false;
        Ensure(laptop);
        var hiddenKiosk = laptop.GetComponentInChildren<RabiesShotKiosk>(true);
        var hiddenZone = hiddenKiosk != null ? hiddenKiosk.GetComponent<InteractionZone>() : null;
        return hiddenZone != null && hiddenZone.Contains(worldPos);
    }

    public static void Ensure(LaptopController laptop)
    {
        if (laptop == null || laptop.GetComponentInChildren<RabiesShotKiosk>(true) != null) return;
        var go = new GameObject("RabiesShotShelf");
        go.transform.SetParent(laptop.transform, false);
        Vector3 ls = laptop.transform.lossyScale; // zone and box in metres whatever the laptop's scale
        go.transform.localScale = new Vector3(1f / Mathf.Max(0.01f, ls.x), 1f / Mathf.Max(0.01f, ls.y), 1f / Mathf.Max(0.01f, ls.z));
        var k = go.AddComponent<RabiesShotKiosk>();
        var z = go.AddComponent<InteractionZone>();
        z.center = new Vector3(0f, 0f, 0f);
        z.size = new Vector3(4.5f, 5f, 4.5f);
        z.mode = ZoneMode.OnFoot;
        z.score = 2.5f; // beats the laptop's own prompt while someone's rabid
        z.targetOverride = k;
        // The imported laptop pivot is in front of its mesh, not on the desktop. Give medicine its own
        // metre-sized freestanding shelf alongside the desk, with a supported tray and four legs.
        var anchor = CritterLook.Node(go.transform, "MedicalShelfAnchor", new Vector3(-0.10f, -0.10f, -0.10f));
        var metal = CritterLook.Mat(new Color(0.32f, 0.40f, 0.38f), 0.45f);
        CritterLook.Part(anchor, PrimitiveType.Cube, "ShelfTop", new Vector3(0f, -0.035f, 0f), new Vector3(0.52f, 0.07f, 0.38f), metal);
        foreach (float x in new[] { -0.21f, 0.21f })
            foreach (float zleg in new[] { -0.14f, 0.14f })
                CritterLook.Part(anchor, PrimitiveType.Cube, "ShelfLeg", new Vector3(x, -0.62f, zleg), new Vector3(0.045f, 1.17f, 0.045f), metal);
        CritterLook.Part(anchor, PrimitiveType.Cube, "LowerShelf", new Vector3(0f, -0.95f, 0f), new Vector3(0.48f, 0.035f, 0.34f), metal);
        var box = CritterLook.Node(anchor, "Box", Vector3.zero);
        CritterLook.Part(box, PrimitiveType.Cube, "Case", new Vector3(0f, 0.08f, 0f), new Vector3(0.22f, 0.16f, 0.14f), CritterLook.Mat(new Color(0.95f, 0.95f, 0.95f), 0.5f));
        CritterLook.Part(box, PrimitiveType.Cube, "CrossH", new Vector3(0f, 0.08f, 0.072f), new Vector3(0.12f, 0.035f, 0.005f), CritterLook.Mat(new Color(0.9f, 0.15f, 0.15f), 0.4f), default, false);
        CritterLook.Part(box, PrimitiveType.Cube, "CrossV", new Vector3(0f, 0.08f, 0.072f), new Vector3(0.035f, 0.12f, 0.005f), CritterLook.Mat(new Color(0.9f, 0.15f, 0.15f), 0.4f), default, false);
        CritterLook.Part(box, PrimitiveType.Cylinder, "Syringe", new Vector3(0f, 0.2f, 0f), new Vector3(0.04f, 0.12f, 0.04f), CritterLook.Mat(new Color(0.6f, 1f, 0.6f), 0.9f, 0.6f), new Vector3(0f, 0f, 80f), false);
    }

    ulong Me => NightNet.Streaming ? NightNet.Instance.LocalId : 0;

    /// <summary>Who would get the shot: me, or a rabid friend I'm carrying / standing next to who's down.</summary>
    bool Patient(out ulong client, out string name)
    {
        client = Me;
        name = null;
        if (GameSession.HasRabies) return true;
        var net = HandsNet.Instance;
        if (net == null || !net.Active || !NightNet.Streaming) return false;
        var mine = net.BodyOf(net.LocalId);
        if (mine.carrying != HandsNet.None && NightNet.InfectedAt(mine.carrying) >= 0) { client = mine.carrying; name = HandsNet.NameOf(client); return true; }
        var me = NightPlayers.LocalPlayer;
        if (me == null) return false;
        foreach (var b in PlayerBody.All)
        {
            if (b == null || b.IsLocal || b.Player == null) continue;
            if ((b.transform.position - me.transform.position).sqrMagnitude > 3f * 3f) continue;
            if (NightNet.InfectedAt(b.ClientId) < 0) continue;
            var st = b.State;
            if (st != BodyState.Flopped && st != BodyState.Carried && st != BodyState.KnockedOut) continue;
            client = b.ClientId;
            name = b.DisplayName;
            return true;
        }
        return false;
    }

    public bool CanInteract() => Time.time - lastUse > 1f && Patient(out _, out _);

    public string GetInteractionPrompt()
    {
        if (!Patient(out _, out string name)) return "";
        string price = Economy.Money(NightShift.RabiesShotPrice);
        return name == null ? $"Rabies Shot from the first-aid box ({price})" : $"Jab {name} with a Rabies Shot ({price})";
    }

    public void OnInteract(GameObject interactor)
    {
        if (!Patient(out ulong client, out string name)) return;
        lastUse = Time.time;
        if (NightNet.Streaming && !NightNet.Instance.IsServer)
        {
            // The host verifies the patient and charges this player's business before curing anyone.
            NightNet.ShotFor(client);
            return;
        }
        if (!Ledger.Spend(NightShift.RabiesShotPrice, name == null ? "Rabies Shot" : $"Rabies Shot for {name}", NightShift.LedgerShot))
        {
            GameHUD.Toast($"Not enough cash for a Rabies Shot ({Economy.Money(NightShift.RabiesShotPrice)}).", ToastStyle.Bad, 2.5f);
            return;
        }
        NightAudit.Event("kiosk", new Dictionary<string, object> { { "for", (long)client }, { "self", name == null } });
        if (name == null) RabiesStatus.CureWithShot();
        else
        {
            NightNet.ShotFor(client);
            GameHUD.Toast($"You jabbed {name} with a Rabies Shot. What a friend.", ToastStyle.Good, 2.5f);
            Sfx.Play("squeak", transform.position, 0.8f, 0.6f);
        }
    }

    public void Highlight(bool highlight) { }
}
