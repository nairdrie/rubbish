using UnityEngine;

/// <summary>
/// When the host takes a truck back from a guest who has left its driver's seat (Docs/Multiplayer.md §4.4, "Ownership
/// follows the simulation"). The guest's machine keeps simulating the empty truck, parking brake on, while the host watches
/// its own copy; once that copy has held still for <see cref="StillSeconds"/>, both machines agree on a truck at rest and it
/// changes hands without a visible jump. Taking it back straight away restarted the physics on the host's copy, which trails
/// the guest's, from a standstill wherever that copy happened to be. Pure rules; <c>TruckControllerV2</c> feeds it the
/// host's copy every frame.
/// </summary>
public sealed class TruckHandbackWatch
{
    /// <summary>The host's copy must hold still this long. It trails the guest's truck by the interpolation delay (about
    /// 0.15 s after the eject request lands), and the suspension settles from the exit well inside this.</summary>
    public const float StillSeconds = 0.5f;
    /// <summary>Moving further than this from where it last came to rest restarts the wait.</summary>
    public const float StillMetres = 0.01f, StillDegrees = 0.5f;
    /// <summary>Taken back after this long whatever it is doing (sliding down a slope, rocking on its side, being towed).</summary>
    public const float MaxSeconds = 10f;

    Vector3 restPosition;
    Quaternion restRotation;
    float stillSince, giveUpAt;

    /// <summary>A guest has left the seat and the host hasn't taken the truck back yet.</summary>
    public bool Active { get; private set; }

    /// <summary>The seat was freed while a guest owns the truck: start watching the host's copy, posed here now.</summary>
    public void Begin(float now, Vector3 position, Quaternion rotation)
    {
        Active = true;
        restPosition = position;
        restRotation = rotation;
        stillSince = now;
        giveUpAt = now + MaxSeconds;
    }

    public void Cancel() => Active = false;

    /// <summary>One sample of the host's copy. True once (then inactive) when it is time to take the truck back.</summary>
    public bool Tick(float now, Vector3 position, Quaternion rotation)
    {
        if (!Active) return false;
        if (Moved(restPosition, restRotation, position, rotation))
        {
            restPosition = position;
            restRotation = rotation;
            stillSince = now;
        }
        if (now - stillSince < StillSeconds && now < giveUpAt) return false;
        Active = false;
        return true;
    }

    /// <summary>Further apart than the still limits. A NaN position counts as moving.</summary>
    public static bool Moved(Vector3 fromPosition, Quaternion fromRotation, Vector3 position, Quaternion rotation) =>
        !((position - fromPosition).sqrMagnitude <= StillMetres * StillMetres) || Quaternion.Angle(fromRotation, rotation) > StillDegrees;
}
