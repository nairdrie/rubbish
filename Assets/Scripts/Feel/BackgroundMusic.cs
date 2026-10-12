using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The town's background music, on this machine only (cosmetic, never networked): calm, hookless tracks from
/// <c>Resources/Music</c>, shuffled, one at a time, with a short equal-power crossfade at every seam. A lone track therefore
/// loops without a click, and a new one slides in under the tail of the last. It starts before the first scene on a
/// <c>DontDestroyOnLoad</c> object, so the main menu and the game share one unbroken stream; nothing in a scene or prefab
/// refers to it. Drop more clips into <c>Resources/Music</c> and they join the shuffle.
/// <para>Volume is <see cref="BaseVolume"/> × Settings → Audio → Music (<see cref="GameSettings.MusicVolume"/>, 70 by default,
/// read every frame so the slider is heard as it is dragged), and Master is the AudioListener, as for every other source in the
/// project (there is no AudioMixer). The Sound effects and Ambience sliders never touch it. It fades out inside the Lucky
/// Raccoon, whose house band (<see cref="CasinoMusic"/>) plays instead, and back in when you leave.</para>
/// </summary>
public class BackgroundMusic : MonoBehaviour
{
    public const string ResourcesFolder = "Music";
    /// <summary>Level of a full-scale track before the Music setting. The tracks sit around -16 dBFS RMS, so this tucks
    /// them well under footsteps, pickups and the UI.</summary>
    public const float BaseVolume = 0.4f;
    /// <summary>Seconds the end of one track overlaps the start of the next (shorter for a very short clip).</summary>
    public const float Crossfade = 3f;
    /// <summary>How fast the whole stream fades in (at launch, leaving the casino) and out (entering it), 1 = one second.</summary>
    public const float FadeInRate = 0.5f, FadeOutRate = 1f;

    static BackgroundMusic instance;

    readonly AudioSource[] voices = new AudioSource[2];
    readonly List<int> bag = new List<int>();
    AudioClip[] clips;
    int live, last = -1;
    float fade = -1f, fadeLen = Crossfade, duck;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null || Application.isBatchMode) return;
        var found = Resources.LoadAll<AudioClip>(ResourcesFolder);
        if (found.Length == 0) return;
        var go = new GameObject("BackgroundMusic");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<BackgroundMusic>();
        instance.clips = found;
    }

    void Awake()
    {
        for (int i = 0; i < voices.Length; i++)
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = false;
            s.spatialBlend = 0f;
            s.priority = 32;
            s.bypassReverbZones = true;
            s.ignoreListenerPause = true; // the pause menu shouldn't stop the music
            s.volume = 0f;
            voices[i] = s;
        }
    }

    void Update()
    {
        if (clips == null || clips.Length == 0) return;
        float dt = Time.unscaledDeltaTime;

        bool house = CasinoWorld.SceneReady && CasinoWorld.LocalInside;
        duck = Mathf.MoveTowards(duck, house ? 0f : 1f, dt * (house ? FadeOutRate : FadeInRate));

        var cur = voices[live];
        if (fade < 0f)
        {
            if (!cur.isPlaying) Play(live, Pick()); // the first track, or the audio device dropped it
            else if (cur.clip.length - cur.time <= (fadeLen = FadeFor(cur.clip)))
            {
                Play(1 - live, Pick()); // the next track starts under the tail of this one
                fade = 0f;
            }
        }

        float outgoing = 1f, incoming = 0f;
        if (fade >= 0f)
        {
            fade += dt / fadeLen;
            if (fade >= 1f)
            {
                cur.Stop();
                live = 1 - live;
                fade = -1f;
            }
            else
            {
                float a = fade * Mathf.PI * 0.5f;
                outgoing = Mathf.Cos(a);
                incoming = Mathf.Sin(a);
            }
        }

        float level = BaseVolume * GameSettings.MusicVolume * duck;
        voices[live].volume = level * (fade >= 0f ? outgoing : 1f);
        voices[1 - live].volume = fade >= 0f ? level * incoming : 0f;
    }

    void Play(int voice, int clip)
    {
        var s = voices[voice];
        s.clip = clips[clip];
        s.time = 0f;
        s.Play();
    }

    static float FadeFor(AudioClip c) => Mathf.Min(Crossfade, c.length * 0.25f);

    /// <summary>Next track from a shuffled bag, so every track plays once before any repeats (and the same one never plays
    /// twice running unless it is the only one).</summary>
    int Pick()
    {
        if (bag.Count == 0)
        {
            for (int i = 0; i < clips.Length; i++) bag.Add(i);
            for (int i = bag.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (bag[i], bag[j]) = (bag[j], bag[i]);
            }
            if (bag.Count > 1 && bag[bag.Count - 1] == last) (bag[bag.Count - 1], bag[0]) = (bag[0], bag[bag.Count - 1]);
        }
        int n = bag[bag.Count - 1];
        bag.RemoveAt(bag.Count - 1);
        last = n;
        return n;
    }
}
