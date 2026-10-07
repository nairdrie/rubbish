using System;
using NUnit.Framework;

public class SortComboAudioTests
{
    static float Pitch(string method, int streak) => (float)Type.GetType("SortCombo, Assembly-CSharp", true)
        .GetMethod(method).Invoke(null, new object[] { streak });

    [TestCase(1, 0)]
    [TestCase(2, 2)]
    [TestCase(3, 4)]
    [TestCase(4, 5)]
    [TestCase(5, 7)]
    public void CorrectSortsClimbMusicallyWithoutTheOldShrillRegister(int streak, int semitones)
    {
        Assert.That(Pitch("DingPitchFor", streak), Is.EqualTo(.75f * Math.Pow(2, semitones / 12.0)).Within(.0001));
    }

    [TestCase(5)]
    [TestCase(25)]
    [TestCase(int.MaxValue)]
    public void LongStreaksCapBothLayersNotJustTheDing(int streak)
    {
        Assert.That(Pitch("DingPitchFor", streak), Is.EqualTo(Pitch("DingPitchFor", 5)));
        Assert.That(Pitch("DingPitchFor", streak), Is.LessThan(1.13f));
        Assert.That(Pitch("FlourishPitchFor", streak), Is.EqualTo(Pitch("DingPitchFor", streak) * .85f).Within(.0001));
        Assert.That(Pitch("FlourishPitchFor", streak), Is.LessThan(.96f));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void RobotOrEmptyStreakUsesTheGentleStartingNote(int streak)
    {
        Assert.That(Pitch("DingPitchFor", streak), Is.EqualTo(.75f));
    }
}
