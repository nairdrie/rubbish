using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>
/// Settings → Audio → Music (Nick, 2026-10-12: "we need a music volume control separate from effects/ambience"): every music
/// source reads <c>GameSettings.MusicVolume</c>, which follows the Music setting and nothing else. Assembly-CSharp is reached by
/// reflection from the EditMode test assembly.
/// </summary>
public sealed class MusicVolumeSettingTests
{
    static Type Settings => Type.GetType("GameSettings, Assembly-CSharp", true);
    static Type SettingsData => Type.GetType("GameSettingsData, Assembly-CSharp", true);

    static object Audio(object data) => data.GetType().GetField("audio").GetValue(data);
    static void Put(object audio, string field, int value) => audio.GetType().GetField(field).SetValue(audio, value);
    static int Read(object audio, string field) => (int)audio.GetType().GetField(field).GetValue(audio);
    static float Volume(string property) => (float)Settings.GetProperty(property, BindingFlags.Public | BindingFlags.Static).GetValue(null);

    static object Install(int master, int sfx, int ambience, int music)
    {
        var data = Activator.CreateInstance(SettingsData);
        var audio = Audio(data);
        Put(audio, "master", master);
        Put(audio, "sfx", sfx);
        Put(audio, "ambience", ambience);
        Put(audio, "music", music);
        Settings.GetField("data", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, data);
        return audio;
    }

    [TearDown]
    public void TearDown()
    {
        Settings.GetMethod("Unload", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
    }

    [Test]
    public void MusicHasItsOwnVolumeAndTheOtherSlidersNeverMoveIt()
    {
        var audio = Install(master: 100, sfx: 20, ambience: 30, music: 80);
        Assert.AreEqual(0.8f, Volume("MusicVolume"), 1e-4f);
        Assert.AreEqual(0.2f, Volume("SfxVolume"), 1e-4f);
        Assert.AreEqual(0.3f, Volume("AmbienceVolume"), 1e-4f);

        Put(audio, "sfx", 0);
        Put(audio, "ambience", 0);
        Put(audio, "master", 10);
        Assert.AreEqual(0.8f, Volume("MusicVolume"), 1e-4f, "effects, ambience and master leave music alone");

        Put(audio, "music", 0);
        Put(audio, "sfx", 60);
        Put(audio, "ambience", 90);
        Assert.AreEqual(0f, Volume("MusicVolume"), 1e-4f, "music at 0 is silent however loud the rest is");
        Assert.AreEqual(0.6f, Volume("SfxVolume"), 1e-4f, "and turning music down leaves effects and ambience alone");
        Assert.AreEqual(0.9f, Volume("AmbienceVolume"), 1e-4f);
    }

    [Test]
    public void MusicDefaultsToSeventyAndTheSliderRangeIsClamped()
    {
        Assert.AreEqual(70, Read(Audio(Activator.CreateInstance(SettingsData)), "music"));

        var data = Activator.CreateInstance(SettingsData);
        Put(Audio(data), "music", 250);
        SettingsData.GetMethod("Sanitize").Invoke(data, null);
        Assert.AreEqual(100, Read(Audio(data), "music"));
        Put(Audio(data), "music", -5);
        SettingsData.GetMethod("Sanitize").Invoke(data, null);
        Assert.AreEqual(0, Read(Audio(data), "music"));
    }

    [Test]
    public void AnOlderFileWithoutAMusicValueStillReadsAsTheDefault()
    {
        var args = new object[] { "{\"schema\":1,\"audio\":{\"master\":37}}", null };
        var data = Settings.GetMethod("FromJson", BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
        Assert.AreEqual(70, Read(Audio(data), "music"));
        Assert.AreEqual(37, Read(Audio(data), "master"));
    }
}
