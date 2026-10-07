// Run on the host in an isolated two-player test. Put the selected player outdoors first.
// Change king to true to repeat against the King; cure the victim between runs using the usual shot or test-only CureRabies.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying || NetBoot.Manager == null || !NetBoot.Manager.IsServer)
    throw new System.InvalidOperationException("Requires an isolated live host session.");
const ulong victimId = 1;
const bool king = false;
var victim = NightPlayers.ForClient(victimId);
if (victim == null || NightPlayers.IsIndoors(victim) || victim.IsDriving)
    throw new System.InvalidOperationException("Selected victim must be on foot outdoors.");
GameClock.DebugSetHour(22f);
GameClock.SetHeld(true);
Vector3 at = victim.transform.position + victim.transform.forward * 1.8f;
Critter animal = king ? (Critter)RaccoonKing.Spawn(at, 40) : Raccoon.Spawn(at, Quaternion.identity, false, null, null);
animal.name = "Aggression verification " + (king ? "King" : "plain");
var targetField = typeof(Critter).GetField("target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
System.Collections.IEnumerator Probe()
{
    yield return new WaitForSecondsRealtime(0.5f);
    animal.NetBonk(victim.transform.forward * 4f, victimId); // same authoritative reaction as a landed punch/projectile
    string last = "";
    for (float elapsed = 0f; elapsed < 22f && animal != null && !animal.Despawning; elapsed += 0.2f)
    {
        string state = animal is Raccoon r ? r.Current.ToString() : ((RaccoonKing)animal).Phase.ToString();
        if (state != last)
        {
            var target = (NightTarget)targetField.GetValue(animal);
            Debug.Log("RACCOON_ENCOUNTER " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                king, elapsed, state, animal.NetId, target = target.client,
                distance = target.dist, angry = animal is Raccoon rr && rr.Angry,
                at = animal.transform.position.ToString("F2")
            }));
            last = state;
        }
        yield return new WaitForSecondsRealtime(0.2f);
    }
    Debug.Log("RACCOON_ENCOUNTER_DONE " + (king ? "King" : "plain") + " — inspect victim's HasRabies and night.hit/king-bite audit.");
}
NetBoot.Manager.StartCoroutine(Probe());
return "Provoked encounter probe started; observe genuine state-machine attacks for 22 seconds.";
