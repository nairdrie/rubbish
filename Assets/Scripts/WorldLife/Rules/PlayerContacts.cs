using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package E (Docs/WorldLife.md §8.4): players on foot and the things they throw, against people.
/// <list type="bullet">
/// <item><b>Bumps</b> (detected on the player's own machine, L): walking into someone gets a "pardon me" (their avoidance
/// already sidesteps you); sprinting (≥ 4.5 m/s) stumbles them 0.5 m back and spills their drink ("excuse YOU"). No fine;
/// 1 s per NPC. F relays it as a ≤ 16 B WorldFx stumble.</item>
/// <item><b>Thrown things</b> (the thrower's machine claims): a bag or item thrown into a person boops them (&lt; 6 m/s),
/// stumbles them (6–11 m/s, "HEY!") or short-flops them (≥ 11 m/s, Rep −1 on the host), once per thrower and NPC every
/// 4 s. Puppets have no colliders, so the hit is by rule (rule 13): the thing bonks off their head with a fraction of its
/// speed and never bursts on them.</item>
/// </list>
/// Players come from <see cref="CoopPlayerHooks.Spawned"/> (every copy; only this machine's own are checked), never
/// PlayerController.Local (rule 8). 0 B per frame after warm-up.
/// </summary>
public sealed class PlayerContacts
{
    // ------------------------------------------------------------------ players (static: they spawn before WorldLife wakes)

    sealed class Player
    {
        public PlayerController pc;
        public CharacterController cc;
        public TrashCarrier carrier;
        public Vector3 last;
        public bool hasLast;
    }

    static readonly List<Player> players = new List<Player>(8);

    struct Flying
    {
        public Carryable c;
        public byte slot;
        public float since;
    }

    static readonly List<Flying> flying = new List<Flying>(RulesTuning.MaxThrownTracked);

    public static int Tracked => flying.Count;
    public static int ThrowsSeen { get; private set; }

    /// <summary>Idempotent (static hooks).</summary>
    public static void Boot()
    {
        CoopPlayerHooks.Spawned -= OnSpawned;
        CoopPlayerHooks.Spawned += OnSpawned;
    }

    static void OnSpawned(PlayerController pc)
    {
        if (pc == null) return;
        for (int i = 0; i < players.Count; i++) if (players[i].pc == pc) return;
        var p = new Player { pc = pc, cc = pc.GetComponent<CharacterController>(), carrier = pc.GetComponent<TrashCarrier>() };
        players.Add(p);
        if (p.carrier != null)
        {
            var carrier = p.carrier;
            carrier.Released += c => OnReleased(carrier, c);
        }
    }

    /// <summary>A carrier let go of something: track a real throw by this machine's own player.</summary>
    static void OnReleased(TrashCarrier carrier, Carryable c)
    {
        if (c == null || carrier == null || c.State != CarryState.Thrown || c.IsInScriptedFlight) return;
        if (!carrier.IsLocalOwner) return;
        Track(c, 0);
    }

    /// <summary>Track a thrown thing (also the test kit's entry point).</summary>
    public static void Track(Carryable c, byte slot)
    {
        if (c == null) return;
        for (int i = 0; i < flying.Count; i++) if (flying[i].c == c) return;
        if (flying.Count >= RulesTuning.MaxThrownTracked) flying.RemoveAt(0);
        flying.Add(new Flying { c = c, slot = slot, since = Time.time });
        ThrowsSeen++;
    }

    // ------------------------------------------------------------------ per frame: bumps

    readonly List<ILifeBody> found = new List<ILifeBody>(8);
    public int Pardons { get; private set; }
    public int Stumbles { get; private set; }

    static readonly string[] Pardon = { "Pardon me!", "Oh! Excuse me.", "After you!", "Oops, sorry, dear." };
    static readonly Color Spill = new Color(0.45f, 0.28f, 0.12f);

