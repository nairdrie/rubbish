using System;
using NUnit.Framework;

/// <summary>
/// B3: which identity a build uses (Docs/Accounts.md §1.2: fixed per build, never by whether Steam is running) and the
/// dev-name resolution order. Steam's running state isn't an input at all, which is the point.
/// </summary>
public class SteamPolicyTests
{
    static BuildFacts Editor(params string[] args) => new BuildFacts { isEditor = true, steamworksCompiled = true, args = args };
    static BuildFacts DevBuild(params string[] args) => new BuildFacts { isDevelopmentBuild = true, steamworksCompiled = true, args = args };
    static BuildFacts Playtest(params string[] args) => new BuildFacts { isDevelopmentBuild = true, isPlaytestBuild = true, steamworksCompiled = true, args = args };
    static BuildFacts Release(params string[] args) => new BuildFacts { steamworksCompiled = true, args = args };

    [Test]
    public void EditorIsDevUnlessTheDefineOptsIn()
    {
        Assert.AreEqual(IdentityMode.Dev, SteamPolicy.Decide(Editor()).Mode);
        Assert.AreEqual(IdentityMode.Dev, SteamPolicy.Decide(Editor("-steam")).Mode, "-steam doesn't flip the Editor");
        var f = Editor(); f.isPlaytestBuild = true;
        Assert.AreEqual(IdentityMode.Dev, SteamPolicy.Decide(f).Mode, "an active playtest profile doesn't flip the Editor");
        f = Editor(); f.editorSteamDefine = true;
        Assert.AreEqual(IdentityMode.Steam, SteamPolicy.Decide(f).Mode);
        f.isMppmClone = true;
        var d = SteamPolicy.Decide(f);
        Assert.AreEqual(IdentityMode.Dev, d.Mode, "MPPM clones never use Steam");
        Assert.IsTrue(d.SteamSkipped);
    }

    [Test]
    public void DevBuildsNeedTheFlag()
    {
        Assert.AreEqual(IdentityMode.Dev, SteamPolicy.Decide(DevBuild()).Mode);
        Assert.AreEqual(IdentityMode.Steam, SteamPolicy.Decide(DevBuild("-steam")).Mode);
        Assert.AreEqual(IdentityMode.Dev, SteamPolicy.Decide(DevBuild("-steam", "-nosteam")).Mode, "-nosteam wins");
        Assert.AreEqual(IdentityMode.Steam, SteamPolicy.Decide(DevBuild("-STEAM")).Mode, "flags are case-insensitive");
    }

    [Test]
    public void PlaytestBuildsUseSteamByThemselves()
    {
        Assert.AreEqual(IdentityMode.Steam, SteamPolicy.Decide(Playtest()).Mode);
        Assert.AreEqual(SteamPolicy.PlaytestDefine, SteamPolicy.Decide(Playtest()).Reason);
        var d = SteamPolicy.Decide(Playtest("-nosteam"));
        Assert.AreEqual(IdentityMode.Dev, d.Mode);
        Assert.IsTrue(d.SteamSkipped);
        var releaseConfig = Playtest(); releaseConfig.isDevelopmentBuild = false;
        Assert.AreEqual(IdentityMode.Steam, SteamPolicy.Decide(releaseConfig).Mode);
        Assert.AreEqual(IdentityMode.Dev, SteamPolicy.Decide(new BuildFacts { isPlaytestBuild = true, args = new[] { "-nosteam" } }).Mode,
                        "a release-config playtest still honours -nosteam");
    }

    [Test]
    public void ReleaseAlwaysUsesSteam()
    {
        Assert.AreEqual(IdentityMode.Steam, SteamPolicy.Decide(Release()).Mode);
        Assert.AreEqual(IdentityMode.Steam, SteamPolicy.Decide(Release("-nosteam")).Mode, "release ignores -nosteam");
    }

    [Test]
    public void BatchmodeNeverUsesSteam()
    {
        foreach (var f in new[] { DevBuild("-steam"), Playtest(), Release(), Editor() })
        {
            var b = f; b.isBatchmode = true; b.editorSteamDefine = true;
            var d = SteamPolicy.Decide(b);
            Assert.AreEqual(IdentityMode.Dev, d.Mode, b.ToString());
            Assert.IsTrue(d.SteamSkipped, b.ToString());
            Assert.AreEqual("batchmode", d.Reason);
        }
    }

    [Test]
    public void RestartAppOnlyInReleaseWithARealAppId()
    {
        Assert.IsFalse(SteamPolicy.ShouldRestartApp(false, false, 480));
        Assert.IsFalse(SteamPolicy.ShouldRestartApp(false, false, 0));
        Assert.IsFalse(SteamPolicy.ShouldRestartApp(false, true, 123456));
        Assert.IsFalse(SteamPolicy.ShouldRestartApp(true, false, 123456));
        Assert.IsTrue(SteamPolicy.ShouldRestartApp(false, false, 123456));
    }

