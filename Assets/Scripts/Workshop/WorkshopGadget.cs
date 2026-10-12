using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Shop toys (Scrapazon decor) are opt-in, local to one shop, and never pay money or replace purchased automation.</summary>
public sealed class WorkshopGadget : MonoBehaviour, IInteractable
{
    public WorkshopModel model;
    public bool RadioOn { get; private set; }
    public ulong SeatedClient { get; private set; } = HandsNet.None;
    public bool Feeding { get; private set; }
    public float NextUse { get; set; }
    AudioSource radio;
    AudioClip tune;
    readonly Dictionary<ulong, Vector3> previous = new Dictionary<ulong, Vector3>();
    readonly HashSet<ulong> scored = new HashSet<ulong>();
    float nextHoopScan;
    float nextStateRequest;
    int stateRequests;
    bool stateReceived;

    /// <summary>Resolve the named player's copy, never a different local player as a guest-authority fallback.</summary>
    public static PlayerController PlayerFor(ulong client)
    {
        var body = PlayerBody.For(client);
        if (body != null) return body.Player;
        var manager = NetBoot.Manager;
        if (manager != null && manager.IsServer && manager.ConnectedClients.TryGetValue(client, out var connection) && connection.PlayerObject != null)
            return connection.PlayerObject.GetComponent<PlayerController>();
        var local = PlayerController.Local; // coop:allow(local) fallback is restricted below to this exact client and an owned copy (solo has no PlayerBody)
        return client == Permissions.LocalClientId && local != null && CoopPlayerHooks.IsLocalOwner(local) ? local : null;
    }

    public static void Attach(WorkshopModel m)
    {
        if (m == null || !IsGadget(m.recipeId) || m.GetComponent<WorkshopGadget>() != null) return;
        var g = m.gameObject.AddComponent<WorkshopGadget>(); g.model = m;
        if (m.recipeId != "bag_hoop")
        {
            var action = m.Anchor("Action") ?? m.transform;
            var z = action.gameObject.AddComponent<InteractionZone>();
            z.center = Vector3.up * .9f; z.size = new Vector3(2.6f, 2.8f, 2.6f);
            z.mode = ZoneMode.OnFoot; z.score = 2.4f; z.targetOverride = g;
        }
    }

    public static bool IsGadget(string id) => id == "bag_hoop" || id == "sorting_chute" || id == "spring_launcher" ||
        id == "break_sofa" || id == "scrap_radio" || id == "doorbell";
    public Vector3 ActionPosition => (model.Anchor("Action") ?? transform).position;
    public bool CanInteract() => model != null && isActiveAndEnabled && !PlayerController.IsUIBlocking;
    public string GetInteractionPrompt() => model.recipeId switch
    {
        "sorting_chute" => Feeding ? "Chute feeding — up to 3 bags" : "Crank sorting chute (up to 3 floor bags)",
        "spring_launcher" => "Spring-launch the bag in your hands",
        "break_sofa" => SeatedClient == Permissions.LocalClientId ? "Get off the questionable sofa" : "Take a break on the sofa",
        "scrap_radio" => RadioOn ? "Switch scrap radio OFF" : "Switch scrap radio ON",
        "doorbell" => "Ring the ridiculous doorbell",
        _ => "Shop gadget"
    };
    public void OnInteract(GameObject interactor)
    {
        if (CanInteract() && HandsNet.Instance != null) HandsNet.Instance.UseWorkshopGadget(model.lot, model.recipeId);
    }
    public void Highlight(bool on) { }

