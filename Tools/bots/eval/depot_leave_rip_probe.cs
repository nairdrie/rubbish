// Guest-side, isolated-save probe. Needs the employee seated at host lot 0 holding leave-rip-bag-2.
if (!SaveRoot.IsOverridden || NetBoot.Manager == null || NetBoot.Manager.IsServer)
    throw new System.InvalidOperationException("Requires an isolated guest test save.");
var station = DepotRowScene.Lot(0).interior.GetComponentInChildren<SortStation>();
var bag = PlayerController.Local.GetComponent<TrashCarrier>().Held as TrashBag;
if (!station.IsActive || bag == null || bag.Contents.bagId != "leave-rip-bag-2")
    throw new System.InvalidOperationException("Sit at the host table holding the second test bag.");
station.table.spillInterval = 0.6f;
var report = new UnityEngine.GameObject("MidRipProbe pending");
System.Collections.IEnumerator Probe()
{
    float until = UnityEngine.Time.unscaledTime + 15f;
    LooseItem found = null;
    while (UnityEngine.Time.unscaledTime < until)
    {
        found = LooseItem.All.FirstOrDefault(x => x.Item.uid == "leave-rip-2-0");
        if (found != null && found.Revealed) break;
        yield return null;
    }
    int leavingCount = LooseItem.All.Count(x => x.Item.uid.StartsWith("leave-rip-2-"));
    bool fullCard = UnityEngine.Object.FindFirstObjectByType<FeelHud>().RevealShowing;
    station.Exit();
    DepotRowWorld.LocalGoInside(1, false);
    yield return new UnityEngine.WaitForSecondsRealtime(0.6f);
    bool heldInterior = DepotRowScene.Lot(0).InteriorOn;
    while (UnityEngine.Time.unscaledTime < until && station.table.IsRipping) yield return null;
    yield return new UnityEngine.WaitForSecondsRealtime(0.6f);
    int finished = LooseItem.All.Count(x => x.Item.uid.StartsWith("leave-rip-2-"));
    report.name = $"MidRipProbe leaving={leavingCount} finderCard={fullCard} keptInterior={heldInterior} finished={finished} interiorAfter={DepotRowScene.Lot(0).InteriorOn}";
}
NetBoot.Manager.StartCoroutine(Probe());
typeof(SortStation).GetMethod("SlamBag", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(station, new object[] { bag });
return "Started automatic mid-spray departure and finder-reveal probe";
