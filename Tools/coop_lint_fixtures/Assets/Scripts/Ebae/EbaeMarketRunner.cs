using System.IO;
// coop_lint fixture: only EbaeMarket.cs itself is exempt, not its neighbours.
public static class EbaeMarketRunner
{
    static void Go(string p, string s) { File.Copy(p, s); }   // hit
}
