using UnityEngine;

/// <summary>A crossing pedestrian's rule-based hit box. Physics colliders are deliberately unnecessary:
/// the same LifeBodies sweeps as adult pedestrians decide cartoon impacts.</summary>
public sealed class CrossingWalker : ILifeBody
{
    public LifeId Id { get; }
    public LifeBodyKind BodyKind => LifeBodyKind.Person;
    public Vector3 Position { get; private set; }
    public Quaternion Rotation { get; private set; }
    public Vector3 HalfExtents => guard ? new Vector3(.30f, .88f, .30f) : new Vector3(.25f, .55f, .25f);
    public bool Hittable => Active && !Reacting;
    public string StopId => "";
    public long Serial { get; private set; }
    public bool Active { get; private set; }
    public bool Reacting => hit.kind != NpcHitKind.None && HitAge < ReactionDuration(hit.kind);
    public float HitAge => Mathf.Max(0f, (float)((GameClock.TotalHours - hit.atHours) * GameClock.SecondsPerGameHour));
    public const float ReactionSeconds = 6f;
    static float ReactionDuration(NpcHitKind kind) => kind == NpcHitKind.Flatten || kind == NpcHitKind.ShortFlop ? ReactionSeconds : 1.25f;
    readonly bool guard;
    NpcHit hit;
    Vector3 scheduled;
    Quaternion scheduledRotation;
    bool registered;

    public CrossingWalker(int index, bool guard) { Id = new LifeId(LifeKind.Crossing, index); this.guard = guard; }

    public void Schedule(bool active, long serial, Vector3 position, Quaternion rotation)
    {
        if (serial != Serial) { Serial = serial; hit = default; }
        Active = active;
        scheduled = position; scheduledRotation = rotation;
        Position = Reacting ? ReactionPosition(HitAge) : scheduled;
        Rotation = rotation;
        if (active && !registered) { LifeBodies.Register(this); registered = true; }
        else if (!active && registered) { LifeBodies.Unregister(this); registered = false; }
    }

    public void Dispose() { if (registered) LifeBodies.Unregister(this); registered = Active = false; }

    public void PlayHit(in NpcHit value)
    {
        // Truck impacts arrive via the authoritative crossing RPC. Existing local thrown/bump reactions use this seam.
        var net = HandsNet.Instance;
        if (net != null && net.ReportCrossingContact(in value)) return;
        Apply(in value);
    }

    public void Apply(in NpcHit value)
    {
        if (!Active || value.kind == NpcHitKind.None || value.target != Id ||
            (GameClock.TotalHours - value.atHours) * GameClock.SecondsPerGameHour >= ReactionDuration(value.kind)) return;
        if (hit.kind != NpcHitKind.None && value.atHours <= hit.atHours) return;
        hit = value;
        if (!VehicleCrashRules.Finite(hit.launch)) hit.launch = Vector3.up * 4f;
        Position = ReactionPosition(HitAge);
        var c = WorldLifeRunner.Context;
        if (c != null && (c.Viewer - hit.point).sqrMagnitude < 45f * 45f && HitAge < .8f)
        {
            GameHUD.FloatingText(hit.point + Vector3.up * (guard ? 2f : 1.4f), guard ? "I SAID STOP!" : "WHOOOAA!",
                new Color(1f, .84f, .2f), 1.5f);
            KnockSfx.Play("boop", "boop", hit.point, .4f, guard ? .9f : 1.15f);
        }
    }

    Vector3 Landing()
    {
        Vector3 flat = hit.launch; flat.y = 0;
        if (flat.sqrMagnitude < .01f) { flat = hit.truckVelocity; flat.y = 0; }
        flat = Vector3.ClampMagnitude(flat * .55f, guard ? 5f : 4f);
        return hit.point + flat;
    }

    Vector3 ReactionPosition(float age)
    {
        Vector3 landing = Landing();
        bool flop = hit.kind == NpcHitKind.Flatten || hit.kind == NpcHitKind.ShortFlop;
        if (!flop) return Vector3.Lerp(hit.point, scheduled, Mathf.Clamp01(age / 1.2f));
        if (age < 1.1f)
        {
            float u = age / 1.1f;
            return Vector3.Lerp(hit.point, landing, u) + Vector3.up * (Mathf.Sin(u * Mathf.PI) * (guard ? 1.5f : 1.8f));
        }
        if (age < 4.1f) return landing;
        return Vector3.Lerp(landing, scheduled, Mathf.SmoothStep(0, 1, (age - 4.1f) / 1.9f));
    }

    /// <summary>Pose after the normal walking pose, so recovery cleanly returns to the ongoing procession.</summary>
    public void PoseReaction(PuppetRig rig)
    {
        if (rig == null || !Reacting) return;
        float age = HitAge;
        Position = ReactionPosition(age);
        rig.Place(Position, scheduledRotation.eulerAngles.y);
        bool flop = hit.kind == NpcHitKind.Flatten || hit.kind == NpcHitKind.ShortFlop;
        if (!flop)
        {
            rig.Body(Vector3.zero, Quaternion.Euler(-Mathf.Sin(Mathf.Clamp01(age / 1.2f) * Mathf.PI) * 25f, 0, 0), 0);
            rig.Arms(-75, 30, -75, 30); return;
        }
        float roll = age < 1.1f ? 360f * age / 1.1f : 0;
        float lie = age < 1.1f ? Mathf.Clamp01(age / .3f) : 1f - Mathf.Clamp01((age - 3.4f) / .7f);
        rig.Body(Vector3.up * (rig.layout.height * .22f * lie), Quaternion.Euler(0, roll, 90f * lie), 0);
        rig.Torso(0, 0); rig.Arms(-25, 62, 25, 62); rig.Legs(12, -12, 22);
        rig.Head(0, Mathf.Sin(age * 8f) * 15f, 0);
        rig.SetMouth(.8f, 1f);
    }
}
