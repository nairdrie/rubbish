using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>One awake yard dog on the wire (≈ 15 bytes).</summary>
public struct DogPose : INetworkSerializable
{
    public ushort idx;
    public Vector3 pos;
    public byte yaw, mode;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref idx);
        s.SerializeValue(ref pos);
        s.SerializeValue(ref yaw);
        s.SerializeValue(ref mode);
    }
}

/// <summary>
/// F5 dogs on the wire (Docs/Multiplayer.md §14.5), on <c>NetSession.prefab</c>. The host simulates the yard dogs
/// (<see cref="YardDogs"/>) and streams the awake ones at 10 Hz, unreliable (a stale pose is simply replaced); dogs nobody
/// is near aren't sent and sit in their yards on every screen. Snatched bags travel through N2's own item stream
/// (<see cref="HandsNet.HostYank"/>), not here. Moments (snatch, bonk, caught) go reliable so every screen plays them.
/// </summary>
[NetSessionComponent(Order = 43)]
public class YardDogsNet : NetworkBehaviour
{
    public const float SendInterval = 0.1f;
    public static YardDogsNet Instance { get; private set; }

    readonly List<DogPose> buf = new List<DogPose>(32);

    public override void OnNetworkSpawn() => Instance = this;

    public override void OnNetworkDespawn()
    {
        if (Instance == this) Instance = null;
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    /// <summary>Host: stream the awake dogs.</summary>
    public void HostSend(IReadOnlyList<YardDogs.Dog> dogs)
    {
        if (!IsSpawned || !IsServer || NetworkManager.ConnectedClientsIds.Count <= 1) return;
        buf.Clear();
        for (int i = 0; i < dogs.Count; i++)
        {
            var d = dogs[i];
            if (!d.awake || d.walking) continue;
            buf.Add(new DogPose
            {
                idx = (ushort)d.idx, pos = d.pos, yaw = (byte)Mathf.RoundToInt(Mathf.Repeat(d.yaw, 360f) / 360f * 255f), mode = (byte)d.mode,
            });
        }
        if (buf.Count == 0) return;
        PoseRpc(buf.ToArray());
    }

    [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
    void PoseRpc(DogPose[] poses)
    {
        var y = YardDogs.Instance;
        if (y == null || poses == null) return;
        int n = Mathf.Min(poses.Length, 64);
        for (int i = 0; i < n; i++)
        {
            var p = poses[i];
            y.OnRemote(p.idx, p.pos, p.yaw / 255f * 360f, (YardDogs.Mode)p.mode);
        }
    }

    /// <summary>Host: a dog moment every screen should hear and see.</summary>
    public void HostSay(int idx, string what)
    {
        if (!IsSpawned || !IsServer || NetworkManager.ConnectedClientsIds.Count <= 1) return;
        var w = new FixedString32Bytes();
        w.CopyFromTruncated(what ?? "");
        SayRpc((ushort)idx, w);
    }

    [Rpc(SendTo.NotServer)]
    void SayRpc(ushort idx, FixedString32Bytes what)
    {
        YardDogs.Instance?.OnRemoteSay(idx, what.ToString());
        NetAudit.Event("dog", new Dictionary<string, object> { { "op", "seen-" + what.ToString() }, { "dog", (int)idx } });
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}
