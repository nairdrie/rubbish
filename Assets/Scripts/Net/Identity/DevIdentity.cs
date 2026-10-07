using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>
/// The <c>dev:&lt;name&gt;</c> identity (Docs/Accounts.md §1.2): the Editor (<c>dev:editor</c>), MPPM clones
/// (<c>dev:mppm-&lt;tag&gt;</c>), bots (<c>-playerid dev:bot-N</c>) and development builds without <c>-steam</c>. Carries no
/// Steam proof, so a release host refuses it. The name comes from <see cref="SteamPolicy.ResolveDevName"/>.
/// </summary>
public sealed class DevIdentity : IPlayerIdentity
{
    /// <summary>The per-install id file in the data root (used only by development builds started without -playerid).</summary>
    public const string InstallIdFile = "dev_install_id.txt";

    public PlayerKey Key { get; }
    public string DisplayName { get; }
    public bool IsSteam => false;
    /// <summary>Where the name came from (for logs).</summary>
    public string Source { get; }

    public DevIdentity(PlayerKey key, string displayName, string source)
    {
        if (!key.IsValid) throw new ArgumentException("invalid player key", nameof(key));
        Key = key;
        DisplayName = string.IsNullOrEmpty(displayName) ? key.Value : displayName;
        Source = source ?? "";
    }

    /// <summary>Dev identities have no Steam proof; the host decides whether that's allowed (release builds refuse).</summary>
    public byte[] GetJoinTicket(ulong hostSteamId) => Array.Empty<byte>();

    public void CancelJoinTicket() { }

    /// <summary>
    /// This process's dev identity: a forged <c>-playerid steam:&lt;id&gt;</c> (door tests, non-release only), else the dev
    /// name in the §1.2 order.
    /// </summary>
    public static DevIdentity ForThisProcess(in BuildFacts facts, string mppmTag)
    {
        if (SteamPolicy.TryGetSpoofedSteamKey(facts.args, facts.IsRelease, out PlayerKey spoof))
        {
            Debug.LogWarning($"[Identity] -playerid {spoof} is a FORGED Steam key with no Steam proof (door test). " +
                             "A host that enforces Steam auth must refuse it.");
            return new DevIdentity(spoof, "spoofed " + spoof.SteamId64, "-playerid (forged steam key)");
        }
        string name = SteamPolicy.ResolveDevName(facts.args, facts.isEditor, facts.isMppmClone, mppmTag, ReadOrCreateInstallId,
                                                 out DevNameSource source);
        return new DevIdentity(PlayerKey.Dev(name), name, source.ToString());
    }

    /// <summary>
    /// The per-install id (<c>install-&lt;32 hex&gt;</c>) in <see cref="SaveRoot.Path"/>, created on first use. Never in the Editor
    /// (it is <c>dev:editor</c>). If the file can't be read or written the id lives for this run only, with a warning.
    /// </summary>
    static string ReadOrCreateInstallId()
    {
        string path = null;
        try
        {
            path = Path.Combine(SaveRoot.Path, InstallIdFile);
            // The main file, else its newest backup generation (an id that vanished would look like lost progress).
            if (SafeFile.TryReadNewest(path, b => ParseInstallId(b) != null, out byte[] data, out _)) return ParseInstallId(data);

            string id = "install-" + RandomHex(16);
            bool ok = SafeFile.WriteAtomic(path, Encoding.UTF8.GetBytes(id), DateTime.UtcNow, SafeFile.ProcessSessionId); // coop:allow(save) the per-install dev name file of Accounts.md §1.2 (identity, not game data), atomic via SafeFile
            if (ok) Debug.Log($"[Identity] Created this install's dev id ({InstallIdFile} in the data root).");
            else Debug.LogWarning($"[Identity] Couldn't save this install's dev id; it lasts for this run only. Pass -playerid <name> for a stable one.");
            return id;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Identity] Couldn't keep a per-install dev id at {path ?? "(no data root)"}: {e.Message}. " +
                             "Using one for this run only; pass -playerid <name> for a stable one.");
            return "install-" + RandomHex(16);
        }
    }

    static string ParseInstallId(byte[] data)
    {
        string id = data != null ? SteamPolicy.SanitizeDevName(SafeFile.Utf8(data)) : null;
        return id != null && id.StartsWith("install-", StringComparison.Ordinal) && id.Length > 12 ? id : null;
    }

    static string RandomHex(int bytes)
    {
        var b = new byte[bytes];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        var sb = new StringBuilder(bytes * 2);
        foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    public override string ToString() => $"{Key} ({Source})";
}
