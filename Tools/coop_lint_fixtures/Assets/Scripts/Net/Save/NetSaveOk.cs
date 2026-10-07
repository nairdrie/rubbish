using System.IO;
// coop_lint fixture: Net/Save/** may write files.
public static class NetSaveOk
{
    public static void Go(string p, string s) { File.Copy(p, s); }
}