    public void Present(string effect, ulong client = HandsNet.None)
    {
        Vector3 at = ActionPosition + Vector3.up;
        switch (effect)
        {
            case "ring":
                Sfx.Play("ding", at, .45f, .75f); Sfx.Play("spring", at, .25f, 1.35f);
                GameHUD.FloatingText(at, "DING-DONG… CLANG!", SortingKit.Gold, .95f); break;
            case "launch":
                Sfx.Play("spring", at, .55f, .8f); Fx.Dust(at, .65f);
                GameHUD.FloatingText(at, "BOING!", SortingKit.ComboOrange, 1.1f); break;
            case "feed": Sfx.Play("click", at, .45f, .8f); Fx.Dust(at, .4f); break;
            case "swish":
                Sfx.Play("ding", at, .55f, 1.4f);
                GameHUD.FloatingText((model.Anchor("RingCenter") ?? transform).position + Vector3.up * .4f,
                    "TRASHKET!", SortingKit.Gold, 1.3f); break;
            case "radio_on": RadioOn = true; EnsureRadio(); radio.Play(); break;
            case "radio_off": RadioOn = false; if (radio != null) radio.Stop(); break;
            case "sit":
                if (SeatedClient != client) StandCurrentSitter();
                SeatedClient = client;
                var player = PlayerFor(client);
                if (player != null) WorkshopLounge.Sit(player, this, model.Anchor("Seat") ?? transform);
                break;
            case "stand":
                StandCurrentSitter();
                SeatedClient = HandsNet.None; break;
        }
    }

    void StandCurrentSitter()
    {
        var sitter = PlayerFor(SeatedClient);
        var lounge = sitter != null ? sitter.GetComponent<WorkshopLounge>() : null;
        if (lounge != null && lounge.IsSeatedOn(this)) lounge.Stand();
    }

    public void ApplySnapshot(bool radioOn, ulong seatedClient)
    {
        stateReceived = true;
        if (model.recipeId == "scrap_radio" && RadioOn != radioOn) Present(radioOn ? "radio_on" : "radio_off");
        if (model.recipeId == "break_sofa")
        {
            if (seatedClient == HandsNet.None) Present("stand");
            else Present("sit", seatedClient);
        }
    }

