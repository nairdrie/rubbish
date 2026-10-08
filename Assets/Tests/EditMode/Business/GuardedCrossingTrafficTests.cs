using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class GuardedCrossingTrafficTests
{
    const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static readonly Vector3 From = new Vector3(0f, .15f, -7.4f);
    static readonly Vector3 To = new Vector3(0f, .15f, 7.4f);

    static object Path(params Vector3[] points)
    {
        var type = Runtime("CarPath");
        var path = Activator.CreateInstance(type);
        var arcs = new float[points.Length];
        for (int i = 1; i < points.Length; i++) arcs[i] = arcs[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        type.GetField("pts").SetValue(path, points);
        type.GetField("s").SetValue(path, arcs);
        type.GetField("Length").SetValue(path, arcs[arcs.Length - 1]);
        return path;
    }

    static float Gap(object path, float arc, float halfLength = 2f, float look = 60f, bool reversedCrossing = false) =>
        (float)Runtime("GuardedCrossingRules").GetMethod("SegmentStopGap", PublicStatic).Invoke(null,
            new[] { (object)(reversedCrossing ? To : From), reversedCrossing ? From : To, path, arc, halfLength, look });

    [TestCase(false)]
    [TestCase(true)]
    public void BothTrafficDirectionsStopWithTheNoseBeforeTheZebra(bool reverse)
    {
        var path = reverse
            ? Path(new Vector3(40, 0, 2.5f), new Vector3(-40, 0, 2.5f))
            : Path(new Vector3(-40, 0, -2.5f), new Vector3(40, 0, -2.5f));
        Assert.That(Gap(path, 10f), Is.EqualTo(26.2f).Within(.001f),
            "The crossing is 30m from the car center; stop the 2m-long nose another 1.8m short.");
    }

    [Test]
    public void CrossingEndpointOrderDoesNotChangeTheHoldLine()
    {
        var path = Path(new Vector3(-40, 0, -2.5f), new Vector3(40, 0, -2.5f));
        Assert.That(Gap(path, 10f, reversedCrossing: true), Is.EqualTo(Gap(path, 10f)).Within(.001f));
    }

    [Test]
    public void LargerCarStopsItsCenterEarlierSoItsBumperStillClearsTheCrossing()
    {
        var path = Path(new Vector3(-40, 0, -2.5f), new Vector3(40, 0, -2.5f));
        Assert.That(Gap(path, 10f, 3f), Is.EqualTo(Gap(path, 10f, 1f) - 2f).Within(.001f));
    }

    [Test]
    public void CarAlreadyPastTheHoldLineCanClearInsteadOfStoppingAcrossChildren()
    {
        var path = Path(new Vector3(-40, 0, -2.5f), new Vector3(40, 0, -2.5f));
        Assert.That(Gap(path, 38f), Is.EqualTo(float.PositiveInfinity));
        Assert.That(Gap(path, 50f), Is.EqualTo(float.PositiveInfinity));
    }

    [Test]
    public void UnrelatedParallelRoadAndRoadBeyondCrossingEndpointsAreNotBlocked()
    {
        Assert.That(Gap(Path(new Vector3(20, 0, -40), new Vector3(20, 0, 40)), 10f), Is.EqualTo(float.PositiveInfinity));
        Assert.That(Gap(Path(new Vector3(-40, 0, 20), new Vector3(40, 0, 20)), 10f), Is.EqualTo(float.PositiveInfinity));
    }

    [Test]
    public void TurnBeforeCrossingUsesDistanceAlongTheRouteInsteadOfStraightLineDistance()
    {
        var path = Path(new Vector3(-30, 0, -20), new Vector3(-30, 0, -2.5f), new Vector3(30, 0, -2.5f));
        Assert.That(Gap(path, 10f), Is.EqualTo(33.7f).Within(.001f));
    }

    [Test]
    public void NormalBrakeLookAheadCanMissAFarCrossingButCanonicalSnapIntervalFindsIt()
    {
        var path = Path(new Vector3(-100, 0, -2.5f), new Vector3(100, 0, -2.5f));
        Assert.That(Gap(path, 10f, look: 42f), Is.EqualTo(float.PositiveInfinity));
        Assert.That(Gap(path, 10f, look: 140f), Is.EqualTo(86.2f).Within(.001f),
            "A far traffic LOD/timetable correction cannot jump across a protected crossing.");
    }

    [Test]
    public void ZeroRemainingGapIsStillABlockingGate()
    {
        var path = Path(new Vector3(-40, 0, -2.5f), new Vector3(40, 0, -2.5f));
        Assert.That(Gap(path, 36.2f), Is.EqualTo(0f).Within(.001f));
    }
}
