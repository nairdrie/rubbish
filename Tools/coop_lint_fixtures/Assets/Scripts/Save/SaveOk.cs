using System.IO;
using UnityEngine;
// coop_lint fixture: Save/** may write files.
public static class SaveOk
{
    public static void Go(string p, string s)
    {
        File.WriteAllText(p, s);
        File.Delete(p);
        string root = Application.persistentDataPath;
    }
}
