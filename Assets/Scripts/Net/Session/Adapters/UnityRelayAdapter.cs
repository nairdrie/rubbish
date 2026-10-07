using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

/// <summary>
/// Unity Relay transport (D6, package B1): today's join-code flow, guarded. Signs in anonymously only when needed
/// (<c>IsSignedIn</c> check, one Unity Services profile per PlayerKey so bots and test windows don't share a session),
/// allocates <c>TownCap − 1</c> connections (approval refuses a 5th player with a readable reason), and turns
/// every service failure into a sentence a player can act on. Never throws.
/// </summary>
public sealed class UnityRelayAdapter : INetTransportAdapter
{
    public NetMode Mode => NetMode.UnityRelay;

    /// <summary>"dtls" (default), "udp" or "wss". <c>GameStartup</c> passes its inspector value.</summary>
    public static string ConnectionType = "dtls";

    /// <summary>The last failure as a player-facing sentence (null after a success).</summary>
    public string LastError { get; private set; }

    public async Task<HostPrep> PrepareHostAsync(NetworkManager nm, int maxPlayers, CancellationToken ct)
    {
        LastError = null;
        try
        {
            var ut = Transport(nm);
            if (ut == null) return HostPrep.Fail(LastError = "This build has no Unity Transport on its NetworkManager.");
            string err = await EnsureSignedInAsync(ct);
            if (err != null) return HostPrep.Fail(LastError = err);
            ct.ThrowIfCancellationRequested();

            // maxPlayers = remote connections wanted (NetSession asks for TownCap − 1 = 3, §13.10); never more than the Town holds.
            int connections = Mathf.Clamp(maxPlayers, 1, NetConstants.TownCap - 1);
            Allocation alloc = await RelayService.Instance.CreateAllocationAsync(connections);
            ct.ThrowIfCancellationRequested();
            ut.SetRelayServerData(AllocationUtils.ToRelayServerData(alloc, Protocol()));
            string code = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
            ct.ThrowIfCancellationRequested();
            return HostPrep.Success(code);
        }
        catch (OperationCanceledException) { return HostPrep.Fail(LastError = "Cancelled."); }
        catch (Exception e)
        {
            LastError = Readable(e, hosting: true);
            Debug.LogWarning($"[UnityRelay] Hosting failed: {e.GetType().Name}: {e.Message}");
            return HostPrep.Fail(LastError);
        }
    }

    public async Task<bool> PrepareClientAsync(NetworkManager nm, JoinTarget t, CancellationToken ct)
    {
        LastError = null;
        try
        {
            var ut = Transport(nm);
            if (ut == null) { LastError = "This build has no Unity Transport on its NetworkManager."; return false; }
            string code = SessionKeys.NormalizeJoinCode(t?.joinCode);
            if (code.Length == 0) { LastError = "Type your friend's code first."; return false; }
            string err = await EnsureSignedInAsync(ct);
            if (err != null) { LastError = err; return false; }
            ct.ThrowIfCancellationRequested();

            JoinAllocation join = await RelayService.Instance.JoinAllocationAsync(code);
            ct.ThrowIfCancellationRequested();
            ut.SetRelayServerData(AllocationUtils.ToRelayServerData(join, Protocol()));
            return true;
        }
        catch (OperationCanceledException) { LastError = "Cancelled."; return false; }
        catch (Exception e)
        {
            LastError = Readable(e, hosting: false);
            Debug.LogWarning($"[UnityRelay] Joining failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    public void OnShutdown(NetworkManager nm) { } // the allocation expires on its own once the host disconnects

    static string Protocol() => string.IsNullOrEmpty(ConnectionType) ? "dtls" : ConnectionType;

    static UnityTransport Transport(NetworkManager nm) =>
        nm == null ? null : nm.NetworkConfig.NetworkTransport as UnityTransport ?? nm.GetComponent<UnityTransport>();

    /// <summary>Initialise Unity Services and sign in anonymously if (and only if) we aren't already. Null = ready.</summary>
    static async Task<string> EnsureSignedInAsync(CancellationToken ct)
    {
        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                string profile = SessionKeys.ServicesProfile(NetSession.LocalPlayerKey);
                if (profile != null) options.SetProfile(profile);
                await UnityServices.InitializeAsync(options);
            }
            ct.ThrowIfCancellationRequested();
            var auth = AuthenticationService.Instance;
            if (!auth.IsSignedIn) await auth.SignInAnonymouslyAsync();
            return auth.IsSignedIn ? null : NoService;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Debug.LogWarning($"[UnityRelay] Sign-in failed: {e.GetType().Name}: {e.Message}");
            return Readable(e, hosting: false);
        }
    }

    const string NoInternet = "Couldn't reach Unity's online service. Check your internet connection and try again.";
    const string NoService = "Couldn't sign in to Unity's online service. Try again in a minute.";
    const string Busy = "Unity Relay is having trouble right now. Try again in a minute.";

    /// <summary>A sentence for any services exception (no codes, no stack, nothing secret).</summary>
    public static string Readable(Exception e, bool hosting)
    {
        if (e is RelayServiceException r)
        {
            switch (r.Reason)
            {
                case RelayExceptionReason.JoinCodeNotFound:
                case RelayExceptionReason.InvalidArgument:
                case RelayExceptionReason.InvalidRequest:
                case RelayExceptionReason.EntityNotFound:
                case RelayExceptionReason.AllocationNotFound:
                case RelayExceptionReason.Gone:
                    return hosting
                        ? "Unity Relay refused to open a game. Try again."
                        : "That code didn't work. Codes change every time your friend hosts, so check you have the current one.";
                case RelayExceptionReason.RateLimited:
                    return "Too many tries in a row. Wait a few seconds and try again.";
                case RelayExceptionReason.Unauthorized:
                case RelayExceptionReason.Forbidden:
                case RelayExceptionReason.InactiveProject:
                    return "This build isn't allowed to use Unity Relay (project setup). Tell the developer.";
                case RelayExceptionReason.NetworkError:
                    return NoInternet;
                default:
                    return Busy;
            }
        }
        if (e is ServicesInitializationException) return NoService;
        if (e is RequestFailedException f)
        {
            switch (f.ErrorCode)
            {
                case CommonErrorCodes.TransportError:
                case CommonErrorCodes.Timeout:
                    return NoInternet;
                case CommonErrorCodes.TooManyRequests:
                    return "Too many tries in a row. Wait a few seconds and try again.";
                default:
                    return e is AuthenticationException ? NoService : Busy;
            }
        }
        string msg = SessionEndReason.Clean(e.Message, 120);
        return hosting ? "Couldn't open the game to friends: " + msg : "Couldn't join: " + msg;
    }
}
