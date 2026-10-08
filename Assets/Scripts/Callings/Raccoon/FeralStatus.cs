using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Embrace It (calling_embrace_it): "you got bitten one time too many". Rabies stops being an illness and becomes who you
/// are. Rules are <see cref="FeralRules"/>; this is the local player's side (the gates RabiesStatus, the auto-shot and the
/// boots read, night vision, the pounce) and the questions every machine may ask about any player.
/// <list type="bullet">
/// <item><b>L1 A Little Bit Feral</b>: no pass-out and no hospital bill, ever; a stored Rabies Shot is not used on its own
/// (cure on purpose: buying a shot while infected, a friend's jab, or K twice for a stored shot);
/// the trippy look is capped to a stylised hint and the stumbling drift and hiccup hops are gone.</item>
/// <item><b>L2 Night Creature</b> (while infected): +15% on foot at night, night vision, and wild raccoons leave you alone
/// (no bites, no latches, no revenge for taking their bag).</item>
/// <item><b>L3 Fully Feral</b> (while infected): Y = POUNCE, a 7 m leap on a 9 s cooldown; raccoons within 12 m of where you
/// land are your friends for 20 s and drop whatever they stole. +5% on foot by day too.</item>
/// </list>
/// Rabies Tag is deliberately untouched: a feral player is rabid, so with the Town rule on their knockdowns can still pass
/// it on (the host can turn the rule off). Everyone sees who is feral: raccoon ears and a ringed tail (<see cref="FeralLook"/>).
/// </summary>
public static class FeralStatus
{
    public const KeyCode PounceKey = KeyCode.Y, CureKey = KeyCode.K;

    // ------------------------------------------------------------------ this machine's player

    public static int LocalEmbrace => GameSession.GetUpgradeLevel(CallingIds.EmbraceIt);
    public static bool LocalEmbraced => LocalEmbrace > 0;
    /// <summary>RabiesStatus: may the 20-hour pass-out (and its hospital bill) happen?</summary>
    public static bool LocalPassesOut => FeralRules.PassesOut(LocalEmbrace);
    /// <summary>CritterSpawner / ShopEffectsRunner: may a stored Rabies Shot fire by itself?</summary>
    public static bool LocalAutoShot => FeralRules.AutoShot(LocalEmbrace);
    public static bool LocalStumbles => FeralRules.Stumbles(LocalEmbrace);
    public static float LocalShown(float intensity) => FeralRules.ShownIntensity(intensity, LocalEmbrace);
    public static bool LocalPerks => FeralRules.PerksActive(LocalEmbrace, GameSession.HasRabies);
    /// <summary>BootsMods: on-foot speed multiplier from being feral (1 when not).</summary>
    public static float LocalSpeedMult => FeralRules.SpeedMult(LocalEmbrace, GameSession.HasRabies, GameClock.IsNight);

    // ------------------------------------------------------------------ any player, on any machine

    public static bool Infected(ulong client) => NightNet.InfectedAt(client) >= 0;
    public static int EmbraceOf(ulong client) => Hutch.LevelFor(client, CallingIds.EmbraceIt);
    /// <summary>Embraced and carrying the virus right now (what the ears and tail show).</summary>
    public static bool IsFeral(ulong client) => Infected(client) && EmbraceOf(client) > 0;

    // ------------------------------------------------------------------ local tick (input, messages)

