using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Debug helpers for F4 (Editor evals and the <c>night</c> bot brain). Host only unless noted.
/// </summary>
public static class NightDebug
{
    public static Raccoon SpawnRaccoon(Vector3 pos, bool rabid, TrashBag bag = null)
    {
        pos.y = CritterWorld.GroundY(pos + Vector3.up * 2f, pos.y);
        return Raccoon.Spawn(pos, Quaternion.Euler(0f, NightDice.Host.Range(0f, 360f), 0f), rabid, null, bag);
    }

    /// <summary>A bag with a valuable in it at <paramref name="pos"/>, and a raccoon that robs it right away (the real path:
    /// the loot comes out of the bag's contents).</summary>
    public static Raccoon StageHeist(Vector3 pos, string tag)
    {
        var contents = new BagContents { bagId = "night-test/" + tag, foundAt = "test curb", bagMassKg = 0.15f, sackVariant = 0 };
        contents.items.Add(new ItemInstance { uid = "loot-" + tag, defId = "wedding_ring", massKg = 0.01f, value = 900f, condition = 1f, foundAt = "test curb" });
        var junk = FirstOf(ItemCategory.Junk);
        for (int i = 0; i < 4 && junk != null; i++)
            contents.items.Add(new ItemInstance { uid = $"junk-{tag}-{i}", defId = junk.id, massKg = 0.5f, foundAt = "test curb" });
        var bag = TrashBag.Spawn(contents, pos + Vector3.up * 0.4f, Quaternion.identity);
        var r = SpawnRaccoon(pos + Vector3.left * 0.8f, false, bag);
        r.DebugHeist();
        return r;
    }

    static ItemDef FirstOf(ItemCategory cat)
    {
        foreach (var d in ItemCatalog.All) if (d.category == cat) return d;
        return null;
    }

    /// <summary>Put junk in the host's own truck and park it at <paramref name="pos"/> (bots / evals).</summary>
    public static TruckControllerV2 StageStowawayTruck(Vector3 pos, Quaternion rot)
    {
        TruckControllerV2 truck = null;
        foreach (var t in TruckControllerV2.All) if (t != null && DepotTruckCargo.IsLocal(t)) { truck = t; break; }
        if (truck == null) return null;
        var junk = FirstOf(ItemCategory.Junk);
        var bag = new BagContents { bagId = "night-test/cargo", foundAt = "test", bagMassKg = 0.15f };
        for (int i = 0; i < 8 && junk != null; i++) bag.items.Add(new ItemInstance { uid = "cargo-" + i, defId = junk.id, massKg = 0.6f, foundAt = "test" });
        GameSession.AddBagToTruck(bag);
        pos.y = CritterWorld.GroundY(pos + Vector3.up * 3f, pos.y) + 0.3f;
        var rb = truck.GetComponent<Rigidbody>();
        truck.transform.SetPositionAndRotation(pos, rot);
        if (rb != null) { rb.position = pos; rb.rotation = rot; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        return truck;
    }

    public static int TruckItems()
    {
        int n = 0;
        foreach (var b in GameSession.TruckBags) if (b != null && b.items != null) n += b.items.Count;
        return n;
    }
}

/// <summary>
/// The <c>night</c> bot brain (F4 gates, Docs/Multiplayer.md §8.2 M6): host = bot-1, guests = the rest. A shared schedule on NGO
/// server time:
/// <list type="bullet">
/// <item>0–20 s settle; the host makes it night.</item>
/// <item>20–90 s <b>bites</b>: guests stand on the street, the host (far away) keeps a rabid raccoon near each; guests mash out
/// of latches. bot-2 makes sure it's rabid by 90 s (for the rejoin check).</item>
/// <item>95–150 s <b>bandwidth</b>: the host keeps 8 critters around.</item>
/// <item>150–225 s <b>heists</b>: three staged heists from real bag contents; guests bonk the 1st and 3rd, the 2nd escapes.</item>
/// <item>230–290 s <b>stowaway</b>: the host's loaded truck parks on the street, a raccoon hops in and eats; bot-3 shoos it.</item>
/// <item>295–360 s <b>the Raccoon King</b>: everyone bonks him until he's dethroned (pay + crowns).</item>
/// </list>
/// Rejoin (rabies survives a reconnect): <c>--rejoin bot-2@200</c>. Checker: <c>scratchpad/f4/verify_night.py</c>.
/// </summary>
public static class NightBots
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Register() => BotBrains.Register("night", () => new NightBrain());

