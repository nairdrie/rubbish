using System.IO;
using UnityEngine;
// coop_lint fixture (never compiled): every line marked "hit" is one `save` hit.
public static class SaveViolations
{
    public static void Go(string p, string s)
    {
        File.WriteAllText(p, s);                          // hit 1
        File.AppendAllText(p, s);                         // hit 2
        File.Replace(p, p + ".x", null);                  // hit 3
        File.Delete(p);                                   // hit 4
        File.Move(p, p + ".y");                           // hit 5
        File.Copy(p, p + ".z");                           // hit 6
        PlayerPrefs.SetInt("k", 1);                       // hit 7
        string root = Application.persistentDataPath;     // hit 8
        SafeFile.WriteAtomic(p, s);                       // hit 9: a per-machine file outside Save/**
        string t = File.ReadAllText(p);                   // reads are fine
        int k = PlayerPrefs.GetInt("k");                  // reads are fine
        // File.WriteAllText(p, s);   comments are ignored
    }
}
