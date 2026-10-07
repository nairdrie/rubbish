using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Read-only owner lookups using isolated registry/lot snapshots; no gameplay, transport or account writes.</summary>
public class RaccoonOwnershipTests
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Fields).SetValue(obj, value);
    readonly List<GameObject> made = new List<GameObject>();
    readonly List<Component> services = new List<Component>();
    readonly Dictionary<Type, object> oldServices = new Dictionary<Type, object>();
    Component registry, upgrades, row;

    Component Service(string type)
    {
        var t = Runtime(type);
        oldServices[t] = t.GetProperty("Instance").GetValue(null);
        var go = new GameObject("RaccoonTest " + type);
        go.SetActive(false);
        made.Add(go);
        var c = go.AddComponent(t);
        t.GetMethod("Awake", Fields).Invoke(c, null);
        typeof(NetworkBehaviour).GetProperty("IsSpawned").SetValue(c, true);
        t.GetProperty("Instance").SetValue(null, c);
        services.Add(c);
        return c;
    }

    static object Record(string name, params object[] fields)
    {
        object value = Activator.CreateInstance(Runtime(name));
        for (int i = 0; i < fields.Length; i += 2) Set(value, (string)fields[i], fields[i + 1]);
        return value;
    }

    static void Add(Component c, string field, object value)
    {
        var list = c.GetType().GetField(field, Fields).GetValue(c);
        list.GetType().GetMethod("Add").Invoke(list, new[] { value });
    }

    [SetUp]
    public void SetUp()
    {
        registry = Service("BusinessRegistry");
        upgrades = Service("BusinessUpgradeNet");
        row = Service("DepotRowNet");
        object oneWorld = row.GetType().GetField("oneWorld", Fields).GetValue(row);
        Set(oneWorld, "m_InternalValue", true);
        AddOwner(11, 0, "night-owner-a", 0, 11);
        AddOwner(22, 1, "night-owner-b", 5, 22); // pole + L2 headlights
        AddOwner(33, 2, "night-owner-c", 5, 44); // stale snapshot from a different client
    }

    void AddOwner(ulong client, sbyte lot, string id, byte gear, ulong snapshotOwner)
    {
        Add(registry, "list", Record("BusinessInfo", "id", new FixedString64Bytes(id), "ownerClientId", client, "lot", lot));
        Add(row, "lots", Record("DepotLotState", "lot", (byte)lot, "ownerClientId", client, "flags", (byte)3));
        Add(upgrades, "snapshots", Record("BusinessUpgradeInfo", "businessId", new FixedString64Bytes(id),
            "ownerClientId", snapshotOwner, "gearBits", gear));
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var service in services)
        {
            typeof(NetworkBehaviour).GetProperty("IsSpawned").SetValue(service, false);
            service.GetType().GetProperty("Instance").SetValue(null, oldServices[service.GetType()]);
        }
        for (int i = made.Count - 1; i >= 0; i--) if (made[i] != null) Object.DestroyImmediate(made[i]);
        services.Clear(); made.Clear(); oldServices.Clear();
    }

    [Test]
    public void LightTierUsesThatLotOwnerNotAnotherBusinessesBoughtLights()
    {
        var get = Runtime("BusinessUpgradeNet").GetMethod("LevelForLot");
        string id = (string)Runtime("ShopIds").GetField("TruckHeadlights").GetRawConstantValue();
        Assert.AreEqual(0, get.Invoke(null, new object[] { 0, id }));
        Assert.AreEqual(2, get.Invoke(null, new object[] { 1, id }));
        Assert.AreEqual(0, get.Invoke(null, new object[] { 2, id }), "wrong-owner snapshot is not authoritative");
        Assert.AreEqual(0, get.Invoke(null, new object[] { 3, id }), "unclaimed lot cannot borrow the host's upgrade");
    }

    [TestCase(11UL, false)]
    [TestCase(22UL, true)]
    [TestCase(33UL, false)]
    [TestCase(999UL, false)]
    public void PoleIsPersonalGearAndRejectsMissingOrStaleOwnerSnapshot(ulong client, bool expected)
    {
        Assert.AreEqual(expected, Runtime("BusinessUpgradeNet").GetMethod("HasGrabberPoleForClient").Invoke(null, new object[] { client }));
    }
}
