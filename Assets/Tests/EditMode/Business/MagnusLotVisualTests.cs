using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UObj = UnityEngine.Object;

/// <summary>Reflection because authored garage components live in Assembly-CSharp, outside this test asmdef.</summary>
public class MagnusLotVisualTests
{
    static Type LotType => Type.GetType("DepotLot, Assembly-CSharp", true);
    static Type RigType => Type.GetType("GarageRig, Assembly-CSharp", true);
    static Type AttachmentType => Type.GetType("UpgradeAttachment, Assembly-CSharp", true);
    static Type PoseType => Type.GetType("MagnusPose, Assembly-CSharp", true);
    static Type LotStateType => Type.GetType("DepotLotState, Assembly-CSharp", true);

    readonly List<GameObject> made = new List<GameObject>();

    GameObject Go(string name, Transform parent = null)
    {
        var go = new GameObject(name);
        go.SetActive(false); // These are reference-resolution tests, not play-mode machinery.
        if (parent != null) go.transform.SetParent(parent, false);
        made.Add(go);
        return go;
    }

    (Component lot, GameObject interior) Lot(string name)
    {
        var root = Go(name);
        var lot = root.AddComponent(LotType);
        var interior = Go("Interior", root.transform);
        Set(lot, "interior", interior);
        return (lot, interior);
    }

    static void Set(object obj, string field, object value)
    {
        var f = obj.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(f, obj.GetType().Name + "." + field);
        f.SetValue(obj, value);
    }

    static object Get(object obj, string field) => obj.GetType().GetField(field,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);

    [TearDown]
    public void TearDown()
    {
        for (int i = made.Count - 1; i >= 0; i--) if (made[i] != null) UObj.DestroyImmediate(made[i]);
        made.Clear();
    }

    [Test]
    public void GarageRigReplacesForeignSerializedReferencesOnlyWithSameLotParts()
    {
        var a = Lot("LotA");
        var b = Lot("LotB");
        var localTable = Go("LocalTable", a.interior.transform).AddComponent(Type.GetType("SortingTable, Assembly-CSharp", true));
        var foreignTable = Go("ForeignTable", b.interior.transform).AddComponent(Type.GetType("SortingTable, Assembly-CSharp", true));
        var localStation = Go("LocalStation", a.interior.transform).AddComponent(Type.GetType("SortStation, Assembly-CSharp", true));
        var foreignStation = Go("ForeignStation", b.interior.transform).AddComponent(Type.GetType("SortStation, Assembly-CSharp", true));
        var localTrack = Go("LocalTrack", a.interior.transform).AddComponent(Type.GetType("MagnusTrack, Assembly-CSharp", true));
        var foreignTrack = Go("ForeignTrack", b.interior.transform).AddComponent(Type.GetType("MagnusTrack, Assembly-CSharp", true));
        var rigRoot = Go("GarageUpgrades", a.interior.transform);
        var localLeaf = Go("XLLeaf", Go("Table", rigRoot.transform).transform);
        var foreignLeaf = Go("ForeignLeaf", b.interior.transform);
        var localStop = Go("Sort Stop", rigRoot.transform).transform;
        var foreignStop = Go("Sort Stop", b.interior.transform).transform;
        var rig = rigRoot.AddComponent(RigType);
        Set(rig, "station", foreignStation);
        Set(rig, "table", foreignTable);
        Set(rig, "track", foreignTrack);
        Set(rig, "tableLeaf", foreignLeaf);
        Set(rig, "sortStop", foreignStop);

        RigType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Instance).Invoke(rig, null);