    static Vector3 Arena => DepotRowWorld.Active
        ? new Vector3(DepotRowLayout.LotCenterX(1), 0f, DepotRowLayout.RoadZ + 1f)
        : Vector3.zero;

    static void Log(string phase, Dictionary<string, object> d = null)
    {
        d ??= new Dictionary<string, object>();
        d["phase"] = phase;
        NetAudit.Event("nightbot", d);
    }

    sealed class NightBrain : IBotBrain
    {
        readonly HashSet<string> done = new HashSet<string>();
        float nextAct, nextMash, nextTop, nextBonk, nextLog, nextTrack;
        int heists;
        TruckControllerV2 stowTruck;
        readonly HashSet<string> seenItems = new HashSet<string>();

        public void Begin(BotContext c) { c.Stop(); Log("begin", new Dictionary<string, object> { { "host", c.IsHost } }); }
        public void End(BotContext c)
        {
            var latch = RaccoonLatch.Local;
            Log("end", new Dictionary<string, object>
            {
                { "rabid", GameSession.HasRabies }, { "helplessMax", latch != null ? Math.Round(latch.HelplessMax, 3) : 0 },
                { "inventory", GameSession.Inventory.Count }, { "truckItems", NightDebug.TruckItems() },
            });
        }

        bool Once(string key) => done.Add(key);

        public void Tick(BotContext c, float dt)
        {
            c.Stop();
            var nn = NightNet.Instance;
            if (c.Player == null || nn == null || !nn.IsSpawned || c.Nm == null) return;
            double T = c.Nm.ServerTime.Time;
            var pc = c.Player.GetComponent<PlayerController>();
            if (pc == null) return;
            ulong me = c.Nm.LocalClientId;
            int idx = GuestIndex(c);

            // Always: mash out of a latch (a fast human: ~8 presses a second).
            var latch = RaccoonLatch.Local;
            if (latch != null && latch.Latched && Time.time >= nextMash) { nextMash = Time.time + 0.12f; latch.Mash(); }

            if (!c.IsHost && Time.time >= nextTrack) { nextTrack = Time.time + 0.5f; TrackItems(c); }
            if (c.IsHost && T > 8 && Once("night")) { GameClock.DebugSetHour(22.5f); Log("night", new Dictionary<string, object> { { "hour", GameClock.Hours } }); }
            if (T < 18) return;

            Vector3 arena = Arena;
            Vector3 myStand = c.IsHost ? arena + new Vector3(-62f, 0f, 0f) : arena + new Vector3(idx * 14f, 0f, 0f);

            // ---------------------------------------------------------------- bites (20–90)
            if (T < 92)
            {
                if (Once("stand-bites")) HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(myStand), Vector3.forward);
                if (c.IsHost && Time.time >= nextAct)
                {
                    nextAct = Time.time + 7f;
                    foreach (var b in NightPlayers.All)
                    {
                        if (b == null || NightPlayers.IsLocal(b)) continue;
                        Vector3 p = b.transform.position + new Vector3(4f, 0f, 3f);
                        NightDebug.SpawnRaccoon(p, true);
                    }
                    Log("spawn-rabid");
                }
                if (!c.IsHost && idx == 1 && T > 86 && !GameSession.HasRabies && Once("force-rabid"))
                {
                    GameSession.Infect(GameClock.TotalHours); // the rejoin check needs a rabid guest
                    Log("forced-rabies");
                }
                return;
            }

