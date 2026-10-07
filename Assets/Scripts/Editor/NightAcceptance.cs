using System;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Copy-only, read-only EditMode checks for the F4 night rules and the after-dark curb schedule.
/// Run with -batchmode -quit -executeMethod NightAcceptance.Run in RubbishBuildCopy.
/// Gameplay/network acceptance still needs the night bot run; this catches data and schedule regressions cheaply.
/// </summary>
public static class NightAcceptance
{
    [MenuItem("Rubbish/Debug/Night/Acceptance Check (isolated copy)")]
    public static void Run()
    {
        if (Application.productName != "RubbishBuildCopy" || !Application.isBatchMode || Application.isPlaying)
            throw new InvalidOperationException("[NightAcceptance] Run only in batch mode on the isolated build copy.");

        var log = new StringBuilder("[NightAcceptance]\n");
        int checkedCount = 0;
        void Check(bool ok, string description)
        {
            checkedCount++;
            log.AppendLine((ok ? "PASS " : "FAIL ") + description);
            if (!ok) throw new InvalidOperationException(log.ToString());
        }

        // Mean-normalized puts out the same district total per day, but concentrates the hazard after dark.
        float total = 0f, night = 0f;
        bool validWeights = true;
        for (int i = 0; i < 96; i++)
        {
            float hour = (i + 0.5f) * 0.25f;
            float weight = CurbTrashSpawner.TimeOfDayWeight(hour);
            validWeights &= !float.IsNaN(weight) && !float.IsInfinity(weight) && weight > 0f;
            total += weight;
            if (hour >= GameClock.NightStart || hour < GameClock.DawnStart) night += weight;
        }
        Check(validWeights, "all 96 put-out weight samples are finite and positive");
        float share = night / total;
        Check(Mathf.Abs(total / 96f - 1f) < 0.02f, $"daily put-out budget conserved (mean {total / 96f:0.000})");
        Check(share > 0.48f && share < 0.55f, $"overnight pileup gets {share:P1} of a day's bags");
        Check(CurbTrashSpawner.TimeOfDayWeight(23f) > 2.5f * CurbTrashSpawner.TimeOfDayWeight(13f),
              "23:00 put-out rate exceeds 13:00 by >2.5x");
        Check(CurbTrashSpawner.Cap > 0, "curb cap remains positive");

        Check(Mathf.Approximately(NightShift.NightPayBonus, 0.5f), "night pickups have a 50% bonus (1.5x pay)");
        Check(!NightShift.RabiesTag, "Rabies Tag is off by default");
        Check(Mathf.Approximately(NightShift.RabiesTagChance, 0.5f), "Rabies Tag pass chance is 50%");
        Check(RaccoonLatch.MashToFree == 6 && Mathf.Approximately(RaccoonLatch.Slow, 0.6f),
              "latched player keeps scrambled, slowed control and a six-press escape");
        Check(Mathf.Approximately(NightTraps.BountyFor(TrapNet.Caught), 40f) &&
              Mathf.Approximately(NightTraps.BountyFor(TrapNet.CaughtRabid), 60f),
              "Animal Control pays $40 or $60 for a rabid catch");
        Check(Mathf.Approximately(NightShift.RabiesShotPrice, 35f), "Rabies Shot costs $35");
        Check(Mathf.Approximately(RaccoonKingEvent.ChancePerNight, 0.25f), "natural King rolls once per night at 25%");

        Check(!RaccoonKingEvent.ScheduledHourDue(-1f, 23f) &&
              !RaccoonKingEvent.ScheduledHourDue(22.5f, 22f) &&
              RaccoonKingEvent.ScheduledHourDue(22.5f, 23f) &&
              RaccoonKingEvent.ScheduledHourDue(22.5f, 0.5f) &&
              !RaccoonKingEvent.ScheduledHourDue(22.5f, 6f) &&
              !RaccoonKingEvent.ScheduledHourDue(1.5f, 23f) &&
              !RaccoonKingEvent.ScheduledHourDue(1.5f, 1f) &&
              RaccoonKingEvent.ScheduledHourDue(1.5f, 2f),
              "natural King schedule crosses midnight and expires at dawn");

        CheckItem(RaccoonTrap.PetDefId, "critter_raccoon", Check);
        CheckItem(RaccoonKing.TrophyDefId, "critter_crown", Check);
        log.AppendLine($"PASS {checkedCount} F4 checks (including 96 time samples).");
        Debug.Log(log.ToString());
    }

    static void CheckItem(string id, string visualKey, Action<bool, string> check)
    {
        var def = ItemCatalog.Get(id);
        check(def != null && def.visual == visualKey && def.category == ItemCategory.Valuable,
              $"{id} is a keepable valuable with the intended visual");
        var parent = new GameObject("[NightAcceptance] disposable visual");
        try
        {
            var visual = ItemVisuals.Build(def, parent.transform);
            check(visual != null && visual.GetComponentsInChildren<Renderer>(true).Length > 0 &&
                  visual.GetComponentsInChildren<Collider>(true).Length == 0,
                  $"{id} builds a visible, collider-free shelf prop");
        }
        finally { UnityEngine.Object.DestroyImmediate(parent); }
    }
}
