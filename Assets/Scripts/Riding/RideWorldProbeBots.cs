using System.Collections.Generic;
using UnityEngine;

/// <summary>Sandboxed bot proof of the actual fling-flight interactions with built street props and residents.</summary>
public static class RideWorldProbeBots
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("rideworld", () => new Probe());

    sealed class Probe : IBotBrain
    {
        bool lamp, bin, person, complete;
        float nextTry;

        public void Begin(BotContext c) => c.Stop();
        public void End(BotContext c) { }

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            if (!c.IsHost || complete || c.Elapsed < 25f || Time.time < nextTry) return;
            nextTry = Time.time + 2f;
            if (KnockableDirector.Instance != null && WorldLifeRunner.Context != null)
            {
                if (!lamp) lamp = TryKnock(KnockKind.Lamp);
                if (!bin) bin = TryKnock(KnockKind.WheelieBin) || TryKnock(KnockKind.ParkBin);
            }
            if (!person) person = TryPerson();
            if (lamp && bin && person || c.Elapsed > 95f)
            {
                complete = true;
                NetAudit.Event("rideprobe", new Dictionary<string, object> { { "op", "fling-world" },
                    { "lamp", lamp }, { "bin", bin }, { "person", person },
                    { "complete", lamp && bin && person }, { "knockables", Knockable.Count },
                    { "lifeBodies", LifeBodies.Count } });
            }
        }

        static bool TryKnock(KnockKind kind)
        {
            foreach (var k in Knockable.All)
            {
                if (k == null || k.kind != kind || k.IsDown) continue;
                foreach (var col in k.RestColliders)
                {
                    if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;
                    Vector3 centre = col.bounds.center;
                    var flight = new RideFlight(centre - Vector3.up * 0.75f, Vector3.forward * 12f, false);
                    flight.Step();
                    if (k.IsDown && flight.Knocks > 0) return true;
                }
            }
            return false;
        }

        static bool TryPerson()
        {
            var bodies = LifeBodies.All;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b == null || b.BodyKind != LifeBodyKind.Person || !b.Hittable) continue;
                var flight = new RideFlight(b.Position - Vector3.up * 0.75f, Vector3.forward * 12f, true);
                flight.Step();
                if (flight.People > 0) return true;
            }
            return false;
        }
    }
}
