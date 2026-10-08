using UnityEditor;
using UnityEngine;

/// <summary>Rubbish > Debug > Raccoon Calling: play-mode shortcuts for testing the calling (host or solo only).</summary>
public static class RaccoonCallingDebug
{
    const string Menu = "Rubbish/Debug/Raccoon Calling/";
    static bool Ready(out string id)
    {
        id = null;
        if (!Application.isPlaying || GameSession.IsConnectedGuest) { Debug.Log("[RaccoonCalling] Play mode, host or solo only."); return false; }
        id = Hutch.LocalBusinessId;
        return true;
    }

    [MenuItem(Menu + "Build the whole Raccoon calling (free)")]
    static void BuildAll()
    {
        if (!Ready(out _)) return;
        Debug.Log(Workshop.DebugSetCallings(true, out string why) ? "[RaccoonCalling] Every buildable calling level is built." : "[RaccoonCalling] " + why);
    }

    [MenuItem(Menu + "Add a raccoon to my hutch")]
    static void AddOne() => Add(false);
    [MenuItem(Menu + "Add a RABID raccoon to my hutch")]
    static void AddRabid() => Add(true);
    static void Add(bool rabid)
    {
        if (!Ready(out _)) return;
        var pc = NightPlayers.LocalPlayer;
        bool ok = Hutch.HostSendFromTrap(Permissions.LocalClientId, rabid, pc != null ? pc.transform.position : Vector3.zero);
        Debug.Log("[RaccoonCalling] add raccoon: " + ok);
    }

    [MenuItem(Menu + "Put every adult on the crew")]
    static void AllCrew()
    {
        if (!Ready(out string id)) return;
        Hutch.HostEdit(id, h => { foreach (var r in h.raccoons) if (r.Available) r.job = (int)HutchJob.Crew; return true; });
    }

    [MenuItem(Menu + "A day passes in my hutch (food, kits, growing up)")]
    static void Day()
    {
        if (!Ready(out string id)) return;
        var news = new System.Collections.Generic.List<string>();
        int level = Hutch.LocalLevel(CallingIds.CritterHutch);
        Hutch.HostEdit(id, h => { HutchRules.AdvanceDay(h, Mathf.Max(1, level), NightDice.Host.Range, news); return true; });
        Debug.Log("[RaccoonCalling] a day passed: " + (news.Count == 0 ? "nothing happened" : string.Join(" ", news)));
    }

    [MenuItem(Menu + "Crew sweeps now")]
    static void SweepNow()
    {
        if (!Ready(out _)) return;
        Debug.Log("[RaccoonCalling] " + RaccoonCrew.DebugSweepNow());
    }

    [MenuItem(Menu + "Give myself rabies now")]
    static void Rabies()
    {
        if (!Application.isPlaying) return;
        GameSession.RestoreRabies(GameClock.TotalHours);
    }

    [MenuItem(Menu + "Log my hutch")]
    static void Log()
    {
        if (!Application.isPlaying) return;
        var h = Hutch.Local;
        Debug.Log($"[RaccoonCalling] standing {RaccoonStanding.For(Permissions.LocalClientId)}; hutch ({h.Encode().Length} chars): {h.Encode()}");
    }
}
