using System;
using UnityEngine;

/// <summary>
/// One break-in the host decided, whether or not anyone saw it. THE SEAM FOR A POLICE / WANTED SYSTEM: subscribe to
/// <see cref="ScavengerEvents.Theft"/> on the host and you get who, where, when and how much for every theft, with
/// <see cref="witnessed"/> saying whether the town knows. Nothing in this stage reads it back.
/// </summary>
public struct TheftFact
{
    /// <summary>The burglar: connection id this session, and the stable player key ("" in a plain solo game).</summary>
    public ulong clientId;
    public string playerKey;
    /// <summary>The burglar's OWN business (fines and Rep went there, never to an employer).</summary>
    public string businessId;
    public string stopId, districtId, address;
    /// <summary>The front door.</summary>
    public Vector3 pos;
    public BreakInOutcome outcome;
    /// <summary>Somebody saw or heard it (fine, Rep, grudge and letter were applied).</summary>
    public bool witnessed;
    /// <summary>The door opened (loot left the house).</summary>
    public bool entered;
    public int itemCount;
    /// <summary>ebag appraisal of what was taken, in cents.</summary>
    public long lootCents;
    /// <summary>The fine charged, in cents (0 when unseen or when Town rules waive neighbour fines).</summary>
    public long fineCents;
    /// <summary>What saw it: bit 0 someone indoors, bit 1 the street, bit 2 another player, bit 3 a dog.</summary>
    public byte witnessKinds;
    public int day;
    public double hours;
}

/// <summary>Hot goods changed hands at the fence (host).</summary>
public struct FenceSaleFact
{
    public ulong clientId;
    public string businessId, itemUid, defId;
    public long appraisalCents, paidCents;
    public float standing;
    public int day;
}

/// <summary>Host-side facts the Scavenger calling raises. Listeners must not throw; one that does is logged and skipped.</summary>
public static class ScavengerEvents
{
    public const byte SawIndoors = 1, SawStreet = 2, SawPlayer = 4, SawDog = 8;

    /// <summary>Host: a break-in was decided (every outcome except Denied).</summary>
    public static event Action<TheftFact> Theft;
    /// <summary>Host: the fence bought something.</summary>
    public static event Action<FenceSaleFact> Fenced;

    internal static void RaiseTheft(TheftFact fact) => Invoke(Theft, fact);
    internal static void RaiseFenced(FenceSaleFact fact) => Invoke(Fenced, fact);

    static void Invoke<T>(Action<T> handler, T fact)
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList()) {
            try { ((Action<T>)d)(fact); } catch (Exception e) { Debug.LogException(e); }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Theft = null; Fenced = null; }
}

/// <summary>
/// THE SEAM FOR THE RACCOON STAGE. The fence is the Raccoon King's mob, so how he treats you is theirs to decide: set
/// <see cref="StandingFor"/> on the host to return a price multiplier for a client (1 = strangers' rate; above 1 = he likes
/// you; below 1 = you crossed the King). The result is clamped by <see cref="ScavengerRules.FencePriceCents"/>.
/// <see cref="GreetingFor"/> is presentation on the seller's own machine: return a line and he says it instead of his own.
/// This file depends on nothing in the Raccoon stage.
/// </summary>
public static class FenceTerms
{
    public static Func<ulong, float> StandingFor;
    public static Func<string> GreetingFor;

    public static float Standing(ulong clientId)
    {
        var f = StandingFor;
        if (f == null) return 1f;
        try { float v = f(clientId); return float.IsNaN(v) || float.IsInfinity(v) ? 1f : Mathf.Clamp(v, 0f, 4f); }
        catch (Exception e) { Debug.LogException(e); return 1f; }
    }

    public static string Greeting(string fallback)
    {
        var f = GreetingFor;
        if (f == null) return fallback;
        try { string s = f(); return string.IsNullOrEmpty(s) ? fallback : s; }
        catch (Exception e) { Debug.LogException(e); return fallback; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { StandingFor = null; GreetingFor = null; }
}
