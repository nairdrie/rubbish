using System.IO;
// coop_lint fixture: Ebae/EbaeMarket.cs is a legacy writer, exempt from `save` until M1.
public static class EbaeMarket
{
    static void Go(string p, string s) { File.Replace(s, p, null); }
}
