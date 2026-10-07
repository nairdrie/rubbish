using System;
using System.Collections;
using System.Text;
using StarterAssets;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Per-player co-op hygiene (Docs/Multiplayer.md §8.7.4, package B2). <see cref="PlayerController.OnNetworkSpawn"/> calls
/// <see cref="OnSpawned"/> on every copy of every player, on every machine.
/// <list type="bullet">
/// <item><b>Input belongs to the owner only.</b> A remote player's copy turns its <c>PlayerInput</c> and
/// <c>StarterAssetsInputs</c> off before their <c>Start</c> runs. Otherwise the host's copy spawns first on a guest, pairs the
/// keyboard, and the guest can't move (§8.7.1 finding 10). <c>Setup_Coop</c> also ships <c>PlayerInput</c> off in the prefab,
/// so a remote copy never pairs anything at all; this code works either way.</item>
/// <item><b>The owner re-pairs keyboard and mouse a frame later.</b> After a scene change the old local player is destroyed at
/// the end of the frame, so the new one may enable while the old one still holds the keyboard. One frame later it's gone and
/// <see cref="RepairKeyboardMouse"/> pairs the devices explicitly.</item>
/// <item>Adds <see cref="RosterView"/> if the prefab doesn't have it yet (Dumpling variant by roster slot).</item>
/// </list>
/// Headless bots have no keyboard: their own player keeps <c>PlayerInput</c> and <c>StarterAssetsInputs</c> off (the
/// StarterAssets <c>Start</c> would throw), the re-pair is skipped, and bots write the <c>StarterAssetsInputs</c> fields
/// directly (B5).
/// </summary>
public static class CoopPlayerHooks
{
    /// <summary>The control scheme StarterAssets pairs keyboard + mouse under.</summary>
    public const string KeyboardMouseScheme = "KeyboardMouse";

    /// <summary>Raised after the hooks ran for a player copy (bots, probes, later packages).</summary>
    public static event Action<PlayerController> Spawned;

    /// <summary>Call from <c>PlayerController.OnNetworkSpawn</c> on every copy (owner or not).</summary>
    public static void OnSpawned(PlayerController player)
    {
        if (player == null) return;
        bool owner = IsLocalOwner(player);
        ApplyInputOwnership(player.gameObject, owner);
        if (player.GetComponent<RosterView>() == null) player.gameObject.AddComponent<RosterView>();
        if (player.GetComponent<PlayerNameTag>() == null) player.gameObject.AddComponent<PlayerNameTag>();
        if (owner && player.isActiveAndEnabled) player.StartCoroutine(RepairNextFrame(player));
        try { Spawned?.Invoke(player); }
        catch (Exception e) { Debug.LogException(e); }
    }

    /// <summary>True for the copy this machine controls (or any copy that isn't network-spawned, e.g. a preview).</summary>
    public static bool IsLocalOwner(NetworkBehaviour b) => b != null && (!b.IsSpawned || b.IsOwner);

    /// <summary>
    /// Owner: <c>PlayerInput</c> and <c>StarterAssetsInputs</c> on (input stays deactivated while a UI lock is held).
    /// Anyone else: both off and the input values cleared, so nothing on a remote copy reads or pairs devices.
    /// <para>Headless owner (batchmode bots: no keyboard and no gamepad): both stay off. <c>StarterAssetsInputs.Start</c>
    /// pairs <c>Keyboard.current</c> and <c>Mouse.current</c> and throws without a keyboard, and <c>PlayerInput</c> has
    /// nothing to pair. Bots write the <c>StarterAssetsInputs</c> fields directly; the controller reads them either way.</para>
    /// </summary>
    public static void ApplyInputOwnership(GameObject go, bool owner)
    {
        if (go == null) return;
        var pi = go.GetComponent<PlayerInput>();
        var sai = go.GetComponent<StarterAssetsInputs>();
        if (owner)
        {
            bool keyboard = Keyboard.current != null;
            bool anyDevice = keyboard || Gamepad.current != null;
            if (sai != null && sai.enabled != anyDevice) sai.enabled = anyDevice;
            if (pi != null)
            {
                if (pi.enabled != anyDevice) pi.enabled = anyDevice;
                if (anyDevice && PlayerController.IsUIBlocking && pi.inputIsActive) pi.DeactivateInput();
            }
            return;
        }
        if (pi != null && pi.enabled) pi.enabled = false;   // OnDisable unpairs whatever it grabbed
        if (sai != null)
        {
            sai.move = Vector2.zero;
            sai.look = Vector2.zero;
            sai.jump = false;
            sai.sprint = false;
            sai.enabled = false;                              // before Start: no SwitchCurrentControlScheme on a remote copy
        }
    }