        Assert.AreSame(localStation, Get(rig, "station"));
        Assert.AreSame(localTable, Get(rig, "table"));
        Assert.AreSame(localTrack, Get(rig, "track"));
        Assert.AreSame(localLeaf, Get(rig, "tableLeaf"));
        Assert.AreSame(localStop, Get(rig, "sortStop"));
    }

    [Test]
    public void GarageRigWithMissingInteriorClearsForeignReferences()
    {
        var a = Lot("LotA");
        var b = Lot("LotB");
        var foreignTable = Go("ForeignTable", b.interior.transform).AddComponent(Type.GetType("SortingTable, Assembly-CSharp", true));
        var rig = Go("GarageUpgrades", a.lot.transform).AddComponent(RigType);
        Set(rig, "table", foreignTable);
        Set(rig, "sortStop", Go("ForeignStop", b.interior.transform).transform);
        Set(a.lot, "interior", null);

        RigType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Instance).Invoke(rig, null);

        Assert.IsNull(Get(rig, "table"));
        Assert.IsNull(Get(rig, "sortStop"));
    }

    [Test]
    public void AttachmentResolvesOnlyTheOwningLotAndFailsClosed()
    {
        var a = Lot("LotA");
        var b = Lot("LotB");
        const string path = "Magnus Cart/Magnus/BasePivot";
        Transform MakeTarget(GameObject interior)
        {
            var cart = Go("Magnus Cart", interior.transform);
            var magnus = Go("Magnus", cart.transform);
            return Go("BasePivot", magnus.transform).transform;
        }
        var local = MakeTarget(a.interior);
        var foreign = MakeTarget(b.interior);
        var attachment = Go("Eyes", Go("GarageUpgrades", a.interior.transform).transform).transform;
        var resolve = AttachmentType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(Transform), typeof(string) }, null);
        Assert.NotNull(resolve);

        Assert.AreSame(local, resolve.Invoke(null, new object[] { attachment, path }));
        UObj.DestroyImmediate(local.gameObject);
        Assert.IsNull(resolve.Invoke(null, new object[] { attachment, path }), "never borrow the other lot's Magnus");
        Assert.NotNull(foreign);
        Set(a.lot, "interior", null);
        Assert.IsNull(resolve.Invoke(null, new object[] { attachment, path }), "missing interior must fail closed");
    }

    [Test]
    public void PoseRequiresExactClaimAndBlendsCartAndJoints()
    {
        object pose = Activator.CreateInstance(PoseType);
        object state = Activator.CreateInstance(LotStateType);
        var key = new FixedString64Bytes("owner-business");
        Set(pose, "lot", (byte)2);
        Set(pose, "valid", (byte)1);
        Set(pose, "ownerClientId", 17UL);
        Set(pose, "plopSerial", 4);
        Set(pose, "businessKey", key);
        Set(pose, "baseRotation", Quaternion.identity);
        Set(pose, "cartRotation", Quaternion.identity);
        Set(state, "lot", (byte)2);
        Set(state, "flags", (byte)3); // claimed and online
        Set(state, "ownerClientId", 17UL);
        Set(state, "plopSerial", 4);
        Set(state, "businessKey", key);
        var matches = PoseType.GetMethod("Matches", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(matches);
        Assert.IsTrue((bool)matches.Invoke(pose, new[] { state }));
        Set(state, "plopSerial", 5);
        Assert.IsFalse((bool)matches.Invoke(pose, new[] { state }), "reclaimed lots cannot receive stale poses");
        Set(state, "plopSerial", 4);
        Set(state, "ownerClientId", 18UL);
        Assert.IsFalse((bool)matches.Invoke(pose, new[] { state }));
        Set(state, "ownerClientId", 17UL);
        Set(state, "businessKey", new FixedString64Bytes("another-business"));
        Assert.IsFalse((bool)matches.Invoke(pose, new[] { state }));

        object next = Activator.CreateInstance(PoseType);
        Set(next, "cartPosition", new Vector3(10f, 0f, 0f));
        Set(next, "cartRotation", Quaternion.identity);
        Set(next, "baseRotation", Quaternion.Euler(0f, 90f, 0f));
        var blend = PoseType.GetMethod("Blend", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(blend);
        object midway = blend.Invoke(null, new[] { pose, next, (object)0.5f });
        Assert.That(((Vector3)Get(midway, "cartPosition")).x, Is.EqualTo(5f).Within(0.001f));
        Assert.That(Quaternion.Angle((Quaternion)Get(midway, "baseRotation"), Quaternion.Euler(0f, 45f, 0f)),
            Is.LessThan(0.01f));
    }
}
