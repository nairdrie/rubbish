using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Trash-can traps (F4, Docs/Multiplayer.md §14.4). Buy one on Scrapazon ("Humane-ish Raccoon Trap"), press T on foot to set it
/// down (baited with a fish). At night raccoons within 16 m sniff their way in and the door slams. A caught trap rattles and
/// glares; walk up to it:
/// <list type="bullet">
/// <item>E: <b>Animal Control bounty</b> (+$40, rabid ones +$60) to your business, through the Ledger.</item>
/// <item>F: <b>keep it as a shop pet</b>: "Bandit the Pet Raccoon" goes into your keep crate, and your display shelves show
/// it off like any other find.</item>
/// </list>
/// An empty trap can be picked back up (E). The host owns the list (<see cref="NightNet"/> in co-op); every machine builds the
/// cans from it. Max 2 per player, 8 in the Town.
/// </summary>
public class RaccoonTrap : MonoBehaviour
{
    public const string ConsumableId = "raccoon_trap";
    public const string PetDefId = "pet_raccoon";
    public const float LureRadius = 16f;
    public const KeyCode PlaceKey = KeyCode.T;

    static readonly List<RaccoonTrap> all = new List<RaccoonTrap>();
    public static IReadOnlyList<RaccoonTrap> All => all;

    public int Id { get; private set; }
    public byte State { get; private set; }
    public ulong Owner { get; private set; }
    public bool Armed => State == TrapNet.Armed;
    public bool Caught => State != TrapNet.Armed;
    /// <summary>Where a lured raccoon walks to (the can's open end).</summary>
    public Vector3 Mouth => transform.TransformPoint(new Vector3(0f, 0f, 0.75f));

    /// <summary>Host: a raccoon is already on its way in.</summary>
    public bool HasLuredRaccoon
    {
        get
        {
            foreach (var c in Critter.All) if (c is Raccoon r && r.LureTrap == this && !r.Despawning) return true;
            return false;
        }
    }

    Transform door, eyes, can;
    float nextRattle;

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() => all.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { all.Clear(); NightTraps.ResetStatics(); }

    public static RaccoonTrap Build(TrapNet t)
    {
        var go = new GameObject("Raccoon Trap #" + t.id);
        go.transform.SetPositionAndRotation(t.pos, Quaternion.Euler(0f, t.yaw, 0f));
        var trap = go.AddComponent<RaccoonTrap>();
        trap.Id = t.id;
        trap.Owner = t.owner;
        trap.BuildLook();
        trap.Apply(t);
        Juice.PopIn(go.transform, 0.3f);
        Sfx.Play("thud", t.pos, 0.7f, 1.1f);
        return trap;
    }

    void BuildLook()
    {
        var green = CritterLook.Mat(new Color(0.25f, 0.42f, 0.3f), 0.35f);
        var dark = CritterLook.Mat(new Color(0.12f, 0.14f, 0.13f), 0.2f);
        var lid = CritterLook.Mat(new Color(0.35f, 0.36f, 0.38f), 0.6f);
        can = CritterLook.Node(transform, "Can", new Vector3(0f, 0.38f, 0f));
        // A trash can lying on its side, open end toward +z.
        CritterLook.Part(can, PrimitiveType.Cylinder, "Body", Vector3.zero, new Vector3(0.7f, 0.6f, 0.7f), green, new Vector3(90f, 0f, 0f));
        CritterLook.Part(can, PrimitiveType.Cylinder, "Inside", new Vector3(0f, 0f, 0.45f), new Vector3(0.6f, 0.18f, 0.6f), dark, new Vector3(90f, 0f, 0f));
        for (int i = -1; i <= 1; i++)
            CritterLook.Part(can, PrimitiveType.Cylinder, "Rib", new Vector3(0f, 0f, i * 0.32f), new Vector3(0.74f, 0.03f, 0.74f), lid, new Vector3(90f, 0f, 0f));
        door = CritterLook.Node(can, "DoorHinge", new Vector3(0f, 0.35f, 0.62f));
        CritterLook.Part(door, PrimitiveType.Cylinder, "Door", new Vector3(0f, -0.35f, 0f), new Vector3(0.66f, 0.02f, 0.66f), lid, new Vector3(90f, 0f, 0f));
        // The bait: a sad little fish on a plate.
        CritterLook.Part(transform, PrimitiveType.Capsule, "Fish", new Vector3(0f, 0.08f, 0.15f), new Vector3(0.07f, 0.12f, 0.07f),
                         CritterLook.Mat(new Color(1f, 0.55f, 0.25f), 0.7f), new Vector3(0f, 0f, 90f));
        eyes = CritterLook.Node(can, "Eyes", new Vector3(0f, 0.05f, 0.5f));
        CritterLook.Eye(eyes, "EyeL", new Vector3(-0.09f, 0f, 0f), 0.09f, CritterLook.PupilBlack, 0f, null, 1.2f);
        CritterLook.Eye(eyes, "EyeR", new Vector3(0.09f, 0f, 0f), 0.09f, CritterLook.PupilBlack, 0f, null, 1.2f);
        var col = gameObject.AddComponent<BoxCollider>(); // solid, so it can be bumped into (not carried)
        col.center = new Vector3(0f, 0.38f, 0f);
        col.size = new Vector3(0.75f, 0.75f, 1.25f);

        // PlayerController maps E/F to the top two candidates. Keep both trap choices above
        // nearby curb bags (world scans score at most 2), so F really keeps the pet.
        AddZone(new Vector3(0f, 1f, 0.2f), 3.2f, new TrapAction(this, 0));
        AddZone(new Vector3(0f, 1f, 0.2f), 3.1f, new TrapAction(this, 1));
    }