    [Test]
    public void AuthMode()
    {
        Assert.AreEqual(SteamAuthMode.Enforce, SteamPolicy.AuthModeFor(Release()));
        Assert.AreEqual(SteamAuthMode.Enforce, SteamPolicy.AuthModeFor(Release("-steamauth", "off")), "release can't be talked out of it");
        Assert.AreEqual(SteamAuthMode.Warn, SteamPolicy.AuthModeFor(Playtest()));
        Assert.AreEqual(SteamAuthMode.Warn, SteamPolicy.AuthModeFor(DevBuild()));
        Assert.AreEqual(SteamAuthMode.Enforce, SteamPolicy.AuthModeFor(DevBuild("-steamauth", "enforce")));
        Assert.AreEqual(SteamAuthMode.Enforce, SteamPolicy.AuthModeFor(Playtest("-steamauth=release")));
        Assert.AreEqual(SteamAuthMode.Off, SteamPolicy.AuthModeFor(DevBuild("-steamauth", "off")));
        var releaseConfigPlaytest = Playtest(); releaseConfigPlaytest.isDevelopmentBuild = false;
        Assert.AreEqual(SteamAuthMode.Warn, SteamPolicy.AuthModeFor(releaseConfigPlaytest), "playtests warn in either config");
    }

    // ------------------------------------------------------------------ dev name order: -playerid, MPPM tag, editor, install id

    static string Name(string[] args, bool editor, bool clone, string tag, out DevNameSource src, Func<string> install = null) =>
        SteamPolicy.ResolveDevName(args, editor, clone, tag, install ?? (() => "install-0123456789abcdef"), out src);

    [Test]
    public void PlayerIdWinsEverything()
    {
        Assert.AreEqual("bot-2", Name(new[] { "-playerid", "dev:bot-2" }, true, true, "AutoJoin", out var src));
        Assert.AreEqual(DevNameSource.PlayerId, src);
        Assert.AreEqual("dot", Name(new[] { "-playerid=Dot" }, false, false, null, out src));
        Assert.AreEqual(DevNameSource.PlayerId, src);
        Assert.AreEqual("big-boss", Name(new[] { "-playerid", "Big Boss!" }, false, false, null, out _), "sanitised");
    }

    [Test]
    public void ThenTheMppmTagOnClones()
    {
        Assert.AreEqual("mppm-autojoin", Name(new string[0], true, true, "AutoJoin", out var src));
        Assert.AreEqual(DevNameSource.MppmTag, src);
        Assert.AreEqual("mppm", Name(new string[0], true, true, null, out src), "a clone without a tag");
        Assert.AreEqual("editor", Name(new string[0], true, false, "AutoHost", out src), "the main Editor ignores tags");
        Assert.AreEqual(DevNameSource.Editor, src);
    }

    [Test]
    public void ThenEditorThenInstallId()
    {
        Assert.AreEqual("editor", Name(new[] { "-projectPath", "/x" }, true, false, null, out var src));
        Assert.AreEqual(DevNameSource.Editor, src);
        bool asked = false;
        Assert.AreEqual("install-0123456789abcdef", Name(new string[0], false, false, null, out src, () => { asked = true; return "install-0123456789abcdef"; }));
        Assert.AreEqual(DevNameSource.InstallId, src);
        Assert.IsTrue(asked);
        asked = false;
        Name(new[] { "-playerid", "bot-1" }, false, false, null, out _, () => { asked = true; return "x"; });
        Assert.IsFalse(asked, "the install id file isn't touched when -playerid names us");
        Assert.AreEqual("install", Name(new string[0], false, false, null, out _, () => null), "a missing install id still gives a valid name");
    }

    [Test]
    public void ForgedSteamPlayerIdIsNotADevName()
    {
        var args = new[] { "-playerid", "steam:76561197960287930" };
        Assert.IsTrue(SteamPolicy.TryGetSpoofedSteamKey(args, false, out PlayerKey k));
        Assert.AreEqual("steam:76561197960287930", k.Value);
        Assert.IsFalse(SteamPolicy.TryGetSpoofedSteamKey(args, true, out _), "never in release builds");
        Assert.AreEqual("install-0123456789abcdef", Name(args, false, false, null, out _), "not used as a dev name");
        Assert.IsFalse(SteamPolicy.TryGetSpoofedSteamKey(new[] { "-playerid", "dev:bot-1" }, false, out _));
    }

    [Test]
    public void EveryResolvedNameMakesAValidKey()
    {
        foreach (var raw in new[] { "Editor", "BOT 1", "ünïcode", "a/../b", "..", new string('z', 80), "-", "mppm:AutoJoin" })
        {
            string n = Name(new[] { "-playerid", raw }, false, true, raw, out _);
            Assert.DoesNotThrow(() => PlayerKey.Dev(n), raw + " -> " + n);
            Assert.LessOrEqual(n.Length, PlayerKey.MaxDevNameLength);
        }
    }
}
