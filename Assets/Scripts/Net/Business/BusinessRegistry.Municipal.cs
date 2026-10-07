using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public partial class BusinessRegistry
{
    readonly Dictionary<string, double> municipalSince = new Dictionary<string, double>();
    double nextMunicipal;

    void TickMunicipalHours()
    {
        if (!IsSpawned || !IsServer || GameClock.Held) return;
        double now = GameClock.TotalHours;
        if (now < nextMunicipal) return;
        nextMunicipal = now + 0.2; // ten real seconds; saved on each owner's machine
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            double previous = municipalSince.TryGetValue(b.Id, out double since) ? since : now;
            municipalSince[b.Id] = now;
            float hours = MunicipalEconomy.SanitizeHours(b.municipalHours + (float)System.Math.Max(0, now - previous));
            if (Mathf.Approximately(b.municipalHours, hours)) continue;
            b.municipalHours = hours;
            list[i] = b;
            if (b.Id == GameSession.BusinessId) MunicipalEconomy.SaveLocalHours(hours);
        }
    }

    public void MunicipalReceipt(ulong owner, string message, bool fine)
    {
        if (!IsServer || !IsSpawned) return;
        var text = new FixedString512Bytes();
        text.CopyFromTruncated(message);
        MunicipalReceiptRpc(text, fine, RpcTarget.Single(owner, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void MunicipalReceiptRpc(FixedString512Bytes text, bool fine, RpcParams rpcParams) =>
        MunicipalEconomy.ShowReceipt(text.ToString(), fine);
}