    void EnsureRadio()
    {
        if (radio != null) return;
        radio = gameObject.AddComponent<AudioSource>(); radio.playOnAwake = false; radio.loop = true;
        radio.volume = .25f * GameSettings.MusicVolume; // Do not play a loud first frame before Update applies mute.
        radio.spatialBlend = 1f; radio.rolloffMode = AudioRolloffMode.Linear; radio.minDistance = 1f; radio.maxDistance = 6f;
        const int hz = 22050; const float beat = .375f;
        int[] notes = { 60, 64, 67, 71, 69, 67, 64, 62, 57, 60, 64, 67, 65, 64, 62, 59 };
        var samples = new float[hz * 6];
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)hz; int n = Mathf.Min(notes.Length - 1, (int)(t / beat));
            float part = t % beat, env = Mathf.Min(part / .018f, 1f) * Mathf.Clamp01((beat - part) / .09f);
            float f = 440f * Mathf.Pow(2f, (notes[n] - 69) / 12f);
            samples[i] = Mathf.Sin(t * f * 2f * Mathf.PI) * env * .23f;
        }
        tune = AudioClip.Create("Scrap radio original jingle", samples.Length, 1, hz, false);
        tune.SetData(samples, 0); radio.clip = tune;
    }

    void Update()
    {
        if (radio != null) radio.volume = .25f * GameSettings.MusicVolume;
        if (model == null) return;
        var hands = HandsNet.Instance;
        if (!Businesses.IsAuthority && !stateReceived && stateRequests < 10 && Time.unscaledTime >= nextStateRequest &&
            hands != null && hands.IsSpawned)
        {
            nextStateRequest = Time.unscaledTime + 2f;
            stateRequests++;
            hands.RequestWorkshopGadgetState(model.lot, model.recipeId);
        }
        if (SeatedClient != HandsNet.None)
        {
            var p = PlayerFor(SeatedClient);
            var body = p != null ? p.GetComponent<PlayerBody>() : null;
            bool valid = p != null && (body == null || body.State == BodyState.Normal && body.enabled) &&
                !p.IsDriving && !p.IsPlayingChess && !CompactorCube.IsSuctioning(SeatedClient) && !CompactorCube.IsCubed(SeatedClient);
            if (Businesses.IsAuthority && (!valid || Vector3.Distance(p.transform.position, ActionPosition) > 4f))
                hands?.WorkshopEffect(model.lot, model.recipeId, "stand");
            else if (valid && (p.GetComponent<WorkshopLounge>()?.IsSeatedOn(this) != true))
                WorkshopLounge.Sit(p, this, model.Anchor("Seat") ?? transform); // Avatar can arrive after the gadget snapshot.
        }
        if (!Businesses.IsAuthority) return;
        if (model.recipeId != "bag_hoop" || Time.time < nextHoopScan) return;
        nextHoopScan = Time.time + .04f;
        var ring = model.Anchor("RingCenter"); if (ring == null) return;
        foreach (var c in Carryable.All)
        {
            if (!(c is TrashBag) || c.State != CarryState.Thrown) continue;
            var net = c.GetComponent<NetCarry>(); if (net != null && net.St.Phase != HandsPhase.Flight) continue;
            Vector3 now = c.WorldCenter;
            uint itemKey = c.NetId != 0 ? c.NetId : unchecked((uint)c.GetInstanceID());
            uint throwKey = net != null ? net.St.seq : unchecked((uint)Mathf.RoundToInt(c.LastReleaseTime * 1000f));
            ulong attempt = ((ulong)itemKey << 32) | throwKey;
            if (previous.TryGetValue(attempt, out var old) && !scored.Contains(attempt) && CrossedHoop(old, now, ring.position, .62f))
            {
                scored.Add(attempt); HandsNet.Instance?.WorkshopEffect(model.lot, model.recipeId, "swish");
            }
            previous[attempt] = now;
        }
        if (previous.Count > 256) previous.Clear();
        if (scored.Count > 512) scored.Clear();
    }

    public static bool CrossedHoop(Vector3 from, Vector3 to, Vector3 center, float radius)
    {
        if (from.y <= center.y || to.y > center.y || from.y - to.y > 4f) return false;
        var p = Vector3.Lerp(from, to, (from.y - center.y) / Mathf.Max(.0001f, from.y - to.y));
        p.y = center.y; return (p - center).sqrMagnitude <= radius * radius;
    }

    public void Feed(ulong client) { if (!Feeding) StartCoroutine(FeedBags(client)); }
    IEnumerator FeedBags(ulong client)
    {
        Feeding = true;
        try
        {
            var lot = DepotRowScene.Lot(model.lot);
            Transform shop = lot != null && lot.interior != null ? lot.interior.transform : transform.root;
            var table = shop.GetComponentInChildren<SortingTable>(true);
            var compactor = shop.GetComponentInChildren<Compactor>(true);
            if (lot == null && model.lot == -1 && !DepotRowWorld.Active)
            {
                // Legacy solo's annex is a separate root, but never borrow another additive shop's equipment.
                foreach (var root in gameObject.scene.GetRootGameObjects())
                {
                    if (table == null) table = root.GetComponentInChildren<SortingTable>(true);
                    if (compactor == null) compactor = root.GetComponentInChildren<Compactor>(true);
                }
            }
            if (table == null || compactor == null) yield break;
            for (int i = 0; i < 3; i++)
            {
                // Permission, shutdown and compactor ownership can all change while another bag is ripping.
                while (table != null && table.IsRipping)
                {
                    if (!CanContinueFeed(client, table)) yield break;
                    yield return null;
                }
                if (!CanContinueFeed(client, table)) yield break;
                var bag = GaragePile.Best(compactor, ActionPosition, false);
                if (bag == null || !table.CanAccept(bag, out _) || HandsNet.Instance == null) yield break;
                if (!HandsNet.Instance.WorkshopFeedBag(bag, table)) yield break;
                HandsNet.Instance.WorkshopEffect(model.lot, model.recipeId, "feed");
                yield return new WaitForSeconds(1.1f);
            }
        }
        finally { Feeding = false; }
    }

    bool CanContinueFeed(ulong client, SortingTable table) => table != null && table.isActiveAndEnabled &&
        Businesses.IsAuthority && isActiveAndEnabled && Permissions.CanWorkIn(client, model.lot) &&
        !Compactor.ShopBusy(table) && PlayerFor(client) != null;

    void OnDisable()
    {
        StopAllCoroutines(); Feeding = false; previous.Clear();
        StandCurrentSitter();
        if (radio != null) radio.Stop();
    }
    void OnEnable()
    {
        stateReceived = false; stateRequests = 0; nextStateRequest = 0f;
        if (RadioOn && radio != null) { radio.volume = .25f * GameSettings.MusicVolume; radio.Play(); }
    }
    void OnDestroy() { if (tune != null) Destroy(tune); }
}
