using Unity.Netcode;
// coop_lint fixture: `netsingleton` only applies inside Net/**.
public static class NotNet
{
    static void Go() { var nm = NetworkManager.Singleton; }
}
