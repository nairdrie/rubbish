using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;

/// <summary>
/// One transport behind <c>NetSession</c> (D6): LocalDirect (127.0.0.1), UnityRelay, and later SteamSdr. An adapter only
/// configures the transport on <paramref name="nm"/>; <c>NetSession</c> starts and stops NGO.
/// </summary>
public interface INetTransportAdapter
{
    NetMode Mode { get; }
    /// <summary>Configure the transport for hosting (allocate Relay, open a socket…). Never throws: failures come back in HostPrep.</summary>
    Task<HostPrep> PrepareHostAsync(NetworkManager nm, int maxPlayers, CancellationToken ct);
    /// <summary>Configure the transport to reach <paramref name="t"/>. False on failure (with the reason logged).</summary>
    Task<bool> PrepareClientAsync(NetworkManager nm, JoinTarget t, CancellationToken ct);
    void OnShutdown(NetworkManager nm);
}

public readonly struct HostPrep
{
    public readonly bool Ok;
    /// <summary>What a friend types (Relay code) or null.</summary>
    public readonly string JoinCode;
    public readonly string Error;

    public HostPrep(bool ok, string joinCode, string error)
    {
        Ok = ok;
        JoinCode = joinCode;
        Error = error;
    }

    public static HostPrep Success(string joinCode = null) => new HostPrep(true, joinCode, null);
    public static HostPrep Fail(string error) => new HostPrep(false, null, string.IsNullOrEmpty(error) ? "unknown error" : error);
}

/// <summary>Where to join. Which fields matter depends on <see cref="mode"/>.</summary>
public sealed class JoinTarget
{
    public NetMode mode;
    /// <summary>UnityRelay join code.</summary>
    public string joinCode;
    /// <summary>LocalDirect / direct IP.</summary>
    public string address;
    public ushort port = NetConstants.DefaultPort;
    /// <summary>Steam lobby (B10) and its owner, for host-bound tickets.</summary>
    public ulong steamLobbyId, hostSteamId;

    public static JoinTarget Local(ushort port = NetConstants.DefaultPort) =>
        new JoinTarget { mode = NetMode.LocalDirect, address = "127.0.0.1", port = port };

    public static JoinTarget Relay(string code) => new JoinTarget { mode = NetMode.UnityRelay, joinCode = code };

    public override string ToString() => mode switch
    {
        NetMode.LocalDirect => $"{mode} {address}:{port}",
        NetMode.UnityRelay => $"{mode} code {joinCode}",
        NetMode.SteamSdr => $"{mode} lobby {steamLobbyId}",
        _ => mode.ToString(),
    };
}
