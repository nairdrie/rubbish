using System;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Runs <see cref="StoreTorture"/> in a player started with <c>-storetorture [run|verify]</c> (the bot build, driven by
/// <c>Tools/bots/store_torture.sh</c>), then quits with 0 (pass) or 1 (fail); 2 = refused. It runs at
/// SubsystemRegistration, before any scene, game save or network code, and only with <c>-dataroot</c> pointing away from the
/// real saves. Inert in the Editor and in any player without the flag. BotDriver doesn't need to handle the flag.
/// </summary>
static class StoreTortureBoot
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Boot()
    {
        if (Application.isEditor) return;
        string[] args = Environment.GetCommandLineArgs();
        bool hasValue = CoreSmoke.TryGetArg(args, StoreTorture.Flag, out string mode);
        if (!hasValue && !CoreSmoke.HasFlag(args, StoreTorture.Flag)) return;
        mode = hasValue ? mode.ToLowerInvariant() : "run";
        if (mode != "run" && mode != "verify")
        {
            Debug.LogError($"[StoreTorture] Unknown mode '{mode}' (use run or verify).");
            Application.Quit(2);
            return;
        }

        SaveRoot.Refresh();
        if (!SaveRoot.IsOverridden)
        {
            Debug.LogError("[StoreTorture] Refused: it needs -dataroot <scratch folder> and never runs on the real saves.");
            Application.Quit(2);
            return;
        }

        int seconds = 30;
        if (CoreSmoke.TryGetArg(args, StoreTorture.SecondsFlag, out string s) &&
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            seconds = Mathf.Clamp(v, 1, 3600);
        ulong seed = 0;
        if (CoreSmoke.TryGetArg(args, StoreTorture.SeedFlag, out string sd)) ulong.TryParse(sd, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed);

        var rep = mode == "verify" ? StoreTorture.Verify(SaveRoot.Path) : StoreTorture.Run(SaveRoot.Path, seconds, seed);
        string line = (rep.Ok ? "STORETORTURE OK " : "STORETORTURE FAIL ") + rep.ToJson();
        if (rep.Ok) Debug.Log(line);
        else Debug.LogError(line);
        Application.Quit(rep.Ok ? 0 : 1);
    }
}