    static IEnumerator RepairNextFrame(PlayerController player)
    {
        yield return null;
        if (player == null || !IsLocalOwner(player)) yield break;
        var pi = player.GetComponent<PlayerInput>();
        if (pi == null || !pi.isActiveAndEnabled) yield break;
        if (Keyboard.current != null) RepairKeyboardMouse(pi);
        else if (Gamepad.current != null)
        {
            try { pi.SwitchCurrentControlScheme("Gamepad", Gamepad.current); }
            catch (Exception e) { Debug.LogWarning($"[CoopPlayerHooks] Couldn't pair gamepad to {pi.name}: {e.Message}"); }
        }
    }

    /// <summary>
    /// Pair the keyboard (and mouse, if any) to this <c>PlayerInput</c> under <see cref="KeyboardMouseScheme"/>.
    /// Returns false when there's nothing to pair (no keyboard: batchmode bots) or the input isn't enabled.
    /// </summary>
    public static bool RepairKeyboardMouse(PlayerInput pi)
    {
        if (pi == null || !pi.isActiveAndEnabled) return false;
        var kb = Keyboard.current;
        if (kb == null) return false;
        var mouse = Mouse.current;
        try
        {
            if (mouse != null) pi.SwitchCurrentControlScheme(KeyboardMouseScheme, kb, mouse);
            else pi.SwitchCurrentControlScheme(KeyboardMouseScheme, kb);
            if (PlayerController.IsUIBlocking && pi.inputIsActive) pi.DeactivateInput();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CoopPlayerHooks] Couldn't pair keyboard/mouse to {pi.name}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// One line per player copy for probes and bot logs:
    /// <c>players=2 inputsEnabled=1 | Player(Clone) owner=True client=1 pi=on scheme=KeyboardMouse devices=[Keyboard,Mouse] slot=3 variant=3</c>.
    /// </summary>
    public static string InputReport()
    {
        var sb = new StringBuilder(256);
        var all = UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        int enabled = 0;
        foreach (var p in all)
        {
            var pi = p.GetComponent<PlayerInput>();
            if (pi != null && pi.isActiveAndEnabled) enabled++;
        }
        sb.Append("players=").Append(all.Length).Append(" inputsEnabled=").Append(enabled);
        foreach (var p in all)
        {
            var pi = p.GetComponent<PlayerInput>();
            var sai = p.GetComponent<StarterAssetsInputs>();
            var rv = p.GetComponent<RosterView>();
            var puppet = p.GetComponent<ChuckPuppet>();
            sb.Append(" | ").Append(p.name)
              .Append(" owner=").Append(p.IsSpawned ? p.IsOwner.ToString() : "unspawned")
              .Append(" client=").Append(p.IsSpawned ? p.OwnerClientId.ToString() : "-")
              .Append(" pi=").Append(pi == null ? "none" : pi.isActiveAndEnabled ? "on" : "off")
              .Append(" sai=").Append(sai == null ? "none" : sai.enabled ? "on" : "off");
            if (pi != null && pi.isActiveAndEnabled)
            {
                sb.Append(" scheme=").Append(pi.currentControlScheme ?? "-").Append(" devices=[");
                var devs = pi.devices;
                for (int i = 0; i < devs.Count; i++) sb.Append(i > 0 ? "," : "").Append(devs[i].layout);
                sb.Append(']');
            }
            sb.Append(" slot=").Append(rv != null ? rv.Slot : -1)
              .Append(" variant=").Append(puppet != null ? puppet.Variant : -1);
        }
        return sb.ToString();
    }

    /// <summary>True when <paramref name="pi"/> has the keyboard paired.</summary>
    public static bool HasKeyboard(PlayerInput pi)
    {
        if (pi == null || !pi.isActiveAndEnabled) return false;
        var devs = pi.devices;
        for (int i = 0; i < devs.Count; i++) if (devs[i] is Keyboard) return true;
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Spawned = null;
}
