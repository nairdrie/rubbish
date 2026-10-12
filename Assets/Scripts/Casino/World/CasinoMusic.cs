using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Lucky Raccoon's house band, on this machine only (cosmetic, never networked). One looping track, synthesised at
/// runtime like the rest of the game's audio (no asset: a four-on-the-floor kick, off-beat hats, a clap, a walking bass
/// and organ stabs; 8 bars at 110 BPM), played by one 3D AudioSource:
/// <list type="bullet">
/// <item><b>On the street:</b> it sits at the casino door behind a low-pass filter. At night it thumps: audible from about
/// 30 m, clearly bumping inside 10 m. By day it is barely there. When anyone goes in or out the door "opens": for a
/// moment it is louder and less muffled (<see cref="CasinoFront.DoorOpen01"/>).</item>
/// <item><b>Inside:</b> the same track, unmuffled, as the lounge's music.</item>
/// </list>
/// Volume follows Settings → Audio → Music (<see cref="GameSettings.MusicVolume"/>), like <see cref="BackgroundMusic"/>
/// (which gives way to it inside the casino), not the Ambience or Sound effects sliders; Master is the AudioListener,
/// as for every other source in the project (there is no AudioMixer).
/// </summary>
public class CasinoMusic : MonoBehaviour
{
    public const float Bpm = 110f;
    public const int Bars = 8, SampleRate = 32000;
    public const float StreetNightVolume = 0.85f, StreetDayVolume = 0.05f, InsideVolume = 0.34f;
    public const float MuffledHz = 420f, DoorOpenHz = 2600f, OpenHz = 22000f;
    public const float HearFrom = 32f;

    static AudioClip clip;
    AudioSource source;
    AudioLowPassFilter lowPass;
    Transform speaker;
    float volume, cutoff = MuffledHz, nextScan;
    readonly Dictionary<PlayerController, bool> wasInside = new Dictionary<PlayerController, bool>();
    readonly List<PlayerController> gone = new List<PlayerController>();

    void Update()
    {
        var front = CasinoFront.Instance;
        bool inside = CasinoWorld.LocalInside && CasinoWorld.SceneReady;
        if (!inside && front == null) { Silence(); return; }
        Ensure();
        if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 0.3f; WatchDoor(); }