    public void Tick(WorldLifeContext ctx, float dt)
    {
        if (dt <= 1e-5f) return;
        var dir = ResidentDirector.Instance;
        for (int i = players.Count - 1; i >= 0; i--)
        {
            var p = players[i];
            if (p.pc == null) { players.RemoveAt(i); continue; }
            Vector3 pos = p.pc.transform.position;
            Vector3 vel = p.cc != null && p.cc.enabled ? p.cc.velocity : (p.hasLast ? (pos - p.last) / dt : Vector3.zero);
            p.last = pos; p.hasLast = true;
            if (!p.pc.isActiveAndEnabled || p.pc.IsDriving || !CoopPlayerHooks.IsLocalOwner(p.pc)) continue;
            vel.y = 0f;
            float s = vel.magnitude;
            if (s < RulesTuning.BumpMinSpeed) continue;
            found.Clear();
            LifeBodies.Query(pos, RulesTuning.BumpDistance, found, LifeBodyKind.Person, true);
            for (int k = 0; k < found.Count; k++)
            {
                var b = found[k];
                Vector3 to = b.Position - pos; to.y = 0f;
                if (to.magnitude > RulesTuning.BumpDistance || Vector3.Dot(to, vel) <= 0f) continue;   // touching and heading into them
                var hit = WorldLifeAuthority.Instance.ClaimBump(b, pos, vel, 0, ctx.FixedTick);
                if (hit.target != b.Id) continue;   // cooling down
                var agent = dir != null ? dir.Find(b.Id) : null;
                if (hit.kind == NpcHitKind.Stumble)
                {
                    Stumbles++;
                    if (agent != null && Spills(agent.style.prop) && dir.NearCamera(b.Position, 40f))
                    {
                        global::Fx.Burst(b.Position + Vector3.up * 1.05f, Spill, 10, 2.6f);
                        Sfx.Play("splat", b.Position + Vector3.up, 0.45f, 1.3f);
                    }
                }
                else if (agent != null)
                {
                    Pardons++;
                    dir.Say(agent, Pardon, ResidentLines.Chat, false);
                }
            }
        }
        found.Clear();
    }

    static bool Spills(PuppetProp p) => p == PuppetProp.Mug || p == PuppetProp.Tumbler;

    // ------------------------------------------------------------------ per fixed step: thrown things

    public int Bonks { get; private set; }

    public void FixedTick(WorldLifeContext ctx)
    {
        if (flying.Count == 0) return;
        float now = Time.time;
        for (int i = flying.Count - 1; i >= 0; i--)
        {
            var f = flying[i];
            var c = f.c;
            if (c == null || c.State != CarryState.Thrown || c.IsInScriptedFlight || now - f.since > 6f) { flying.RemoveAt(i); continue; }
            var rb = c.Body;
            if (rb == null) { flying.RemoveAt(i); continue; }
            Vector3 v = rb.linearVelocity;
            if (v.sqrMagnitude < 1f) continue;
            Vector3 pos = rb.worldCenterOfMass;
            found.Clear();
            LifeBodies.Query(pos, 0.6f, found, LifeBodyKind.Person, true);
            for (int k = 0; k < found.Count; k++)
            {
                var b = found[k];
                Vector3 bp = b.Position, he = b.HalfExtents;
                Vector3 d = pos - bp; d.y = 0f;
                if (d.magnitude > he.x + RulesTuning.ThrownHitPad) continue;
                if (pos.y < bp.y - 0.1f || pos.y > bp.y + 2f * he.y + 0.25f) continue;
                var hit = WorldLifeAuthority.Instance.ClaimThrownHit(b, v, f.slot, ctx.FixedTick);
                if (hit.kind == NpcHitKind.None) continue;
                // Bonk off them by rule: back the way it came, slowly, so it never bursts on a neighbour.
                Vector3 flat = new Vector3(v.x, 0f, v.z);
                rb.linearVelocity = -flat * RulesTuning.ThrownBounce + Vector3.up * RulesTuning.ThrownBounceUp;
                Bonks++;
                if (ResidentDirector.Instance != null && ResidentDirector.Instance.NearCamera(bp, 45f))
                {
                    Sfx.Play("thud", pos, 0.6f, 1.2f);
                    GameHUD.FloatingText(bp + Vector3.up * (2f * he.y + 0.5f), hit.kind == NpcHitKind.ShortFlop ? "BONK!!" : "bonk!", ResidentLines.Hey, 0.9f);
                }
                flying.RemoveAt(i);
                break;
            }
        }
        found.Clear();
    }

    public void Clear() { flying.Clear(); found.Clear(); }

    public void ResetStats() { Pardons = Stumbles = Bonks = 0; ThrowsSeen = 0; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { players.Clear(); flying.Clear(); ThrowsSeen = 0; }
}
