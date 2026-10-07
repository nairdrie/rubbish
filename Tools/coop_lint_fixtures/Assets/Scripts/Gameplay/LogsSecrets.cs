using UnityEngine;
// coop_lint fixture: `secrets` fires on Debug.Log* lines that mention tickets, tokens, keys, secrets or passwords (5 hits).
public static class LogsSecrets
{
    static void Go(string hex, string tok, string k, string s, ulong id)
    {
        Debug.Log($"auth ticket {hex}");                  // hit 1
        Debug.LogWarning("UGS Token expired: " + tok);    // hit 2 (case-insensitive)
        UnityEngine.Debug.LogError("apiKey=" + k);        // hit 3
        Debug.LogFormat("{0} secret", s);                 // hit 4
        Debug.Log("password reset");                      // hit 5
        Debug.Log("joined lobby " + id);                  // fine
        var ticket = hex;                                 // fine: not a log line
    }
}
