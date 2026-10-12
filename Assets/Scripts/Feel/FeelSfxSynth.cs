using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural cartoon sound effects. Every Sfx id is rendered once (44.1 kHz mono PCM) from a small recipe of
/// oscillators, FM bells, filtered noise and crackle, then cached as an AudioClip. Pure code: works in edit mode
/// too (used by the verification eval). Rendering a clip takes a few milliseconds.
/// </summary>
public static class FeelSfxSynth
{
    public const int SampleRate = 44100;

    /// <summary>Every id with a dedicated recipe (anything else renders as "blip").</summary>
    public static readonly string[] KnownIds =
    {
        "pickup", "drop", "throw", "whoosh", "thud", "bag_land", "bag_burst", "rustle", "toss_in", "truck_full",
        "ding", "cash", "buzz", "sparkle", "rare", "legendary", "crunch", "compactor", "boom", "click", "door",
        "notify", "squeak", "pop", "combo", "horn", "engine_start", "beep", "slam", "splat",
        // sort streak (SortStreakHud): a climbing pluck per sort, a stinger per tier, a fanfare at x10, a deflating "womp" when it is lost
        "streak_note", "streak_up", "streak_max", "streak_lost", "streak_cool",
        // living world: critters, rabies, day/night ambience
        "hiss", "chitter", "snore", "grumble", "bite", "ow", "syringe", "rooster", "owl", "cricket",
        "goblin_angry", "raccoon_scream", "sniff",
        // world life (Docs/WorldLife.md §11 Feel row): knockables, cars, residents, dogs
        "clang", "sparks", "geyser", "car_horn", "car_alarm", "tire_screech", "door_creak", "oof", "yelp", "bark",
        "mumble", "mumble2", "mumble3",
        "blip",
    };

    static readonly HashSet<string> known = new HashSet<string>(KnownIds);
    static readonly Dictionary<string, AudioClip> cache = new Dictionary<string, AudioClip>();

    /// <summary>Lower-cased id if it has a recipe, else "blip".</summary>
    public static string Normalize(string id)
    {
        if (string.IsNullOrEmpty(id)) return "blip";
        if (known.Contains(id)) return id;
        var lower = id.Trim().ToLowerInvariant();
        return known.Contains(lower) ? lower : "blip";
    }

    /// <summary>Cached synthesized clip for an id (unknown ids get the blip).</summary>
    public static AudioClip GetClip(string id)
    {
        id = Normalize(id);
        if (cache.TryGetValue(id, out var clip) && clip != null) return clip;
        var data = Render(id);
        clip = AudioClip.Create("sfx_" + id, data.Length, 1, SampleRate, false);
        clip.SetData(data, 0);
        clip.hideFlags = HideFlags.DontSave;
        cache[id] = clip;
        return clip;
    }

    /// <summary>Render the raw samples for an id (normalized, -1..1). Deterministic per id.</summary>
    public static float[] Render(string id)
    {
        id = Normalize(id);
        int seed = 17;
        foreach (char ch in id) seed = seed * 31 + ch;
        return Build(id, (uint)seed | 1u);
    }