    static float pounceReadyAt, landAt = -1f, curePressedAt = -99f, nextCooldownFx;
    static bool wasInfected, toldPounce;
    static readonly Dictionary<ulong, float> hostPounceAt = new Dictionary<ulong, float>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        pounceReadyAt = 0f; landAt = -1f; curePressedAt = -99f; nextCooldownFx = 0f; wasInfected = false; toldPounce = false;
        hostPounceAt.Clear();
    }

    internal static void LocalTick()
    {
        var pc = NightPlayers.LocalPlayer;
        if (pc == null) return;
        int level = LocalEmbrace;
        bool infected = GameSession.HasRabies;
        if (level > 0 && infected && !wasInfected)
        {
            GameHUD.Toast(level >= 3 ? "The foam feels... RIGHT. You are FULLY FERAL. Y to pounce." : level == 2
                ? "The foam feels... right. Night Creature: faster after dark, and the raccoons know their own."
                : "A little bit feral. No passing out, no hospital. Just vibes and foam.", ToastStyle.Good, 4.5f);
            Sfx.Play("chitter", pc.transform.position + Vector3.up * 1.6f, 0.8f, 0.8f);
        }
        wasInfected = infected;
        if (level <= 0 || !infected) { landAt = -1f; return; }

        bool free = !PlayerController.IsUIBlocking && !pc.IsDriving && !pc.IsPlayingChess && !NightPlayers.IsRiding(pc) && !SceneController.IsTransitioning;

        // a stored shot never fires by itself any more: K, twice, on purpose
        if (free && Input.GetKeyDown(CureKey) && GameSession.GetConsumable(CritterSpawner.RabiesShotId) > 0)
        {
            if (Time.unscaledTime - curePressedAt > 3f)
            {
                curePressedAt = Time.unscaledTime;
                GameHUD.Toast("Use your stored Rabies Shot and stop being feral? Press K again.", ToastStyle.Info, 3f);
            }
            else
            {
                curePressedAt = -99f;
                GameSession.AddConsumable(CritterSpawner.RabiesShotId, -1);
                RabiesStatus.CureWithShot();
                return;
            }
        }

        if (landAt >= 0f && Time.time >= landAt)
        {
            landAt = -1f;
            NightNet.RaccoonAsk(RaccoonCalling.OpPounce, 0, 0); // the host befriends the raccoons where we landed
        }
        if (!FeralRules.CanPounce(level, infected)) return;
        if (!toldPounce) { toldPounce = true; GameHUD.Toast("FULLY FERAL: press Y to POUNCE. Raccoons where you land are your friends.", ToastStyle.Info, 4f); }
        if (!free || !Input.GetKeyDown(PounceKey)) return;
        var kb = pc.GetComponent<CritterKnockback>();
        if (kb == null || kb.IsKnocking || NightPlayers.IsIndoors(pc)) return;
        Vector3 head = pc.transform.position + Vector3.up * 1.9f;
        if (Time.time < pounceReadyAt)
        {
            if (Time.time >= nextCooldownFx)
            {
                nextCooldownFx = Time.time + 0.8f;
                GameHUD.FloatingText(head, $"pounce in {pounceReadyAt - Time.time:0.0}s", new Color(0.75f, 0.7f, 0.9f), 0.75f);
            }
            return;
        }
        pounceReadyAt = Time.time + RaccoonTuning.PounceCooldown;
        kb.Knock(pc.transform.forward, RaccoonTuning.PounceDistance, RaccoonTuning.PounceHop, RaccoonTuning.PounceSeconds);
        landAt = Time.time + RaccoonTuning.PounceSeconds;
        Sfx.Play("whoosh", head, 0.9f, 1.3f);
        Sfx.Play("chitter", head, 0.9f, 0.7f);
        GameHUD.FloatingText(head, "POUNCE!", new Color(0.72f, 0.5f, 1f), 1.4f);
    }

    /// <summary>Host: <paramref name="sender"/> landed a pounce. Cooldown: RaccoonTuning.PounceCooldown (checked here too).
    /// Counter: none needed, it touches no player. Wild raccoons near them calm down and give back their loot.</summary>
    internal static void HostPounce(ulong sender)
    {
        if (!FeralRules.CanPounce(EmbraceOf(sender), Infected(sender))) return;
        if (hostPounceAt.TryGetValue(sender, out float last) && Time.time - last < RaccoonTuning.PounceCooldown * 0.7f) return;
        var pc = NightPlayers.ForClient(sender);
        if (pc == null || pc.IsDriving || NightPlayers.IsIndoors(pc)) return;
        hostPounceAt[sender] = Time.time;
        Vector3 at = pc.transform.position;
        int friends = 0;
        var critters = new List<Critter>(Critter.All);
        foreach (var c in critters)
        {
            if (!(c is Raccoon r) || r == null || r.Despawning || r.NetPuppet) continue;
            if (CritterWorld.Flat(r.transform.position - at).sqrMagnitude > RaccoonTuning.PounceCharmRadius * RaccoonTuning.PounceCharmRadius) continue;
            r.Befriend(at, RaccoonTuning.PounceCharmSeconds, sender, false);
            friends++;
        }
        NightNet.RaccoonFx(RaccoonCalling.FxPounce, sender, at, new Vector3(friends, 0f, 0f));
        NightAudit.Event("feral", new Dictionary<string, object> { { "op", "pounce" }, { "by", (long)sender }, { "friends", friends } });
    }

    /// <summary>Every machine: the landing.</summary>
    internal static void PlayPounce(ulong who, Vector3 at)
    {
        Fx.Dust(at, 1.6f);
        Fx.Burst(at + Vector3.up * 0.4f, new Color(0.72f, 0.5f, 1f), 16, 4f);
        Sfx.Play("thud", at, 0.8f, 1.2f);
        Sfx.Play("raccoon_scream", at + Vector3.up, 0.6f, 1.5f);
        GameHUD.FloatingText(at + Vector3.up * 2.3f, "*FERAL SCREECH*", new Color(0.72f, 0.5f, 1f), 1.2f);
    }

    /// <summary>The hutch's "Get nibbled": a rabid resident gives its owner the bite on request (no vaccine roll: you
    /// asked for it). Local, like every infection: each player reports their own rabies.</summary>
    public static bool LocalNibble(HutchRaccoon r)
    {
        if (r == null || !r.rabid || !LocalEmbraced || GameSession.HasRabies) return false;
        GameSession.RestoreRabies(GameClock.TotalHours);
        GameHUD.Toast($"{r.name} gives you a tiny, loving, rabid nibble. Here we go.", ToastStyle.Good, 3.5f);
        Sfx.Play("bite", null, 0.7f, 1.3f);
        return true;
    }
}

