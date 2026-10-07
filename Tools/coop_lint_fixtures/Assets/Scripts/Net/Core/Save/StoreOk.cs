using System.IO;
// coop_lint fixture: Net/Core/Save/** may write files.
public static class StoreOk
{
    public static void Go(string p, string s) { File.WriteAllText(p, s); File.Move(p, s); }
}
