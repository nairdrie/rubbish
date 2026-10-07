#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using System;
using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

/// <summary>
/// The <c>steam:&lt;id64&gt;</c> identity (Docs/Accounts.md §1.1, §7.6). The display name is read live from Steam and never
/// saved. <see cref="GetJoinTicket"/> returns a Steam session proof bound to the host's identity, so it can't be replayed to
/// another host; one proof is live at a time and it is reused for the same host for a minute (a pre-warmed proof is ready
/// by the time the connection is up). Never log the bytes (lint rule <c>secrets</c>).
/// </summary>
public sealed class SteamIdentity : IPlayerIdentity
{
    const int MaxProofBytes = 1024;
    const float ReuseSeconds = 60f;

    public PlayerKey Key { get; }
    public string DisplayName => SteamBootstrap.LocalName ?? Key.Value;
    public bool IsSteam => true;

    /// <summary>The proof for the current host is confirmed by Steam (<c>GetAuthSessionTicketResponse_t</c> OK).</summary>
    public bool ProofReady { get; private set; }

    byte[] proof;
    ulong proofHost;
    float proofAt;
#if !DISABLESTEAMWORKS
    HAuthTicket handle = HAuthTicket.Invalid;
    Callback<GetAuthSessionTicketResponse_t> responseCb;
#endif

    SteamIdentity(PlayerKey key)
    {
        Key = key;
#if !DISABLESTEAMWORKS
        responseCb = Callback<GetAuthSessionTicketResponse_t>.Create(OnProofResponse);
#endif
        SteamBootstrap.ShuttingDown += OnShutdown;
    }

    /// <summary>The identity for the signed-in Steam user, or null when Steam isn't up.</summary>
    public static SteamIdentity TryCreate()
    {
        if (!SteamBootstrap.IsInitialized || SteamBootstrap.LocalSteamId == 0) return null;
        return new SteamIdentity(PlayerKey.Steam(SteamBootstrap.LocalSteamId));
    }

    /// <summary>The local identity when it is a Steam one.</summary>
    public static SteamIdentity Local => PlayerIdentity.Local as SteamIdentity;

    /// <summary>
    /// A proof bound to <paramref name="hostSteamId"/> (0 = unbound, e.g. joining by a typed code). Empty when Steam is down.
    /// </summary>
    public byte[] GetJoinTicket(ulong hostSteamId)
    {
#if !DISABLESTEAMWORKS
        if (!SteamBootstrap.IsInitialized) return Array.Empty<byte>();
        if (proof != null && proofHost == hostSteamId && Time.realtimeSinceStartup - proofAt < ReuseSeconds)
            return (byte[])proof.Clone();
        CancelJoinTicket();
        try
        {
            var buf = new byte[MaxProofBytes];
            var who = new SteamNetworkingIdentity();
            if (hostSteamId != 0) who.SetSteamID64(hostSteamId);
            else who.Clear();
            handle = SteamUser.GetAuthSessionTicket(buf, buf.Length, out uint size, ref who);
            if (handle == HAuthTicket.Invalid || size == 0 || size > buf.Length)
            {
                Debug.LogWarning("[SteamIdentity] Steam didn't hand out an auth session for joining.");
                handle = HAuthTicket.Invalid;
                return Array.Empty<byte>();
            }
            proof = new byte[size];
            Buffer.BlockCopy(buf, 0, proof, 0, (int)size);
            proofHost = hostSteamId;
            proofAt = Time.realtimeSinceStartup;
            ProofReady = false;
            return (byte[])proof.Clone();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SteamIdentity] Couldn't get an auth session for joining: {e.Message}");
            return Array.Empty<byte>();
        }
#else
        return Array.Empty<byte>();
#endif
    }

    /// <summary>Ask for the proof early (the router does this once it knows the host), so Steam has confirmed it by approval.</summary>
    public void Prewarm(ulong hostSteamId) => GetJoinTicket(hostSteamId);

    /// <summary>True once the proof for <paramref name="hostSteamId"/> exists and Steam confirmed it.</summary>
    public bool IsProofReadyFor(ulong hostSteamId) => proof != null && proofHost == hostSteamId && ProofReady;

    public void CancelJoinTicket()
    {
#if !DISABLESTEAMWORKS
        if (handle != HAuthTicket.Invalid && SteamBootstrap.IsInitialized)
        {
            try { SteamUser.CancelAuthTicket(handle); }
            catch (Exception) { /* Steam is going away */ }
        }
        handle = HAuthTicket.Invalid;
#endif
        proof = null;
        proofHost = 0;
        ProofReady = false;
    }

#if !DISABLESTEAMWORKS
    void OnProofResponse(GetAuthSessionTicketResponse_t r)
    {
        if (r.m_hAuthTicket != handle) return;
        ProofReady = r.m_eResult == EResult.k_EResultOK;
        if (!ProofReady) Debug.LogWarning($"[SteamIdentity] Steam couldn't confirm our join proof: {r.m_eResult}.");
    }
#endif

    void OnShutdown()
    {
        CancelJoinTicket();
#if !DISABLESTEAMWORKS
        responseCb?.Dispose();
        responseCb = null;
#endif
        SteamBootstrap.ShuttingDown -= OnShutdown;
    }

    public override string ToString() => $"{Key} (Steam)";
}