        float wantVol, wantHz;
        Vector3 at;
        if (inside)
        {
            at = CasinoLayout.Origin + new Vector3(0f, 2.6f, 0f);
            wantVol = InsideVolume;
            wantHz = OpenHz;
            source.spatialBlend = 0.35f; // mostly "in the room", a little direction toward the middle
        }
        else
        {
            at = front.musicAnchor != null ? front.musicAnchor.position : front.transform.position + Vector3.up * 1.5f;
            float open = CasinoFront.DoorOpen01;
            wantVol = Mathf.Lerp(StreetDayVolume, StreetNightVolume, GameClock.Night01) * (1f + 0.4f * open);
            wantHz = Mathf.Lerp(MuffledHz, DoorOpenHz, open);
            source.spatialBlend = 1f;
        }
        speaker.position = at;
        float dt = Time.unscaledDeltaTime;
        volume = Mathf.MoveTowards(volume, wantVol, dt * 1.5f);
        cutoff = Mathf.Lerp(cutoff, wantHz, 1f - Mathf.Exp(-dt * 6f));
        source.volume = volume * GameSettings.MusicVolume;
        lowPass.cutoffFrequency = cutoff;
        bool audible = source.volume > 0.004f;
        if (audible && !source.isPlaying) source.Play();
        else if (!audible && source.isPlaying) source.Pause();
    }

    void Silence()
    {
        volume = 0f;
        if (source != null && source.isPlaying) source.Pause();
    }

    /// <summary>The door "opens" whenever anyone (any machine's player) goes in or out.</summary>
    void WatchDoor()
    {
        var players = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
        foreach (var kv in wasInside) if (kv.Key == null) gone.Add(kv.Key);
        for (int i = 0; i < gone.Count; i++) wasInside.Remove(gone[i]);
        gone.Clear();
        for (int i = 0; i < players.Length; i++)
        {
            var p = players[i];
            if (p == null) continue;
            bool now = CasinoWorld.IsInside(p);
            if (wasInside.TryGetValue(p, out bool was) && was != now) CasinoFront.PulseDoor();
            wasInside[p] = now;
        }
    }

    void Ensure()
    {
        if (source != null) return;
        var go = new GameObject("Casino music");
        go.transform.SetParent(transform, false);
        speaker = go.transform;
        source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.volume = 0f; // never a loud first frame
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.spread = 60f;
        source.rolloffMode = AudioRolloffMode.Custom;
        source.minDistance = 3f;
        source.maxDistance = HearFrom;
        // Full to 4 m, clearly thumping at 10 m, a murmur at 25 m, gone at 32 m.
        source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(4f / HearFrom, 1f), new Keyframe(10f / HearFrom, 0.55f),
            new Keyframe(20f / HearFrom, 0.16f), new Keyframe(27f / HearFrom, 0.04f), new Keyframe(1f, 0f)));
        lowPass = go.AddComponent<AudioLowPassFilter>();
        lowPass.cutoffFrequency = MuffledHz;
        lowPass.lowpassResonanceQ = 1.1f;
        if (clip == null) clip = BuildLoop();
        source.clip = clip;
    }

    void OnDestroy()
    {
        if (source != null) Destroy(source.gameObject);
    }

    // ------------------------------------------------------------------ the track

    /// <summary>
    /// An original 8-bar lounge-house loop, written sample by sample: kick on every beat, hats on the off-beats, a clap on
    /// 2 and 4, an octave-hopping bass and organ stabs over Am7 - Dm7 - G7 - Cmaj7 (two bars each). Every voice wraps
    /// around the end of the buffer, so the loop has no seam.
    /// </summary>
    public static AudioClip BuildLoop()
    {
        float beat = 60f / Bpm;
        int n = Mathf.RoundToInt(Bars * 4 * beat * SampleRate);
        var buf = new float[n];
        uint rng = 0x1962u;
        float Noise() { rng = rng * 1664525u + 1013904223u; return ((rng >> 8) / 8388608f) - 1f; } // cosmetic noise source

        void Add(float atSeconds, float length, System.Func<float, float> voice)
        {
            int start = Mathf.RoundToInt(atSeconds * SampleRate), len = Mathf.RoundToInt(length * SampleRate);
            for (int i = 0; i < len; i++) buf[(start + i) % n] += voice(i / (float)SampleRate);
        }

        // roots (MIDI) per pair of bars, and the stab chords
        int[] roots = { 45, 50, 43, 48 };
        int[][] chords =
        {
            new[] { 57, 60, 64, 67 }, new[] { 57, 60, 62, 65 }, new[] { 55, 59, 62, 65 }, new[] { 55, 59, 60, 64 },
        };
        float Hz(int midi) => 440f * Mathf.Pow(2f, (midi - 69) / 12f);
        const float Tau = Mathf.PI * 2f;

        for (int bar = 0; bar < Bars; bar++)
        {
            int section = (bar / 2) % 4;
            for (int b = 0; b < 4; b++)
            {
                float t0 = (bar * 4 + b) * beat;
                // kick: a sine dropping from 150 to 48 Hz with a click
                Add(t0, 0.32f, t =>
                {
                    float f = 48f + 102f * Mathf.Exp(-t * 34f);
                    float phase = Tau * (48f * t + 102f * (1f - Mathf.Exp(-t * 34f)) / 34f);
                    return Mathf.Sin(phase) * Mathf.Exp(-t * 9f) * 0.62f + (t < 0.004f ? 0.18f * (1f - t / 0.004f) : 0f) + 0f * f;
                });
                // off-beat hat
                float last = 0f;
                Add(t0 + beat * 0.5f, 0.07f, t =>
                {
                    float x = Noise(), hp = x - last; last = x;
                    return hp * Mathf.Exp(-t * 70f) * 0.11f;
                });
                // clap on 2 and 4: three quick noise bursts
                if (b == 1 || b == 3)
                    Add(t0, 0.16f, t =>
                    {
                        float env = Mathf.Exp(-t * 26f) + 0.7f * Mathf.Exp(-Mathf.Abs(t - 0.012f) * 300f) + 0.6f * Mathf.Exp(-Mathf.Abs(t - 0.024f) * 300f);
                        return Noise() * env * 0.085f;
                    });
                // bass: root on the "and", octave on the "a" of some beats
                int root = roots[section];
                for (int s = 0; s < 4; s++)
                {
                    if (s == 0) continue; // the kick owns the downbeat
                    int note = root + (s == 3 && (b == 1 || b == 3) ? 12 : s == 2 ? 0 : (b == 2 ? 7 : 0));
                    if (s == 1 && b % 2 == 0) continue;
                    float f = Hz(note);
                    Add(t0 + beat * 0.25f * s, beat * 0.24f, t =>
                    {
                        float env = Mathf.Min(t / 0.006f, 1f) * Mathf.Exp(-t * 7f);
                        float w = Tau * f * t;
                        return (Mathf.Sin(w) + 0.45f * Mathf.Sin(2f * w) + 0.2f * Mathf.Sin(3f * w)) * env * 0.2f;
                    });
                }
                // organ stabs: on the "and" of 2 and the "and" of 4 (and a pickup into every second bar)
                bool stab = b == 1 || b == 3 || (b == 2 && bar % 2 == 1);
                if (stab)
                {
                    var chord = chords[section];
                    float len = b == 3 ? beat * 0.45f : beat * 0.3f;
                    Add(t0 + beat * 0.5f, len + 0.08f, t =>
                    {
                        float env = Mathf.Min(t / 0.008f, 1f) * (t < len ? 1f : Mathf.Max(0f, 1f - (t - len) / 0.08f));
                        float sum = 0f;
                        for (int k = 0; k < chord.Length; k++)
                        {
                            float w = Tau * Hz(chord[k]) * t;
                            sum += Mathf.Sin(w) + 0.5f * Mathf.Sin(2f * w) + 0.33f * Mathf.Sin(3f * w) + 0.12f * Mathf.Sin(4f * w);
                        }
                        return sum * env * 0.03f * (1f + 0.25f * Mathf.Sin(Tau * 6.2f * t)); // a little Leslie wobble
                    });
                }
            }
        }

        // soft clip so the sum never crackles
        for (int i = 0; i < n; i++) buf[i] = (float)System.Math.Tanh(buf[i] * 1.25f) * 0.85f;
        var c = AudioClip.Create("Lucky Raccoon house band (synth loop)", n, 1, SampleRate, false);
        c.SetData(buf, 0);
        c.hideFlags = HideFlags.DontSave;
        return c;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => clip = null;
}