    void AddZone(Vector3 center, float score, TrapAction action)
    {
        var go = new GameObject("TrapZone");
        go.transform.SetParent(transform, false);
        var z = go.AddComponent<InteractionZone>();
        z.center = center;
        z.size = new Vector3(3.2f, 3f, 3.6f);
        z.mode = ZoneMode.OnFoot;
        z.score = score;
        var t = go.AddComponent<TrapActionTarget>();
        t.action = action;
        z.targetOverride = t;
    }

    public void Apply(TrapNet t)
    {
        bool was = Caught;
        State = t.state;
        Owner = t.owner;
        if (door != null) door.localRotation = Quaternion.Euler(Caught ? 0f : -100f, 0f, 0f);
        if (eyes != null) eyes.gameObject.SetActive(Caught);
        if (Caught && !was)
        {
            Vector3 at = transform.position + Vector3.up * 0.8f;
            Sfx.Play("thud", at, 1f, 0.8f);
            Sfx.Play("raccoon_scream", at, 0.8f, 1.2f);
            GameHUD.FloatingText(at + Vector3.up * 0.3f, "*SLAM* GOTCHA!", new Color(1f, 0.85f, 0.3f), 1.3f);
            Juice.Squash(transform, 0.25f, 0.3f);
        }
    }

    void Update()
    {
        if (!Caught || can == null) return;
        if (Time.time >= nextRattle)
        {
            nextRattle = Time.time + NightDice.Cosmetic.Range(1.2f, 3f);
            Juice.Squash(can, 0.12f, 0.2f);
            if (NightDice.Cosmetic.Chance(0.4f)) Sfx.Play("chitter", transform.position + Vector3.up * 0.5f, 0.5f, 0.9f);
        }
    }

    /// <summary>Host: the lured raccoon walked in.</summary>
    public void HostCatch(Raccoon r)
    {
        if (!Armed || r == null) return;
        bool rabid = r.Rabid;
        NightAudit.Event("trap", new Dictionary<string, object> { { "op", "catch" }, { "id", Id }, { "rabid", rabid }, { "critter", r.NetId } });
        r.RemoveNow();
        NightTraps.HostSetState(Id, rabid ? TrapNet.CaughtRabid : TrapNet.Caught);
    }

    // ---------------------------------------------------------------- E / F on the trap

    public sealed class TrapAction
    {
        public readonly RaccoonTrap trap;
        public readonly byte act; // 0 = bounty / pick up, 1 = keep as pet
        public TrapAction(RaccoonTrap t, byte a) { trap = t; act = a; }
    }
}

/// <summary>One of a trap's two prompts.</summary>
public class TrapActionTarget : MonoBehaviour, IInteractable
{
    public RaccoonTrap.TrapAction action;

    ulong Me => NightNet.Streaming ? NightNet.Instance.LocalId : 0;

    public bool CanInteract()
    {
        var t = action?.trap;
        if (t == null) return false;
        if (action.act == 1) return t.Caught;
        return t.Caught || t.Owner == Me;
    }

    public string GetInteractionPrompt()
    {
        var t = action.trap;
        if (action.act == 1) return "Keep it as a shop pet";
        if (!t.Caught) return "Pick up your trap";
        float bounty = NightTraps.BountyFor(t.State);
        return $"Call Animal Control (+{Economy.Money(bounty)} bounty)";
    }

