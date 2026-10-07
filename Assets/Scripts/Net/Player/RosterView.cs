using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Shows a player's roster slot on their Dumpling (Docs/Multiplayer.md §8.7.4, package B2; Docs/Character.md §14). While
/// <see cref="NetRoster"/> has an entry for this player's owner, it writes <c>ChuckPuppet.variantOverride = slot</c>, so the
/// same person keeps the same look on every machine and after a rejoin (slots are remembered by PlayerKey). With no roster
/// (offline, or before the host has written the entry) it leaves the override at -1 and the puppet falls back to its own
/// <c>OwnerClientId % 4</c>. Lives on the player root: <c>Setup_Coop</c> adds it to the prefab and
/// <see cref="CoopPlayerHooks.OnSpawned"/> adds it at runtime if the prefab doesn't have it yet. Never edits Character files.
/// </summary>
[DisallowMultipleComponent]
public class RosterView : MonoBehaviour
{
    ChuckPuppet puppet;
    NetworkObject netObj;
    NetRoster boundRoster;
    bool lastSpawned;
    ulong lastOwner = ulong.MaxValue;
    bool applied;

    /// <summary>This player's roster slot, or -1 when the roster has no entry for them.</summary>
    public int Slot { get; private set; } = -1;

    void Awake()
    {
        puppet = GetComponent<ChuckPuppet>();
        netObj = GetComponent<NetworkObject>();
    }

    void OnDisable() => Bind(null);

    void OnDestroy() => Bind(null);

    void Update()
    {
        // Rebind when the roster appears, goes away or is replaced; re-apply when this player's spawn state or owner changes
        // (the roster entry can arrive before or after the player object).
        var roster = NetRoster.Instance;
        bool spawned = netObj != null && netObj.IsSpawned;
        ulong owner = spawned ? netObj.OwnerClientId : ulong.MaxValue;
        if (roster != boundRoster)
        {
            Bind(roster);
            Apply();
        }
        else if (spawned != lastSpawned || owner != lastOwner)
        {
            Apply();
        }
        lastSpawned = spawned;
        lastOwner = owner;
    }

    void Bind(NetRoster roster)
    {
        if (boundRoster == roster) return;
        if (boundRoster != null) boundRoster.Changed -= Apply;
        boundRoster = roster;
        if (boundRoster != null) boundRoster.Changed += Apply;
    }

    /// <summary>Look this player up in the roster and set (or clear) the Dumpling variant.</summary>
    public void Apply()
    {
        if (this == null) return;
        int slot = -1;
        if (boundRoster != null && netObj != null && netObj.IsSpawned &&
            boundRoster.TryGetByClient(netObj.OwnerClientId, out var entry))
            slot = entry.slot;
        Slot = slot;
        if (puppet == null) return;
        if (slot >= 0)
        {
            puppet.variantOverride = slot;
            applied = true;
        }
        else if (applied)
        {
            puppet.variantOverride = -1;
            applied = false;
        }
    }
}
