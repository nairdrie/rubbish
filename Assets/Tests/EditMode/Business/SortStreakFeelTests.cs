using System;
using System.Reflection;
using NUnit.Framework;

/// <summary>The sort-streak sounds and melody (Docs/Progression.md §3.6): pure rendering and maths, no scene needed.</summary>
public class SortStreakFeelTests
{
    const BindingFlags Pub = BindingFlags.Public | BindingFlags.Static;
    static Type Synth => Type.GetType("FeelSfxSynth, Assembly-CSharp", true);
    static Type Hud => Type.GetType("SortStreakHud, Assembly-CSharp", true);

    static float[] Render(string id) => (float[])Synth.GetMethod("Render", Pub).Invoke(null, new object[] { id });
    static float Pitch(int streak) => (float)Hud.GetMethod("NotePitch", Pub).Invoke(null, new object[] { streak });

    [TestCase("streak_note", 0.25f, 0.6f)]
    [TestCase("streak_up", 0.5f, 1.2f)]
    [TestCase("streak_max", 1.2f, 2.5f)]
    [TestCase("streak_lost", 0.3f, 0.9f)]
    [TestCase("streak_cool", 0.1f, 0.4f)]
    public void EveryStreakSoundRendersCleanAndTheRightLength(string id, float minSeconds, float maxSeconds)
    {
        Assert.Contains(id, (string[])Synth.GetField("KnownIds", Pub).GetValue(null), "registered, or it would play as a generic blip");
        float[] data = Render(id);
        float seconds = data.Length / 44100f;
        Assert.That(seconds, Is.InRange(minSeconds, maxSeconds), id + " length");
        float peak = 0f;
        foreach (float x in data)
        {
            Assert.IsFalse(float.IsNaN(x) || float.IsInfinity(x), id + " has a bad sample");
            peak = Math.Max(peak, Math.Abs(x));
        }
        Assert.That(peak, Is.InRange(0.3f, 1.0f), id + " peak (audible, never clipping)");
        Assert.That(Math.Abs(data[0]), Is.LessThan(0.05f), id + " starts without a click");
        Assert.That(Math.Abs(data[data.Length - 1]), Is.LessThan(0.01f), id + " ends without a click");
    }

    [Test]
    public void TheMelodyClimbsTheFirstElevenNotesThenLoopsTheTopFive()
    {
        float last = 0f;
        for (int streak = 6; streak < 6 + 11; streak++)
        {
            float p = Pitch(streak);
            Assert.Greater(p, last, $"note {streak - 5} climbs");
            last = p;
        }
        Assert.AreEqual(Pitch(12), Pitch(17), 1e-4f, "past the top it restarts the top five (notes 7-11)");
        Assert.AreEqual(Pitch(16), Pitch(21), 1e-4f, "and stays inside them forever");
    }

    [Test]
    public void TheMelodyStaysInsideTheSoundPoolsPitchRange()
    {
        // FeelSfxPlayer clamps pitch to 0.1..3; the top note must not be flattened into a repeat of the one below it
        for (int streak = 1; streak <= 500; streak++)
        {
            float p = Pitch(streak);
            Assert.That(p, Is.InRange(0.69f, 2.9f), "streak " + streak);
        }
        Assert.AreEqual(0.7f, Pitch(6), 1e-4f);
        Assert.AreEqual(0.7f * 4f, Pitch(6 + 10), 1e-3f, "two octaves above the first note");
    }
}
