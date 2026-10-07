using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

/// <summary>
/// LocalDirect transport (D6, package B1): Unity Transport straight to an address. No sign-in, no internet. Used for
/// solo (127.0.0.1 on a port the OS picks, so two copies of the game never fight over 7777, and approval refuses every
/// remote client), and for bots and Multiplayer Play Mode (127.0.0.1:7777 or <c>-botport</c>).
/// </summary>
public sealed class LocalDirectAdapter : INetTransportAdapter
{
    public NetMode Mode => NetMode.LocalDirect;

    /// <summary>Where the next host listens. <see cref="NetSession"/> sets these from <see cref="HostOptions"/>.</summary>
    public string ListenAddress = "127.0.0.1";
    public ushort Port = NetConstants.DefaultPort;

    public Task<HostPrep> PrepareHostAsync(NetworkManager nm, int maxPlayers, CancellationToken ct)
    {
        var ut = Transport(nm);
        if (ut == null) return Task.FromResult(HostPrep.Fail("This build has no Unity Transport on its NetworkManager."));
        string listen = string.IsNullOrEmpty(ListenAddress) ? "127.0.0.1" : ListenAddress;
        // forceOverrideCommandLineArgs: NGO would otherwise let -port / -ip on the command line override us.
        ut.SetConnectionData(true, "127.0.0.1", Port, listen);
        return Task.FromResult(HostPrep.Success());
    }

    public Task<bool> PrepareClientAsync(NetworkManager nm, JoinTarget t, CancellationToken ct)
    {
        var ut = Transport(nm);
        if (ut == null || t == null) return Task.FromResult(false);
        string address = string.IsNullOrEmpty(t.address) ? "127.0.0.1" : t.address;
        ushort port = t.port == 0 ? NetConstants.DefaultPort : t.port;
        ut.SetConnectionData(true, address, port);
        return Task.FromResult(true);
    }

    public void OnShutdown(NetworkManager nm) { }

    static UnityTransport Transport(NetworkManager nm) =>
        nm == null ? null : nm.NetworkConfig.NetworkTransport as UnityTransport ?? nm.GetComponent<UnityTransport>();
}
