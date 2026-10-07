using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pooled AudioSources for Sfx.Play: a small 2D pool for UI sounds and a 3D pool for world sounds.
/// Adds random pitch/volume variation per play and throttles identical ids fired within ~30 ms
/// (so two systems reacting to the same event don't double up).
/// Volume categories (Settings → Audio, Docs/UIPass.md §6.2): ambience ids (cricket, owl, rooster) follow the Ambience
/// slider, everything else the Sound effects slider (Master is AudioListener.volume). The 2D pool ignores listener pause,
/// so UI sounds still play during the solo freeze (§5.4).
/// </summary>
public sealed class FeelSfxPlayer
{
    public const float ThrottleSeconds = 0.03f;
    const int Pool2D = 12;
    const int Pool3D = 24;

    readonly AudioSource[] src2D = new AudioSource[Pool2D];
    readonly AudioSource[] src3D = new AudioSource[Pool3D];
    int next2D, next3D;

    static readonly Dictionary<string, float> lastPlayed = new Dictionary<string, float>();
    static readonly Dictionary<string, AudioClip> overrides = new Dictionary<string, AudioClip>();

    // Musical sounds only get a hair of detune so they stay in tune; noisy ones vary more.
    static readonly HashSet<string> ambience = new HashSet<string> { "cricket", "owl", "rooster" };

    /// <summary>The settings slider this id follows: Ambience (cricket, owl, rooster) or Sound effects.</summary>
    public static float CategoryVolume(string key) =>
        Application.isPlaying ? (ambience.Contains(key) ? GameSettings.AmbienceVolume : GameSettings.SfxVolume) : 1f;

    static readonly HashSet<string> musical = new HashSet<string>
    { "ding", "cash", "rare", "legendary", "notify", "combo", "beep", "horn", "truck_full", "sparkle", "car_horn", "car_alarm" };

    public FeelSfxPlayer(Transform parent)
    {
        var root = new GameObject("Sfx").transform;
        root.SetParent(parent, false);
        for (int i = 0; i < Pool2D; i++)
        {
            src2D[i] = MakeSource(root, "2D_" + i, false);
            src2D[i].ignoreListenerPause = true; // UI sounds stay audible while the solo pause holds the listener
        }
        for (int i = 0; i < Pool3D; i++) src3D[i] = MakeSource(root, "3D_" + i, true);
    }

    static AudioSource MakeSource(Transform parent, string name, bool spatial)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = false;
        s.dopplerLevel = 0f;
        s.spatialBlend = spatial ? 1f : 0f;
        if (spatial)
        {
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = 2.5f;
            s.maxDistance = 60f;
            s.spread = 30f;
        }
        return s;
    }

    public void Play(string id, Vector3? pos, float volume, float pitch)
    {
        string key = FeelSfxSynth.Normalize(id);
        float now = Time.realtimeSinceStartup;
        if (lastPlayed.TryGetValue(key, out var last) && now - last < ThrottleSeconds) return;
        lastPlayed[key] = now;

        var clip = ClipFor(key);
        if (clip == null) return;

        float pv = musical.Contains(key) ? 0.015f : 0.08f;
        float vol = Mathf.Clamp(volume, 0f, 4f) * Random.Range(0.85f, 1f) * Sfx.MasterVolume * CategoryVolume(key);
        float pit = Mathf.Clamp(pitch, 0.1f, 3f) * Random.Range(1f - pv, 1f + pv);
        if (vol <= 0.001f) return;

        AudioSource s;
        if (pos.HasValue)
        {
            s = Pick(src3D, ref next3D);
            s.transform.position = pos.Value;
            s.minDistance = MinDistanceFor(key);
        }
        else s = Pick(src2D, ref next2D);
        if (s == null) return;

        s.Stop();
        s.clip = clip;
        s.volume = Mathf.Min(1f, vol);
        s.pitch = pit;
        s.Play();
    }

    static float MinDistanceFor(string key) => key switch
    {
        "boom" => 8f,
        "horn" => 6f,
        "engine_start" or "compactor" or "slam" or "truck_full" => 5f,
        "rooster" => 10f,                       // far-off yard, still audible
        "owl" => 7f,                            // up in the trees
        "raccoon_scream" or "goblin_angry" => 4f,
        "cricket" => 3f,
        "click" or "rustle" or "pop" or "chitter" or "snore" or "sniff" => 1.5f,
        // world life
        "car_alarm" or "car_horn" or "tire_screech" or "clang" => 5f,
        "geyser" or "bark" => 3.5f,
        "mumble" or "mumble2" or "mumble3" or "door_creak" or "yelp" => 1.8f,
        _ => 2.5f,
    };

    static AudioSource Pick(AudioSource[] pool, ref int next)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            var s = pool[i];
            if (s != null && !s.isPlaying) return s;
        }
        // all busy: steal round-robin (roughly the oldest)
        for (int k = 0; k < pool.Length; k++)
        {
            var s = pool[next];
            next = (next + 1) % pool.Length;
            if (s != null) return s;
        }
        return null;
    }

    /// <summary>A clip registered in RubbishAssets as "sfx/&lt;id&gt;" wins over the synth.</summary>
    static AudioClip ClipFor(string key)
    {
        if (!overrides.TryGetValue(key, out var o))
        {
            o = RubbishAssets.Clip("sfx/" + key);
            overrides[key] = o;
        }
        return o != null ? o : FeelSfxSynth.GetClip(key);
    }

    public static void ResetStatics()
    {
        lastPlayed.Clear();
        overrides.Clear();
    }
}
