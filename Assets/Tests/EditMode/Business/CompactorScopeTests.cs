using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Reflection fixtures exercise runtime selection without a live session, saves, or gameplay outcomes.</summary>
public class CompactorScopeTests
{
    const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static Type Compactor => Runtime("Compactor");
    static Type Carryable => Runtime("Carryable");
    static Type Row => Runtime("DepotRowNet");
    readonly List<GameObject> made = new List<GameObject>();
    readonly List<Component> carryables = new List<Component>();
    readonly List<Component> conveyors = new List<Component>();
    Component fakeRow;
    object oldRow;

    GameObject Go(string name, Transform parent = null)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        made.Add(go);
        return go;
    }

    static void Set(object obj, string field, object value) => obj.GetType().GetField(field, InstanceFields).SetValue(obj, value);
    static void Property(object obj, string property, object value) => obj.GetType().GetProperty(property, InstanceFields).SetValue(obj, value);
    static Vector3 Origin(int lot) => (Vector3)Runtime("DepotRowLayout").GetMethod("InteriorOrigin").Invoke(null, new object[] { lot });
    static bool Available(Component c) => (bool)Compactor.GetMethod("AvailableForCompact").Invoke(null, new object[] { c });

    Component Bag(Vector3 at)
    {
        var go = Go("CompactorTestBag");
        go.transform.position = at;
        var bag = go.AddComponent(Runtime("TrashBag"));
        go.GetComponent<Rigidbody>().useGravity = false;
        // EditMode tests do not depend on MonoBehaviour lifecycle callbacks being executed by the editor.
        Carryable.GetMethod("OnEnable", InstanceFields).Invoke(bag, null);
        carryables.Add(bag);
        return bag;
    }

    static void State(Component c, string name) => Carryable.GetMethod("NetSetState").Invoke(c,
        new[] { Enum.Parse(Runtime("CarryState"), name) });

    Component NetworkCopy(Component bag, string phase = "Rest")
    {
        var nc = bag.gameObject.AddComponent(Runtime("NetCarry"));
        var state = Activator.CreateInstance(Runtime("CarryNetState"));
        Set(state, "phase", Convert.ToByte(Enum.Parse(Runtime("HandsPhase"), phase)));
        Set(nc, "St", state);
        return nc;
    }

    void EnableDepotGeometry()
    {
        oldRow = Row.GetProperty("Instance").GetValue(null);
        var root = Go("CompactorTestRow");
        root.SetActive(false);
        fakeRow = root.AddComponent(Row);
        // Only the read-only Active/geometry contract is needed: no network spawn, callbacks or transport.
        typeof(NetworkBehaviour).GetProperty("IsSpawned").SetValue(fakeRow, true);
        object variable = Row.GetField("oneWorld", InstanceFields).GetValue(fakeRow);
        variable.GetType().GetField("m_InternalValue", InstanceFields).SetValue(variable, true);
        Row.GetProperty("Instance").SetValue(null, fakeRow);
    }

    Component DepotCompactor(int lot)
    {
        var root = Go("CompactorTestLot" + lot);
        root.SetActive(false);
        root.transform.position = Origin(lot);
        var depot = root.AddComponent(Runtime("DepotLot"));
        Set(depot, "lot", lot);
        var interior = Go("Interior", root.transform);
        Set(depot, "interior", interior);
        var compactor = Go("Compactor", interior.transform).AddComponent(Compactor);
        // Real work requests target an active interior. GetComponentInParent deliberately ignores inactive parents.
        root.SetActive(true);
        return compactor;
    }

    Component AttachToConveyor(Component compact, Component bag, string stage)
    {
        // "Belt" = riding the Bag Belt; "Rack" = hopping off the slide into the queue rack. (The Direct Feed chute and
        // the auto-slitter are gone: the conveyor only carries bags.)
        string type = stage == "Belt" ? "ConveyorLine" : "ConveyorTray";
        var machine = Go("CompactorTest" + stage, compact.transform.parent).AddComponent(Runtime(type));
        machine.GetType().GetMethod("OnEnable", InstanceFields).Invoke(machine, null);
        conveyors.Add(machine);
        State(bag, "Thrown");
        bag.GetComponent<Rigidbody>().isKinematic = true;
        if (stage == "Belt")
        {
            var carrier = Activator.CreateInstance(Runtime("ConveyorLine").GetNestedType("Carrier", BindingFlags.NonPublic));
            Set(carrier, "bag", bag);
            ((IList)machine.GetType().GetField("carriers", InstanceFields).GetValue(machine)).Add(carrier);
        }
        else
        {
            var arriving = Array.CreateInstance(Runtime("TrashBag"), 1);
            arriving.SetValue(bag, 0);
            Set(machine, "arriving", arriving);
        }
        return machine;
    }

    static IList Collect(Component compact, bool bulk = true) =>
        (IList)Compactor.GetMethod("CollectForCompact").Invoke(compact, new object[] { bulk });

    static bool Owns(Component machine, Component bag) =>
        (bool)machine.GetType().GetMethod("Owns").Invoke(machine, new object[] { bag });

    static void Detach(Component bag) => Runtime("ConveyorStock").GetMethod("Detach").Invoke(null, new object[] { bag });

    [TearDown]
    public void TearDown()
    {
        foreach (var c in carryables)
            if (c != null) Carryable.GetMethod("OnDisable", InstanceFields).Invoke(c, null);
        carryables.Clear();
        foreach (var machine in conveyors)
            if (machine != null) machine.GetType().GetMethod("OnDisable", InstanceFields).Invoke(machine, null);
        conveyors.Clear();
        if (fakeRow != null)
        {
            typeof(NetworkBehaviour).GetProperty("IsSpawned").SetValue(fakeRow, false);
            Row.GetProperty("Instance").SetValue(null, oldRow);
        }
        for (int i = made.Count - 1; i >= 0; i--) if (made[i] != null) Object.DestroyImmediate(made[i]);
        made.Clear();
        fakeRow = null;
    }

    [Test]
    public void LocalHeldConsumedAndMachineCarriedBagsAreNotAvailable()
    {
        var bag = Bag(Vector3.zero);
        Assert.IsTrue(Available(bag));
        State(bag, "Carried");
        Assert.IsFalse(Available(bag));
        State(bag, "Consumed");
        Assert.IsFalse(Available(bag));
        State(bag, "Thrown");
        bag.GetComponent<Rigidbody>().isKinematic = true;
        Assert.IsFalse(Available(bag), "generic availability cannot authorize unknown scripted machinery");
        bag.gameObject.SetActive(false);
        Assert.IsFalse(Available(bag));
    }

    [Test]
    public void GuestKinematicRestCopiesRemainAvailableButHostMachineMotionDoesNot()
    {
        var bag = Bag(Vector3.zero);
        NetworkCopy(bag);
        bag.GetComponent<Rigidbody>().isKinematic = true;
        Property(bag, "NetRemote", true);
        Assert.IsTrue(Available(bag), "guest rest copies are necessarily kinematic");
        Property(bag, "NetRemote", false);
        State(bag, "Thrown");
        Assert.IsFalse(Available(bag), "host SetInFlight can leave the network record at Rest");
    }

    [TestCase("Flight")]
    [TestCase("Held")]
    [TestCase("Tug")]
    [TestCase("Gone")]
    public void NonRestNetworkPhasesAreNeverCaptured(string phase)
    {
        var bag = Bag(Vector3.zero);
        NetworkCopy(bag, phase);
        Assert.IsFalse(Available(bag));
    }

    [TestCase("WindowOpen")]
    [TestCase("PendingGrab")]
    [TestCase("PendingGone")]
    public void InProgressNetworkClaimsAreNeverCaptured(string field)
    {
        var bag = Bag(Vector3.zero);
        var nc = NetworkCopy(bag);
        Set(nc, field, true);
        Assert.IsFalse(Available(bag));
    }

    [Test]
    public void ContainsShopPointNeverIncludesOtherDepotsOrTown()
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        var contains = Compactor.GetMethod("ContainsShopPoint");
        Assert.IsTrue((bool)contains.Invoke(compact, new object[] { Origin(2) + Vector3.up }));
        Assert.IsFalse((bool)contains.Invoke(compact, new object[] { Origin(1) + Vector3.up }));
        Assert.IsFalse((bool)contains.Invoke(compact, new object[] { Vector3.zero }));
    }

    [Test]
    public void BulkCollectsOnlyLoosePhysicalStockInItsOwnDepot()
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        var loose = Bag(Origin(2) + Vector3.up);
        var held = Bag(Origin(2) + Vector3.right);
        State(held, "Carried");
        var foreign = Bag(Origin(1) + Vector3.up);
        var street = Bag(Vector3.zero);
        // Saved inventory/truck cargo are data, not Carryable objects. Display meshes are not carryables either.
        var display = Go("SavedTreasureDisplay");
        display.transform.position = Origin(2);
        display.AddComponent<BoxCollider>();
        var found = (IList)Compactor.GetMethod("CollectForCompact").Invoke(compact, new object[] { true });
        Assert.Contains(loose, found);
        Assert.IsFalse(found.Contains(held), "held trash stays with its player");
        Assert.IsFalse(found.Contains(foreign), "another business's stock stays untouched");
        Assert.IsFalse(found.Contains(street), "town trash stays untouched");
        foreach (object item in found) Assert.IsTrue(Carryable.IsInstanceOfType(item), "inventory/display data is not physical stock");
    }

    [TestCase("Belt")]
    [TestCase("Rack")]
    public void BulkIncludesEveryConveyorStageButOrdinaryChamberDoesNot(string stage)
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        var bag = Bag(Origin(2) + Vector3.up * 2f);
        AttachToConveyor(compact, bag, stage);
        Set(compact, "chamber", compact.transform);
        Set(compact, "chamberSize", Vector3.one * 12f);
        Assert.IsFalse(Available(bag), "generic availability must still reject scripted machinery");
        Assert.Contains(bag, Collect(compact), "bulk explicitly owns and revokes the conveyor anchor");
        Assert.IsFalse(Collect(compact, false).Contains(bag), "ordinary SMASH cannot reach onto the conveyor");
    }

    [TestCase("Rest", 0ul, true)]
    [TestCase("Flight", 0ul, true)]
    [TestCase("Flight", 7ul, false)]
    [TestCase("Held", 0ul, false)]
    [TestCase("Tug", 0ul, false)]
    [TestCase("Gone", 0ul, false)]
    public void ConveyorExceptionOnlyAllowsUnclaimedHostStock(string phase, ulong holder, bool expected)
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        var bag = Bag(Origin(2) + Vector3.up);
        AttachToConveyor(compact, bag, "Belt");
        var nc = NetworkCopy(bag, phase);
        var state = nc.GetType().GetField("St").GetValue(nc);
        Set(state, "holder", holder);
        Set(nc, "St", state);
        Assert.AreEqual(expected, Collect(compact).Contains(bag));
    }

    [TestCase("WindowOpen")]
    [TestCase("PendingGrab")]
    [TestCase("PendingGone")]
    public void ConveyorExceptionDoesNotStealPendingNetworkClaims(string field)
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        var bag = Bag(Origin(2) + Vector3.up);
        AttachToConveyor(compact, bag, "Rack");
        Set(NetworkCopy(bag, "Flight"), field, true);
        Assert.IsFalse(Collect(compact).Contains(bag));
    }

    [Test]
    public void ConveyorOwnerAndPhysicalStockMustBothBelongToThisDepot()
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        var foreign = DepotCompactor(1);
        var wrongMachine = Bag(Origin(2) + Vector3.up);
        AttachToConveyor(foreign, wrongMachine, "Belt");
        var wrongPlace = Bag(Origin(1) + Vector3.up);
        AttachToConveyor(compact, wrongPlace, "Rack");
        var ordinaryFlight = Bag(Origin(2) + Vector3.up);
        State(ordinaryFlight, "Thrown");
        ordinaryFlight.GetComponent<Rigidbody>().isKinematic = true;
        Assert.IsEmpty(Collect(compact), "no cross-depot vacuum and no catch-all flight exception");
    }

    [TestCase("Belt")]
    [TestCase("Rack")]
    public void DetachingStockRevokesMachineOwnershipWithoutDroppingOrRepublishing(string stage)
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        var bag = Bag(Origin(2) + Vector3.up);
        var machine = AttachToConveyor(compact, bag, stage);
        Assert.IsTrue(Owns(machine, bag));
        Detach(bag);
        Detach(bag); // Idempotent across the consume/late-message boundary.
        Assert.IsFalse(Owns(machine, bag));
        Assert.IsTrue(bag.GetComponent<Rigidbody>().isKinematic, "detach must not trigger EndCarry/Rest publication");
        Assert.AreEqual("Thrown", Carryable.GetProperty("State").GetValue(bag).ToString());
        Assert.IsFalse(Collect(compact).Contains(bag));
        if (stage == "Rack")
            Assert.IsFalse((bool)machine.GetType().GetMethod("IsArriving").Invoke(machine, new object[] { bag }), "the rack let go of the bag hopping in");
    }

    static IEnumerator MovingBag(Component bag)
    {
        while (true) { bag.transform.position += Vector3.right; yield return null; }
    }

    [Test]
    public void DetachedArcCannotKeepMovingBagOnLaterFrames()
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        var bag = Bag(Origin(2) + Vector3.up);
        var machine = AttachToConveyor(compact, bag, "Rack");
        Func<bool> owns = () => Owns(machine, bag);
        var arc = (IEnumerator)Runtime("ConveyorStock").GetMethod("WhileOwned", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { bag, owns, MovingBag(bag) });
        Assert.IsTrue(arc.MoveNext());
        var at = bag.transform.position;
        Detach(bag);
        Assert.IsFalse(arc.MoveNext());
        Assert.AreEqual(at, bag.transform.position, "no hop/settle coroutine can overwrite suction or a later holder");
    }

    [Test]
    public void DuplicateAnchorsCannotCountStockTwiceAndConsumedBagsCannotReenter()
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        var bag = Bag(Origin(2) + Vector3.up);
        AttachToConveyor(compact, bag, "Belt");
        AttachToConveyor(compact, bag, "Rack");
        Assert.AreEqual(1, Collect(compact).Count, "one physical bag contributes one payment, even across a handoff");
        State(bag, "Consumed");
        Assert.IsEmpty(Collect(compact), "a stale conveyor reference cannot recompact an already consumed bag");
    }

    static void HoldConfirmation(Component compact, float now) =>
        Compactor.GetMethod("HoldBulkConfirmation", InstanceFields).Invoke(compact, new object[] { now });

    static bool HeldAt(Component compact, float now) =>
        (bool)Compactor.GetMethod("BulkConfirmationHeldAt", InstanceFields).Invoke(compact, new object[] { now });

    [Test]
    public void HostConfirmationHoldExpiresWithoutDependingOnUiOrScaledTime()
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        Assert.IsFalse(HeldAt(compact, 10f));
        HoldConfirmation(compact, 10f);
        Assert.IsTrue(HeldAt(compact, 10f));
        Assert.IsTrue(HeldAt(compact, 13.999f));
        Assert.IsFalse(HeldAt(compact, 14f));
        // The UI timer is unrelated: a remote guest displaying a warning cannot pause host machinery.
        Set(compact, "crushAllArmedUntil", float.MaxValue);
        Assert.IsFalse(HeldAt(compact, 14f));
        HoldConfirmation(compact, 15f);
        Assert.IsTrue(HeldAt(compact, 18.9f));
        Compactor.GetMethod("OnDisable", InstanceFields).Invoke(compact, null);
        Assert.IsFalse(HeldAt(compact, 15f), "closing a depot revokes its hold immediately");
    }

    [Test]
    public void BulkConfirmationKeepsRackStockAvailableAndTheConveyorNeverOpensBags()
    {
        EnableDepotGeometry();
        var compact = DepotCompactor(2);
        var bag = Bag(Origin(2) + Vector3.up);
        var rack = AttachToConveyor(compact, bag, "Rack");
        // The conveyor only carries bags now: there is no slitter to pause, at any level.
        Assert.IsNull(Runtime("ConveyorTray").GetMethod("CanStartAutoSlit", InstanceFields));
        Assert.IsNull(Runtime("ConveyorTray").GetField("slitting", InstanceFields));
        Assert.IsNull(Runtime("ConveyorTray").GetMethod("Take", InstanceFields), "nothing takes a bag out of the rack by script");
        HoldConfirmation(compact, Time.unscaledTime);
        Assert.Contains(bag, Collect(compact), "the hold neither consumes nor steals the rack bag");
        Assert.IsTrue(Owns(rack, bag));
    }
}
