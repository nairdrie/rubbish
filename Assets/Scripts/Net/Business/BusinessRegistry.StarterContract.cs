using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;

public partial class BusinessRegistry
{
    // Retained for this entire town session, including disconnect/reconnect and switching away from a business.
    readonly Dictionary<string, int> starterProgress = new Dictionary<string, int>(StringComparer.Ordinal);
    bool starterClientLedgerRequest;

    void SeedStarterContract(BusinessInfo business, int savedProgress)
    {
        int previous = starterProgress.TryGetValue(business.Id, out int remembered) ? remembered : 0;
        starterProgress[business.Id] = StarterCollectionContract.MergeProgress(previous, savedProgress);
        SyncStarterProgress(business);
    }

    void TrackStarterCollection(LedgerEntry entry)
    {
        if (!IsServer || !IsSpawned || starterClientLedgerRequest) return;
        int index = IndexOf(entry.businessId);
        if (index < 0 || !starterProgress.TryGetValue(entry.businessId, out int before)) return;
        int next = StarterCollectionContract.Advance(before, entry.kind, entry.id, entry.cents, true, out bool award);
        if (next == before) return;
        var business = list[index];
        starterProgress[business.Id] = next; // terminal BEFORE posting the reward: the nested ledger event cannot pay it twice
        if (business.IsHostBusiness) StarterCollectionContract.SaveLocalProgress(next, announceStart: true);
        if (award)
        {
            using (Ledger.ToBusiness(business.Id))
                Ledger.Credit(StarterCollectionContract.Reward, StarterCollectionContract.RewardReason, StarterCollectionContract.LedgerId);
            if (business.IsHostBusiness) StarterCollectionContract.ShowCompleted();
        }
        SyncStarterProgress(business);
    }

    void SyncStarterProgress(BusinessInfo business)
    {
        if (!starterProgress.TryGetValue(business.Id, out int progress)) return;
        if (business.IsHostBusiness)
        {
            StarterCollectionContract.SaveLocalProgress(progress);
            return;
        }
        StarterProgressRpc(business.id, (byte)progress, RpcTarget.Single(business.ownerClientId, RpcTargetUse.Temp));
    }

    /// <summary>Reliable, owner-only progress mirror; identity guard ignores a late answer after choosing another save.</summary>
    [Rpc(SendTo.SpecifiedInParams)]
    void StarterProgressRpc(FixedString64Bytes businessId, byte progress, RpcParams rpcParams)
    {
        if (IsServer || !StarterCollectionContract.SnapshotMatches(GameSession.BusinessId, businessId.ToString(), progress)) return;
        StarterCollectionContract.SaveLocalProgress(progress, announceStart: true);
    }
}