/// <summary>
/// How the raccoons see a player. Default-safe on every machine and with no session: an unknown client is a Stranger.
/// Other systems (the raccoon-mob fence, shops, the King) may read it; nothing here writes anything.
/// </summary>
public static class RaccoonStanding
{
    struct Entry { public float at; public bool protects; public RaccoonTier tier; }
    static readonly Dictionary<ulong, Entry> cache = new Dictionary<ulong, Entry>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => cache.Clear();

    static Entry Look(ulong client)
    {
        if (cache.TryGetValue(client, out var e) && Time.unscaledTime - e.at < 0.5f) return e;
        e = new Entry { at = Time.unscaledTime };
        if (client != ulong.MaxValue && Hutch.TryBusinessOf(client, out var business))
        {
            int embrace = Hutch.LevelFor(client, CallingIds.EmbraceIt), pack = Hutch.LevelFor(client, CallingIds.PackLeader);
            e.protects = FeralRules.RaccoonsDefer(embrace, embrace >= 2 && FeralStatus.Infected(client), pack);
            e.tier = RaccoonStandingRules.Tier(Hutch.LevelFor(client, CallingIds.CritterHutch), embrace, Hutch.LevelFor(client, CallingIds.CollarChip),
                Hutch.LevelFor(client, CallingIds.BrainChip), pack, Hutch.LevelFor(client, CallingIds.KingsRival), Hutch.For(business.Id).collars);
        }
        cache[client] = e;
        return e;
    }

    /// <summary>The player's tier with the raccoon underworld (Stranger when unknown).</summary>
    public static RaccoonTier For(ulong clientId) => Look(clientId).tier;
    /// <summary>Host rule: wild raccoons do not attack, latch onto or get angry at this player.</summary>
    public static bool Protects(ulong clientId) => clientId != ulong.MaxValue && Look(clientId).protects;
}

/// <summary>
/// The tell, on every machine and every player (your own too): raccoon ears and a ringed tail while they are feral.
/// Reads replicated state only (their calling level and the host's rabies record).
/// </summary>
[DisallowMultipleComponent]
public sealed class FeralLook : MonoBehaviour
{
    PlayerController pc;
    Transform ears, tail;
    readonly List<Transform> segments = new List<Transform>();
    float nextCheck;
    bool feral;

    void Awake() => pc = GetComponent<PlayerController>();
    void OnDisable() => Clear();

    void Clear()
    {
        if (ears != null) Destroy(ears.gameObject);
        if (tail != null) Destroy(tail.gameObject);
        ears = tail = null; segments.Clear();
    }

