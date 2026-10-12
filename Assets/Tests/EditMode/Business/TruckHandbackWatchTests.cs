using NUnit.Framework;
using UnityEngine;

public class TruckHandbackWatchTests
{
    static readonly Vector3 Parked = new Vector3(12f, 0.4f, -30f);
    static readonly Quaternion Heading = Quaternion.Euler(0f, 35f, 0f);

    static TruckHandbackWatch Started()
    {
        var w = new TruckHandbackWatch();
        w.Begin(0f, Parked, Heading);
        return w;
    }

    [Test]
    public void TruckGoesBackOnlyAfterHoldingStillForTheWholeWindow()
    {
        var w = Started();
        for (float t = 0.05f; t < TruckHandbackWatch.StillSeconds - 0.01f; t += 0.05f)
            Assert.IsFalse(w.Tick(t, Parked, Heading), $"too early at {t:0.00} s");
        Assert.IsTrue(w.Tick(TruckHandbackWatch.StillSeconds, Parked, Heading));
        Assert.IsFalse(w.Active);
        Assert.IsFalse(w.Tick(TruckHandbackWatch.StillSeconds + 0.1f, Parked, Heading), "answers true once");
    }

    [Test]
    public void ARollingTruckRestartsTheWaitUntilItStops()
    {
        var w = Started();
        Vector3 p = Parked;
        float t = 0f;
        for (int i = 0; i < 20; i++)   // braking from 3 m/s to about 0.5 m/s, sampled at 20 Hz
        {
            t += 0.05f;
            p += Vector3.forward * (0.15f - i * 0.0065f);
            Assert.IsFalse(w.Tick(t, p, Heading), $"still rolling at {t:0.00} s");
        }
        float stopped = t;
        Assert.IsFalse(w.Tick(stopped + TruckHandbackWatch.StillSeconds - 0.05f, p, Heading));
        Assert.IsTrue(w.Tick(stopped + TruckHandbackWatch.StillSeconds + 0.01f, p, Heading));
    }

    [Test]
    public void SuspensionSettlingUnderTheLimitsCountsAsStill()
    {
        var w = Started();
        Vector3 bob = Vector3.up * (TruckHandbackWatch.StillMetres * 0.6f);
        Quaternion tilt = Heading * Quaternion.Euler(TruckHandbackWatch.StillDegrees * 0.6f, 0f, 0f);
        Assert.IsFalse(w.Tick(0.2f, Parked + bob, tilt));
        Assert.IsFalse(w.Tick(0.4f, Parked - bob, Heading));
        Assert.IsTrue(w.Tick(TruckHandbackWatch.StillSeconds, Parked + bob, tilt));
    }

    [Test]
    public void TurningInPlaceRestartsTheWait()
    {
        var w = Started();
        Quaternion turned = Heading * Quaternion.Euler(0f, TruckHandbackWatch.StillDegrees * 3f, 0f);
        Assert.IsFalse(w.Tick(0.3f, Parked, turned));
        Assert.IsFalse(w.Tick(0.3f + TruckHandbackWatch.StillSeconds - 0.01f, Parked, turned));
        Assert.IsTrue(w.Tick(0.3f + TruckHandbackWatch.StillSeconds + 0.01f, Parked, turned));
    }

    [Test]
    public void ATruckThatNeverSettlesStillGoesBackAfterTheLimit()
    {
        var w = Started();
        Vector3 p = Parked;
        float t = 0f;
        while (t < TruckHandbackWatch.MaxSeconds - 0.1f)
        {
            t += 0.1f;
            p += Vector3.right * 0.05f;   // sliding down a slope at 0.5 m/s
            Assert.IsFalse(w.Tick(t, p, Heading), $"handed back early at {t:0.0} s");
        }
        Assert.IsTrue(w.Tick(TruckHandbackWatch.MaxSeconds, p + Vector3.right * 0.05f, Heading));
    }

    [Test]
    public void CancelledOrNeverStartedWatchesNeverHandBack()
    {
        var idle = new TruckHandbackWatch();
        Assert.IsFalse(idle.Active);
        Assert.IsFalse(idle.Tick(TruckHandbackWatch.MaxSeconds * 2f, Parked, Heading));

        var w = Started();
        Assert.IsTrue(w.Active);
        w.Cancel();
        Assert.IsFalse(w.Active);
        Assert.IsFalse(w.Tick(TruckHandbackWatch.MaxSeconds * 2f, Parked, Heading));
    }

    [Test]
    public void ANonFinitePoseNeverCountsAsStanding()
    {
        var w = Started();
        var nan = new Vector3(float.NaN, 0f, 0f);
        for (float t = 0.1f; t < TruckHandbackWatch.MaxSeconds - 0.05f; t += 0.1f)
            Assert.IsFalse(w.Tick(t, nan, Heading), $"handed back on a NaN pose at {t:0.0} s");
        Assert.IsTrue(TruckHandbackWatch.Moved(Parked, Heading, nan, Heading));
        Assert.IsFalse(TruckHandbackWatch.Moved(Parked, Heading, Parked, Heading));
    }
}
