using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Low-volume day/night ambience, ticked by the Feel runner. While GameClock says it's night (and a little into
/// dusk) and you're in the Neighborhood: sparse cricket chirps scattered around the listener in 3D, and an owl
/// hooting from the trees every so often. At GameClock.MorningCame: a rooster (far off in the Neighborhood,
/// faint through the walls elsewhere). Nothing plays in menus (no local player).
/// </summary>
public sealed class FeelAmbience
{
    public const float CricketVolume = 0.16f;
    public const float OwlVolume = 0.26f;
    public const float RoosterVolume = 0.45f;
    public const float RoosterIndoorVolume = 0.16f;

    float nextCricket, nextOwl;
    bool wasNight;

    /// <summary>True when the active scene is the Neighborhood (or any scene with a TrashRoute).</summary>
    public static bool InNeighborhood()
    {
        if (CasinoWorld.LocalInside) return false; // the casino pocket: no crickets, owls or rooster at full volume
        if (DepotRowWorld.Active) return !DepotRowWorld.LocalIndoors; // one world: indoors = inside a Depot Row shop
        if (TrashRoute.Current != null) return true;
        var n = SceneManager.GetActiveScene().name;
        return !string.IsNullOrEmpty(n) && n.IndexOf("Neighborhood", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool HasPlayer => PlayerController.Local != null;

    static Vector3 ListenerPos()
    {
        var cam = Camera.main;
        if (cam != null) return cam.transform.position;
        var pc = PlayerController.Local;
        return pc != null ? pc.transform.position : Vector3.zero;
    }

    static Vector3 AroundListener(float minR, float maxR, float up)
    {
        var p = ListenerPos();
        var dir = Random.insideUnitCircle.normalized;
        if (dir.sqrMagnitude < 0.01f) dir = Vector2.right;
        float r = Random.Range(minR, maxR);
        return new Vector3(p.x + dir.x * r, p.y + up, p.z + dir.y * r);
    }

    /// <summary>Call every frame (unscaled time).</summary>
    public void Tick()
    {
        if (!Application.isPlaying || !HasPlayer || !InNeighborhood()) { wasNight = false; return; }
        float now = Time.unscaledTime;
        bool night = GameClock.IsNight;
        // crickets fade in through dusk (Night01 ramps 19:00-20:30) and out at dawn
        float amount = Mathf.Clamp01((GameClock.Night01 - 0.35f) / 0.65f);

        if (night && !wasNight) nextOwl = now + Random.Range(3f, 7f); // first hoot shortly after nightfall
        wasNight = night;
        if (amount <= 0.01f) return;

        if (now >= nextCricket)
        {
            Sfx.Play("cricket", AroundListener(5f, 16f, 0.2f), CricketVolume * Random.Range(0.6f, 1f) * amount, Random.Range(0.92f, 1.12f));
            // sometimes a second cricket answers right away
            nextCricket = now + (Random.value < 0.25f ? Random.Range(0.25f, 0.5f) : Random.Range(0.9f, 2.8f) / Mathf.Max(0.35f, amount));
        }
        if (night && now >= nextOwl)
        {
            Sfx.Play("owl", AroundListener(14f, 28f, 6f), OwlVolume * Random.Range(0.75f, 1f), Random.Range(0.94f, 1.04f));
            nextOwl = now + Random.Range(18f, 40f);
        }
    }

    /// <summary>GameClock.MorningCame: cock-a-doodle-doo.</summary>
    public void OnMorning()
    {
        if (!Application.isPlaying || !HasPlayer) return;
        if (InNeighborhood()) Sfx.Play("rooster", AroundListener(20f, 32f, 1.5f), RoosterVolume, Random.Range(0.96f, 1.04f));
        else Sfx.Play("rooster", null, RoosterIndoorVolume, 0.97f);
    }
}
