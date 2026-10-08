using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Focused F4 integration brain: two trapped raccoons (Animal Control and pet), a friend bonking off a latch,
/// Rabies Tag through real shoves, a host-charged self-shot, then a flopped friend carried and jabbed (no kiosk any more).
/// Three bots, ~120 seconds.
/// NightBots covers heist/stowaway/latch/King payout; this covers the previously unexercised paths.
/// </summary>
public static class NightExtrasBots
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register()
    {
        BotBrains.Register("nightextra", () => new Brain());
        BotBrains.Register("nightkingnatural", () => new NaturalKingBrain());
    }

    static Vector3 Arena => DepotRowWorld.Active
        ? DepotRowLayout.RoadPoint(1) + DepotRowLayout.StreetFacing * Vector3.forward
        : Vector3.zero;

    static void Log(string op, Dictionary<string, object> fields = null)
    {
        fields ??= new Dictionary<string, object>();
        fields["op"] = op;
        NetAudit.Event("nightx", fields);
    }

    sealed class Brain : IBotBrain
    {
        readonly HashSet<string> done = new HashSet<string>();
        float nextShove, nextClear, nextLatchTry, nextPoke;
        int shoves;
        Raccoon latchRaccoon;

        bool Once(string key) => done.Add(key);

        public void Begin(BotContext c) { c.Stop(); Log("begin", new Dictionary<string, object> { { "host", c.IsHost } }); }

        public void End(BotContext c)
        {
            Log("end", new Dictionary<string, object>
            {
                { "rabid", GameSession.HasRabies }, { "shoves", shoves }, { "inventory", GameSession.Inventory.Count },
                { "tag", NightShift.RabiesTag },
            });
        }

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            if (c.Player == null || c.Nm == null || NightNet.Instance == null || !NightNet.Instance.IsSpawned ||
                HandsNet.Instance == null || !HandsNet.Instance.Active) return;
            double t = c.Nm.ServerTime.Time;
            int index = GuestIndex(c);
            Vector3 arena = Arena;
            if (t < 6) return;

            if (c.IsHost && Once("night"))
            {
                GameClock.DebugSetHour(23f);
                Log("night", new Dictionary<string, object> { { "hour", GameClock.Hours } });
            }

            if (t > 8 && Once("stand"))
            {
                Vector3 at = c.IsHost ? arena + new Vector3(-12f, 0f, 2f)
                           : arena + new Vector3(index == 1 ? -3f : 3f, 0f, 5f);
                HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(at), Vector3.forward);
            }

            if (c.IsHost) Host(c, t, arena);
            else if (index == 1) GuestTwo(c, t, arena);
            else if (index == 2) GuestThree(c, t, arena);
        }

        void Host(BotContext c, double t, Vector3 arena)
        {
            if (t > 11 && Once("traps"))
            {
                NightTraps.HostPlace(c.Nm.LocalClientId, HandsBots.Ground(arena + new Vector3(-3f, 0f, 1f)), 0f);
                NightTraps.HostPlace(c.Nm.LocalClientId, HandsBots.Ground(arena + new Vector3(3f, 0f, 1f)), 0f);
                Log("traps", new Dictionary<string, object> { { "count", NightNet.Instance.Traps.Count } });
            }
            if (t > 16 && Once("catch"))
            {
                int caught = 0;
                var traps = new List<RaccoonTrap>(RaccoonTrap.All);
                traps.Sort((a, b) => a.Id.CompareTo(b.Id));
                for (int i = 0; i < traps.Count && i < 2; i++)
                {
                    var trap = traps[i];
                    if (trap == null || !trap.Armed) continue;
                    var raccoon = NightDebug.SpawnRaccoon(trap.Mouth, i == 1);
                    trap.HostCatch(raccoon);
                    caught++;
                }
                Log("catch", new Dictionary<string, object> { { "count", caught } });
            }
            if (t > 28 && Once("clear-before-latch")) CritterSpawner.DebugClearCritters();
            if (t > 30 && Once("latch-raccoon"))
            {
                var victim = NightPlayers.ForClient(PlayerAtIndex(2));
                if (victim != null) latchRaccoon = NightDebug.SpawnRaccoon(victim.transform.position + Vector3.back * 0.3f, true);
                Log("latch-raccoon", new Dictionary<string, object> { { "spawned", latchRaccoon != null } });
            }
            if (t > 31 && t < 35 && latchRaccoon != null && latchRaccoon.LatchVictim == ulong.MaxValue &&
                Time.time >= nextLatchTry)
            {
                nextLatchTry = Time.time + 0.35f;
                if (latchRaccoon.DebugLatch())
                    Log("latch-start", new Dictionary<string, object> { { "victim", (long)latchRaccoon.LatchVictim } });
            }
            if (t > 41 && Once("tag-on")) NightNet.Instance.HostSetRabiesTag(true);
            if (t > 76 && Once("tag-off")) NightNet.Instance.HostSetRabiesTag(false);
            // Fresh businesses start with $0. Seed the patient payer so this scenario can verify two paid shots
            // independently of the trap bounty (whose exact $40 receipt is checked separately).
            if (t > 35 && !done.Contains("shot-funds"))
            {
                ulong guest = PlayerAtIndex(1);
                if (guest != HandsNet.None && Businesses.TryGetForClient(guest, out var business))
                {
                    using (Ledger.ToBusiness(business.id.ToString()))
                        Ledger.Credit(100f, "Night bot pharmacy float", "nightx_seed");
                    Once("shot-funds");
                    Log("shot-funds", new Dictionary<string, object> { { "business", business.id.ToString() }, { "cents", 10000 } });
                }
            }
            if (t > 39 && t < 108 && Time.time > nextClear)
            {
                nextClear = Time.time + 4f;
                CritterSpawner.DebugClearCritters();
            }
            if (t > 83 && Once("self-shot-probe")) ProbeKiosk(PlayerAtIndex(1), "self");
            if (t > 102 && Once("friend-shot-probe"))
            {
                ProbeKiosk(PlayerAtIndex(1), "friend-payer");
                ProbeKiosk(PlayerAtIndex(2), "friend-patient");
            }
            if (t > 115 && Once("summary"))
                Log("summary", new Dictionary<string, object>
                {
                    { "traps", NightNet.Instance.Traps.Count }, { "tag", NightShift.RabiesTag },
                    { "jabs", UnityEngine.Object.FindObjectsByType<RabiesJab>(FindObjectsSortMode.None).Length },
                });
        }

        void GuestTwo(BotContext c, double t, Vector3 arena)
        {
            var n = NightNet.Instance;
            var hands = HandsNet.Instance;
            if (t > 21 && Once("bounty-stand"))
            {
                var trap = FindTrap(TrapNet.Caught);
                if (trap.HasValue) HandsBots.Teleport(c.Player.gameObject, trap.Value.pos, Vector3.forward);
                Log("bounty-stand", new Dictionary<string, object> { { "found", trap.HasValue } });
            }
            if (t > 25 && Once("bounty"))
            {
                var trap = FindTrap(TrapNet.Caught);
                if (trap.HasValue) n.RequestTrapAct(trap.Value.id, 0);
                Log("bounty-request", new Dictionary<string, object> { { "found", trap.HasValue }, { "id", trap.HasValue ? trap.Value.id : -1 } });
            }
            if (t > 29 && Once("latch-help-stand"))
            {
                var victim = NightPlayers.ForClient(PlayerAtIndex(2));
                if (victim != null)
                    HandsBots.Teleport(c.Player.gameObject, victim.transform.position + new Vector3(-2f, 0f, 1f), Vector3.right);
            }
            if (t > 32 && t < 37 && Time.time >= nextPoke)
            {
                nextPoke = Time.time + 0.6f;
                foreach (var cr in Critter.All)
                {
                    if (!(cr is Raccoon r) || !r.NetPuppet || r.NetState != (byte)Raccoon.State.Latched) continue;
                    NightNet.RequestPoke(r);
                    Log("friend-poke", new Dictionary<string, object> { { "critter", r.NetId } });
                    break;
                }
            }
            if (t > 44 && Once("infect-attacker"))
            {
                GameSession.Infect(GameClock.TotalHours - Economy.RabiesIncubationHours - 0.5);
                Log("infect-attacker", new Dictionary<string, object> { { "rabid", GameSession.HasRabies } });
            }
            if (t > 50 && Once("tag-stand"))
                HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(arena + new Vector3(0f, 0f, 9f)), Vector3.right);
            if (t > 54 && t < 74 && Time.time >= nextShove)
            {
                nextShove = Time.time + 5.8f;
                ulong target = PlayerAtIndex(2);
                if (target != HandsNet.None)
                {
                    hands.LocalShove(target, false);
                    shoves++;
                    Log("tag-shove", new Dictionary<string, object> { { "target", (long)target }, { "n", shoves } });
                }
            }
            if (t > 80 && Once("self-shot-stand"))
                Log("self-shot-stand", new Dictionary<string, object> { { "kiosk", ToKiosk(c.Player.gameObject, Vector3.zero) } });
            if (t > 85 && Once("self-shot"))
            {
                bool atKiosk = NearKiosk(c.Player.gameObject);
                if (atKiosk) NightNet.ShotFor(c.Nm.LocalClientId);
                Log("self-shot-request", new Dictionary<string, object> { { "kiosk", atKiosk }, { "rabid", GameSession.HasRabies } });
            }
            if (t > 90 && Once("self-shot-result"))
                Log("self-shot-result", new Dictionary<string, object> { { "rabid", GameSession.HasRabies } });

            if (t > 96 && Once("carry-stand"))
                ToKiosk(c.Player.gameObject, new Vector3(0.6f, 0f, 0f), 0);
            if (t > 101 && Once("carry"))
            {
                ulong target = PlayerAtIndex(2);
                if (target != HandsNet.None) hands.LocalCarry(target);
                Log("carry-request", new Dictionary<string, object> { { "target", (long)target } });
            }
            if (t > 108 && Once("friend-shot"))
            {
                ulong target = PlayerAtIndex(2);
                bool carrying = hands.BodyOf(c.Nm.LocalClientId).carrying == target;
                if (target != HandsNet.None) NightNet.ShotFor(target);
                Log("friend-shot-request", new Dictionary<string, object> { { "target", (long)target }, { "carrying", carrying } });
            }
        }

        void GuestThree(BotContext c, double t, Vector3 arena)
        {
            var n = NightNet.Instance;
            if (t > 21 && Once("pet-stand"))
            {
                var trap = FindTrap(TrapNet.CaughtRabid);
                if (trap.HasValue) HandsBots.Teleport(c.Player.gameObject, trap.Value.pos, Vector3.forward);
                Log("pet-stand", new Dictionary<string, object> { { "found", trap.HasValue } });
            }
            if (t > 26 && Once("pet"))
            {
                var trap = FindTrap(TrapNet.CaughtRabid);
                if (trap.HasValue) n.RequestTrapAct(trap.Value.id, 1);
                Log("pet-request", new Dictionary<string, object> { { "found", trap.HasValue }, { "id", trap.HasValue ? trap.Value.id : -1 } });
            }
            if (t > 50 && Once("tag-stand"))
            {
                GameSession.CureRabies(); // the victim starts healthy even if a natural raccoon nipped them earlier
                HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(arena + new Vector3(1f, 0f, 9f)), Vector3.left);
            }
            if (t > 76 && Once("tag-result"))
                Log("tag-result", new Dictionary<string, object> { { "rabid", GameSession.HasRabies } });

            if (t > 91 && Once("friend-infect"))
            {
                if (!GameSession.HasRabies) GameSession.Infect(GameClock.TotalHours);
                Log("friend-infect", new Dictionary<string, object> { { "rabid", GameSession.HasRabies } });
            }
            if (t > 94 && Once("flop"))
            {
                bool atKiosk = ToKiosk(c.Player.gameObject, new Vector3(-0.6f, 0f, 0f), 0);
                HandsNet.Instance.LocalFlop(true);
                Log("flop", new Dictionary<string, object> { { "kiosk", atKiosk } });
            }
            if (t > 118 && Once("friend-shot-result"))
                Log("friend-shot-result", new Dictionary<string, object> { { "rabid", GameSession.HasRabies }, { "pets", CountPets() } });
        }

        static TrapNet? FindTrap(byte state)
        {
            var traps = NightNet.Instance.Traps;
            for (int i = 0; i < traps.Count; i++)
                if (traps[i].state == state) return traps[i];
            return null;
        }

        static ulong PlayerAtIndex(int index)
        {
            var sorted = HandsBots.Sorted();
            return sorted.Count > index ? sorted[index].ClientId : HandsNet.None;
        }

        /// <summary>Stand by a shop's laptop desk (where the first-aid shelf used to be; any spot would do now).</summary>
        static bool ToKiosk(GameObject player, Vector3 offset, int lot = -1)
        {
            int targetLot = lot >= 0 ? lot : DepotRowWorld.LocalLot;
            var laptop = DepotRowScene.Lot(targetLot)?.laptop;
            if (laptop == null) return false;
            var at = laptop.transform.position + Vector3.down * 0.8f + offset + Vector3.up * 0.05f;
            var pc = player.GetComponent<PlayerController>();
            if (pc != null) DepotRowWorld.TeleportPlayer(pc, at, Quaternion.identity);
            else HandsBots.Teleport(player, at, Vector3.forward);
            return true;
        }

        /// <summary>Shots are no longer tied to a place.</summary>
        static bool NearKiosk(GameObject player) => player != null;

        static void ProbeKiosk(ulong client, string label)
        {
            var player = NightPlayers.ForClient(client);
            bool hasBusiness = Businesses.TryGetForClient(client, out _);
            Log("kiosk-probe", new Dictionary<string, object>
            {
                { "label", label }, { "client", (long)client },
                { "player", player != null }, { "pos", player != null ? player.transform.position.ToString("F2") : "missing" },
                { "inside", player != null && NearKiosk(player.gameObject) },
                { "valid", player != null },
                { "lot", player != null ? DepotRowLayout.InteriorAt(player.transform.position) : -1 },
                { "infected", NightNet.InfectedAt(client) >= 0 }, { "business", hasBusiness },
                { "body", HandsNet.Instance != null ? HandsNet.Instance.BodyOf(client).state : (byte)255 },
            });
        }

        static int CountPets()
        {
            int count = 0;
            foreach (var item in GameSession.Inventory)
                if (item != null && item.defId == RaccoonTrap.PetDefId) count++;
            return count;
        }

        static int GuestIndex(BotContext c)
        {
            string label = c.Options != null ? c.Options.label : "";
            int dash = label.LastIndexOf('-');
            return dash >= 0 && int.TryParse(label.Substring(dash + 1), out int n) ? n - 1 : 1;
        }
    }

    /// <summary>
    /// Two-bot diagnostic: advance through real nightly 25% rolls while host and guest are indoors. Once a roll is
    /// pending, only the guest steps outdoors. The King must wait while everyone is inside and spawn while the host
    /// remains in their shop. This uses the production HostTick, not DebugStart.
    /// </summary>
    sealed class NaturalKingBrain : IBotBrain
    {
        int phase, nights;
        float next;
        bool finished;
        bool guestInside, guestOutside;
        float outdoorDeadline;

        public void Begin(BotContext c) { c.Stop(); Log("natural-begin"); }

        public void End(BotContext c) => Log("natural-end", new Dictionary<string, object>
        {
            { "nights", nights }, { "finished", finished }, { "king", RaccoonKingEvent.Active != null },
        });

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            if (finished || c.Player == null || c.Nm == null || c.Nm.ServerTime.Time < 7 || !DepotRowWorld.Active) return;
            var pc = c.Player.GetComponent<PlayerController>();
            if (pc == null) return;
            if (!c.IsHost)
            {
                if (!guestInside)
                {
                    HandsBots.Teleport(c.Player.gameObject, DepotRowLayout.InteriorOrigin(0) + new Vector3(1f, 0.2f, 0f), Vector3.forward);
                    guestInside = true;
                    Log("natural-guest-inside", new Dictionary<string, object> { { "indoors", NightPlayers.IsIndoors(pc) } });
                }
                if (!guestOutside && GameClock.Hours >= 4.2f && GameClock.Hours < GameClock.DawnStart)
                {
                    HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(Arena), Vector3.forward);
                    guestOutside = true;
                    Log("natural-guest-outside", new Dictionary<string, object> { { "indoors", NightPlayers.IsIndoors(pc) } });
                }
                return;
            }
            if (Time.time < next) return;
            next = Time.time + 1.2f; // let the host clock offset and remote indoor/outdoor transform settle under bad Wi-Fi
            switch (phase)
            {
                case 0:
                    HandsBots.Teleport(c.Player.gameObject, DepotRowLayout.InteriorOrigin(0) + new Vector3(0f, 0.2f, 0f), Vector3.forward);
                    if (!NightPlayers.IsIndoors(pc)) { Fail("player did not enter the shop interior"); return; }
                    if (NightPlayers.Count < 2 || !AllPlayersIndoors()) return;
                    GameClock.DebugSetHour(22f);
                    nights++;
                    phase = 1;
                    break;
                case 1:
                    // A real HostTick has now rolled this night. Every successful 22:00–03:00 time is due by 03:15.
                    GameClock.DebugSetHour(3.25f);
                    phase = 2;
                    break;
                case 2:
                    if (!AllPlayersIndoors()) { Fail("a player was outdoors before the pending-roll check"); return; }
                    if (RaccoonKingEvent.Active != null) { Fail("King spawned while every player was indoors"); return; }
                    if (RaccoonKingEvent.PendingScheduledHour < 0f)
                    {
                        if (nights >= 20) { Fail("20 natural nightly rolls all missed"); return; }
                        phase = 0;
                        break;
                    }
                    Log("natural-pending", new Dictionary<string, object>
                    {
                        { "nights", nights }, { "scheduled", RaccoonKingEvent.PendingScheduledHour }, { "indoors", true },
                    });
                    // The guest sees the synced 04:15 clock as the release cue and walks outside; host stays indoors.
                    GameClock.DebugSetHour(4.25f);
                    outdoorDeadline = Time.time + 12f;
                    phase = 3;
                    break;
                case 3:
                    if (Time.time > outdoorDeadline) { Fail("guest did not trigger the pending King from outdoors"); return; }
                    if (AllPlayersIndoors() || RaccoonKingEvent.Active == null) break;
                    if (!NightPlayers.IsIndoors(pc))
                    {
                        Fail("host left the shop during the outdoor-guest check");
                        return;
                    }
                    finished = true;
                    Log("natural-pass", new Dictionary<string, object>
                    {
                        { "nights", nights }, { "hp", RaccoonKingEvent.Active.Hp }, { "hour", GameClock.Hours },
                        { "hostIndoors", true }, { "guestOutdoors", true },
                    });
                    break;
            }
        }

        static bool AllPlayersIndoors()
        {
            var players = NightPlayers.All;
            if (players.Count < 2) return false;
            foreach (var p in players)
                if (p == null || !p.isActiveAndEnabled || !NightPlayers.IsIndoors(p)) return false;
            return true;
        }

        void Fail(string reason)
        {
            finished = true;
            Log("natural-fail", new Dictionary<string, object> { { "reason", reason }, { "nights", nights } });
        }
    }
}