    public void OnInteract(GameObject interactor)
    {
        if (!CanInteract()) return;
        Sfx.Play("click", action.trap.transform.position, 0.8f, 1.1f);
        NightTraps.RequestAct(action.trap.Id, action.act);
    }

    public void Highlight(bool highlight) { }
}

/// <summary>Host logic for the traps, and the T key (F4).</summary>
public static class NightTraps
{
    public const int MaxPerPlayer = 2, MaxTotal = 8;

    static readonly List<TrapNet> solo = new List<TrapNet>();
    static readonly Dictionary<int, RaccoonTrap> objects = new Dictionary<int, RaccoonTrap>();
    static int nextId = 1;

    internal static void ResetStatics() { solo.Clear(); objects.Clear(); nextId = 1; }

    public static float BountyFor(byte state) => state == TrapNet.CaughtRabid ? NightShift.TrapBounty * 1.5f : NightShift.TrapBounty;

    static bool Net => NightNet.Streaming;

    static List<TrapNet> Current()
    {
        if (!Net) return solo;
        var list = new List<TrapNet>();
        var tr = NightNet.Instance.Traps;
        for (int i = 0; i < tr.Count; i++) list.Add(tr[i]);
        return list;
    }

    static void Write(List<TrapNet> list)
    {
        if (Net) { NightNet.Instance.HostWriteTraps(list); return; }
        if (!ReferenceEquals(list, solo)) { solo.Clear(); solo.AddRange(list); }
        SyncList(solo);
    }

    public static void Clear()
    {
        foreach (var kv in objects) if (kv.Value != null) Object.Destroy(kv.Value.gameObject);
        objects.Clear();
    }

    /// <summary>Every machine: build / update / remove the cans to match the host's list.</summary>
    public static void SyncFrom(Unity.Netcode.NetworkList<TrapNet> list)
    {
        var tmp = new List<TrapNet>();
        for (int i = 0; i < list.Count; i++) tmp.Add(list[i]);
        SyncList(tmp);
    }

    /// <summary>Solo: the cans live in the town scene; rebuild them when it loads again.</summary>
    public static void SoloRebuild()
    {
        if (Net) return;
        var dead = new List<int>();
        foreach (var kv in objects) if (kv.Value == null) dead.Add(kv.Key);
        foreach (var id in dead) objects.Remove(id);
        if (solo.Count > 0) SyncList(solo);
    }

    static void SyncList(List<TrapNet> list)
    {
        if (!Net && !CritterWorld.InNeighborhood) return; // solo in the garage: they wait for the street
        var keep = new HashSet<int>();
        foreach (var t in list)
        {
            keep.Add(t.id);
            if (!objects.TryGetValue(t.id, out var o) || o == null) objects[t.id] = RaccoonTrap.Build(t);
            else o.Apply(t);
        }
        var gone = new List<int>();
        foreach (var kv in objects) if (!keep.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            if (objects[id] != null) Juice.ShrinkAndDestroy(objects[id].gameObject, 0.25f);
            objects.Remove(id);
        }
    }

    // ---------------------------------------------------------------- placing (local player)

    /// <summary>T on foot with a trap in your pocket.</summary>
    public static void LocalTick()
    {
        var pc = NightPlayers.LocalPlayer;
        if (pc == null || PlayerController.IsUIBlocking || !GameControls.Down(RaccoonTrap.PlaceKey, GameControls.Action.Trap)) return;
        if (GameSession.GetConsumable(RaccoonTrap.ConsumableId) <= 0) return;
        if (pc.IsDriving || pc.IsPlayingChess || NightPlayers.IsRiding(pc) || NightPlayers.IsIndoors(pc))
        {
            GameHUD.Toast("Set traps outside, on the ground.", ToastStyle.Info, 2f);
            return;
        }
        Vector3 fwd = CritterWorld.Flat(pc.transform.forward).normalized;
        Vector3 pos = pc.transform.position + fwd * 1.4f;
        pos.y = CritterWorld.GroundY(pos, pc.transform.position.y);
        if (CritterWorld.KeepOut(pos, true)) { GameHUD.Toast("No traps on the Depot Row lots (raccoons can't go there anyway).", ToastStyle.Info, 2.5f); return; }
        GameSession.AddConsumable(RaccoonTrap.ConsumableId, -1);
        float yaw = Quaternion.LookRotation(-fwd, Vector3.up).eulerAngles.y; // open end faces away from you
        if (Net) NightNet.Instance.RequestTrapPlace(pos, yaw);
        else HostPlace(0, pos, yaw);
        GameHUD.Toast(GameClock.IsNight ? "Trap set. Now wait (from a distance)..." : "Trap set. Raccoons come out at night.", ToastStyle.Info, 2.5f);
    }

