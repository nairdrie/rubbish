using System.IO;
// coop_lint fixture: Editor/ is never scanned.
public static class EditorIgnored
{
    static void Go(string p) { File.WriteAllText(p, ""); var f = UnityEngine.Random.Range(0, 1); }
}
