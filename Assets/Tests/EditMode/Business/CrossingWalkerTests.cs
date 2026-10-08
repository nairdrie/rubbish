using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class CrossingWalkerTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object Get(object o, string name) => o.GetType().GetProperty(name).GetValue(o);
    static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name).Invoke(o, args);
    object body;
    [TearDown] public void Cleanup() { if (body != null) Call(body, "Dispose"); body = null; }
    void Create(bool guard)
    {
        body = Activator.CreateInstance(Runtime("CrossingWalker"), 0, guard);
        Call(body, "Schedule", true, 123L, new Vector3(18000, 0, 18000), Quaternion.identity);
    }
    object Hit(double secondsAgo = 0)
    {
        object h = Activator.CreateInstance(Runtime("NpcHit"));
        double now = (double)Runtime("GameClock").GetProperty("TotalHours").GetValue(null);
        void Set(string name, object value) => h.GetType().GetField(name).SetValue(h, value);
        Set("target", Get(body, "Id"));
        Set("kind", Enum.Parse(Runtime("NpcHitKind"), "Flatten"));
        Set("atHours", now - secondsAgo / 50.0);
        Set("point", Get(body, "Position"));
        Set("launch", new Vector3(6, 4, 0));
        return h;
    }
    [TestCase(false)] [TestCase(true)]
    public void CrossingChildrenAndGuardsHaveHittableRuleBasedBoxes(bool guard)
    {
        Create(guard);
        Assert.IsTrue((bool)Get(body, "Hittable"));
        Assert.AreEqual("Person", Get(body, "BodyKind").ToString());
        var half = (Vector3)Get(body, "HalfExtents");
        Assert.Greater(half.x, .2f);
        Assert.That(half.y, Is.EqualTo(guard ? .88f : .55f).Within(.001));
        Assert.IsFalse(typeof(Component).IsAssignableFrom(body.GetType()), "Rule bodies don't become rigidbody roadblocks.");
    }
    [Test]
    public void HitPlaysThenRecoversInsteadOfKillingOrContinuouslyRelauching()
    {
        Create(false);
        Call(body, "Apply", Hit());
        Assert.IsTrue((bool)Get(body, "Reacting"));
        Assert.IsFalse((bool)Get(body, "Hittable"));
        var h = Hit(8);
        Runtime("CrossingWalker").GetField("hit", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(body, h);
        Call(body, "Schedule", true, 123L, new Vector3(18001, 0, 18000), Quaternion.identity);
        Assert.IsFalse((bool)Get(body, "Reacting"));
        Assert.IsTrue((bool)Get(body, "Hittable"));
        Assert.AreEqual(new Vector3(18001, 0, 18000), Get(body, "Position"));
    }
    [Test]
    public void ExpiredSnapshotDoesNotRestartKnockdownAndNextGroupDoesNotInheritIt()
    {
        Create(true);
        Call(body, "Apply", Hit(8));
        Assert.IsFalse((bool)Get(body, "Reacting"));
        Call(body, "Apply", Hit());
        Assert.IsTrue((bool)Get(body, "Reacting"));
        Call(body, "Schedule", true, 124L, new Vector3(18002, 0, 18000), Quaternion.identity);
        Assert.IsFalse((bool)Get(body, "Reacting"));
        Assert.IsTrue((bool)Get(body, "Hittable"));
    }
    [Test]
    public void SleepingCrossingUnregistersItsBodies()
    {
        Create(false);
        var id = Get(body, "Id");
        var bodies = Runtime("LifeBodies");
        Assert.AreSame(body, bodies.GetMethod("Find").Invoke(null, new[] { id }));
        Call(body, "Dispose");
        Assert.IsNull(bodies.GetMethod("Find").Invoke(null, new[] { id }));
        Assert.IsFalse((bool)Get(body, "Hittable"));
    }
}
