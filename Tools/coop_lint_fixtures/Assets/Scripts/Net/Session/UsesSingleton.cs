using Unity.Netcode;
// coop_lint fixture: `netsingleton` fires inside Net/** (2 hits).
public static class UsesSingleton
{
    static void Go()
    {
        var nm = NetworkManager.Singleton;                // hit 1
        if (NetworkManager.Singleton.IsHost) { }          // hit 2
    }
}