    public static void HostPlace(ulong owner, Vector3 pos, float yaw)
    {
        var list = Current();
        int mine = 0;
        foreach (var t in list) if (t.owner == owner) mine++;
        if (mine >= MaxPerPlayer || list.Count >= MaxTotal)
        {
            NightNet.HostRefundTrap(owner);
            NightNet.HostToastNear(pos, 5f, $"Too many traps out (max {MaxPerPlayer} each). Trap refunded.");
            return;
        }
        var tn = new TrapNet { id = nextId++, pos = pos, yaw = yaw, state = TrapNet.Armed, owner = owner };
        list.Add(tn);
        Write(list);
        NightAudit.Event("trap", new Dictionary<string, object> { { "op", "place" }, { "id", tn.id }, { "owner", (long)owner } });
    }

    public static void HostSetState(int id, byte state)
    {
        var list = Current();
        int i = list.FindIndex(t => t.id == id);
        if (i < 0) return;
        var t = list[i];
        t.state = state;
        list[i] = t;
        Write(list);
    }

    public static void RequestAct(int id, byte act)
    {
        if (Net) NightNet.Instance.RequestTrapAct(id, act);
        else HostAct(0, id, act);
    }

    /// <summary>Host: someone pressed E (0: bounty / pick up) or F (1: keep as a pet) on trap <paramref name="id"/>.</summary>
    public static void HostAct(ulong client, int id, byte act)
    {
        if (act > 1) return;
        var list = Current();
        int i = list.FindIndex(t => t.id == id);
        if (i < 0) return;
        var t = list[i];
        var pc = NightPlayers.ForClient(client);
        if (Net && (pc == null || (pc.transform.position - t.pos).sqrMagnitude > 5f * 5f)) return;
        string op;
        if (t.state == TrapNet.Armed)
        {
            if (act != 0 || t.owner != client) return;
            op = "pickup";
            NightNet.HostRefundTrap(client);
        }
        else if (act == 0)
        {
            op = "bounty";
            float bounty = BountyFor(t.state);
            using (Ledger.ToBusiness(BusinessOf(client)))
                Ledger.Credit(bounty, t.state == TrapNet.CaughtRabid ? "Animal Control bounty (rabid!)" : "Animal Control bounty", NightShift.LedgerBounty, t.pos);
            NightNet.Say(t.pos + Vector3.up * 1.4f, $"+{Economy.Money(bounty)} Animal Control!", 4);
        }
        else
        {
            op = "pet";
            string uid = $"pet-{GameClock.Day}-{id}-{(long)(GameClock.TotalHours * 1000) % 100000}";
            NightNet.HostGiveItem(client, RaccoonTrap.PetDefId, uid, t.state == TrapNet.CaughtRabid ? 180f : 140f,
                                  t.state == TrapNet.CaughtRabid ? "Foamy. Do not pet. (pet him)" : "Caught fair and square. Loves fish.");
            NightNet.Say(t.pos + Vector3.up * 1.4f, "New shop pet!", 4);
        }
        list.RemoveAt(i);
        Write(list);
        NightAudit.Event("trap", new Dictionary<string, object> { { "op", op }, { "id", id }, { "by", (long)client } });
    }

    /// <summary>The business to pay for <paramref name="client"/> (null = the host's own / solo).</summary>
    public static string BusinessOf(ulong client)
    {
        if (!Net || client == NightNet.Instance.LocalId) return null;
        return Businesses.TryGetForClient(client, out var b) ? b.id.ToString() : null;
    }

    /// <summary>This machine's player got an item for their keep crate.</summary>
    public static void ReceiveItem(string defId, string uid, float value, string note)
    {
        var def = ItemCatalog.Get(defId);
        float mass = def != null ? (def.minMass + def.maxMass) * 0.5f : 1f;
        var item = new ItemInstance { uid = uid, defId = defId, massKg = mass, value = value, condition = 1f, foundAt = "Night shift", dayFound = GameClock.Day, note = note };
        GameSession.AddToInventory(item);
        GameHUD.Toast($"{(def != null ? def.name : defId)} is in your keep crate! Display it when you've got shelf space.", ToastStyle.Good, 3.5f);
        Sfx.Play("sparkle", null, 0.8f, 1.1f);
        NightAudit.Event("item", new Dictionary<string, object> { { "def", defId }, { "uid", uid } });
    }
}