    void Update()
    {
        if (pc == null) { enabled = false; return; }
        if (Time.unscaledTime >= nextCheck)
        {
            nextCheck = Time.unscaledTime + 0.5f;
            feral = NightPlayers.IsLocal(pc) ? FeralStatus.LocalEmbraced && GameSession.HasRabies : FeralStatus.IsFeral(NightPlayers.ClientOf(pc));
        }
        bool show = feral && !pc.IsDriving && !pc.IsPlayingChess;
        if (!show) { if (ears != null || tail != null) Clear(); return; }
        if (tail == null) Build();
        float t = Time.time;
        for (int i = 0; i < segments.Count; i++)
            segments[i].localRotation = Quaternion.Euler(i == 0 ? -55f : -14f, Mathf.Sin(t * 3.2f - i * 0.8f) * 16f, 0f);
    }

    void Build()
    {
        var fur = CritterLook.Mat(CritterLook.RaccoonFur, 0.15f);
        var dark = CritterLook.Mat(CritterLook.MaskBlack, 0.35f);
        var puppet = GetComponent<ChuckPuppet>();
        Transform mount = puppet != null ? puppet.HeadlampMount : null;
        ears = CritterLook.Node(mount != null ? mount : transform, "Feral ears", mount != null ? new Vector3(0f, 0.05f, -0.24f) : new Vector3(0f, 1.78f, 0f));
        for (int s = -1; s <= 1; s += 2)
        {
            var ear = CritterLook.Node(ears, s < 0 ? "Ear L" : "Ear R", new Vector3(0.2f * s, 0f, 0f), new Vector3(0f, 0f, -20f * s));
            CritterLook.Part(ear, PrimitiveType.Sphere, "Outer", new Vector3(0f, 0.07f, 0f), new Vector3(0.17f, 0.2f, 0.07f), fur);
            CritterLook.Part(ear, PrimitiveType.Sphere, "Inner", new Vector3(0f, 0.07f, 0.02f), new Vector3(0.1f, 0.13f, 0.04f), dark, default, false);
        }
        tail = CritterLook.Node(transform, "Feral tail", new Vector3(0f, 0.72f, -0.2f));
        var seg = CritterLook.Node(tail, "Tail 0", Vector3.zero);
        float size = 0.17f;
        for (int i = 0; i < 6; i++)
        {
            CritterLook.Part(seg, PrimitiveType.Sphere, "Fluff", new Vector3(0f, 0f, -size * 0.45f), new Vector3(size, size, size * 1.2f), i % 2 == 1 || i == 5 ? dark : fur);
            segments.Add(seg);
            if (i == 5) break;
            seg = CritterLook.Node(seg, "Tail " + (i + 1), new Vector3(0f, 0f, -size * 0.8f));
            size *= 0.94f;
        }
        Vector3 at = transform.position + Vector3.up * 1.2f;
        Fx.Burst(at, new Color(0.72f, 0.5f, 1f), 14, 3f);
    }
}

/// <summary>
/// Night Creature's eyes: a soft, shadowless, cool light that follows this machine's own player after dark, so a feral
/// player simply sees further at night. Local only (nobody else sees it), no post-processing (so it works with any camera
/// setup and has nothing for Settings > Accessibility > Screen effects to turn down), one light.
/// </summary>
public sealed class FeralNightVision
{
    public const float Range = 24f, Intensity = 2.6f;
    Light light;
    float shown;

    public void Apply(bool on, Transform follow)
    {
        shown = Mathf.MoveTowards(shown, on && follow != null ? 1f : 0f, Time.unscaledDeltaTime * 0.8f);
        if (shown <= 0.001f || follow == null) { Dispose(); return; }
        if (light == null)
        {
            var go = new GameObject("[FeralNightVision]");
            Object.DontDestroyOnLoad(go);
            light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.shadows = LightShadows.None;
            light.color = new Color(0.72f, 0.8f, 1f);
            light.range = Range;
        }
        light.transform.position = follow.position + Vector3.up * 3.4f;
        light.intensity = Intensity * shown;
    }

    public void Dispose()
    {
        shown = 0f;
        if (light != null) Object.Destroy(light.gameObject);
        light = null;
    }
}
