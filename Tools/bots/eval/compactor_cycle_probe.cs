// Run on the acting host/guest, using real press+confirmation entry points. Isolated saves only.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Requires an isolated live test session.");
const int lot = 1;
var depot = DepotRowScene.Lot(lot);
var c = depot.interior.GetComponentInChildren<Compactor>(true);
var me = PlayerController.Local;
if (me == null || DepotRowLayout.InteriorAt(me.transform.position) != lot)
    throw new System.InvalidOperationException("Enter the target shop first.");
System.Collections.IEnumerator Probe()
{
    c.CrushAllPressed();
    yield return new UnityEngine.WaitForSecondsRealtime(0.65f);
    c.CrushAllPressed();
    yield return new UnityEngine.WaitForSecondsRealtime(1.1f);
    var pulls = PlayerTells.All.Select(p => new {client=p.ClientId, pulling=CompactorCube.IsSuctioning(p.ClientId), at=p.transform.position.ToString("F2")}).ToArray();
    UnityEngine.Debug.Log("COMPACTOR_MID " + Newtonsoft.Json.JsonConvert.SerializeObject(new {busy=c.Busy, ghosts=UnityEngine.Object.FindObjectsByType<UnityEngine.Transform>(UnityEngine.FindObjectsSortMode.None).Count(t=>t.name=="Vacuum trash ghost"), pulls}));
    UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(SaveRoot.Path, "compactor-mid.png"));
    yield return new UnityEngine.WaitForSecondsRealtime(3f);
    UnityEngine.Debug.Log("COMPACTOR_DONE " + Newtonsoft.Json.JsonConvert.SerializeObject(new {busy=c.Busy, cash=GameSession.Cash, players=PlayerTells.All.Select(p=>new {client=p.ClientId, cube=CompactorCube.Remaining(p.ClientId), at=p.transform.position.ToString("F2"), controls=p.Player.enabled, controller=p.GetComponent<UnityEngine.CharacterController>().enabled}).ToArray()}));
    UnityEngine.ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(SaveRoot.Path, "compactor-boxes.png"));
}
c.StartCoroutine(Probe());
return "Press / confirm / midpoint screenshot / completion probe started.";