            // ---------------------------------------------------------------- bandwidth (95–150)
            if (T < 150)
            {
                if (Once("stand-bw")) HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(myStand + new Vector3(0f, 0f, -2f)), Vector3.forward);
                if (c.IsHost && T > 100 && Once("nightpay"))
                {
                    // Night pay x1.5 through the real collect path: one curb bag into the host's truck, one into a guest's.
                    string guestBiz = null;
                    foreach (var p in NightPlayers.All)
                        if (p != null && !NightPlayers.IsLocal(p) && Businesses.TryGetForClient(NightPlayers.ClientOf(p), out var bi)) { guestBiz = bi.id.ToString(); break; }
                    int n = 0;
                    foreach (var rec in new List<CurbBag>(GameSession.CurbBags))
                    {
                        if (rec == null || rec.contents == null) continue;
                        string biz = n == 0 ? null : guestBiz;
                        float pay = TruckHopper.CollectCurbBag(rec.contents, biz, arena, false, rec.stopId);
                        Log("nightpay", new Dictionary<string, object> { { "business", biz ?? "host" }, { "cents", Mathf.RoundToInt(pay * 100f) }, { "hour", Math.Round(GameClock.Hours, 2) } });
                        if (++n >= 2) break;
                    }
                }
                if (c.IsHost && Time.time >= nextTop)
                {
                    nextTop = Time.time + 1.5f;
                    int live = 0;
                    foreach (var cr in Critter.All) if (cr != null && !cr.Despawning && !cr.NetPuppet) live++;
                    for (int i = live; i < 8; i++)
                    {
                        Vector3 p = arena + new Vector3(NightDice.Host.Range(-20f, 40f), 0f, NightDice.Host.Range(4f, 14f));
                        if (i % 3 == 0) PileGoblin.Spawn(null, HandsBots.Ground(p));
                        else NightDebug.SpawnRaccoon(p, false);
                    }
                }
                return;
            }

