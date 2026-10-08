using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Interrupted pocket-interior jobs must not strand Magnus behind stale coroutine flags.</summary>
public class MagnusWorkRecoveryTests
{
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    readonly List<GameObject> made = new();
    GameObject Make(string name)
    {
        var go = new GameObject(name);
        go.SetActive(false);
        made.Add(go);
        return go;
    }
    static FieldInfo Field(object obj, string name)
    {
        Assert.NotNull(obj, "Fixture component missing before looking up " + name);
        for (Type type = obj.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        Assert.Fail(obj.GetType().FullName + " has no field " + name);
        return null;
    }
    static void Set(object obj, string name, object value) => Field(obj, name).SetValue(obj, value);
    static object Get(object obj, string name) => Field(obj, name).GetValue(obj);
    static object Call(object obj, string name, params object[] args)
    {
        Assert.NotNull(obj, "Fixture component missing before invoking " + name);
        for (Type type = obj.GetType(); type != null; type = type.BaseType)
        {
            var method = type.GetMethod(name, Instance | BindingFlags.DeclaredOnly);
            if (method != null) return method.Invoke(obj, args);
        }
        Assert.Fail(obj.GetType().FullName + " has no method " + name);
        return null;
    }
    static bool Flag(object obj, string name)
    {
        Assert.NotNull(obj, "Fixture component missing before reading " + name);
        var property = obj.GetType().GetProperty(name, Instance);
        Assert.NotNull(property, obj.GetType().FullName + "." + name);
        return (bool)property.GetValue(obj);
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = made.Count - 1; i >= 0; i--) if (made[i] != null) Object.DestroyImmediate(made[i]);
        made.Clear();
    }

    [Test]
    public void DisabledRailTripClearsDrivingAndAllowsRecoveryTrip()
    {
        var track = Make("cart").AddComponent(Runtime("MagnusTrack"));
        var stop = Make("sort stop").transform;
        stop.position = Vector3.right * 3f;
        var trip = (IEnumerator)Call(track, "DriveTo", stop);
        Assert.IsTrue(trip.MoveNext());
        Assert.IsTrue(Flag(track, "IsDriving"));
        Call(track, "OnDisable");
        Assert.IsFalse(Flag(track, "IsDriving"), "Unity stops a disabled object's coroutines, not its C# IsDriving flag.");
        ((IDisposable)trip).Dispose();
        var recovery = (IEnumerator)Call(track, "DriveTo", stop);
        Assert.IsTrue(recovery.MoveNext(), "A later recovery trip must not immediately yield-break as already driving.");
        Assert.IsTrue(Flag(track, "IsDriving"));
        ((IDisposable)recovery).Dispose();
        Assert.IsFalse(Flag(track, "IsDriving"));
    }

    [Test]
    public void DisposingAnInterruptedDriveAlwaysClearsItsBusyFlag()
    {
        var track = Make("cart").AddComponent(Runtime("MagnusTrack"));
        var stop = Make("stop").transform;
        stop.position = Vector3.forward;
        var trip = (IEnumerator)Call(track, "DriveTo", stop);
        Assert.IsTrue(trip.MoveNext());
        ((IDisposable)trip).Dispose();
        Assert.IsFalse(Flag(track, "IsDriving"));
    }

