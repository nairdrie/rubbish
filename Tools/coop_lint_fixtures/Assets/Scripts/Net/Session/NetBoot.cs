using Unity.Netcode;
// coop_lint fixture: NetBoot.cs is the one Net/** file allowed to touch the singleton.
public static class NetBoot
{
    static void Go() { var nm = NetworkManager.Singleton; }
}