            // ---------------------------------------------------------------- heists (150–225)
            if (T < 226)
            {
                if (c.IsHost)
                {
                    if (Once("clear-bw")) { CritterSpawner.DebugClearCritters(); Log("cleared"); }
                    if (T > 156 && heists < 3 && Time.time >= nextAct)
                    {
                        nextAct = Time.time + 18f;
                        heists++;
                        var r = NightDebug.StageHeist(arena + new Vector3(heists * 9f, 0f, 9f), "h" + heists + "-" + (long)T);
                        Log("heist", new Dictionary<string, object> { { "n", heists }, { "critter", r != null ? r.NetId : 0 }, { "loot", r != null ? r.LootDefId : null } });
                    }
                }
                else
                {
                    // Guests chase heisting raccoons and bonk them, except during the 2nd heist (it should escape).
                    bool skip = T > 176 && T < 198;
                    if (!skip && Time.time >= nextBonk)
                    {
                        nextBonk = Time.time + 1f;
                        foreach (var cr in Critter.All)
                        {
                            if (!(cr is Raccoon r) || !r.NetPuppet || r.Despawning || r.NetState != (byte)Raccoon.State.Heist) continue;
                            Vector3 to = r.transform.position - CritterWorld.Flat(r.transform.position - pc.transform.position).normalized * 3f;
                            HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(to), r.transform.position - to);
                            NightNet.RequestBonk(r, (r.transform.position - pc.transform.position).normalized * 9f, me);
                            Log("bonk", new Dictionary<string, object> { { "critter", r.NetId } });
                            break;
                        }
                    }
                }
                return;
            }

            // ---------------------------------------------------------------- stowaway (230–290)
            if (T < 292)
            {
                if (T < 230) return;
                if (c.IsHost)
                {
                    if (Once("stow-stage"))
                    {
                        HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(arena + new Vector3(-62f, 0f, 0f)), Vector3.forward);
                        stowTruck = NightDebug.StageStowawayTruck(arena + new Vector3(30f, 0f, 1f), Quaternion.Euler(0f, 90f, 0f));
                        Log("stow-truck", new Dictionary<string, object> { { "ok", stowTruck != null }, { "items", NightDebug.TruckItems() } });
                    }
                    if (stowTruck != null && T > 236 && Once("stow-raccoon"))
                    {
                        var r = NightDebug.SpawnRaccoon(stowTruck.transform.TransformPoint(new Vector3(2f, 0f, -7f)), false);
                        bool ok = r.TryStowaway(stowTruck);
                        Log("stow-raccoon", new Dictionary<string, object> { { "ok", ok }, { "critter", r.NetId } });
                    }
                    if (stowTruck != null && T > 285 && Once("stow-result"))
                        Log("stow-result", new Dictionary<string, object> { { "items", NightDebug.TruckItems() } });
                }
                else if (idx == 2 || (idx == 1 && T > 270))
                {
                    if (T > 255 && Time.time >= nextBonk)
                    {
                        nextBonk = Time.time + 1.5f;
                        foreach (var cr in Critter.All)
                        {
                            if (!(cr is Raccoon r) || !r.NetPuppet || !r.Boarded) continue;
                            Vector3 to = r.transform.position - r.transform.forward * -1.5f;
                            to = r.AnchorTransform != null ? r.AnchorTransform.TransformPoint(new Vector3(0f, 0f, -5.2f)) : to;
                            HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(to), r.transform.position - to);
                            NightNet.RequestPoke(r);
                            Log("poke", new Dictionary<string, object> { { "critter", r.NetId } });
                            break;
                        }
                    }
                }
                return;
            }

            // ---------------------------------------------------------------- the Raccoon King (295–360)
            if (T < 296) return;
            if (c.IsHost && Once("king")) Log("king", new Dictionary<string, object> { { "msg", RaccoonKingEvent.DebugStart() } });
            if (Time.time < nextBonk) return;
            nextBonk = Time.time + 0.8f;
            foreach (var cr in Critter.All)
            {
                if (!(cr is RaccoonKing k) || k.Despawning) continue;
                if (c.IsHost) { if (k.NetPuppet) continue; }
                else if (!k.NetPuppet) continue;
                Vector3 to = k.transform.position + CritterWorld.Flat(pc.transform.position - k.transform.position).normalized * 8f;
                if (Once("king-stand")) HandsBots.Teleport(c.Player.gameObject, HandsBots.Ground(to), k.transform.position - to);
                Vector3 v = (k.transform.position - pc.transform.position).normalized * 10f;
                if (c.IsHost) k.NetBonk(v, me);
                else NightNet.RequestBonk(k, v, me);
                break;
            }
            if (Time.time >= nextLog) { nextLog = Time.time + 10f; Log("tick", new Dictionary<string, object> { { "T", Math.Round(T, 1) } }); }
        }

        /// <summary>Every loose item this machine has, logged once per (key, copies) (heist loot conservation: each dropped uid
        /// shows up on every guest, exactly once).</summary>
        void TrackItems(BotContext c)
        {
            var hn = HandsNet.Instance;
            if (hn == null) return;
            var count = new Dictionary<string, int>();
            foreach (var nc in hn.Items.Values)
            {
                if (nc == null || nc.Desc.kind != CarryDesc.KindItem) continue;
                string k = nc.Desc.key.ToString();
                count[k] = count.TryGetValue(k, out int n) ? n + 1 : 1;
            }
            foreach (var kv in count)
                if (seenItems.Add(kv.Key + "#" + kv.Value))
                    Log("loot-seen", new Dictionary<string, object> { { "uid", kv.Key }, { "copies", kv.Value } });
        }

        static int GuestIndex(BotContext c)
        {
            if (c.IsHost) return 0;
            string label = c.Options != null ? c.Options.label : "";
            int dash = label.LastIndexOf('-');
            return dash >= 0 && int.TryParse(label.Substring(dash + 1), out int n) ? n - 1 : 1;
        }
    }
}