    Component Arm()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Magnus.prefab");
        Assert.NotNull(prefab);
        var go = Object.Instantiate(prefab);
        go.SetActive(false);
        made.Add(go);
        // Magnus.prefab contains the original IK/API scripts only. Shop scene setup adds MagnusArm.
        // Reproduce that wiring explicitly while inactive; tests should not run scene setup/chess/network logic.
        var arm = go.GetComponent(Runtime("MagnusArm")) ?? go.AddComponent(Runtime("MagnusArm"));
        Assert.NotNull(arm);
        var basePivot = go.transform.Find("BasePivot");
        var shoulder = basePivot.Find("Shoulder");
        var elbow = shoulder.Find("Elbow");
        var wrist = elbow.Find("Wrist");
        Set(arm, "basePivot", basePivot);
        Set(arm, "shoulder", shoulder);
        Set(arm, "elbow", elbow);
        Set(arm, "wrist", wrist);
        Set(arm, "clawCenter", wrist.Find("Claw Center"));
        Set(arm, "pointer", wrist.Find("Pointer"));
        Set(arm, "thumb", wrist.Find("Thumb"));
        return arm;
    }

    IList Steps() => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Runtime("MagnusArm+Step")));

    [Test]
    public void DisableCancelsQueuedWorkWithoutCreditingUnfinishedDeliveries()
    {
        var arm = Arm();
        var piece = Make("held chess piece").transform;
        Vector3 square = new Vector3(2f, 0f, 3f);
        piece.position = new Vector3(5f, 5f, 5f);
        bool credited = false;
        object step = Activator.CreateInstance(Runtime("MagnusArm+Step"));
        Set(step, "piece", piece);
        Set(step, "to", Vector3.one * 10f);
        Set(step, "onPlaced", (Action)(() => credited = true));
        var steps = Steps(); steps.Add(step);
        Set(arm, "currentSteps", steps);
        Set(arm, "heldPiece", piece);
        Set(arm, "heldStartPosition", square);
        var gestures = Get(arm, "gestureQueue");
        gestures.GetType().GetMethod("Enqueue").Invoke(gestures, new[] { Enum.ToObject(Runtime("MagnusArm+Gesture"), 0) });

        Call(arm, "OnDisable");

        Assert.IsFalse(Flag(arm, "IsBusy"));
        Assert.IsFalse(Flag(arm, "HasPendingPlans"));
        Assert.IsNull(Get(arm, "heldPiece"));
        Assert.AreEqual(0, gestures.GetType().GetProperty("Count").GetValue(gestures));
        Assert.AreEqual(square, piece.position, "An interrupted chess move returns to its prior square, not suspended in the claw.");
        Assert.IsFalse(credited, "Disabling a lot must not complete a delivery or award money.");
    }

    [Test]
    public void EmptyPlanCompletesWithoutStartingADeadRunner()
    {
        var arm = Arm();
        Call(arm, "PlayPlan", 17, Steps());
        Assert.IsFalse(Flag(arm, "IsBusy"));
        Assert.IsFalse(Flag(arm, "HasPendingPlans"));
        Assert.AreEqual(17, arm.GetType().GetProperty("LastCompletedPlanId").GetValue(arm));
    }

    [Test]
    public void QueueRunnerYieldsBeforeItCanClearItsOwnNewHandle()
    {
        var arm = Arm();
        var runner = (IEnumerator)Call(arm, "RunQueue");
        Assert.IsTrue(runner.MoveNext(), "StartCoroutine must return before RunQueue can set runner=null.");
        Assert.IsFalse(runner.MoveNext());
        Assert.IsFalse(Flag(arm, "IsBusy"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChessSnapshotsFlushChessMovesButNeverSortingPlans(bool atChess)
    {
        var arm = Arm();
        var track = Make("cart").AddComponent(Runtime("MagnusTrack"));
        var stop = Make("chess stop").transform;
        Set(track, "chessStop", stop);
        track.transform.position = atChess ? stop.position : stop.position + Vector3.right * 3f;
        var board = Make("board").AddComponent(Runtime("DepotChessBoard"));
        Set(board, "arm", arm);
        Set(board, "track", track);
        Set(board, "pieces", Array.CreateInstance(Runtime("DepotChessPiece"), 0));
        var state = Activator.CreateInstance(Runtime("DepotChessNet+ChessState"));
        Set(state, "locations", Array.Empty<byte>());
        Set(state, "types", Array.Empty<byte>());
        bool delivered = false;
        var piece = Make(atChess ? "chess piece" : "unsorted item").transform;
        object step = Activator.CreateInstance(Runtime("MagnusArm+Step"));
        Set(step, "piece", piece);
        Set(step, "to", Vector3.one * 10f);
        Set(step, "onPlaced", (Action)(() => delivered = true));
        var steps = Steps(); steps.Add(step);
        Set(arm, "currentSteps", steps);

        Call(board, "Apply", state, false);

        Assert.AreEqual(atChess, delivered, "A spectator's board snapshot cannot grant an unfinished sort delivery.");
        Assert.AreEqual(!atChess, Flag(arm, "HasPendingPlans"));
        Assert.AreEqual(atChess ? Vector3.one * 10f : Vector3.zero, piece.position);
    }

    [TestCase(0f, 0f, true)]
    [TestCase(.6f, 1.4f, true)]
    [TestCase(0f, 1.5f, false)]
    [TestCase(0f, 20f, false)]
    [TestCase(0f, -20f, false)]
    [TestCase(1.2f, 0f, false)]
    public void SortStopBlockersMustBeInTheSameRoom(float x, float height, bool expected)
    {
        var stop = new Vector3(103f, -20f, -399f);
        var player = stop + new Vector3(x, height, 0f);
        Assert.AreEqual(expected, Runtime("MagnusSorter").GetMethod("BlocksSortStop").Invoke(null, new object[] { player, stop, 1.1f }));
    }

    [TestCase(false, 6f, 22f, "not HEAVY ones", "06:00–22:00")]
    [TestCase(true, 0f, 24f, "any bag", "24h")]
    public void WorkHintExplainsPreparationAndHoursWithoutGrantingAnotherUpgrade(bool heavyToo, float start, float end, string work, string hours)
    {
        string hint = (string)Runtime("MagnusSorter").GetMethod("ReadyHint").Invoke(null, new object[] { heavyToo, start, end });
        StringAssert.Contains(work, hint);
        StringAssert.Contains("by the table", hint, "from his first step he rips bags within reach; nobody has to rip them for him");
        StringAssert.DoesNotContain("tipping floor", hint, "he can never take from the dump pile");
        StringAssert.Contains("step away", hint);
        StringAssert.Contains(hours, hint);
    }
}