    // ------------------------------------------------------------------------------------------------
    // Recipes
    // ------------------------------------------------------------------------------------------------
    static float[] Build(string id, uint seed)
    {
        Synth s;
        switch (id)
        {
            case "pickup":
                s = new Synth(0.17f, seed);
                s.Tone(0f, 0.15f, 320f, 780f, W.Sine, 0.8f, 0.004f, 1.6f);
                s.Tone(0f, 0.12f, 640f, 1560f, W.Soft, 0.12f, 0.004f, 2f);
                s.Noise(0f, 0.05f, 0.25f, F.BP, 3500f, 5000f, 0.001f, 2f);
                return s.Finish(0.75f);

            case "drop":
                s = new Synth(0.19f, seed);
                s.Tone(0f, 0.17f, 540f, 170f, W.Sine, 0.7f, 0.003f, 2f);
                s.Noise(0f, 0.06f, 0.5f, F.LP, 900f, 300f, 0.001f, 3f);
                return s.Finish(0.7f);

            case "throw":
                s = new Synth(0.27f, seed);
                s.Noise(0f, 0.25f, 1f, F.BP, 450f, 2800f, 0.07f, 1.4f, 0.7f);
                s.Tone(0f, 0.12f, 190f, 420f, W.Tri, 0.25f, 0.01f, 1.5f);
                return s.Finish(0.7f);

            case "whoosh":
                s = new Synth(0.45f, seed);
                s.Noise(0f, 0.42f, 1f, F.BP, 350f, 2600f, 0.19f, 1.3f, 0.6f, 0f, 0f, 700f);
                s.Noise(0f, 0.42f, 0.3f, F.BP, 900f, 5000f, 0.2f, 1.6f, 0.8f, 0f, 0f, 1500f);
                return s.Finish(0.7f);

            case "thud":
                s = new Synth(0.25f, seed);
                s.Tone(0f, 0.23f, 125f, 40f, W.Sine, 1f, 0.002f, 2.2f);
                s.Noise(0f, 0.05f, 0.45f, F.LP, 500f, 150f, 0.001f, 2.5f);
                return s.Finish(0.9f);

            case "bag_land":
                s = new Synth(0.35f, seed);
                s.Noise(0f, 0.26f, 0.8f, F.LP, 800f, 180f, 0.002f, 2f);
                s.Tone(0f, 0.2f, 100f, 45f, W.Sine, 0.8f, 0.002f, 2f);
                s.Crackle(0.02f, 0.28f, 220f, 0.3f, 4200f);
                return s.Finish(0.85f);

            case "bag_burst":
                s = new Synth(0.6f, seed);
                s.Noise(0f, 0.07f, 1f, F.BP, 2600f, 1800f, 0.001f, 1.5f, 0.8f);
                s.Tone(0.01f, 0.35f, 340f, 70f, W.Sine, 0.75f, 0.003f, 1.8f);
                s.Crackle(0.02f, 0.5f, 380f, 0.45f, 3800f);
                s.Noise(0.03f, 0.45f, 0.35f, F.LP, 1100f, 300f, 0.01f, 1.6f);
                return s.Finish(0.9f, 1.5f);

            case "rustle":
                s = new Synth(0.38f, seed);
                s.Crackle(0f, 0.36f, 700f, 0.8f, 4800f, 0.12f, 1.2f);
                s.Noise(0f, 0.36f, 0.15f, F.BP, 5000f, 5500f, 0.12f, 1.2f);
                return s.Finish(0.7f, 3f);

            case "toss_in":
                s = new Synth(0.4f, seed);
                s.Noise(0f, 0.3f, 0.55f, F.BP, 1000f, 240f, 0.04f, 1.4f, 0.9f);
                s.Tone(0.03f, 0.33f, 280f, 62f, W.Sine, 0.95f, 0.004f, 2f);
                s.Tone(0.03f, 0.25f, 140f, 55f, W.Tri, 0.3f, 0.004f, 2f);
                s.Crackle(0.05f, 0.2f, 150f, 0.15f, 3500f);
                return s.Finish(0.9f);

            case "truck_full": // sad trombone: wah wah wah waaaah
                s = new Synth(1.6f, seed);
                s.Brass(0.00f, 0.30f, 293.7f, 0.8f, 0f, 0f, 280f, 1700f);
                s.Brass(0.32f, 0.30f, 277.2f, 0.8f, 0f, 0f, 280f, 1700f);
                s.Brass(0.64f, 0.30f, 261.6f, 0.8f, 0f, 0f, 280f, 1700f);
                s.Brass(0.96f, 0.62f, 246.9f, 0.85f, 6f, 0.03f, 280f, 1500f, 0f, -0.04f);
                return s.Finish(0.85f);

            case "ding":
                s = new Synth(0.95f, seed);
                s.Bell(0f, 0.95f, 1318.5f, 3.5f, 2.2f, 0.8f, 0.32f, 0.12f);
                s.Bell(0f, 0.6f, 2637f, 2f, 0.6f, 0.18f, 0.18f, 0.1f);
                return s.Finish(0.75f);

            case "cash": // cha-ching
                s = new Synth(0.9f, seed);
                s.Noise(0f, 0.035f, 0.7f, F.HP, 5000f, 5000f, 0.001f, 2f);
                s.Noise(0.04f, 0.06f, 0.4f, F.BP, 4000f, 3000f, 0.001f, 1.5f);
                s.Bell(0.06f, 0.8f, 2093f, 1.41f, 2.5f, 0.55f, 0.28f, 0.08f);
                s.Bell(0.1f, 0.8f, 3136f, 1.41f, 2f, 0.6f, 0.3f, 0.08f);
                s.Ring(0.06f, 0.5f, 1046.5f, 0.15f, 0.2f);
                s.Crackle(0.08f, 0.3f, 90f, 0.15f, 6000f);
                return s.Finish(0.8f);

            case "buzz": // wrong-bin buzzer
                s = new Synth(0.46f, seed);
                s.Tone(0f, 0.42f, 110f, 106f, W.Square, 0.4f, 0.004f, 0.25f);
                s.Tone(0f, 0.42f, 116.5f, 112f, W.Saw, 0.35f, 0.004f, 0.25f);
                s.FilterRegion(0f, 0.46f, F.LP, 2400f);
                return s.Finish(0.7f);

            case "sparkle":
            {
                s = new Synth(0.65f, seed);
                float[] notes = { 2093f, 2349.3f, 2637f, 3136f, 3520f, 4186f };
                for (int i = 0; i < 8; i++)
                {
                    float t = i * 0.05f + s.Rand01() * 0.02f;
                    float f = notes[(int)(s.Rand01() * notes.Length) % notes.Length];
                    s.Ring(t, 0.2f, f, 0.35f, 0.06f);
                    s.Ring(t, 0.15f, f * 2f, 0.08f, 0.04f);
                }
                return s.Finish(0.55f);
            }

            case "rare": // rising arpeggio
            {
                s = new Synth(0.95f, seed);
                float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f, 1318.5f };
                for (int i = 0; i < notes.Length; i++)
                {
                    bool last = i == notes.Length - 1;
                    float t = i * 0.075f, dur = last ? 0.6f : 0.25f;
                    s.Tone(t, dur, notes[i], notes[i], W.Tri, 0.45f, 0.004f, last ? 1.2f : 2f);
                    s.Ring(t, dur, notes[i] * 2f, 0.12f, 0.12f);
                }
                s.Bell(0.3f, 0.6f, 2637f, 3.5f, 1.2f, 0.2f, 0.25f, 0.08f);
                return s.Finish(0.8f);
            }

            case "legendary": // da-da-da DAAAA
            {
                s = new Synth(1.9f, seed);
                for (int i = 0; i < 3; i++) s.Brass(i * 0.13f, 0.11f, 392f, 0.55f, 0f, 0f, 700f, 2600f);
                s.Brass(0.40f, 1.35f, 523.25f, 0.5f, 5.5f, 0.012f, 700f, 2400f);
                s.Brass(0.40f, 1.35f, 659.25f, 0.4f, 5.5f, 0.012f, 700f, 2400f);
                s.Brass(0.40f, 1.35f, 783.99f, 0.4f, 5.5f, 0.012f, 700f, 2400f);
                s.Brass(0.40f, 1.35f, 261.6f, 0.35f, 5.5f, 0.01f, 400f, 1200f);
                s.Tone(0.40f, 0.6f, 90f, 50f, W.Sine, 0.6f, 0.002f, 2f);
                s.Noise(0.40f, 1.0f, 0.25f, F.HP, 6000f, 6000f, 0.002f, 2.5f);
                float[] notes = { 2093f, 2637f, 3136f, 4186f };
                for (int i = 0; i < 10; i++)
                    s.Ring(0.6f + i * 0.1f + s.Rand01() * 0.04f, 0.2f, notes[i % notes.Length], 0.15f, 0.06f);
                return s.Finish(0.9f);
            }

            case "crunch":
                s = new Synth(0.5f, seed);
                s.Crackle(0f, 0.45f, 1600f, 0.9f, 2200f, 0.002f, 1.2f);
                s.Crackle(0f, 0.45f, 500f, 0.4f, 5200f, 0.002f, 1.2f);
                s.Noise(0f, 0.4f, 0.5f, F.LP, 1500f, 400f, 0.003f, 1.5f);
                s.Tone(0f, 0.3f, 75f, 40f, W.Sine, 0.45f, 0.003f, 2f);
                return s.Finish(0.85f, 1.5f);

            case "compactor": // hydraulic whirr ... CLUNK
                s = new Synth(1.1f, seed);
                s.Tone(0f, 0.74f, 48f, 82f, W.Saw, 0.45f, 0.08f, 0.3f);
                s.FilterRegion(0f, 0.74f, F.LP, 500f);
                s.Noise(0f, 0.72f, 0.22f, F.BP, 1400f, 2200f, 0.1f, 0.3f, 1f, 12f, 0.5f);
                s.Tone(0.72f, 0.3f, 95f, 40f, W.Sine, 1f, 0.002f, 2f);
                s.Noise(0.72f, 0.12f, 0.7f, F.LP, 2000f, 400f, 0.001f, 2f);
                s.Ring(0.72f, 0.35f, 220f, 0.2f, 0.12f);
                s.Ring(0.72f, 0.35f, 563f, 0.15f, 0.1f);
                s.Ring(0.72f, 0.3f, 890f, 0.1f, 0.08f);
                return s.Finish(0.9f);

            case "boom":
                s = new Synth(1.4f, seed);
                s.Noise(0f, 0.05f, 0.6f, F.BP, 1500f, 800f, 0.001f, 2f);
                s.Noise(0f, 1.3f, 1f, F.LP, 3500f, 120f, 0.002f, 2.4f, 0.9f);
                s.Tone(0f, 1.0f, 120f, 26f, W.Sine, 1f, 0.003f, 1.6f);
                s.Crackle(0.05f, 0.7f, 70f, 0.3f, 2500f);
                return s.Finish(0.95f, 1.4f);

            case "click":
                s = new Synth(0.045f, seed);
                s.Tone(0f, 0.025f, 2300f, 1800f, W.Sine, 0.5f, 0.0005f, 3f);
                s.Noise(0f, 0.012f, 0.6f, F.HP, 4000f, 4000f, 0.0005f, 2f);
                return s.Finish(0.5f);

            case "door": // rolling rattle then clunk
                s = new Synth(0.85f, seed);
                s.Noise(0f, 0.62f, 0.55f, F.BP, 700f, 1100f, 0.05f, 0.4f, 0.6f, 21f, 0.9f);
                s.Tone(0f, 0.6f, 60f, 70f, W.Saw, 0.15f, 0.05f, 0.4f);
                s.Tone(0.6f, 0.25f, 110f, 45f, W.Sine, 0.9f, 0.002f, 2f);
                s.Noise(0.6f, 0.1f, 0.5f, F.LP, 1200f, 300f, 0.001f, 2f);
                s.Ring(0.6f, 0.25f, 330f, 0.12f, 0.08f);
                return s.Finish(0.8f);

            case "notify": // ding-dong up
                s = new Synth(0.62f, seed);
                s.Bell(0f, 0.45f, 1318.5f, 2f, 0.8f, 0.6f, 0.18f, 0.08f);
                s.Bell(0.12f, 0.48f, 1975.5f, 2f, 0.8f, 0.6f, 0.2f, 0.08f);
                return s.Finish(0.7f);

            case "squeak":
                s = new Synth(0.2f, seed);
                s.Tone(0f, 0.18f, 950f, 1900f, W.Sine, 0.7f, 0.03f, 0.8f, 32f, 0.05f);
                s.Tone(0f, 0.18f, 1900f, 3800f, W.Sine, 0.1f, 0.03f, 0.8f, 32f, 0.05f);
                return s.Finish(0.6f);

            case "pop":
                s = new Synth(0.1f, seed);
                s.Tone(0f, 0.07f, 420f, 1500f, W.Sine, 0.9f, 0.001f, 2.2f);
                s.Noise(0f, 0.008f, 0.4f, F.HP, 3000f, 3000f, 0.0005f, 1f);
                return s.Finish(0.75f);

            case "combo":
                s = new Synth(0.3f, seed);
                s.Tone(0f, 0.08f, 880f, 880f, W.Soft, 0.4f, 0.002f, 1.5f);
                s.Tone(0.065f, 0.22f, 1318.5f, 1318.5f, W.Soft, 0.45f, 0.002f, 1.5f);
                s.Ring(0.065f, 0.22f, 2637f, 0.12f, 0.08f);
                return s.Finish(0.65f);

            // ---------------------------------------------------------------- sort streak (SortStreakHud)
            case "streak_note": // one bright pluck; the HUD pitches it up a pentatonic ladder as the streak grows
                s = new Synth(0.42f, seed);
                s.Bell(0f, 0.4f, 659.25f, 2f, 1.6f, 0.7f, 0.14f, 0.07f);
                s.Ring(0f, 0.3f, 1318.5f, 0.2f, 0.07f);
                s.Ring(0f, 0.2f, 3136f, 0.06f, 0.03f);
                s.Noise(0f, 0.02f, 0.25f, F.HP, 5000f, 5000f, 0.0005f, 2f);
                return s.Finish(0.6f);

            case "streak_up": // tier up: a fast rising run into a shimmering top note, with an air whoosh underneath
            {
                s = new Synth(0.85f, seed);
                float[] run = { 784f, 987.8f, 1174.7f, 1568f };
                for (int i = 0; i < run.Length; i++)
                {
                    bool top = i == run.Length - 1;
                    s.Bell(i * 0.055f, top ? 0.7f : 0.22f, run[i], 2f, 2f, 0.5f, top ? 0.3f : 0.1f, 0.1f);
                    s.Tone(i * 0.055f, top ? 0.5f : 0.14f, run[i] * 0.5f, run[i] * 0.5f, W.Tri, 0.18f, 0.003f, 1.6f);
                }
                s.Noise(0f, 0.3f, 0.3f, F.BP, 500f, 5200f, 0.2f, 1.5f, 0.7f);
                for (int i = 0; i < 7; i++)
                    s.Ring(0.2f + i * 0.045f + s.Rand01() * 0.02f, 0.22f, 2637f + 523f * (i % 4), 0.1f, 0.06f);
                return s.Finish(0.85f);
            }

            case "streak_max": // x10: a brass run, a held major chord, a cymbal wash and a sparkle shower
            {
                s = new Synth(2.1f, seed);
                float[] run = { 523.25f, 659.25f, 783.99f, 1046.5f };
                for (int i = 0; i < run.Length; i++) s.Brass(i * 0.085f, 0.12f, run[i], 0.5f, 0f, 0f, 700f, 2800f);
                float[] chord = { 523.25f, 659.25f, 783.99f, 1046.5f, 1318.5f };
                for (int i = 0; i < chord.Length; i++) s.Brass(0.36f, 1.5f, chord[i], i == 0 ? 0.5f : 0.34f, 5.5f, 0.012f, 600f, 2600f);
                s.Tone(0.36f, 0.7f, 110f, 48f, W.Sine, 0.7f, 0.003f, 2f);
                s.Noise(0.36f, 1.6f, 0.3f, F.HP, 7000f, 5500f, 0.004f, 2.2f);
                s.Noise(0.34f, 0.35f, 0.4f, F.BP, 1200f, 6000f, 0.25f, 1.2f, 0.7f);
                float[] stars = { 2093f, 2637f, 3136f, 4186f, 3520f };
                for (int i = 0; i < 18; i++)
                    s.Ring(0.5f + i * 0.07f + s.Rand01() * 0.03f, 0.3f, stars[i % stars.Length], 0.14f, 0.07f);
                return s.Finish(0.9f, 1.3f);
            }

            case "streak_lost": // a deflating "womp" with a soft thud: sad, but not a scold
                s = new Synth(0.7f, seed);
                s.Tone(0f, 0.55f, 420f, 130f, W.Saw, 0.5f, 0.01f, 1.4f, 7f, 0.03f);
                s.FilterRegion(0f, 0.55f, F.LP, 1100f);
                s.Tone(0.02f, 0.3f, 150f, 55f, W.Sine, 0.7f, 0.003f, 2f);
                s.Noise(0.04f, 0.4f, 0.18f, F.LP, 2400f, 300f, 0.01f, 1.6f);
                return s.Finish(0.7f);

            case "streak_cool": // a heat tier slips away: one small falling bloop
                s = new Synth(0.22f, seed);
                s.Tone(0f, 0.2f, 780f, 440f, W.Sine, 0.6f, 0.004f, 1.6f);
                s.Ring(0f, 0.15f, 1170f, 0.12f, 0.05f);
                return s.Finish(0.5f);

            case "horn": // HONK
                s = new Synth(0.6f, seed);
                s.Brass(0f, 0.55f, 262f, 0.55f, 5f, 0.004f, 1300f, 1500f, 0.08f);
                s.Brass(0f, 0.55f, 330f, 0.5f, 5f, 0.004f, 1300f, 1500f, 0.08f);
                return s.Finish(0.8f, 2f);

            case "engine_start": // rrr-rrr-rrr VROOM
                s = new Synth(1.35f, seed);
                for (int k = 0; k < 3; k++)
                {
                    float t = k * 0.18f;
                    s.Noise(t, 0.14f, 0.45f, F.LP, 600f, 400f, 0.01f, 0.6f, 1f, 18f, 1f);
                    s.Tone(t, 0.14f, 38f, 44f, W.Square, 0.25f, 0.01f, 0.6f);
                }
                s.Tone(0.56f, 0.78f, 38f, 95f, W.Saw, 0.6f, 0.02f, 0.6f, 0f, 0f, 68f, 0.4f);
                s.FilterRegion(0.54f, 0.81f, F.LP, 700f);
                s.Noise(0.56f, 0.75f, 0.3f, F.LP, 500f, 300f, 0.02f, 0.8f, 1f, 28f, 0.8f);
                return s.Finish(0.85f, 1.5f);

            case "beep": // 1 kHz reverse beeper
                s = new Synth(0.3f, seed);
                s.Tone(0f, 0.24f, 1000f, 1000f, W.Soft, 0.6f, 0.004f, 0.08f);
                return s.Finish(0.45f);

            case "slam":
                s = new Synth(0.6f, seed);
                s.Noise(0f, 0.12f, 0.9f, F.LP, 3000f, 500f, 0.001f, 2f);
                s.Tone(0f, 0.3f, 90f, 42f, W.Sine, 1f, 0.002f, 2f);
                s.Ring(0f, 0.55f, 181f, 0.22f, 0.2f);
                s.Ring(0f, 0.5f, 473.5f, 0.2f, 0.16f);
                s.Ring(0f, 0.45f, 476.5f, 0.12f, 0.16f);
                s.Ring(0f, 0.45f, 791f, 0.15f, 0.13f);
                s.Ring(0f, 0.4f, 1234f, 0.1f, 0.1f);
                return s.Finish(0.95f);

            case "splat":
                s = new Synth(0.34f, seed);
                s.Noise(0f, 0.28f, 0.9f, F.LP, 2200f, 250f, 0.001f, 2.6f, 0.8f);
                s.Tone(0f, 0.16f, 260f, 55f, W.Sine, 0.6f, 0.001f, 1.6f);
                s.Crackle(0.015f, 0.25f, 320f, 0.35f, 1800f);
                return s.Finish(0.85f);

            // ---------------------------------------------------------------- living world
            case "hiss": // angry cat-raccoon HSSSSS with a spitty start and a little throat growl
                s = new Synth(0.72f, seed);
                s.Noise(0f, 0.05f, 0.8f, F.BP, 2600f, 2400f, 0.001f, 2f, 0.7f);
                s.Noise(0.01f, 0.66f, 1f, F.HP, 3200f, 4200f, 0.03f, 0.9f);
                s.Noise(0.01f, 0.66f, 0.55f, F.BP, 5200f, 4600f, 0.03f, 0.9f, 0.5f, 19f, 0.35f);
                s.Voice(0.02f, 0.5f, 118f, 96f, 0.3f, 0.03f, 0.8f, 520f, 1150f, 430f, 950f, 23f, 0.05f, 0.5f);
                return s.Finish(0.7f, 1.2f);

            case "chitter": // raccoon chatter: a burst of squeaky little chirps + teeth clicks
            {
                s = new Synth(0.6f, seed);
                float t = 0f;
                for (int i = 0; i < 10 && t < 0.52f; i++)
                {
                    float f = 1700f + s.Rand01() * 1300f;
                    float d = 0.03f + s.Rand01() * 0.025f;
                    s.Tone(t, d, f, f * (s.Rand01() < 0.5f ? 1.35f : 0.78f), W.Soft, 0.55f + s.Rand01() * 0.3f, 0.002f, 1.4f, 55f, 0.07f);
                    t += d + 0.012f + s.Rand01() * 0.025f;
                }
                s.Crackle(0f, 0.55f, 110f, 0.25f, 4200f);
                return s.Finish(0.62f);
            }

            case "snore": // hnnnkkk-shhh... mimimimi
                s = new Synth(2.0f, seed);
                s.Noise(0f, 1.0f, 0.65f, F.LP, 450f, 900f, 0.35f, 0.5f, 1f, 26f, 0.85f);
                s.Tone(0f, 1.0f, 68f, 80f, W.Saw, 0.35f, 0.35f, 0.5f, 26f, 0.03f);
                s.FilterRegion(0f, 1.02f, F.LP, 750f);
                s.Tone(1.1f, 0.78f, 1050f, 1500f, W.Sine, 0.32f, 0.08f, 0.9f, 6.5f, 0.05f);
                s.Noise(1.05f, 0.85f, 0.22f, F.BP, 1800f, 1250f, 0.1f, 1.3f, 0.8f);
                return s.Finish(0.6f);

            case "grumble": // goblin muttering: a string of low, cranky syllables
            {
                s = new Synth(1.05f, seed);
                float[,] vowels = { { 620f, 1100f }, { 450f, 850f }, { 560f, 1650f }, { 320f, 900f }, { 700f, 1250f } };
                float t = 0f;
                for (int k = 0; k < 6 && t < 0.9f; k++)
                {
                    int va = (int)(s.Rand01() * 5f) % 5, vb = (int)(s.Rand01() * 5f) % 5;
                    float p = 105f + s.Rand01() * 55f;
                    float d = 0.1f + s.Rand01() * 0.08f;
                    s.Voice(t, d, p, p * (0.78f + s.Rand01() * 0.3f), 0.7f + s.Rand01() * 0.3f, 0.015f, 0.7f,
                            vowels[va, 0], vowels[va, 1], vowels[vb, 0], vowels[vb, 1], 9f, 0.04f, 0.25f);
                    t += d + 0.02f + s.Rand01() * 0.05f;
                }
                return s.Finish(0.75f, 1.6f);
            }

            case "bite": // CHOMP: tooth click, meaty crunch, thump
                s = new Synth(0.32f, seed);
                s.Noise(0f, 0.025f, 1f, F.BP, 3200f, 2600f, 0.001f, 2f, 0.6f);
                s.Tone(0f, 0.13f, 280f, 70f, W.Sine, 0.85f, 0.002f, 2f);
                s.Crackle(0.01f, 0.2f, 950f, 0.65f, 2500f);
                s.Noise(0.02f, 0.16f, 0.45f, F.LP, 1600f, 380f, 0.002f, 2f);
                s.Noise(0.085f, 0.02f, 0.5f, F.BP, 3600f, 3000f, 0.001f, 2f, 0.6f);
                return s.Finish(0.9f, 1.3f);

            case "ow": // cartoon "OW!": bright "a" collapsing into "w", pitch squeaks up then drops
                s = new Synth(0.58f, seed);
                s.Voice(0f, 0.52f, 390f, 540f, 1f, 0.012f, 0.55f, 820f, 1300f, 330f, 650f, 6f, 0.02f, 0.06f, 290f, 0.22f);
                s.Voice(0f, 0.52f, 780f, 1080f, 0.18f, 0.012f, 0.6f, 900f, 2500f, 400f, 1400f, 0f, 0f, 0f, 580f, 0.22f);
                return s.Finish(0.85f, 1.2f);

            case "syringe": // tink! ... psssht ... squeeeak ... boing
                s = new Synth(0.95f, seed);
                s.Ring(0f, 0.25f, 3200f, 0.4f, 0.05f);
                s.Ring(0f, 0.2f, 4850f, 0.15f, 0.03f);
                s.Noise(0.1f, 0.32f, 0.45f, F.BP, 2400f, 5200f, 0.02f, 1.2f, 0.6f);
                s.Tone(0.16f, 0.45f, 700f, 1500f, W.Sine, 0.35f, 0.03f, 0.6f, 24f, 0.04f);
                s.Tone(0.62f, 0.32f, 300f, 620f, W.Sine, 0.32f, 0.004f, 1.5f, 17f, 0.14f);
                return s.Finish(0.75f);

            case "rooster": // cock-a-doodle-doooo (buzzy, nasal, a bit off-key)
                s = new Synth(1.65f, seed);
                s.Voice(0f, 0.14f, 520f, 610f, 0.8f, 0.01f, 0.5f, 760f, 1700f, 560f, 1300f, 0f, 0f, 0.1f);
                s.Voice(0.17f, 0.12f, 600f, 650f, 0.75f, 0.01f, 0.5f, 800f, 1600f, 780f, 1500f, 0f, 0f, 0.1f);
                s.Voice(0.32f, 0.2f, 650f, 740f, 0.85f, 0.01f, 0.5f, 480f, 1100f, 400f, 950f, 0f, 0f, 0.1f);
                s.Voice(0.56f, 0.95f, 770f, 930f, 1f, 0.02f, 0.7f, 520f, 1250f, 380f, 820f, 7f, 0.03f, 0.18f, 610f, 0.25f);
                return s.Finish(0.8f, 1.6f);

            case "owl": // hoo ... hoo-hoooo (soft, breathy)
                s = new Synth(1.4f, seed);
                s.Tone(0f, 0.28f, 420f, 392f, W.Sine, 0.7f, 0.05f, 0.9f);
                s.Tone(0f, 0.28f, 840f, 784f, W.Sine, 0.07f, 0.05f, 0.9f);
                s.Noise(0f, 0.28f, 0.1f, F.BP, 460f, 400f, 0.05f, 1f, 0.6f);
                s.Tone(0.5f, 0.18f, 432f, 404f, W.Sine, 0.6f, 0.035f, 0.9f);
                s.Tone(0.74f, 0.6f, 430f, 366f, W.Sine, 0.75f, 0.06f, 0.9f, 5f, 0.012f);
                s.Tone(0.74f, 0.6f, 860f, 732f, W.Sine, 0.06f, 0.06f, 0.9f);
                s.Noise(0.74f, 0.6f, 0.1f, F.BP, 450f, 380f, 0.06f, 1f, 0.6f);
                return s.Finish(0.6f);

            case "cricket": // two chirps of four quick pulses at ~4.4 kHz
                s = new Synth(0.46f, seed);
                for (int c = 0; c < 2; c++)
                    for (int k = 0; k < 4; k++)
                    {
                        float t = c * 0.22f + k * 0.029f;
                        s.Tone(t, 0.018f, 4420f, 4380f, W.Sine, 0.6f, 0.003f, 1.1f);
                        s.Tone(t, 0.018f, 8840f, 8760f, W.Sine, 0.08f, 0.003f, 1.4f);
                    }
                return s.Finish(0.5f);

            case "goblin_angry": // RAAAGH! gravelly shout with a growl flutter
                s = new Synth(0.95f, seed);
                s.Voice(0f, 0.85f, 175f, 145f, 1f, 0.02f, 0.5f, 760f, 1180f, 640f, 1000f, 11f, 0.06f, 0.3f, 105f, 0.45f);
                s.Voice(0f, 0.85f, 180f, 150f, 0.6f, 0.02f, 0.5f, 700f, 1300f, 600f, 1050f, 13f, 0.07f, 0.3f, 108f, 0.45f);
                s.Noise(0f, 0.75f, 0.35f, F.LP, 900f, 500f, 0.02f, 0.8f, 1f, 34f, 0.8f);
                return s.Finish(0.9f, 2.5f);

            case "raccoon_scream": // SKREEEE! (raccoons really do sound like this)
                s = new Synth(0.82f, seed);
                s.Voice(0f, 0.72f, 880f, 1450f, 1f, 0.01f, 0.6f, 1050f, 2500f, 820f, 2000f, 31f, 0.045f, 0.45f, 760f, 0.3f);
                s.Noise(0f, 0.7f, 0.4f, F.BP, 3000f, 2600f, 0.01f, 0.8f, 0.6f, 40f, 0.5f);
                return s.Finish(0.85f, 2f);

            case "sniff": // sniff-sniff-SNIIIFF (a critter smelling your Raccoon-Be-Gone)
                s = new Synth(0.62f, seed);
                s.Noise(0f, 0.09f, 0.7f, F.BP, 1400f, 2600f, 0.02f, 1.2f, 1.2f);
                s.Noise(0.14f, 0.09f, 0.75f, F.BP, 1500f, 2800f, 0.02f, 1.2f, 1.2f);
                s.Noise(0.3f, 0.26f, 1f, F.BP, 1300f, 3300f, 0.03f, 0.9f, 1.1f);
                return s.Finish(0.6f);

            // ---------------------------------------------------------------- world life
            case "clang": // a hollow lamp post getting bopped: strike, thump, then a long inharmonic metal ring
                s = new Synth(1.3f, seed);
                s.Noise(0f, 0.04f, 0.9f, F.BP, 3200f, 2200f, 0.0005f, 2f, 0.7f);
                s.Tone(0f, 0.18f, 160f, 70f, W.Sine, 0.6f, 0.002f, 2f);
                s.Bell(0f, 1.0f, 784f, 2.76f, 3f, 0.3f, 0.35f, 0.05f);
                s.Ring(0f, 1.25f, 392f, 0.45f, 0.42f);
                s.Ring(0f, 1.25f, 395.5f, 0.3f, 0.42f);       // a beating pair = wobbly pole
                s.Ring(0f, 1.1f, 1061f, 0.38f, 0.28f);
                s.Ring(0f, 0.9f, 1987f, 0.25f, 0.18f);
                s.Ring(0f, 0.7f, 3123f, 0.16f, 0.12f);
                s.Ring(0f, 0.5f, 4410f, 0.08f, 0.07f);
                return s.Finish(0.9f);

            case "sparks": // ZAP, then an electric fizz-crackle over a mains hum
                s = new Synth(1.0f, seed);
                s.Tone(0f, 0.9f, 120f, 118f, W.Square, 0.22f, 0.005f, 1.2f);
                s.FilterRegion(0f, 0.92f, F.LP, 900f);
                s.Tone(0f, 0.07f, 2400f, 600f, W.Saw, 0.35f, 0.001f, 1.5f);
                s.Noise(0f, 0.08f, 0.55f, F.HP, 4000f, 5000f, 0.0005f, 2f);
                s.Crackle(0f, 1.0f, 2200f, 0.9f, 5000f, 0.001f, 1.3f);
                s.Crackle(0f, 0.6f, 600f, 0.5f, 2600f, 0.001f, 1.6f);
                return s.Finish(0.7f);

            case "geyser": // a hydrant gushing: rushing water, spray hiss, rumble, droplets and gurgles (re-fired every ~1.6 s)
            {
                s = new Synth(1.8f, seed);
                s.Noise(0f, 1.8f, 1f, F.BP, 900f, 1400f, 0.15f, 0.2f, 0.8f, 7f, 0.35f, 1100f);
                s.Noise(0f, 1.8f, 0.35f, F.HP, 3500f, 4500f, 0.12f, 0.25f);
                s.Noise(0f, 1.8f, 0.45f, F.LP, 260f, 180f, 0.15f, 0.25f, 1f, 4f, 0.4f);
                s.Crackle(0f, 1.8f, 160f, 0.35f, 2600f, 0.1f, 0.4f);
                for (int i = 0; i < 6; i++)
                {
                    float t = 0.1f + i * 0.27f + s.Rand01() * 0.1f, f = 260f + s.Rand01() * 240f;
                    s.Tone(t, 0.09f, f, f * 1.9f, W.Sine, 0.2f, 0.005f, 1.5f);
                }
                return s.Finish(0.7f);
            }

            case "car_horn": // a little car's "meep meep" (higher and thinner than the truck's HONK)
                s = new Synth(0.46f, seed);
                s.Brass(0f, 0.17f, 415f, 0.55f, 0f, 0f, 1500f, 2400f, 0.04f);
                s.Brass(0f, 0.17f, 523f, 0.45f, 0f, 0f, 1500f, 2400f, 0.04f);
                s.Brass(0.24f, 0.2f, 415f, 0.55f, 0f, 0f, 1500f, 2400f, 0.04f);
                s.Brass(0.24f, 0.2f, 523f, 0.45f, 0f, 0f, 1500f, 2400f, 0.04f);
                return s.Finish(0.75f, 1.8f);

            case "car_alarm": // one 0.44 s siren whoop; callers re-fire it on a 0.45 s beat, alternating the pitch
                s = new Synth(0.44f, seed);
                s.Tone(0f, 0.43f, 620f, 1250f, W.Square, 0.5f, 0.01f, 0.12f);
                s.Tone(0f, 0.43f, 1240f, 2500f, W.Sine, 0.12f, 0.01f, 0.12f);
                s.FilterRegion(0f, 0.44f, F.LP, 3200f);
                return s.Finish(0.6f, 1.2f);

            case "tire_screech": // rubber squeal with a juddering wobble
                s = new Synth(0.85f, seed);
                s.Tone(0f, 0.8f, 1650f, 1450f, W.Saw, 0.35f, 0.03f, 0.6f, 37f, 0.025f);
                s.Tone(0f, 0.8f, 2480f, 2200f, W.Sine, 0.3f, 0.03f, 0.6f, 41f, 0.02f);
                s.FilterRegion(0f, 0.85f, F.LP, 4500f);
                s.Noise(0f, 0.8f, 0.55f, F.BP, 2600f, 2000f, 0.02f, 0.7f, 0.5f, 23f, 0.4f);
                return s.Finish(0.6f, 1.4f);

            case "door_creak": // a slow stick-slip hinge creak, then the latch
                s = new Synth(0.8f, seed);
                s.Voice(0.02f, 0.62f, 48f, 135f, 1f, 0.03f, 0.5f, 650f, 1500f, 1050f, 2300f, 19f, 0.25f, 0.15f, 70f, 0.55f);
                s.Noise(0.66f, 0.03f, 0.5f, F.BP, 2500f, 1800f, 0.0005f, 2f, 0.6f);
                s.Ring(0.66f, 0.12f, 1850f, 0.15f, 0.03f);
                return s.Finish(0.6f, 1.3f);

            case "oof": // cartoon "OOF!": a short "oo" grunt into an "f", over a body thud
                s = new Synth(0.42f, seed);
                s.Voice(0f, 0.3f, 230f, 150f, 1f, 0.01f, 0.9f, 380f, 900f, 330f, 750f, 0f, 0f, 0.15f);
                s.Noise(0.2f, 0.16f, 0.35f, F.BP, 2200f, 1500f, 0.02f, 1.5f, 0.9f);
                s.Tone(0f, 0.2f, 120f, 50f, W.Sine, 0.7f, 0.002f, 2f);
                s.Noise(0f, 0.05f, 0.4f, F.LP, 700f, 200f, 0.001f, 2.5f);
                return s.Finish(0.85f);

            case "yelp": // a small dog's "YIPE!"
                s = new Synth(0.3f, seed);
                s.Voice(0f, 0.27f, 950f, 1500f, 1f, 0.006f, 0.8f, 700f, 2600f, 900f, 1700f, 0f, 0f, 0.05f, 800f, 0.3f);
                return s.Finish(0.75f, 1.2f);

            case "bark": // one medium "WOOF" (callers pitch it per dog)
                s = new Synth(0.26f, seed);
                s.Voice(0f, 0.22f, 330f, 400f, 1f, 0.006f, 1.1f, 500f, 1100f, 750f, 1300f, 31f, 0.05f, 0.35f, 240f, 0.35f);
                s.Noise(0f, 0.04f, 0.4f, F.BP, 1200f, 900f, 0.002f, 2f, 0.8f);
                return s.Finish(0.8f, 1.6f);

            case "mumble": // Animal-Crossing babble, three takes (callers pitch it per resident)
            case "mumble2":
            case "mumble3":
            {
                s = new Synth(0.62f, seed);
                float[,] vowels = { { 730f, 1090f }, { 530f, 1840f }, { 270f, 2290f }, { 570f, 840f }, { 440f, 1020f }, { 660f, 1700f } };
                float t = 0.01f;
                int syllables = 4 + (int)(s.Rand01() * 3f);
                for (int k = 0; k < syllables && t < 0.52f; k++)
                {
                    int va = (int)(s.Rand01() * 6f) % 6, vb = (int)(s.Rand01() * 6f) % 6;
                    float p = 240f * (0.85f + s.Rand01() * 0.4f);
                    float d = 0.055f + s.Rand01() * 0.045f;
                    s.Voice(t, d, p, p * (0.9f + s.Rand01() * 0.25f), 0.8f + s.Rand01() * 0.2f, 0.006f, 0.5f,
                            vowels[va, 0], vowels[va, 1], vowels[vb, 0], vowels[vb, 1], 0f, 0f, 0.04f);
                    t += d + 0.015f + s.Rand01() * 0.03f;
                }
                return s.Finish(0.6f);
            }

            default: // "blip"
                s = new Synth(0.08f, seed);
                s.Tone(0f, 0.07f, 880f, 990f, W.Soft, 0.5f, 0.002f, 1.5f);
                return s.Finish(0.5f);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Tiny synth
    // ------------------------------------------------------------------------------------------------
    enum W { Sine, Tri, Saw, Square, Soft }
    enum F { LP, BP, HP }

    /// <summary>Chamberlin state-variable filter (cutoff clamped to keep it stable).</summary>
    struct Svf
    {
        float lp, bp;
        public static float Coef(float cutoff) => 2f * Mathf.Sin(Mathf.PI * Mathf.Clamp(cutoff, 20f, 6000f) / SampleRate);
        public float Run(float x, float cutoff, float q, F mode) => Step(x, Coef(cutoff), q, mode);
        public float Step(float x, float f, float q, F mode)
        {
            lp += f * bp;
            float hp = x - lp - q * bp;
            bp += f * hp;
            return mode == F.LP ? lp : mode == F.BP ? bp : hp;
        }
    }

    sealed class Synth
    {
        readonly float[] buf;
        readonly int n;
        uint rng;

        public Synth(float seconds, uint seed)
        {
            n = Mathf.Max(1, Mathf.CeilToInt(seconds * SampleRate));
            buf = new float[n];
            rng = seed == 0 ? 0x9E3779B9u : seed;
        }

        uint Next() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }
        public float Rand01() => (Next() & 0xFFFFFF) / 16777216f;
        float RandSigned() => Rand01() * 2f - 1f;

        int Idx(float t) => Mathf.Clamp(Mathf.RoundToInt(t * SampleRate), 0, n);

        static float Wave(W w, double ph)
        {
            float p = (float)(ph - System.Math.Floor(ph));
            switch (w)
            {
                case W.Tri: return 1f - 4f * Mathf.Abs(p - 0.5f);
                case W.Saw: return 2f * p - 1f;
                case W.Square: return p < 0.5f ? 1f : -1f;
                case W.Soft: return (float)System.Math.Tanh(3.0 * System.Math.Sin(2.0 * System.Math.PI * p)) * 1.0049f;
                default: return Mathf.Sin(2f * Mathf.PI * p);
            }
        }

        /// <summary>Attack then (1-p)^pow decay.</summary>
        static float Env(float t, float dur, float atk, float pow)
        {
            if (t < atk) return atk <= 0f ? 1f : t / atk;
            float d = Mathf.Max(1e-5f, dur - atk);
            float p = Mathf.Clamp01((t - atk) / d);
            return Mathf.Pow(1f - p, pow);
        }

        static float ExpLerp(float a, float b, float p) => a * Mathf.Pow(b / a, Mathf.Clamp01(p));

        /// <summary>Oscillator with exponential glide f0->f1 (optionally ->f2 after midP), vibrato.</summary>
        public void Tone(float t0, float dur, float f0, float f1, W w, float amp, float atk, float pow,
                         float vibHz = 0f, float vibDepth = 0f, float f2 = 0f, float midP = 0.5f)
        {
            int a = Idx(t0), b = Idx(t0 + dur);
            double ph = 0;
            for (int i = a; i < b; i++)
            {
                float t = (i - a) / (float)SampleRate;
                float p = t / dur;
                float f;
                if (f2 > 0f) f = p < midP ? ExpLerp(f0, f1, p / midP) : ExpLerp(f1, f2, (p - midP) / (1f - midP));
                else f = ExpLerp(f0, f1, p);
                if (vibHz > 0f) f *= 1f + vibDepth * Mathf.Sin(2f * Mathf.PI * vibHz * t);
                ph += f / SampleRate;
                buf[i] += Wave(w, ph) * amp * Env(t, dur, atk, pow);
            }
        }

        /// <summary>Plucked/struck partial: 2 ms attack, exponential decay with time constant tau.</summary>
        public void Ring(float t0, float dur, float f, float amp, float tau, W w = W.Sine)
        {
            int a = Idx(t0), b = Idx(t0 + dur);
            double ph = 0;
            for (int i = a; i < b; i++)
            {
                float t = (i - a) / (float)SampleRate;
                ph += f / SampleRate;
                float e = Mathf.Min(1f, t / 0.002f) * Mathf.Exp(-t / tau);
                float tail = Mathf.Clamp01((dur - t) / 0.01f);
                buf[i] += Wave(w, ph) * amp * e * tail;
            }
        }

        /// <summary>2-operator FM bell: carrier fc, modulator fc*ratio, decaying index.</summary>
        public void Bell(float t0, float dur, float fc, float ratio, float index, float amp, float tau, float indexTau)
        {
            int a = Idx(t0), b = Idx(t0 + dur);
            double pc = 0, pm = 0;
            float fm = fc * ratio;
            for (int i = a; i < b; i++)
            {
                float t = (i - a) / (float)SampleRate;
                pc += fc / SampleRate;
                pm += fm / SampleRate;
                float I = index * Mathf.Exp(-t / indexTau);
                float y = Mathf.Sin((float)(2.0 * System.Math.PI * (pc - System.Math.Floor(pc))) + I * Mathf.Sin((float)(2.0 * System.Math.PI * (pm - System.Math.Floor(pm)))));
                float e = Mathf.Min(1f, t / 0.002f) * Mathf.Exp(-t / tau) * Mathf.Clamp01((dur - t) / 0.02f);
                buf[i] += y * amp * e;
            }
        }

        /// <summary>
        /// Filtered white noise (normalized to peak 1 before the envelope so layer levels are predictable).
        /// Cutoff glides c0->c1 (or c0->c1->c2 when c2 > 0). Optional amplitude modulation (rattle/putt-putt).
        /// </summary>
        public void Noise(float t0, float dur, float amp, F mode, float c0, float c1, float atk, float pow,
                          float q = 1f, float amHz = 0f, float amDepth = 0f, float c2 = 0f)
        {
            int a = Idx(t0), b = Idx(t0 + dur);
            if (b <= a) return;
            var tmp = new float[b - a];
            var svf = new Svf();
            float peak = 1e-6f;
            float coef = 0f;
            for (int i = 0; i < tmp.Length; i++)
            {
                if ((i & 7) == 0)
                {
                    float p = i / (float)tmp.Length;
                    float c = c2 > 0f ? (p < 0.5f ? ExpLerp(c0, c1, p * 2f) : ExpLerp(c1, c2, p * 2f - 1f)) : ExpLerp(c0, c1, p);
                    coef = Svf.Coef(c);
                }
                float y = svf.Step(RandSigned(), coef, q, mode);
                tmp[i] = y;
                float ay = Mathf.Abs(y);
                if (ay > peak) peak = ay;
            }
            float g = amp / peak;
            for (int i = 0; i < tmp.Length; i++)
            {
                float t = i / (float)SampleRate;
                float e = Env(t, dur, atk, pow);
                if (amHz > 0f) e *= 1f - amDepth * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * amHz * t));
                buf[a + i] += tmp[i] * g * e;
            }
        }

        /// <summary>Sparse random impulses through a band-pass (plastic crinkle, crunch, coins).</summary>
        public void Crackle(float t0, float dur, float density, float amp, float center, float atk = 0.002f, float pow = 1f)
        {
            int a = Idx(t0), b = Idx(t0 + dur);
            if (b <= a) return;
            var tmp = new float[b - a];
            var svf = new Svf();
            float prob = density / SampleRate;
            float peak = 1e-6f;
            float coef = Svf.Coef(center);
            for (int i = 0; i < tmp.Length; i++)
            {
                float x = Rand01() < prob ? RandSigned() * (0.4f + 0.6f * Rand01()) * 8f : 0f;
                float y = svf.Step(x, coef, 0.5f, F.BP);
                tmp[i] = y;
                float ay = Mathf.Abs(y);
                if (ay > peak) peak = ay;
            }
            float g = amp / peak;
            for (int i = 0; i < tmp.Length; i++)
                buf[a + i] += tmp[i] * g * Env(i / (float)SampleRate, dur, atk, pow);
        }

        /// <summary>
        /// Brassy note: saw + a bit of square through a resonant low-pass whose cutoff swells ("wah").
        /// scoop = start this fraction flat and bend up; bend = pitch drift over the note (sad trombone sag).
        /// </summary>
        public void Brass(float t0, float dur, float f, float amp, float vibHz, float vibDepth, float cutLo, float cutHi,
                          float scoop = 0f, float bend = 0f)
        {
            int a = Idx(t0), b = Idx(t0 + dur);
            double ph = 0;
            var svf = new Svf();
            float rel = Mathf.Min(0.06f, dur * 0.3f);
            float coef = 0f, fmul = 1f;
            for (int i = a; i < b; i++)
            {
                float t = (i - a) / (float)SampleRate;
                float p = t / dur;
                if (((i - a) & 7) == 0)
                {
                    // control-rate: pitch bend/scoop/vibrato + "wah" filter sweep
                    fmul = 1f + bend * p;
                    if (scoop > 0f) fmul *= 1f - scoop * Mathf.Clamp01(1f - t / 0.05f);
                    if (vibHz > 0f) fmul *= 1f + vibDepth * Mathf.Clamp01((t - 0.12f) / 0.15f) * Mathf.Sin(2f * Mathf.PI * vibHz * t);
                    float wah = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(Mathf.PI * Mathf.Clamp01(p * 1.4f))), 0.7f);
                    coef = Svf.Coef(Mathf.Lerp(cutLo, cutHi, wah));
                }
                ph += f * fmul / SampleRate;
                float frac = (float)(ph - System.Math.Floor(ph));
                float x = (2f * frac - 1f) * 0.8f + (frac < 0.5f ? 0.2f : -0.2f);
                float y = svf.Step(x, coef, 0.55f, F.LP);
                float e = Mathf.Min(1f, t / 0.03f) * Mathf.Clamp01((dur - t) / rel);
                buf[i] += y * amp * e;
            }
        }

        /// <summary>
        /// Cartoon voice: a buzzy glottal pulse (saw + click, plus optional breath noise) through two band-pass
        /// formants that glide from vowel (f1a, f2a) to vowel (f1b, f2b). Pitch glides p0->p1 (->p2 after midP when
        /// p2 > 0), with optional vibrato (fast vibrato = growl/flutter). Layer normalized to peak amp before the
        /// envelope, like Noise, so critter layers mix predictably.
        /// </summary>
        public void Voice(float t0, float dur, float p0, float p1, float amp, float atk, float pow,
                          float f1a, float f2a, float f1b, float f2b,
                          float vibHz = 0f, float vibDepth = 0f, float breath = 0f, float p2 = 0f, float midP = 0.5f)
        {
            int a = Idx(t0), b = Idx(t0 + dur);
            if (b <= a) return;
            var tmp = new float[b - a];
            var s1 = new Svf();
            var s2 = new Svf();
            double ph = 0;
            float f = p0, c1 = 0f, c2 = 0f, peak = 1e-6f;
            for (int i = 0; i < tmp.Length; i++)
            {
                float t = i / (float)SampleRate;
                float p = t / dur;
                if ((i & 7) == 0)
                {
                    f = p2 > 0f
                        ? (p < midP ? ExpLerp(p0, p1, p / midP) : ExpLerp(p1, p2, (p - midP) / (1f - midP)))
                        : ExpLerp(p0, p1, p);
                    if (vibHz > 0f) f *= 1f + vibDepth * Mathf.Sin(2f * Mathf.PI * vibHz * t);
                    float q = Mathf.SmoothStep(0f, 1f, p);
                    c1 = Svf.Coef(Mathf.Lerp(f1a, f1b, q));
                    c2 = Svf.Coef(Mathf.Lerp(f2a, f2b, q));
                }
                ph += f / SampleRate;
                float frac = (float)(ph - System.Math.Floor(ph));
                float src = (2f * frac - 1f) + (frac < 0.1f ? 0.6f : 0f) + breath * RandSigned();
                float y = s1.Step(src, c1, 0.3f, F.BP) + 0.6f * s2.Step(src, c2, 0.36f, F.BP);
                tmp[i] = y;
                float ay = Mathf.Abs(y);
                if (ay > peak) peak = ay;
            }
            float g = amp / peak;
            for (int i = 0; i < tmp.Length; i++)
                buf[a + i] += tmp[i] * g * Env(i / (float)SampleRate, dur, atk, pow);
        }

        /// <summary>Filter everything already in [t0, t0+dur) in place (applies to all earlier layers).</summary>
        public void FilterRegion(float t0, float dur, F mode, float cutoff)
        {
            int a = Idx(t0), b = Idx(t0 + dur);
            var svf = new Svf();
            float coef = Svf.Coef(cutoff);
            for (int i = a; i < b; i++) buf[i] = svf.Step(buf[i], coef, 1f, mode);
        }

        /// <summary>Optional tanh drive, normalize to peak, 1 ms fade-in / 6 ms fade-out.</summary>
        public float[] Finish(float peakTarget, float drive = 0f)
        {
            if (drive > 0f)
                for (int i = 0; i < n; i++) buf[i] = (float)System.Math.Tanh(buf[i] * drive);
            float peak = 1e-6f;
            for (int i = 0; i < n; i++) { float a = Mathf.Abs(buf[i]); if (a > peak) peak = a; }
            float g = peakTarget / peak;
            int fin = Mathf.Min(n, SampleRate / 1000), fout = Mathf.Min(n, SampleRate * 6 / 1000);
            for (int i = 0; i < n; i++)
            {
                float e = 1f;
                if (i < fin) e *= i / (float)fin;
                if (i > n - fout) e *= (n - i) / (float)fout;
                buf[i] *= g * e;
            }
            return buf;
        }
    }
}
