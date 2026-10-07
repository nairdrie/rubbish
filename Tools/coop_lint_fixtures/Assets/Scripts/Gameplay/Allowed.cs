using System.IO;
// coop_lint fixture for `// coop:allow(<rule>) <reason>`.
public static class Allowed
{
    static void Go(string p, string s)
    {
        File.WriteAllText(p, s); // coop:allow(save) fixture: a same-line allow is honoured
        // coop:allow(save) fixture: a comment line directly above applies to the next code line
        File.Delete(p);
        // coop:allow(save, random) fixture: one allow can name two rules
        File.Copy(p, s); var r = new System.Random(1);
        File.Move(p, s); // coop:allow(save)
        GameSession.AddCash(1f); // coop:allow(cassh) typo in the rule name
        File.Replace(p, s, null); // coop:allow(random) fixture: an allow for another rule doesn't hide this save hit
    }
}
