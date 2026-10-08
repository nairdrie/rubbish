using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A friend's Rabies Shot (F4, Docs/Multiplayer.md §14.4). A rabid friend who is down (passed out, flopped or being
/// carried) can be jabbed right where they are: walk up (or carry them) and take the "Jab ..." option ($35, your own
/// business pays; the host verifies the patient and charges before curing anyone: NightNet.ShotFor).
/// This replaces the first-aid shelf that used to stand by every shop laptop: there is no kiosk any more. Curing
/// yourself is a Rabies Shot from Scrapazon (bought while rabid it is used at once; a stored one fires on infection).
/// One per remote player body (NightRunner ensures it); the prompt only shows while that player is rabid and down.
/// </summary>
public class RabiesJab : MonoBehaviour, IInteractable
{
    /// <summary>How close (m) you must be to jab a downed friend; the host allows the same 3 m.</summary>
    public const float Reach = 3f;

    PlayerBody body;
    float lastUse = -9f;

    public static void Ensure(PlayerController player)
    {
        if (player == null || player.GetComponentInChildren<RabiesJab>(true) != null) return;
        var b = player.GetComponent<PlayerBody>();
        if (b == null) return;
        var go = new GameObject("RabiesJab");
        go.transform.SetParent(player.transform, false);
        Vector3 ls = player.transform.lossyScale; // the zone in metres whatever the body's scale
        go.transform.localScale = new Vector3(1f / Mathf.Max(0.01f, ls.x), 1f / Mathf.Max(0.01f, ls.y), 1f / Mathf.Max(0.01f, ls.z));
        var jab = go.AddComponent<RabiesJab>();
        jab.body = b;
        var z = go.AddComponent<InteractionZone>();
        z.center = new Vector3(0f, 1f, 0f);
        z.size = new Vector3(Reach * 1.2f, 4f, Reach * 1.2f);
        z.mode = ZoneMode.OnFoot;
        z.score = 0.9f; // below "pick them up", so E still carries and the jab is the other option
        z.targetOverride = jab;
    }

    /// <summary>Is this body a rabid friend who is down right now (never the local player)?</summary>
    bool Patient(out ulong client, out string name)
    {
        client = HandsNet.None;
        name = null;
        if (body == null || body.Player == null || body.IsLocal || !NightNet.Streaming) return false;
        var net = HandsNet.Instance;
        if (net == null || !net.Active) return false;
        client = body.ClientId;
        if (NightNet.InfectedAt(client) < 0) return false;
        var st = body.State;
        if (st != BodyState.Flopped && st != BodyState.Carried && st != BodyState.KnockedOut) return false;
        name = body.DisplayName;
        return true;
    }

    public bool CanInteract() => Time.time - lastUse > 1f && Patient(out _, out _);

    public string GetInteractionPrompt() =>
        Patient(out _, out string name) ? $"Jab {name} with a Rabies Shot ({Economy.Money(NightShift.RabiesShotPrice)})" : "";

    public void OnInteract(GameObject interactor)
    {
        if (!Patient(out ulong client, out string name)) return;
        lastUse = Time.time;
        if (!NightNet.Instance.IsServer)
        {
            // The host verifies the patient and charges this player's business before curing anyone.
            NightNet.ShotFor(client);
            return;
        }
        if (!Ledger.Spend(NightShift.RabiesShotPrice, $"Rabies Shot for {name}", NightShift.LedgerShot))
        {
            GameHUD.Toast($"Not enough cash for a Rabies Shot ({Economy.Money(NightShift.RabiesShotPrice)}).", ToastStyle.Bad, 2.5f);
            return;
        }
        NightAudit.Event("jab", new Dictionary<string, object> { { "for", (long)client }, { "self", false } });
        NightNet.ShotFor(client);
        GameHUD.Toast($"You jabbed {name} with a Rabies Shot. What a friend.", ToastStyle.Good, 2.5f);
        Sfx.Play("squeak", transform.position, 0.8f, 0.6f);
    }

    public void Highlight(bool highlight) { }
}
